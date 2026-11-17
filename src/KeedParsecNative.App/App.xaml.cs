using System.IO;
using System.Windows;
using KeedParsecNative.App.ViewModels;
using KeedParsecNative.Infrastructure.Configuration;

namespace KeedParsecNative.App;

public partial class App : Application
{
    private SessionViewModel? _sessionViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Unhandled UI exception:\n{args.Exception}", "KeedParsecNative Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var basePath = AppContext.BaseDirectory;
            var environment = System.Diagnostics.Debugger.IsAttached ? "Development" : "Production";
            var config = AppConfigLoader.Load(basePath, environment);

            _sessionViewModel = new SessionViewModel(config);

            if (MainWindow is { } window)
            {
                window.DataContext = _sessionViewModel;
                window.Closed += async (_, _) =>
                {
                    if (_sessionViewModel is not null)
                    {
                        await _sessionViewModel.DisposeAsync();
                    }
                };
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed:\n{ex}", "KeedParsecNative Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
