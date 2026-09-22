using System.Globalization;
using System.Text;

namespace TTSerial.Core;

public enum TimeSource { Arrival, FixedRate, DeviceMilliseconds }
public sealed record ParserOptions(char Delimiter = ',', TimeSource TimeSource = TimeSource.Arrival, double SampleRate = 1000, int[]? Fields = null);
public sealed record SampleFrame(double Time, double[] Values, bool Gap = false);

public sealed class NumericLineParser
{
    private readonly List<byte> line = new(256);
    private bool dropping, gap;
    private long index;
    private double? origin;
    private double lastTime = double.NegativeInfinity;
    private int channelCount;
    public ParserOptions Options { get; }
    public long RejectedLines { get; private set; }
    public NumericLineParser(ParserOptions options)
    {
        if (!double.IsFinite(options.SampleRate) || options.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(options.TimeSource) || options.Fields?.Any(f => f < 0 || f > 63 || (options.TimeSource == TimeSource.DeviceMilliseconds && f == 0)) == true || options.Fields?.Length > 8 || options.Fields?.Distinct().Count() != options.Fields?.Length) throw new ArgumentException("字段编号或时间来源无效。");
        Options = options;
    }
    public List<SampleFrame> Feed(ReadOnlySpan<byte> data, double arrivalSeconds)
    {
        var result = new List<SampleFrame>();
        foreach (byte value in data)
        {
            if (value == 10)
            {
                if (!dropping && line.Count > 0) Parse(result, arrivalSeconds);
                line.Clear(); dropping = false;
            }
            else if (!dropping && value != 13)
            {
                if (line.Count >= 8192) { line.Clear(); dropping = true; Reject(); }
                else line.Add(value);
            }
        }
        return result;
    }
    private void Parse(List<SampleFrame> result, double arrival)
    {
        string text = Encoding.UTF8.GetString(line.ToArray()).Trim().TrimStart('\uFEFF');
        if (text.Length == 0) return;
        var parts = Options.Delimiter == ' ' ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : text.Split(Options.Delimiter);
        var values = new double[parts.Length];
        if (parts.Length > 65) { Reject(); return; }
        for (int i = 0; i < parts.Length; i++) if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i])) { Reject(); return; }
        int offset = Options.TimeSource == TimeSource.DeviceMilliseconds ? 1 : 0;
        var fields = Options.Fields is { Length: > 0 } ? Options.Fields : Enumerable.Range(offset, Math.Max(0, values.Length - offset)).ToArray();
        if (fields.Length is < 1 or > 8 || fields.Any(f => f >= values.Length || (offset == 1 && f == 0))) { Reject(); return; }
        if (channelCount != 0 && fields.Length != channelCount) { Reject(); return; }
        double t = Options.TimeSource switch { TimeSource.FixedRate => index / Options.SampleRate, TimeSource.DeviceMilliseconds => values[0] / 1000.0, _ => arrival };
        if (Options.TimeSource == TimeSource.DeviceMilliseconds)
        {
            origin ??= t; t -= origin.Value;
            if (t < 0 || t <= lastTime) { Reject(); return; }
        }
        channelCount = fields.Length; lastTime = t; index++;
        result.Add(new(t, fields.Select(f => values[f]).ToArray(), gap)); gap = false;
    }
    private void Reject() { RejectedLines++; gap = true; index++; }
}

/// <summary>Bounded, time-ordered history. A snapshot owns its array and is safe to use on the UI thread.</summary>
public sealed class SampleBuffer(int capacity = 120000)
{
    private readonly SampleFrame?[] frames = new SampleFrame[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private readonly object gate = new();
    private int head, count;
    public int Count { get { lock (gate) return count; } }
    public long Evicted { get; private set; }
    private SampleFrame At(int n) => frames[(head + n) % frames.Length]!;
    public void AddRange(IEnumerable<SampleFrame> values)
    {
        lock (gate) foreach (var frame in values)
        {
            if (count > 0 && frame.Time < At(count - 1).Time) throw new InvalidDataException("样本时间必须递增。");
            if (count == frames.Length) { frames[head] = frame; head = (head + 1) % frames.Length; Evicted++; }
            else { frames[(head + count) % frames.Length] = frame; count++; }
        }
    }
    public (double First, double Last) Range { get { lock (gate) return count == 0 ? (0, 0) : (At(0).Time, At(count - 1).Time); } }
    public SampleFrame[] Snapshot(double start = double.NegativeInfinity, double end = double.PositiveInfinity)
    {
        lock (gate)
        {
            int lo = 0, hi = count;
            while (lo < hi) { int m = (lo + hi) / 2; if (At(m).Time < start) lo = m + 1; else hi = m; }
            int begin = Math.Max(0, lo - 1); hi = count;
            while (lo < hi) { int m = (lo + hi) / 2; if (At(m).Time <= end) lo = m + 1; else hi = m; }
            int finish = Math.Min(count, lo + 1);
            return Enumerable.Range(begin, Math.Max(0, finish - begin)).Select(At).ToArray();
        }
    }
    public void Clear() { lock (gate) { Array.Clear(frames); head = count = 0; Evicted = 0; } }
}

public static class SendCodec
{
    public static byte[] Encode(string text, bool hex, string newline, Encoding encoding)
    {
        if (!hex) return encoding.GetBytes(text + newline);
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length % 2 != 0 || compact.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("HEX 必须由成对的十六进制数字组成，例如 01 03 00 FF。");
        return Convert.FromHexString(compact);
    }
}
