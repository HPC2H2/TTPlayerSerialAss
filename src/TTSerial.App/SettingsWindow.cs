using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using TTSerial.Core;
using TTSerial.IO;

namespace TTSerial.App;
public sealed class SettingsWindow : Window
{
    public SerialOptions Serial { get; private set; }
    public ParserOptions Protocol { get; private set; }
    public string TextEncoding { get; private set; }
    public SettingsWindow(UserSettings settings, bool connected)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "串口与数值协议设置"; Width = 570; Height = Math.Min(690, SystemParameters.WorkArea.Height); ResizeMode = ResizeMode.CanResize; MinWidth = 480; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Serial = settings.Serial; Protocol = settings.Protocol; TextEncoding = settings.Encoding;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var port = Combo([], Serial.Port, true); void Refresh() { port.ItemsSource = new[] { "演示设备" }.Concat(SerialPort.GetPortNames().Order()).ToArray(); port.Text = Serial.Port; }
        try { Refresh(); } catch { port.ItemsSource = new[] { "演示设备" }; }
        var portRow = new DockPanel(); var refresh = new Button { Content = "刷新", Margin = new(6, 0, 0, 0) }; refresh.Click += (_, _) => { try { Refresh(); } catch (Exception e) { MessageBox.Show(this, e.Message); } }; DockPanel.SetDock(refresh, Dock.Right); portRow.Children.Add(refresh); portRow.Children.Add(port); Row("串口", portRow);
        var baud = Combo(["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600", "1000000", "2000000"], Serial.Baud.ToString(), true); Row("波特率", baud);
        var data = Combo(["5", "6", "7", "8"], Serial.DataBits.ToString()); Row("数据位", data);
        var parity = Combo(Enum.GetNames<Parity>(), Serial.Parity.ToString()); Row("校验", parity);
        var stop = Combo(["One", "Two", "OnePointFive"], Serial.StopBits.ToString()); Row("停止位", stop);
        var flow = Combo(Enum.GetNames<Handshake>(), Serial.Handshake.ToString()); Row("流控", flow);
        var flags = new StackPanel { Orientation = Orientation.Horizontal }; var dtr = new CheckBox { Content = "DTR", IsChecked = Serial.Dtr }; var rts = new CheckBox { Content = "RTS", IsChecked = Serial.Rts }; dtr.SetResourceReference(ForegroundProperty, "TitleInk"); rts.SetResourceReference(ForegroundProperty, "TitleInk"); flags.Children.Add(dtr); flags.Children.Add(rts); Row("控制线", flags);
        var encoding = Combo(["UTF-8", "GB18030", "ASCII"], TextEncoding); Row("文本编码", encoding);
        var delimiter = Combo(["逗号 ,", "制表符 TAB", "分号 ;", "空白分隔"], Protocol.Delimiter switch { '\t' => "制表符 TAB", ';' => "分号 ;", ' ' => "空白分隔", _ => "逗号 ," }); Row("数值分隔符", delimiter);
        var time = Combo(["电脑到达时间", "固定采样率", "首列设备时间 / ms"], Protocol.TimeSource switch { TimeSource.FixedRate => "固定采样率", TimeSource.DeviceMilliseconds => "首列设备时间 / ms", _ => "电脑到达时间" }); Row("时间轴来源", time);
        var rate = new TextBox { Text = Protocol.SampleRate.ToString(CultureInfo.InvariantCulture) }; Row("采样率 / Hz", rate);
        var fields = new TextBox { Text = Protocol.Fields == null ? "" : string.Join(',', Protocol.Fields.Select(i => i + 1)), ToolTip = "留空：读取全部数值列；也可填 2,4,5（从 1 开始）。设备时间模式的第 1 列不能作为通道。" }; Row("通道列号", fields);
        panel.Children.Add(new TextBlock { Text = "每行 1–8 个数值，以 LF / CRLF 结束。\n设备时间模式的首列为毫秒；演示模式自动使用 1 kHz。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 10), FontSize = 12 });
        var apply = new Button { Content = connected ? "当前已连接，请先断开后修改" : "应用设置", IsEnabled = !connected };
        apply.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(port.Text) || !int.TryParse(baud.Text, out int b) || b <= 0 || b > 4000000) throw new FormatException("请输入有效端口及 1–4000000 波特率。");
                if (!double.TryParse(rate.Text, CultureInfo.InvariantCulture, out double hz) || !double.IsFinite(hz) || hz <= 0 || hz > 10000000) throw new FormatException("采样率必须大于 0 且不超过 10 MHz。");
                int[]? map = string.IsNullOrWhiteSpace(fields.Text) ? null : fields.Text.Split(',').Select(s => int.Parse(s.Trim()) - 1).ToArray();
                var source = (TimeSource)time.SelectedIndex;
                if (map?.Any(i => i < 0 || i > 63 || (source == TimeSource.DeviceMilliseconds && i == 0)) == true || map?.Length > 8 || map?.Distinct().Count() != map?.Length) throw new FormatException("通道列号应互不重复、从 1 开始，最多 8 列；第 1 列设备时间不能重复使用。");
                Serial = new(port.Text.Trim(), b, int.Parse(data.Text), Enum.Parse<Parity>(parity.Text), Enum.Parse<StopBits>(stop.Text), Enum.Parse<Handshake>(flow.Text), dtr.IsChecked == true, rts.IsChecked == true);
                Protocol = new(delimiter.SelectedIndex switch { 1 => '\t', 2 => ';', 3 => ' ', _ => ',' }, source, hz, map); TextEncoding = encoding.Text; DialogResult = true;
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "参数检查", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        panel.Children.Add(apply);
        void Row(string label, UIElement control) { var row = new Grid { Margin = new(0, 0, 0, 6) }; row.ColumnDefinitions.Add(new() { Width = new(118) }); row.ColumnDefinitions.Add(new()); row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); }
    }
    private static ComboBox Combo(string[] values, string selected, bool editable = false) { var combo = new ComboBox { ItemsSource = values, IsEditable = editable }; if (editable) combo.Text = selected; else combo.SelectedItem = selected; return combo; }
}
