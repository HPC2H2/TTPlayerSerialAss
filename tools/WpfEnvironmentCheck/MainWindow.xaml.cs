using System.ComponentModel;
using System.Windows;

namespace WpfEnvironmentCheck;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private int clickCount;
    public string CountText => $"点击次数：{clickCount}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        clickCount++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
    }
}
