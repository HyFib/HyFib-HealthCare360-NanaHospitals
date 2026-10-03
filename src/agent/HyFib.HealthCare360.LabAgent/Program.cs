using HyFib.HealthCare360.LabAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

// HC360 lab agent.
//   HC360LabAgent.exe enrol --server https://HC360-SRV:7360/ --code ABCD-EFGH --ca-fingerprint "AB12 …" [--name "…"] [--config <file>]
//   HC360LabAgent.exe [--config <file>]      runs the agent (as the HC360LabAgent Windows service, or in a console to try it)
const string ServiceName = "HC360LabAgent";
const string EventSource = "HyFib HC360 Lab Agent";

if (args.Length > 0 && args[0] is "enrol" or "enroll")
{
    using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    return await EnrolCommand.RunAsync(args[1..], Console.Out, Console.Error, stopping.Token);
}

var options = EnrolCommand.Parse(args);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(service => service.ServiceName = ServiceName);
builder.Services.Configure<EventLogSettings>(eventLog =>
{
    eventLog.SourceName = EventSource;
    eventLog.LogName = "Application";
});

// Only warnings and errors reach the Windows event log (the install script registers its source). No message content is
// ever logged: the agent logs ports, connections and byte counts.
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new AgentPaths(options.GetValueOrDefault("config") ?? AgentConfiguration.DefaultPath));
builder.Services.AddSingleton<ISerialLineFactory, SerialPortLineFactory>();
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;
