using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfEnvironmentCheck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--verify")
        {
            Verify(Path.GetFullPath(e.Args[1]));
            return;
        }
        new MainWindow().Show();
    }

    private async void Verify(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            // Load compiled XAML, execute a C# handler, verify data binding and render WPF.
            var window = new MainWindow();
            // WPF propagates inherited DataContext through its Dispatcher queue.
            // Give initial bindings the same opportunity to run as in an interactive app.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.TestButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.CountLabel.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            if (window.CountLabel.Text != "点击次数：1")
                throw new InvalidOperationException($"WPF data binding did not update: '{window.CountLabel.Text}'.");

            var visual = window.RootPanel;
            visual.Measure(new Size(760, 420));
            visual.Arrange(new Rect(0, 0, 760, 420));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(760, 420, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(outputDirectory, "wpf-render.png")))
                encoder.Save(stream);
            File.WriteAllText(Path.Combine(outputDirectory, "verification.json"),
                JsonSerializer.Serialize(new
                {
                    result = "PASS",
                    framework = RuntimeInformation.FrameworkDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    compiledXaml = true,
                    buttonEvent = true,
                    dataBinding = window.CountLabel.Text,
                    render = "wpf-render.png",
                    pixelWidth = bitmap.PixelWidth,
                    pixelHeight = bitmap.PixelHeight,
                    checkedAt = DateTimeOffset.Now
                }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "verification-error.txt"), error.ToString());
            Shutdown(1);
        }
    }
}
