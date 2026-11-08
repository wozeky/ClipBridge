using System.Windows;

namespace ClipBridge.App;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs args)
    {
        if (args.Args is not ["--smoke-test" or "--capture-smoke", _])
        {
            _instance = new Mutex(initiallyOwned: false, @"Local\ClipBridge.Desktop", out bool created);
            if (!created)
            {
                Shutdown();
                return;
            }
        }
        base.OnStartup(args);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        _instance?.Dispose();
        base.OnExit(args);
    }
}
