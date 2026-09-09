using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using SshDockerBackup.App.Services;
using SshDockerBackup.App.ViewModels;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Remote;
using SshDockerBackup.Core.Ssh;
using SshDockerBackup.Core.Synology;

namespace SshDockerBackup.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>Folder holding the rolling Serilog files. Surfaced in the Log tab.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SshDockerBackup", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLogging();
        HookGlobalExceptionHandlers();

        _services = BuildServices();

        var window = _services.GetRequiredService<MainWindow>();
        window.DataContext = _services.GetRequiredService<MainViewModel>();
        MainWindow = window;
        window.Show();

        // Version first: "which build is this?" is the opening question on every bug report,
        // and a published single-file binary carries no other clue.
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        Log.Information("SshDockerBackup {Version} started. Logs: {LogDirectory}", version, LogDirectory);
    }

    private static void ConfigureLogging()
    {
        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .WriteTo.Sink(new UiLogSink())
            .CreateLogger();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        // One SSH session for the whole app: connect once, reuse everywhere.
        services.AddSingleton<ISshSession, SshSession>();
        services.AddSingleton<IDockerService, DockerService>();
        services.AddSingleton<IRemoteFileSystem, RemoteFileSystem>();
        services.AddSingleton<ISynologyProjects, SynologyProjects>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestoreService, RestoreService>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<AppState>();

        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<ContainersViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<RestoreViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Anything that escapes lands in the log file rather than vanishing behind a crash dialog,
    /// which is the whole point of wiring Serilog up first.
    /// </summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled exception on the UI thread");

            try
            {
                Views.MessageDialog.Show(
                    "Something went wrong",
                    $"{args.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                    $"The details were written to:{Environment.NewLine}{LogDirectory}",
                    Views.MessageDialogKind.Error, isQuestion: false, MainWindow);
            }
            catch
            {
                // The themed dialog needs a working UI, which is exactly what may have just broken.
                MessageBox.Show(
                    $"{args.Exception.Message}\n\nDetails were written to:\n{LogDirectory}",
                    "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception on a background thread");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down");
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
