using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.TodoFs;
using NineP.TodoFs.Storage;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using NLogLevel = NLog.LogLevel;

// todofs: the per-user to-do tree of ARCHITECTURE §7, authenticated against a Keycloak realm.
// Exit codes: 0 when the server stopped on request, 1 on a runtime failure, 3 on a bad command
// line or a database this build will not open.
try
{
    TodoFsOptions options = TodoFsOptions.Parse(args);

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

    ILogger logger = loggerFactory.CreateLogger("todofs");

    await using TodoFsHost host = await TodoFsHost.CreateAsync(options, logger);

    using CancellationTokenSource stopping = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    Task serving = host.Server.ServeAsync(host.Filesystem, stopping.Token);

    try
    {
        await TodoFsHost.AnnounceAsync(host.Server, options);
    }
    catch
    {
        // The announce fails only because a listener did; awaiting the server surfaces that same
        // failure once and leaves nothing about it unobserved.
        await serving;
        throw;
    }

    await serving;
    return 0;
}
catch (TodoFsUsageException failure)
{
    await Console.Error.WriteLineAsync("todofs: " + failure.Message);
    await Console.Error.WriteLineAsync(TodoFsOptions.Usage);
    return 3;
}
catch (TodoSchemaException failure)
{
    await Console.Error.WriteLineAsync("todofs: " + failure.Message);
    return 3;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (SocketException failure)
{
    // A port already in use, or an address that is not this machine's: one line, exit 1, rather
    // than a stack trace or a process that spins for ever announcing an address it never bound.
    await Console.Error.WriteLineAsync("todofs: cannot listen: " + failure.Message);
    return 1;
}
catch (IOException failure)
{
    await Console.Error.WriteLineAsync("todofs: " + failure.Message);
    return 1;
}
