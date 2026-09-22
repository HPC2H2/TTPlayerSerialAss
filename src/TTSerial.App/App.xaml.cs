using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TTSerial.App;
public partial class App : Application
{
    public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTPlayerSerialAss");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += (_, args) => { File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), DateTime.Now + " " + args.Exception + "\n"); MessageBox.Show(args.Exception.Message, "千千串口助手", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.Contains("--smoke-test")) { ShutdownMode = ShutdownMode.OnExplicitShutdown; _ = window.SmokeTestAsync(e.Args); }
        else window.Show();
    }
}
