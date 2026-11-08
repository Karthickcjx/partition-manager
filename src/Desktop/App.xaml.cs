using System.Windows;
using System.Windows.Threading;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Operations;
using PartitionManager.Desktop.Services;
using PartitionManager.Desktop.ViewModels;
using PartitionManager.Infrastructure.Logging;
using PartitionManager.Core.Execution;
using PartitionManager.Infrastructure.Ipc;
using PartitionManager.Infrastructure.WindowsApi;
using System.IO;

namespace PartitionManager.Desktop;

public partial class App : Application
{
    private FileOperationLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log = new FileOperationLog();
        DispatcherUnhandledException += OnUnhandled;

        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        var theme = args.Contains("--dark") ? AppTheme.Dark : args.Contains("--light") ? AppTheme.Light : ThemeManager.SystemTheme();
        ThemeManager.Apply(theme);

        _log.Info("app", $"start; elevated={Elevation.IsElevated}; args={string.Join(' ', e.Args)}");

        IDiskDiscoveryService discovery = new WmiDiskDiscoveryService(_log, new WmiBitLockerProbe());
        var log = _log;
        var helperPath = Path.Combine(AppContext.BaseDirectory, "PartitionManager.Helper.exe");
        IHelperPolicySource policy = new RegistryHelperPolicySource();
        var router = new ExecutionRouter(
            () => new HelperExecutor(ct => HelperClient.LaunchAsync(helperPath, ct), log),
            policy,
            helperPath);
        log.Info("app", $"write mode {policy.Load().Mode}; helper {(File.Exists(helperPath) ? "present" : "absent")}");

        var vm = new MainViewModel(discovery, router, new DialogService(), _log, _log.Directory, demoMode: false,
            () => new HelperRecoveryClient(ct => HelperClient.LaunchAsync(helperPath, ct)));
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        _ = vm.InitializeAsync();

        var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArg is not null && int.TryParse(pageArg["--page=".Length..], out var page) && page >= 0 && page < vm.Navigation.Count)
            vm.Current = vm.Navigation[page];
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("app", "exit");
        base.OnExit(e);
    }

    private readonly HashSet<string> _reportedErrors = [];

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("app", e.Exception.ToString());
        e.Handled = true;
        // Layout-time exceptions recur on every pass; tell the user once per distinct error.
        if (_reportedErrors.Add(e.Exception.Message))
            MessageBox.Show(e.Exception.Message + "\n\nDetails were written to the log.", "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
