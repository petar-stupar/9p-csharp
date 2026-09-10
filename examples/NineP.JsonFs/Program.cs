using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NineP.JsonFs;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using NLogLevel = NLog.LogLevel;

// jsonfs: the workspace architecture §7 mapping, served over 9P. Exit codes: 0 when the server
// stopped on request, 1 on a runtime failure, 3 on a bad command line or a document jsonfs will
// not serve.
try
{
    JsonFsOptions options = JsonFsOptions.Parse(args);
    JsonTree tree = JsonTree.Load(options.File, options.MaxEntries);

    // IR-1: the packages log through Microsoft.Extensions.Logging; the example picks the concrete
    // sink, and NLog is this repository's choice. The configuration is built in code rather than
    // from NLog.config so that a reader sees the whole wiring in one place: one target, stderr,
    // the layout the old built-in writer produced, and the floor --log asked for.
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        if (options.MinimumLogLevel is not LogLevel floor)
        {
            return;
        }

        LoggingConfiguration configuration = new();
        ConsoleTarget stderr = new("stderr")
        {
            StdErr = true,
            Layout = "${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffZ} ${level} ${message}${onexception: ${exception:format=tostring}}",
        };

        configuration.AddRule(NLogLevel.FromOrdinal((int)floor), NLogLevel.Fatal, stderr);
        builder.SetMinimumLevel(floor).AddNLog(configuration);
    });

    ILogger logger = loggerFactory.CreateLogger("jsonfs");

    // Declared before the server so that it is disposed after it: the server stops taking
    // requests first, then the filesystem writes back whatever an open --write-back-delay window
    // still owes. That final rewrite is what keeps a change made just before Ctrl-C on disk.
    using JsonFilesystem filesystem = new(
        tree,
        options.Writable,
        options.WriteBack ? options.File : null,
        writeBackDelay: options.WriteBackDelay);

    await using NinePServer server = new(new ServerOptions
    {
        Listen = options.Listen,
        Transports = JsonFsHost.Transports(options, logger),
        Dialects = options.Dialects,
        Authenticator = options.Authenticator,
        Limits = Limits.Default with { MaxMsize = options.Msize },
        Logger = logger,
    });

    using CancellationTokenSource stopping = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    Task serving = server.ServeAsync(filesystem, stopping.Token);

    try
    {
        await JsonFsHost.AnnounceAsync(server, options);
    }
    catch
    {
        // The announce fails only because a listener did. Awaiting the server surfaces that same
        // failure once, and leaves nothing about it unobserved.
        await serving;
        throw;
    }

    await serving;
    return 0;
}
catch (JsonFsUsageException failure)
{
    await Console.Error.WriteLineAsync("jsonfs: " + failure.Message);
    await Console.Error.WriteLineAsync(JsonFsOptions.Usage);
    return 3;
}
catch (JsonFsStartupException failure)
{
    await Console.Error.WriteLineAsync("jsonfs: " + failure.Message);
    return 3;
}
catch (FileNotFoundException failure)
{
    await Console.Error.WriteLineAsync("jsonfs: " + failure.Message);
    return 3;
}
catch (DirectoryNotFoundException failure)
{
    await Console.Error.WriteLineAsync("jsonfs: " + failure.Message);
    return 3;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (NinePException failure)
{
    // The one NinePException that reaches here is the final write-back's: the server has
    // stopped, the document in memory has changes the file does not, and exiting clean would
    // say otherwise.
    await Console.Error.WriteLineAsync("jsonfs: the final write-back failed: " + failure.Error.Ename);
    return 1;
}
catch (SocketException failure)
{
    // A port already in use, or an address that is not this machine's: one line, exit 1, rather
    // than a stack trace or a process that spins for ever announcing an address it never bound.
    await Console.Error.WriteLineAsync("jsonfs: cannot listen: " + failure.Message);
    return 1;
}
catch (IOException failure)
{
    await Console.Error.WriteLineAsync("jsonfs: " + failure.Message);
    return 1;
}
