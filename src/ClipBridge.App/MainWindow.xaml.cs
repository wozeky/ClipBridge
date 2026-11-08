using System.IO;
using System.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs() is [_, "--smoke-test", var marker])
        {
            ContentRendered += (_, _) =>
            {
                File.WriteAllText(marker, "ClipBridge window rendered");
                Application.Current.Shutdown();
            };
        }
    }
}
