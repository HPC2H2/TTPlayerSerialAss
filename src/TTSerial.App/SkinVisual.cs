using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TTSerial.Core;

namespace TTSerial.App;

public sealed class SkinVisual
{
    private readonly Dictionary<string, BitmapSource> cache = new(StringComparer.OrdinalIgnoreCase);
    public SkinDocument Document { get; }
    public BitmapSource Background { get; }
    public SkinVisual(SkinDocument document) { Document = document; Background = Image(document.Player.Definition.Get("image")) ?? throw new InvalidDataException("背景图片无法读取。"); }
    public BitmapSource? Image(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (cache.TryGetValue(name, out var cached)) return cached;
        if (Document.Asset(name) is not { } data) return null;
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) throw new InvalidDataException("皮肤图片尺寸过大。");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
            var transparent = ColorOf(Document.TransparentColor, Colors.Magenta);
            for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] == transparent.B && pixels[i + 1] == transparent.G && pixels[i + 2] == transparent.R) pixels[i + 3] = 0;
            var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); bitmap.Freeze(); cache[name] = bitmap; return bitmap;
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or ArgumentException) { return null; }
    }
    public BitmapSource? State(SkinElement e, int state)
    {
        var image = Image(e.Get("image")); if (image == null) return null;
        int expectedW = (int)e.Rect.Width, expectedH = (int)e.Rect.Height;
        int frames = expectedW > 0 && image.PixelWidth % expectedW == 0 && image.PixelWidth / expectedW is >= 2 and <= 4 ? image.PixelWidth / expectedW : 1;
        bool vertical = false;
        if (frames == 1 && expectedH > 0 && image.PixelHeight % expectedH == 0 && image.PixelHeight / expectedH is >= 2 and <= 4) { frames = image.PixelHeight / expectedH; vertical = true; }
        if (frames == 1 && image.PixelWidth % 4 == 0 && image.PixelWidth >= 2 * image.PixelHeight) frames = 4;
        if (frames == 1) return image;
        string key = e.Get("image") + "#" + frames + "#" + vertical + "#" + Math.Min(state, frames - 1);
        if (cache.TryGetValue(key, out var cached)) return cached;
        int w = vertical ? image.PixelWidth : image.PixelWidth / frames, h = vertical ? image.PixelHeight / frames : image.PixelHeight;
        var cropped = new CroppedBitmap(image, new Int32Rect(vertical ? 0 : w * Math.Min(state, frames - 1), vertical ? h * Math.Min(state, frames - 1) : 0, w, h)); cropped.Freeze(); cache[key] = cropped; return cropped;
    }
    public static Color ColorOf(string text, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(text); } catch { return fallback; } }
    public void ApplyTheme()
    {
        string n = Document.Name;
        string[] colors = n.Contains("iBlue", StringComparison.OrdinalIgnoreCase) ? ["#87C9D6", "#4A91A4", "#143644", "#101E28", "#DEEFF1", "#A7C8D2", "#72E9EE", "#E5F0F3"]
            : n.Contains("HiFi", StringComparison.OrdinalIgnoreCase) ? ["#9FB8B6", "#536E71", "#142E30", "#08221F", "#C3E7DA", "#90B6AD", "#52E6B3", "#E6EFEC"]
            : ["#BECFE4", "#728BA6", "#182C44", "#101725", "#D6EAF7", "#A8BED3", "#68E0DA", "#E9EFF6"];
        string[] keys = ["Shell", "Edge", "TitleInk", "Screen", "Ink", "Muted", "Accent", "Workspace"];
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush(ColorOf(colors[i], Colors.Gray));
    }
}

public sealed class SkinPlayer : FrameworkElement
{
    public SkinVisual? Skin { get; set; }
    public string Info { get; set; } = "未连接 · 请选择串口";
    public string Status { get; set; } = "OFF";
    public string Time { get; set; } = "00:00";
    public int Channels { get; set; }
    public bool Playing { get; set; }
    public SampleFrame[] Samples { get; set; } = [];
    public double Progress { get; set; } = 1;
    public double Volume { get; set; } = .35;
    public event Action<string, double>? Action;
    private string hover = "", pressed = "";
    private static readonly Dictionary<string, string> actions = new() { ["play"] = "开始监视 / 冻结显示", ["stop"] = "断开串口", ["prev"] = "上一条发送预设", ["next"] = "下一条发送预设", ["open"] = "串口设置", ["playlist"] = "收发窗口", ["lyric"] = "波形窗口", ["equalizer"] = "通道控制", ["minimize"] = "最小化", ["exit"] = "退出", ["minimode"] = "迷你模式", ["mute"] = "错误提示音", ["progress"] = "历史定位", ["volume"] = "波形时间范围", ["browser"] = "使用说明", ["set"] = "串口设置" };
    public SkinPlayer() { Focusable = true; MouseMove += Move; MouseLeave += (_, _) => { hover = ""; InvalidateVisual(); }; MouseLeftButtonDown += Down; MouseLeftButtonUp += Up; }
    protected override Size MeasureOverride(Size size)
    {
        double w = Skin?.Background.PixelWidth ?? 268, h = Skin?.Background.PixelHeight ?? 165;
        double width = Math.Min(double.IsInfinity(size.Width) ? w * 1.5 : size.Width, MaxHeight * w / h);
        return new(width, width * h / w);
    }
    private Rect Box(SkinRect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));
    protected override void OnRender(DrawingContext dc)
    {
        if (Skin == null || ActualWidth <= 0) return;
        double w = Skin.Background.PixelWidth, h = Skin.Background.PixelHeight;
        double scale = Math.Min(ActualWidth / w, ActualHeight / h);
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - w * scale) / 2, (ActualHeight - h * scale) / 2));
        dc.DrawImage(Skin.Background, new(0, 0, w, h));
        var elements = Skin.Document.Player.Elements;
        foreach (var (name, e) in elements)
        {
            if (!actions.ContainsKey(name) || name is "progress" or "volume" || e.Rect.Width <= 0 || e.Rect.Height <= 0) continue;
            var actual = name == "play" && Playing && elements.TryGetValue("pause", out var pause) ? pause : e;
            var image = Skin.State(actual, pressed == name ? 2 : hover == name ? 1 : 0);
            if (image != null) dc.DrawImage(image, Box(e.Rect));
        }
        foreach (string slider in new[] { "progress", "volume" })
        {
            if (!elements.TryGetValue(slider, out var e)) continue;
            var rect = Box(e.Rect); double value = slider == "progress" ? Progress : Volume;
            bool vertical = e.Get("vertical").Equals("true", StringComparison.OrdinalIgnoreCase);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(35, 100, 190, 240)), null, rect, 2, 2);
            var point = vertical ? new Point(rect.Left + rect.Width / 2, rect.Bottom - rect.Height * value) : new Point(rect.Left + rect.Width * value, rect.Top + rect.Height / 2);
            dc.DrawLine(new Pen(Brushes.LightCyan, 2), vertical ? new(rect.Left + rect.Width / 2, rect.Bottom) : new(rect.Left, rect.Top + rect.Height / 2), point);
            dc.DrawEllipse(Brushes.LightSteelBlue, new Pen(Brushes.SlateGray, .5), point, 3, 3);
        }
        foreach (var (key, text) in new[] { ("info", Info), ("status", Status), ("stereo", Channels + " CH"), ("led", Time) })
        {
            if (!elements.TryGetValue(key, out var e) || e.Rect.Width <= 0 || e.Rect.Height <= 0) continue;
            var rect = Box(e.Rect); var color = SkinVisual.ColorOf(e.Get("color"), Colors.Aquamarine);
            double font = Math.Clamp(e.Rect.Height * .75, 6, 13);
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), font, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, rect.Width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = e.Get("align") == "right" ? TextAlignment.Right : TextAlignment.Left };
            if (e.Get("bkgnd") is { Length: > 0 } background) dc.DrawRectangle(new SolidColorBrush(SkinVisual.ColorOf(background, Colors.Transparent)), null, rect);
            dc.PushClip(new RectangleGeometry(rect)); dc.DrawText(ft, new(rect.X, rect.Y + Math.Max(0, (rect.Height - ft.Height) / 2))); dc.Pop();
        }
        if (elements.TryGetValue("visual", out var v) && Samples.Length > 1 && v.Rect.Width > 0 && v.Rect.Height > 0)
        {
            var r = Box(v.Rect); dc.PushClip(new RectangleGeometry(r));
            double min = Samples.Min(s => s.Values[0]), max = Samples.Max(s => s.Values[0]); if (max - min < .01) max = min + 1;
            var geometry = new StreamGeometry(); using (var g = geometry.Open()) for (int i = 0; i < Samples.Length; i++) { var p = new Point(r.X + i * r.Width / (Samples.Length - 1), r.Bottom - 2 - (Samples[i].Values[0] - min) / (max - min) * Math.Max(1, r.Height - 4)); if (i == 0 || Samples[i].Gap) g.BeginFigure(p, false, false); else g.LineTo(p, true, false); }
            dc.DrawGeometry(null, new Pen(Brushes.Turquoise, 1), geometry); dc.Pop();
        }
        dc.Pop();
    }
    private Point SkinPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(this); if (Skin == null) return p;
        double w = Skin.Background.PixelWidth, h = Skin.Background.PixelHeight, scale = Math.Max(1e-6, Math.Min(ActualWidth / w, ActualHeight / h));
        return new((p.X - (ActualWidth - w * scale) / 2) / scale, (p.Y - (ActualHeight - h * scale) / 2) / scale);
    }
    private string Hit(Point p) => Skin?.Document.Player.Elements.FirstOrDefault(k => actions.ContainsKey(k.Key) && Box(k.Value.Rect).Contains(p)).Key ?? "";
    private void Move(object sender, MouseEventArgs e) { var p = SkinPoint(e); hover = Hit(p); ToolTip = actions.GetValueOrDefault(hover); Cursor = hover.Length > 0 ? Cursors.Hand : Cursors.Arrow; if (pressed is "progress" or "volume" && e.LeftButton == MouseButtonState.Pressed) Slider(pressed, p); InvalidateVisual(); }
    private void Down(object sender, MouseButtonEventArgs e) { pressed = Hit(SkinPoint(e)); if (pressed.Length == 0) { try { Window.GetWindow(this)?.DragMove(); } catch (InvalidOperationException) { } } else { CaptureMouse(); if (pressed is "volume" or "progress") Slider(pressed, SkinPoint(e)); } InvalidateVisual(); }
    private void Up(object sender, MouseButtonEventArgs e) { string action = pressed; pressed = ""; ReleaseMouseCapture(); if (action.Length > 0 && action == Hit(SkinPoint(e)) && action is not "progress" and not "volume") Action?.Invoke(action, 0); InvalidateVisual(); }
    private void Slider(string name, Point p) { if (Skin == null || !Skin.Document.Player.Elements.TryGetValue(name, out var e)) return; double value = e.Get("vertical").Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 - (p.Y - e.Rect.Y) / Math.Max(1, e.Rect.Height) : (p.X - e.Rect.X) / Math.Max(1, e.Rect.Width); Action?.Invoke(name, Math.Clamp(value, 0, 1)); }
}
