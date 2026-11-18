using System.Windows;

namespace LegionGoConverter.App;

public partial class App : Application
{
    AppComposition? _composition;
    GamepadNavigator? _gamepad;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int code = StartupGuard.Run(
            () =>
            {
#if DEBUG
                if (DemoMode.Parse(e.Args) is { } demo)
                {
                    // Debug-only visual check: fake services, no system access. See DemoMode.cs.
                    _gamepad = demo.Run(this);
                    return;
                }
#endif
                ThemeManager.FollowSystem();
                _composition = new AppComposition(Dispatcher);
                var window = new MainWindow { DataContext = _composition.ViewModel };
                MainWindow = window;
                window.Show();
                _gamepad = _composition.Gamepad;
                _gamepad.Start();
            },
            msg => MessageBox.Show(msg, "Legion Go Driver Port", MessageBoxButton.OK, MessageBoxImage.Error),
            StartupGuard.WriteLogFile);
        if (code != 0) Shutdown(code);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _gamepad?.Stop(); } catch { }
        try { ThemeManager.StopFollowing(); } catch { }
        base.OnExit(e);
    }
}
