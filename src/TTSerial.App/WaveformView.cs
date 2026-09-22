using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TTSerial.Core;

namespace TTSerial.App;

public sealed class ChannelSetting : System.ComponentModel.INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Label => "CH" + (Index + 1);
    public Brush Color { get; init; } = Brushes.Turquoise;
    private bool enabled = true;
    private double scale = 1, offset;
    private string name = "", unit = "V";
    public bool Enabled { get => enabled; set { enabled = value; Changed(nameof(Enabled)); } }
    public double Scale { get => scale; set { if (!double.IsFinite(value)) throw new ArgumentException("比例必须为有限数值。"); scale = value; Changed(nameof(Scale)); } }
    public double Offset { get => offset; set { if (!double.IsFinite(value)) throw new ArgumentException("偏移必须为有限数值。"); offset = value; Changed(nameof(Offset)); } }
    public string Name { get => name; set { name = value; Changed(nameof(Name)); } }
    public string Unit { get => unit; set { unit = value; Changed(nameof(Unit)); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

public sealed class WaveformView : FrameworkElement
{
    public SampleFrame[] Samples { get; set; } = [];
    public ChannelSetting[] Channels { get; set; } = [];
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public bool AutoY { get; set; } = true;
    public double YMin { get; set; } = 0;
    public double YMax { get; set; } = 3.3;
    public event Action<double>? Zoom;
    public event Action<double>? Pan;
    public event Action? Live;
    public event Action<string>? CursorReadout;
    private Point? cursor, drag;
    private Rect plot;
    private double visibleMin, visibleMax;
    public WaveformView()
    {
        MinHeight = 180; ClipToBounds = true; Focusable = true;
        MouseWheel += (_, e) => { Zoom?.Invoke(e.Delta > 0 ? .8 : 1.25); e.Handled = true; };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Live?.Invoke(); return; } drag = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { drag = null; ReleaseMouseCapture(); };
        MouseMove += (_, e) => { var p = e.GetPosition(this); cursor = p; if (drag is { } old && e.LeftButton == MouseButtonState.Pressed) { Pan?.Invoke(-(p.X - old.X) / Math.Max(1, plot.Width) * (End - Start)); drag = p; } InvalidateVisual(); };
        MouseLeave += (_, _) => { if (drag == null) cursor = null; InvalidateVisual(); };
        ToolTip = "滚轮缩放时间轴 · 拖动查看历史 · 双击返回实时 · 鼠标指向曲线测量";
    }
    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight; if (width < 100 || height < 100) return;
        var ink = Resource("Ink", Brushes.White); var muted = Resource("Muted", Brushes.LightGray); var gridBrush = Resource("Edge", Brushes.SlateGray);
        dc.DrawRectangle(Resource("Screen", Brushes.Black), null, new(0, 0, width, height));
        plot = new(57, 23, Math.Max(1, width - 73), Math.Max(1, height - 64));
        double min = YMin, max = YMax;
        if (AutoY)
        {
            min = double.PositiveInfinity; max = double.NegativeInfinity;
            foreach (var s in Samples) if (s.Time >= Start && s.Time <= End) for (int i = 0; i < Math.Min(s.Values.Length, Channels.Length); i++) if (Channels[i].Enabled)
            { double v = s.Values[i] * Channels[i].Scale + Channels[i].Offset; if (double.IsFinite(v)) { min = Math.Min(min, v); max = Math.Max(max, v); } }
            if (!double.IsFinite(min)) { min = 0; max = 3.3; }
            double padding = Math.Max(.01, (max - min) * .1); min -= padding; max += padding;
        }
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max) { min = 0; max = 3.3; }
        visibleMin = min; visibleMax = max;
        var units = Channels.Where(c => c.Enabled).Select(c => c.Unit).Distinct().ToArray();
        Text(dc, units.Length == 1 ? units[0] : "工程量 / 多单位", new(plot.Left, 0), ink, 11);
        var gridPen = new Pen(gridBrush, .5) { DashStyle = new DashStyle([2, 4], 0) };
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4; dc.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            Text(dc, (max - (max - min) * i / 4).ToString("G4", CultureInfo.InvariantCulture), new(0, y - 7), muted, 11, 52);
            double x = plot.Left + plot.Width * i / 4; dc.DrawLine(gridPen, new(x, plot.Top), new(x, plot.Bottom));
            Text(dc, (Start + (End - Start) * i / 4).ToString("0.###", CultureInfo.InvariantCulture), new(x - (i == 0 ? 0 : i == 4 ? 39 : 20), plot.Bottom + 6), muted, 11, 40);
        }
        Text(dc, "时间 / s", new(Math.Max(plot.Left, plot.Right - 52), height - 15), muted, 11);
        dc.PushClip(new RectangleGeometry(plot));
        for (int channel = 0; channel < Channels.Length; channel++)
        {
            var setting = Channels[channel]; if (!setting.Enabled) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                bool started = false; int bucket = int.MinValue; double bucketMin = 0, bucketMax = 0, lastY = 0;
                void Flush()
                {
                    if (bucket == int.MinValue) return;
                    double x = plot.Left + bucket;
                    if (!started) { g.BeginFigure(new(x, bucketMin), false, false); started = true; }
                    else g.LineTo(new(x, bucketMin), true, false);
                    g.LineTo(new(x, bucketMax), true, false); g.LineTo(new(x, lastY), true, false);
                }
                foreach (var sample in Samples)
                {
                    if (channel >= sample.Values.Length || sample.Time < Start || sample.Time > End) continue;
                    double value = sample.Values[channel] * setting.Scale + setting.Offset;
                    if (!double.IsFinite(value)) { Flush(); started = false; bucket = int.MinValue; continue; }
                    if (sample.Gap) { Flush(); started = false; bucket = int.MinValue; }
                    int x = (int)((sample.Time - Start) / Math.Max(1e-9, End - Start) * plot.Width);
                    double y = Math.Clamp(plot.Bottom - (value - min) / (max - min) * plot.Height, -10000, 10000);
                    if (x != bucket) { Flush(); bucket = x; bucketMin = bucketMax = y; } else { bucketMin = Math.Min(bucketMin, y); bucketMax = Math.Max(bucketMax, y); }
                    lastY = y;
                }
                Flush();
            }
            var pen = new Pen(setting.Color, 1.5); if (channel % 2 == 1) pen.DashStyle = new DashStyle([4, 2], 0);
            dc.DrawGeometry(null, pen, geometry);
        }
        if (cursor is { } p && plot.Contains(p))
        {
            dc.DrawLine(new Pen(muted, .7), new(p.X, plot.Top), new(p.X, plot.Bottom));
            double t = Start + (p.X - plot.Left) / plot.Width * (End - Start);
            var nearest = Samples.MinBy(s => Math.Abs(s.Time - t));
            if (nearest != null)
            {
                string values = string.Join("  ", Channels.Where(c => c.Enabled && c.Index < nearest.Values.Length).Take(4).Select(c => $"{(string.IsNullOrWhiteSpace(c.Name) ? c.Label : c.Name)} {(nearest.Values[c.Index] * c.Scale + c.Offset):G5} {c.Unit}"));
                CursorReadout?.Invoke($"t = {nearest.Time:0.000000} s   {values}");
            }
        }
        dc.Pop();
        if (Samples.Length == 0) Text(dc, "连接设备或启动演示，等待数值行…", new(plot.Left + 15, plot.Top + plot.Height / 2), muted, 13, Math.Max(1, plot.Width - 30));
    }
    private void Text(DrawingContext dc, string value, Point point, Brush brush, double size, double maxWidth = 200)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, point);
    }
}
