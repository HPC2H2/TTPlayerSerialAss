using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TTSerial.Core;
using TTSerial.IO;

namespace TTSerial.App;

public partial class MainWindow : Window
{
    private sealed record SkinChoice(string Name, string Path);
    private readonly bool smoke = Environment.GetCommandLineArgs().Contains("--smoke-test");
    private readonly UserSettings settings;
    private readonly Session session = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer periodic = new();
    private readonly Dictionary<ContentControl, Window> floating = new();
    private readonly ChannelSetting[] channels;
    private readonly List<SkinChoice> skins = [];
    private bool ready, busy, frozen, closing, closed, historyChanging, compact, sound;
    private double span = 1, viewEnd, normalWidth, normalHeight;
    private SampleFrame[] frozenSamples = [];
    private WirePacket[] frozenPackets = [];
    private byte[] scheduledPayload = [];
    private SerialOptions activeSerial = new();
    private ParserOptions activeProtocol = new();
    private SkinChoice? currentSkin;
    private Decoder rxDecoder = Encoding.UTF8.GetDecoder(), txDecoder = Encoding.UTF8.GetDecoder();
    private string? lastError, lastRecordingError;

    public MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        settings = smoke ? new() : UserSettings.Load();
        InitializeComponent();
        Brush[] colors = [Brushes.Turquoise, Brushes.Gold, Brushes.HotPink, Brushes.LightSkyBlue, Brushes.LawnGreen, Brushes.Orange, Brushes.Plum, Brushes.White];
        channels = Enumerable.Range(0, 8).Select(i =>
        {
            var saved = settings.Channels.ElementAtOrDefault(i) ?? new();
            return new ChannelSetting { Index = i, Color = colors[i], Enabled = saved.Enabled, Name = saved.Name, Scale = double.IsFinite(saved.Scale) ? saved.Scale : 1, Offset = double.IsFinite(saved.Offset) ? saved.Offset : 0, Unit = saved.Unit };
        }).ToArray();
        ChannelToggles.ItemsSource = channels; ChannelSelector.ItemsSource = channels; ChannelSelector.SelectedIndex = 0;
        ChannelEditor.DataContext = channels[0]; Wave.Channels = channels;
        PresetSelector.ItemsSource = settings.Presets; PresetSelector.SelectedIndex = 0;
        if (settings.Presets.Count > 0) { SendText.Text = settings.Presets[0].Text; HexSend.IsChecked = settings.Presets[0].Hex; }
        Width = Math.Clamp(double.IsFinite(settings.Width) ? settings.Width : 1140, 900, Math.Max(900, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(double.IsFinite(settings.Height) ? settings.Height : 780, 700, Math.Max(700, SystemParameters.WorkArea.Height));
        Player.Action += PlayerAction;
        Wave.Zoom += factor => { span = Math.Clamp(span * factor, .01, 120); Draw(); };
        Wave.Pan += delta => { Freeze(); viewEnd = Math.Clamp(viewEnd + delta, FirstTime(), Math.Max(FirstTime(), LastTime())); Draw(); };
        Wave.Live += Resume; Wave.CursorReadout += text => MeasureLabel.Text = text;
        SendText.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SendClick(this, new()); e.Handled = true; } };
        ChannelEditor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox box) { box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); e.Handled = true; } };
        refresh.Tick += (_, _) => Tick();
        periodic.Tick += (_, _) => { try { session.Connection.Send(scheduledPayload); } catch (Exception e) { PeriodicSend.IsChecked = false; Report(e); } };
        Closing += OnClosing;
        RootGrid.SizeChanged += (_, _) => Player.MaxHeight = compact ? 220 : Math.Clamp(RootGrid.ActualHeight - 520, 120, 220);
        LoadSkinList(); ResetDecoders(); ready = true; UpdateConnectionSummary(); refresh.Start();
    }

    private void LoadSkinList()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Skins");
        var paths = (Directory.Exists(directory) ? Directory.GetFiles(directory, "*.skn") : []).Concat(settings.ImportedSkins.Where(File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string fallback = Path.Combine(App.DataDirectory, "Default.skn");
        using (var embedded = typeof(App).Assembly.GetManifestResourceStream("TTSerial.Default.skn"))
        {
            if (embedded != null && !File.Exists(fallback)) { using var file = File.Create(fallback); embedded.CopyTo(file); }
        }
        if (paths.Count == 0 && File.Exists(fallback)) paths.Add(fallback);
        int Rank(string p) => Path.GetFileName(p).Contains("经典皮肤") ? 0 : p.Contains("iBlue", StringComparison.OrdinalIgnoreCase) ? 1 : p.Contains("HiFi", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        skins.Clear(); skins.AddRange(paths.OrderBy(Rank).ThenBy(Path.GetFileName).Select(p => new SkinChoice(Path.GetFileNameWithoutExtension(p), p)));
        SkinSelector.ItemsSource = skins;
        var selected = skins.FirstOrDefault(s => s.Path == settings.SkinPath) ?? skins.FirstOrDefault();
        if (selected != null)
        {
            try { ApplySkin(selected); }
            catch { if (File.Exists(fallback)) ApplySkin(new("经典皮肤", fallback)); }
            SkinSelector.SelectedItem = selected;
        }
    }
    private void ApplySkin(SkinChoice choice)
    {
        var visual = new SkinVisual(SkinReader.Load(choice.Path));
        // Resolve every main-window sprite before committing a theme change.
        foreach (var element in visual.Document.Player.Elements.Values) visual.State(element, 0);
        visual.ApplyTheme(); Player.Skin = visual; Player.InvalidateMeasure(); Player.InvalidateVisual(); Wave.InvalidateVisual();
        currentSkin = choice; settings.SkinPath = choice.Path;
        StatusText.Text = $"皮肤：{visual.Document.Name}  ·  {visual.Document.Author}";
    }
    private void SkinSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || SkinSelector.SelectedItem is not SkinChoice choice || choice == currentSkin) return;
        try { ApplySkin(choice); SaveSettings(); } catch (Exception error) { Report(error); SkinSelector.SelectedItem = currentSkin; }
    }
    private void ImportSkin(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "千千静听皮肤 (*.skn)|*.skn", Title = "导入原版皮肤" };
        if (dialog.ShowDialog(this) == true) Import(dialog.FileName);
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(f => f.EndsWith(".skn", StringComparison.OrdinalIgnoreCase)) is { } file) Import(file);
    }
    private void Import(string path)
    {
        try
        {
            _ = new SkinVisual(SkinReader.Load(path));
            string folder = Path.Combine(App.DataDirectory, "Skins"); Directory.CreateDirectory(folder);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..12];
            string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-" + hash + ".skn");
            if (!File.Exists(target)) File.Copy(path, target);
            if (!settings.ImportedSkins.Contains(target)) settings.ImportedSkins.Add(target);
            var choice = new SkinChoice(Path.GetFileNameWithoutExtension(path), target); ApplySkin(choice);
            if (!skins.Any(s => s.Path == target)) skins.Add(choice);
            SkinSelector.Items.Refresh(); SkinSelector.SelectedItem = skins.First(s => s.Path == target); SaveSettings();
        }
        catch (Exception error) { Report(error); }
    }

    private async Task ConnectAsync(bool demo = false)
    {
        if (busy) return;
        busy = true; PeriodicSend.IsChecked = false;
        try
        {
            activeSerial = demo ? new() : settings.Serial;
            activeProtocol = activeSerial.Port == "演示设备" ? new(TimeSource: TimeSource.FixedRate, SampleRate: 1000) : settings.Protocol;
            ReceiveLog.Clear(); ResetDecoders(); frozen = false; frozenSamples = []; frozenPackets = []; lastError = lastRecordingError = null;
            await session.OpenAsync(activeSerial, activeProtocol);
            UpdateConnectionSummary(); StatusText.Text = activeSerial.Port == "演示设备" ? "演示已启动：模拟 1 kHz 四通道，未连接实物串口。" : "串口已连接，等待接收。";
        }
        catch (Exception error) { Report(error); }
        finally { busy = false; Tick(); }
    }
    private async void MonitorClick(object sender, RoutedEventArgs e)
    {
        if (!session.Connection.Connected) await ConnectAsync(); else if (frozen) Resume(); else Freeze();
    }
    private async void StartDemo(object sender, RoutedEventArgs e) => await ConnectAsync(true);
    private async void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; PeriodicSend.IsChecked = false;
        try { await session.CloseAsync(); Freeze(); StatusText.Text = "连接已断开；接收历史仍可查看和导出。"; }
        catch (Exception error) { Report(error); }
        finally { busy = false; Tick(); }
    }
    private void Freeze()
    {
        if (frozen) return;
        frozenSamples = session.Buffer.Snapshot(); frozenPackets = session.WireSnapshot(); viewEnd = frozenSamples.LastOrDefault()?.Time ?? 0; frozen = true;
        StatusText.Text = "显示已冻结；后台仍在接收和录制。点击“恢复显示”返回实时。"; Draw();
    }
    private void Resume() { frozen = false; frozenSamples = []; frozenPackets = []; historyChanging = true; HistorySlider.Value = 100; historyChanging = false; RebuildLog(); Draw(); }
    private void GoLive(object sender, RoutedEventArgs e) => Resume();
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new SettingsWindow(settings, session.Connection.Connected) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings.Serial = dialog.Serial; settings.Protocol = dialog.Protocol; settings.Encoding = dialog.TextEncoding;
        ResetDecoders(); SaveSettings(); UpdateConnectionSummary(); StatusText.Text = "设置已保存，点击“开始监视”连接。";
    }
    private void UpdateConnectionSummary()
    {
        var serial = session.Connection.Connected ? activeSerial : settings.Serial;
        ConnectionSummary.Text = serial.Port == "演示设备" ? "演示设备 · 模拟 ADC · 4 通道 · 1 kHz" : $"{serial.Port}  ·  {serial.Baud} baud  ·  {serial.DataBits}/{serial.Parity}/{serial.StopBits}  ·  {serial.Handshake}";
        var protocol = session.Connection.Connected ? activeProtocol : settings.Protocol;
        TimeSourceLabel.Text = protocol.TimeSource switch { TimeSource.FixedRate => $"{protocol.SampleRate:G6} Hz", TimeSource.DeviceMilliseconds => "设备时间", _ => "到达时间" };
    }
    private void Tick()
    {
        if (!ready || closing) return;
        var text = new StringBuilder();
        while (session.DisplayPackets.TryDequeue(out var packet)) if (!frozen) text.Append(FormatPacket(packet));
        if (text.Length > 0) AppendLog(text.ToString());
        if (!session.Connection.Connected && PeriodicSend.IsChecked == true) PeriodicSend.IsChecked = false;
        if (session.Error != null && session.Error != lastError) { lastError = session.Error; StatusText.Text = "串口已停止：" + lastError; if (sound) System.Media.SystemSounds.Exclamation.Play(); }
        if (session.RecordingError != null && session.RecordingError != lastRecordingError) { lastRecordingError = session.RecordingError; StatusText.Text = "录制已停止：" + lastRecordingError; }
        CounterLabel.Text = $"RX {session.Rx:N0}  TX {session.Tx:N0}";
        CounterLabel.ToolTip = $"字节计数。为保证界面响应跳过显示的接收块：{session.DroppedDisplay}；原始日志保留最近 4 MiB。";
        SampleLabel.Text = $"{session.Buffer.Count:N0} 点 · 无效 {session.Rejected:N0}";
        SampleLabel.ToolTip = $"历史最多 120,000 点；已滚动移出 {session.Buffer.Evicted:N0} 点。CSV 导出当前历史；连续保存请使用录制。";
        MonitorButton.Content = session.Connection.Connected ? frozen ? "恢复显示" : "冻结显示" : "开始监视";
        MonitorButton.IsEnabled = DemoButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy && session.Connection.Connected;
        SendButton.IsEnabled = session.Connection.Connected && !busy;
        RecordButton.Content = session.Recording ? "停止录制 ●" : "开始录制";
        LiveLabel.Text = frozen ? "HOLD" : session.Connection.Connected ? activeSerial.Port == "演示设备" ? "DEMO" : "LIVE" : "OFF";
        Player.Playing = session.Connection.Connected && !frozen; Player.Status = LiveLabel.Text;
        Player.Info = session.Connection.Connected ? activeSerial.Port == "演示设备" ? "模拟 ADC · 4 通道 · 1 kHz" : $"{activeSerial.Port} · {activeSerial.Baud} baud" : "未连接 · 点击打开设置";
        Draw();
    }
    private double FirstTime() => frozen ? frozenSamples.FirstOrDefault()?.Time ?? 0 : session.Buffer.Range.First;
    private double LastTime() => frozen ? frozenSamples.LastOrDefault()?.Time ?? 0 : session.Buffer.Range.Last;
    private void Draw()
    {
        if (!ready) return;
        if (!frozen) viewEnd = session.Buffer.Range.Last;
        double end = Math.Max(span, viewEnd), start = end - span;
        Wave.Samples = frozen ? frozenSamples.Where(s => s.Time >= start && s.Time <= end).ToArray() : session.Buffer.Snapshot(start, end);
        Wave.Start = start; Wave.End = end; Wave.AutoY = AutoYCheck.IsChecked == true;
        TimeRange.Text = span.ToString("0.###", CultureInfo.InvariantCulture) + " s";
        if (double.TryParse(YMinimum.Text, CultureInfo.InvariantCulture, out var min) && double.TryParse(YMaximum.Text, CultureInfo.InvariantCulture, out var max) && double.IsFinite(min) && double.IsFinite(max) && min < max) { Wave.YMin = min; Wave.YMax = max; YMinimum.ToolTip = "Y 轴最小值"; }
        else YMinimum.ToolTip = "量程无效，仍使用上次有效范围；最小值必须小于最大值。";
        Wave.InvalidateVisual();
        Player.Samples = Wave.Samples.Length > 600 ? Wave.Samples.Where((_, i) => i % Math.Max(1, Wave.Samples.Length / 500) == 0).ToArray() : Wave.Samples;
        Player.Channels = Wave.Samples.LastOrDefault()?.Values.Length ?? 0;
        Player.Time = TimeSpan.FromSeconds(Math.Max(0, viewEnd)).ToString(viewEnd >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
        Player.Progress = LastTime() <= FirstTime() ? 1 : Math.Clamp((viewEnd - FirstTime()) / (LastTime() - FirstTime()), 0, 1);
        Player.Volume = Math.Clamp(Math.Log(span / .01) / Math.Log(120 / .01), 0, 1); Player.InvalidateVisual();
        historyChanging = true; HistorySlider.Value = Player.Progress * 100; historyChanging = false;
    }
    private void TimeRangeChanged(object sender, SelectionChangedEventArgs e) { if (ready && TimeRange.SelectedItem is ComboBoxItem item) { span = double.Parse((string)item.Tag, CultureInfo.InvariantCulture); Draw(); } }
    private void HistoryChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || historyChanging) return;
        Freeze(); viewEnd = FirstTime() + (LastTime() - FirstTime()) * e.NewValue / 100; Draw();
    }
    private void ChannelSelected(object sender, SelectionChangedEventArgs e) { if (ready) ChannelEditor.DataContext = ChannelSelector.SelectedItem; }

    private Encoding TextEncoding() { try { return Encoding.GetEncoding(settings.Encoding); } catch { return Encoding.UTF8; } }
    private void ResetDecoders() { rxDecoder = TextEncoding().GetDecoder(); txDecoder = TextEncoding().GetDecoder(); }
    private string FormatPacket(WirePacket packet)
    {
        string prefix = (TimeStampCheck.IsChecked == true ? packet.Timestamp.ToString("HH:mm:ss.fff ") : "") + (packet.Transmit ? "TX > " : "RX < ");
        if (HexReceive.IsChecked == true) return prefix + Convert.ToHexString(packet.Data).Chunk(2).Select(c => new string(c)).Aggregate(new StringBuilder(), (b, s) => b.Append(s).Append(' ')).ToString() + "\r\n";
        var decoder = packet.Transmit ? txDecoder : rxDecoder;
        var chars = new char[TextEncoding().GetMaxCharCount(packet.Data.Length)]; int count = decoder.GetChars(packet.Data, chars, false);
        string body = new(chars, 0, count);
        return prefix + body.Replace("\0", "␀") + (body.EndsWith('\n') ? "" : "\r\n");
    }
    private void AppendLog(string text)
    {
        if (text.Length > 100000) text = text[^100000..];
        if (ReceiveLog.Text.Length + text.Length > 160000) ReceiveLog.Text = ReceiveLog.Text[^Math.Min(60000, ReceiveLog.Text.Length)..];
        ReceiveLog.AppendText(text); ReceiveLog.ScrollToEnd();
    }
    private void RebuildLog()
    {
        ReceiveLog.Clear(); ResetDecoders(); var all = frozen ? frozenPackets : session.WireSnapshot();
        // Limit formatting work as well as the TextBox, even if the wire ring is full.
        int first = all.Length, bytes = 0; while (first > 0 && bytes < 24000) bytes += all[--first].Data.Length;
        var text = new StringBuilder(); foreach (var packet in all.Skip(first)) text.Append(FormatPacket(packet)); AppendLog(text.ToString());
    }
    private void RefreshLog(object sender, RoutedEventArgs e) { if (ready) RebuildLog(); }
    private void ClearLog(object sender, RoutedEventArgs e) { ReceiveLog.Clear(); StatusText.Text = "显示已清空，原始日志和波形历史继续保留。"; }
    private byte[] CurrentPayload() => SendCodec.Encode(SendText.Text, HexSend.IsChecked == true, NewlineSelector.SelectedIndex switch { 1 => "\r\n", 2 => "\n", 3 => "\r", _ => "" }, TextEncoding());
    private void SendClick(object sender, RoutedEventArgs e)
    {
        try { session.Connection.Send(CurrentPayload()); StatusText.Text = "已加入发送队列；TX 计数在实际写入后更新。"; } catch (Exception error) { Report(error); }
    }
    private void PeriodicChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        periodic.Stop(); if (PeriodicSend.IsChecked != true) return;
        try
        {
            if (!session.Connection.Connected) throw new InvalidOperationException("请先连接串口。");
            if (!int.TryParse(IntervalText.Text, out var ms) || ms < 20 || ms > 86400000) throw new FormatException("周期应为 20–86400000 ms。");
            scheduledPayload = CurrentPayload(); if (scheduledPayload.Length is < 1 or > 65536) throw new FormatException("单次发送长度应为 1–65536 字节。");
            periodic.Interval = TimeSpan.FromMilliseconds(ms); periodic.Start(); StatusText.Text = $"每 {ms} ms 发送启用时的内容；修改内容后请重新启用周期发送。";
        }
        catch (Exception error) { PeriodicSend.IsChecked = false; Report(error); }
    }
    private void PresetSelected(object sender, SelectionChangedEventArgs e) { if (ready && PresetSelector.SelectedItem is CommandPreset preset) { SendText.Text = preset.Text; HexSend.IsChecked = preset.Hex; } }
    private void SavePreset(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "保存发送预设", Width = 380, Height = 165, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(18) }; var input = new TextBox { Text = "新预设", Margin = new(0, 0, 0, 10) }; var button = new Button { Content = "保存" };
        panel.Children.Add(input); panel.Children.Add(button); dialog.Content = panel;
        button.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true) return;
        var preset = new CommandPreset(input.Text.Trim(), SendText.Text, HexSend.IsChecked == true);
        int old = settings.Presets.FindIndex(p => p.Name == preset.Name); if (old >= 0) settings.Presets[old] = preset; else settings.Presets.Add(preset);
        PresetSelector.Items.Refresh(); PresetSelector.SelectedItem = preset; SaveSettings();
    }
    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (session.Recording) { await session.StopRecordingAsync(); if (session.RecordingError != null) throw new IOException(session.RecordingError); StatusText.Text = "录制已停止，数据已写入磁盘。"; return; }
            await session.StopRecordingAsync();
            if (!session.Connection.Connected) throw new InvalidOperationException("请先连接设备或启动演示。");
            var dialog = new OpenFolderDialog { Title = "选择录制目录（自动创建独立会话子目录）" };
            if (dialog.ShowDialog(this) != true) return;
            string folder = Path.Combine(dialog.FolderName, "TTSerial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            BeginRecording(folder); StatusText.Text = "正在录制原始数据和数值 CSV：" + folder;
        }
        catch (Exception error) { Report(error); }
    }
    private void BeginRecording(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new { created = DateTimeOffset.Now, serial = activeSerial, protocol = activeProtocol, encoding = settings.Encoding, skin = currentSkin?.Name, rawCsv = true, channels = channels.Select(c => new { c.Label, c.Name, c.Enabled, c.Scale, c.Offset, c.Unit }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        session.StartRecording(folder);
    }
    private async void SaveWireLog(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "原始串口日志 (*.jsonl)|*.jsonl", FileName = "serial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var packets = session.WireSnapshot(); await Task.Run(() => File.WriteAllLines(dialog.FileName, packets.Select(p => JsonSerializer.Serialize(new { timestamp = p.Timestamp, direction = p.Transmit ? "TX" : "RX", hex = Convert.ToHexString(p.Data) })), new UTF8Encoding(false)));
            StatusText.Text = $"已保存最近 {packets.Sum(p => p.Data.Length):N0} 字节（日志缓存上限 4 MiB）。";
        }
        catch (Exception error) { Report(error); }
    }
    private async void ExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "数值数据 (*.csv)|*.csv", FileName = "samples-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var samples = frozen ? frozenSamples : session.Buffer.Snapshot(); await Task.Run(() => WriteCsv(dialog.FileName, samples)); StatusText.Text = $"已导出 {samples.Length:N0} 个原始采样点；换算比例不修改 CSV。";
        }
        catch (Exception error) { Report(error); }
    }
    private static void WriteCsv(string file, SampleFrame[] samples) => File.WriteAllLines(file, new[] { "time_s,ch1,ch2,ch3,ch4,ch5,ch6,ch7,ch8,gap" }.Concat(samples.Select(SessionRecorder.Csv)), new UTF8Encoding(true));
    private void SavePlot(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "波形图 (*.png)|*.png", FileName = "scope-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) == true) try { Render(Wave, dialog.FileName); StatusText.Text = "当前波形图已保存。"; } catch (Exception error) { Report(error); }
    }
    private static void Render(FrameworkElement element, string file)
    {
        element.UpdateLayout(); var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            drawing.DrawRectangle(element.TryFindResource("Workspace") as Brush ?? Brushes.White, null, rect);
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, rect);
        }
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(file); encoder.Save(stream);
    }

    private void Toggle(ContentControl host) { if (floating.TryGetValue(host, out var window)) { window.Activate(); return; } host.Visibility = host.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
    private void Float(ContentControl host, string title, double width, double height)
    {
        if (floating.TryGetValue(host, out var existing)) { existing.Activate(); return; }
        if (host.Content is not FrameworkElement panel) return;
        host.Content = null; host.Visibility = Visibility.Collapsed;
        var window = new Window { Title = title + " · 千千串口助手", Owner = this, Width = width, Height = height, MinWidth = 430, MinHeight = 260, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        floating[host] = window;
        window.Closed += (_, _) => { window.Content = null; host.Content = panel; host.Visibility = Visibility.Visible; floating.Remove(host); };
        window.Show();
    }
    private void FloatTerminal(object sender, RoutedEventArgs e) => Float(TerminalHost, "串口收发", 590, 530);
    private void FloatScope(object sender, RoutedEventArgs e) => Float(ScopeHost, "数值示波器", 820, 530);
    private void FloatChannels(object sender, RoutedEventArgs e) => Float(ChannelsHost, "通道控制", 620, 290);
    private void WindowsMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("显示 / 隐藏收发窗口", () => Toggle(TerminalHost)); Item("显示 / 隐藏波形窗口", () => Toggle(ScopeHost)); Item("显示 / 隐藏通道控制", () => Toggle(ChannelsHost));
        Item("恢复全部窗口", () => { foreach (var window in floating.Values.ToArray()) window.Close(); TerminalHost.Visibility = ScopeHost.Visibility = ChannelsHost.Visibility = Visibility.Visible; if (compact) Compact(); });
        Item("迷你模式 / 完整模式", Compact); Item("使用说明", Help);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void Compact()
    {
        compact = !compact;
        if (compact) { normalWidth = Width; normalHeight = Height; MinWidth = 640; MinHeight = 330; RightGrid.Visibility = TerminalHost.Visibility = Visibility.Collapsed; WorkspaceGrid.ColumnDefinitions[2].MinWidth = 0; WorkspaceGrid.ColumnDefinitions[2].Width = new(0); WorkspaceGrid.ColumnDefinitions[1].Width = new(0); Width = 680; Height = 460; Player.MaxWidth = 430; }
        else { MinWidth = 900; MinHeight = 700; Width = normalWidth; Height = normalHeight; RightGrid.Visibility = TerminalHost.Visibility = Visibility.Visible; WorkspaceGrid.ColumnDefinitions[2].MinWidth = 460; WorkspaceGrid.ColumnDefinitions[2].Width = new(1.2, GridUnitType.Star); WorkspaceGrid.ColumnDefinitions[1].Width = new(14); Player.MaxWidth = double.PositiveInfinity; }
    }
    private void PlayerAction(string name, double value)
    {
        switch (name)
        {
            case "play": MonitorClick(this, new()); break;
            case "stop": DisconnectClick(this, new()); break;
            case "open": case "set": OpenSettings(this, new()); break;
            case "prev": case "next": if (settings.Presets.Count > 0) PresetSelector.SelectedIndex = (PresetSelector.SelectedIndex + (name == "next" ? 1 : -1) + settings.Presets.Count) % settings.Presets.Count; break;
            case "playlist": Toggle(TerminalHost); break;
            case "lyric": Toggle(ScopeHost); break;
            case "equalizer": Toggle(ChannelsHost); break;
            case "minimize": WindowState = WindowState.Minimized; break;
            case "exit": Close(); break;
            case "minimode": Compact(); break;
            case "mute": sound = !sound; StatusText.Text = sound ? "错误提示音已启用。" : "错误提示音已关闭。"; break;
            case "progress": Freeze(); viewEnd = FirstTime() + (LastTime() - FirstTime()) * value; Draw(); break;
            case "volume": span = .01 * Math.Pow(120 / .01, value); Draw(); break;
            case "browser": Help(); break;
        }
    }
    private void Help() => MessageBox.Show(this, "1. 点击“串口设置”，选择 COM 口、波特率及数值格式，再点“开始监视”。\n2. 单片机每行发送 1–8 个逗号分隔数值，以换行结束，例如 1.23,2.34\\r\\n。\n3. 无设备时点击“演示”。冻结仅暂停显示，接收与录制继续。\n4. 波形滚轮缩放、拖动平移、双击回实时。设备时间模式以首列毫秒作为时间。\n5. 播放=监视/冻结，停止=断开，上一/下一=选择预设，打开=串口设置，PL/LRC/EQ=收发/波形/通道。\n6. 录制保存完整会话；CSV 导出当前最多 12 万点原始数值。\n\n预设命令只是示例，需匹配你的固件协议。", "千千串口助手 · 使用说明");
    private void Report(Exception error) { StatusText.Text = error.Message; if (smoke) throw new InvalidOperationException("Smoke test UI error", error); MessageBox.Show(this, error.Message, "千千串口助手", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void SaveSettings()
    {
        if (smoke) return;
        settings.Channels = channels.Select(c => new SavedChannel(c.Enabled, c.Name, c.Scale, c.Offset, c.Unit)).ToArray();
        if (!compact && WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; }
        try { settings.Save(); } catch (Exception e) { StatusText.Text = "设置未保存：" + e.Message; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true; if (closing) return; closing = true; refresh.Stop(); periodic.Stop(); SaveSettings();
        try { await session.DisposeAsync(); } finally { foreach (var window in floating.Values.ToArray()) window.Close(); closed = true; Close(); }
    }

    public async Task SmokeTestAsync(string[] args)
    {
        string output = args.SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(output);
        try
        {
            await ConnectAsync(true); await Task.Delay(1100); Tick();
            if (session.Buffer.Count < 500 || Wave.Samples.Length < 100) throw new Exception("演示数据未到达波形。");
            BeginRecording(Path.Combine(output, "recording"));
            session.Connection.Send(Encoding.UTF8.GetBytes("TEST\r\n"));
            Freeze(); int before = session.Buffer.Count; double held = viewEnd; await Task.Delay(200); Tick();
            if (session.Buffer.Count <= before || viewEnd != held || !session.Connection.Connected) throw new Exception("冻结影响了后台采集。");
            await session.StopRecordingAsync(); Resume(); Tick();
            if (session.Tx != 6 || !session.WireSnapshot().Any(p => !p.Transmit && Encoding.UTF8.GetString(p.Data).Contains("ECHO TEST"))) throw new Exception("演示发送 / 回显失败。");
            long beforePreset = session.Tx; PlayerAction("next", 0); await Task.Delay(50);
            if (PresetSelector.SelectedIndex != 1 || session.Tx != beforePreset || SendText.Text != "READ_ADC") throw new Exception("预设切换错误或发生自动发送。");
            SendText.Text = "P"; NewlineSelector.SelectedIndex = 0; IntervalText.Text = "30"; PeriodicSend.IsChecked = true;
            await Task.Delay(160); PeriodicSend.IsChecked = false; await Task.Delay(80);
            if (session.Tx <= beforePreset + 1 || periodic.IsEnabled) throw new Exception("周期发送未启动或未停止。");
            SendText.Text = "GET_STATUS"; NewlineSelector.SelectedIndex = 1; PresetSelector.SelectedIndex = 0; IntervalText.Text = "1000";
            // Exercise bitmap decoding and sprite crops for every skin, beyond XML parsing.
            int decoded = 0;
            foreach (var choice in skins)
            {
                var visual = new SkinVisual(SkinReader.Load(choice.Path));
                foreach (var element in visual.Document.Player.Elements.Values) for (int state = 0; state < 3; state++) visual.State(element, state);
                decoded++;
            }
            Width = 1140; Height = 830;
            RootGrid.Width = 1108; RootGrid.Height = 740; RootGrid.Margin = new(0);
            foreach (var choice in skins.Where(s => s.Path.Contains("经典皮肤") || s.Path.Contains("iBlue") || s.Path.Contains("HiFi")))
            {
                ApplySkin(choice); RootGrid.Measure(new(1108, 740)); RootGrid.Arrange(new Rect(0, 0, 1108, 740)); RootGrid.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Tick();
                if (RootGrid.ActualHeight > 800 || Wave.ActualHeight > 600 || ReceiveLog.ActualHeight > 400) throw new Exception("窗口布局未受到视口约束。");
                Render(RootGrid, Path.Combine(output, choice.Path.Contains("iBlue") ? "iblue.png" : choice.Path.Contains("HiFi") ? "hifi.png" : "classic.png"));
            }
            ApplySkin(skins.First(s => s.Path.Contains("经典皮肤")));
            RootGrid.Width = 868; RootGrid.Height = 642; RootGrid.Measure(new(868, 642)); RootGrid.Arrange(new Rect(0, 0, 868, 642));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RootGrid.UpdateLayout();
            if (IntervalText.TranslatePoint(new Point(0, IntervalText.ActualHeight), RootGrid).Y > RootGrid.ActualHeight - 15) throw new Exception("小窗口发送区域被裁切。");
            Render(RootGrid, Path.Combine(output, "compact-layout.png"));
            RootGrid.Width = 1108; RootGrid.Height = 740; RootGrid.Measure(new(1108, 740)); RootGrid.Arrange(new Rect(0, 0, 1108, 740)); RootGrid.UpdateLayout();
            var settingsWindow = new SettingsWindow(settings, false);
            if (settingsWindow.Content is FrameworkElement form) { form.Measure(new(570, 660)); form.Arrange(new Rect(0, 0, 570, 660)); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Render(form, Path.Combine(output, "settings.png")); }
            WriteCsv(Path.Combine(output, "export.csv"), session.Buffer.Snapshot()); Render(Wave, Path.Combine(output, "scope.png"));
            await session.CloseAsync(); if (session.Connection.Connected) throw new Exception("关闭连接失败。");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), runtime = Environment.Version.ToString(), points = session.Buffer.Count, rx = session.Rx, tx = session.Tx, availablePorts = SerialPort.GetPortNames(), skins = skins.Count, decodedSkins = decoded, checks = new[] { "demo receive", "freeze keeps acquiring", "TX echo", "record flush", "CSV export", "PNG render", "three skin layouts", "preset selects without TX", "periodic start/stop", "all skin sprites decode", "disconnect" } }, new JsonSerializerOptions { WriteIndented = true }));
            refresh.Stop(); Application.Current.Shutdown(0);
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), e.ToString()); await session.DisposeAsync(); Application.Current.Shutdown(1); }
    }
}
