using Microsoft.Extensions.Logging;

namespace NineP.TestSupport;

/// <summary>
/// The <see cref="ILogger"/> the tests assert against: it keeps every record's level and its
/// formatted text, and it is safe to read while a listener is still logging into it. One shared
/// fake replaces the three near-identical private ones the transport suites used to carry.
/// </summary>
public sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _records = [];

    /// <summary>Every record so far, oldest first.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>The text of every record at <see cref="LogLevel.Warning"/> or above.</summary>
    public IReadOnlyList<string> Warnings =>
        [.. Records.Where(record => record.Level >= LogLevel.Warning).Select(record => record.Message)];

    /// <summary>Scopes are not recorded; nothing under test opens one.</summary>
    /// <typeparam name="TState">The scope state.</typeparam>
    /// <param name="state">The scope state.</param>
    /// <returns>Null, which <see cref="ILogger"/> allows.</returns>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <summary>Every level is enabled, so a test sees whatever the code under test emits.</summary>
    /// <param name="logLevel">The level being considered.</param>
    /// <returns>Always true.</returns>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>Records one entry, formatted the way a text sink would render it.</summary>
    /// <typeparam name="TState">The state the message template was bound to.</typeparam>
    /// <param name="logLevel">The severity.</param>
    /// <param name="eventId">The event id.</param>
    /// <param name="state">The bound state.</param>
    /// <param name="exception">The failure being reported, when there is one.</param>
    /// <param name="formatter">Renders the state and the exception into the record's text.</param>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_records)
        {
            _records.Add((logLevel, formatter(state, exception)));
        }
    }
}
