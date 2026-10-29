using System.Runtime.Versioning;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Formatting;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Where the server's logs go (SRS OBS-01): the console, for whoever runs the server by hand; a rolling file of structured
/// events, one JSON object per line (<see cref="JsonLogFormatter"/>), kept for <see cref="RetentionDays"/> days in the logs
/// folder; and, for the
/// Windows service, errors in the Event Log, where Windows tools and monitoring agents look first. Every sink masks phone,
/// Aadhaar and ABHA numbers, e-mail addresses and secrets (SRS SEC-10).
/// </summary>
internal static class HospitalLogging
{
    /// <summary>The rolling file's name: the day is added before the extension (hc360-server-20261023.json).</summary>
    public const string FileName = "hc360-server-.json";

    public const string EventLogSource = "HyFib HealthCare360";

    /// <summary>
    /// How long the log files are kept: a year, as the Digital Personal Data Protection Rules 2025 ask from 13 May 2027
    /// (r.6), which covers the 180 days within India that CERT-In's Directions of 28 April 2022 ask for (direction (iv)).
    /// The file sink dates each day's file from its midnight, and deletes it once that midnight is older than this; the
    /// day's last events are a day younger. So this is a leap year's 366 days, and the day the file holds: every event is
    /// kept a full year. By age, not by count: a busy day rolls into several files at 100 MB each.
    /// </summary>
    public const int RetentionDays = 367;

    /// <param name="logs">The folder the rolling file is written in.</param>
    /// <param name="development">A developer's machine: the console shows debug messages too.</param>
    /// <param name="errorLog">Writes errors to the Event Log, given the formatter to write them with; null where there is none.</param>
    public static LoggerConfiguration WriteToHospitalSinks(
        this LoggerConfiguration logger, string logs, bool development, Action<LoggerSinkConfiguration, ITextFormatter>? errorLog)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.WriteTo.Console(
                new RedactingFormatter("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"),
                restrictedToMinimumLevel: development ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                new JsonLogFormatter(),
                Path.Combine(logs, FileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(RetentionDays),
                fileSizeLimitBytes: 100 * 1024 * 1024,
                rollOnFileSizeLimit: true);

        if (errorLog is not null)
        {
            var formatter = new RedactingFormatter("{Message:lj}{NewLine}{Exception}");
            logger.WriteTo.Logger(errors => errorLog(errors.Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Error).WriteTo, formatter));
        }

        return logger;
    }

    /// <summary>
    /// The Windows Event Log (Application, source <see cref="EventLogSource"/>, which the installer registers: the service
    /// cannot create it without being an administrator).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void EventLog(LoggerSinkConfiguration sink, ITextFormatter formatter) =>
        sink.EventLog(formatter, EventLogSource, manageEventSource: false);
}
