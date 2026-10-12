using System.IO;
using System.Windows;
using System.Windows.Threading;
using HyFib.HealthCare360.Desktop.Services;
using Serilog;

namespace HyFib.HealthCare360.Desktop;

/// <summary>
/// Startup: one instance per Windows session (a second launch or an <c>hc360://</c> link is forwarded to the
/// running window), first-run setup when no server is configured, then the main window.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DesktopSettings.AppDataDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DesktopSettings.AppDataDirectory, "logs", "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("HC360 Desktop {Version} starting", DesktopSettings.AppVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _instance = SingleInstance.TryAcquire(e.Args);
        if (_instance is null)
        {
            Log.Information("Another instance is running; forwarded the launch arguments to it");
            Shutdown();
            return;
        }

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => _main?.Activate(args));

        var settings = DesktopSettings.Load();
        if (!settings.IsConfigured)
        {
            var setup = new SetupWindow(settings);
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        ShowMainWindow(settings, e.Args);
    }

    internal void ShowMainWindow(DesktopSettings settings, string[] args, string? notice = null)
    {
        _main = new MainWindow(settings, args, notice);
        _main.Closed += (_, _) =>
        {
            if (_main?.RestartRequested != true)
            {
                Shutdown();
            }
        };
        _main.Show();
    }

    /// <summary>"Change server": re-runs setup, then reopens the main window with the new configuration.</summary>
    internal void Reconfigure(MainWindow current)
    {
        var settings = DesktopSettings.Load();
        var setup = new SetupWindow(settings) { Owner = current };
        if (setup.ShowDialog() == true)
        {
            current.RestartRequested = true;
            current.Close();
            ShowMainWindow(DesktopSettings.Load(), []);
        }
    }

    /// <summary>
    /// Reopens the main window on the server's other address, where it answered when its usual one did not
    /// (<see cref="ServerFallback"/>, SRS DED-03). <paramref name="notice"/> says so from the tray.
    /// </summary>
    internal void SwitchServer(MainWindow current, string notice)
    {
        current.RestartRequested = true;
        current.Close();
        ShowMainWindow(DesktopSettings.Load(), [], notice);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        Log.Information("HC360 Desktop exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        MessageBox.Show(
            "Something went wrong in HC360 Desktop. Your work on the server is safe. If this keeps happening, contact IT and mention the time it occurred.",
            "HC360 Desktop",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
