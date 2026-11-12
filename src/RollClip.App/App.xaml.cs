using System.Windows;

namespace RollClip.App;

public partial class App : Application
{
    private const string ShowEventName = @"Local\ClipBridge.Desktop.Show";
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

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
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowEventName);
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, timedOut) =>
            {
                if (!timedOut)
                    Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.BringToFront());
            }, null, -1, executeOnlyOnce: false);
            UiSounds.Register();
        }
        base.OnStartup(args);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        UiSounds.Dispose();
        _instance?.Dispose();
        base.OnExit(args);
    }
}
