namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// fo-dicom's logger for the worklist server. fo-dicom writes a line at Information for every association, request and
/// response; those go to Debug here, so the service log keeps only warnings and errors from it (HC360 logs its own
/// summary of each query). fo-dicom never logs datasets: dataset and PDU logging stay off in its options.
/// </summary>
internal sealed class DemotedLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(Demote(logLevel));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        inner.Log(Demote(logLevel), eventId, state, exception, formatter);

    private static LogLevel Demote(LogLevel level) => level == LogLevel.Information ? LogLevel.Debug : level;
}
