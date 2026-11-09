using System.Windows;

namespace RollClip.App;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs args)
    {
        if (args.Args is ["--capture-smoke", _])
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (args.Args is not ["--smoke-test" or "--capture-smoke" or "--material-smoke", _])
        {
            // Keep the pre-rename mutex so both versions cannot write the same archive.
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
