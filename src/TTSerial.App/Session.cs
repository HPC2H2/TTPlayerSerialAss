using System.Collections.Concurrent;
using System.Diagnostics;
using TTSerial.Core;
using TTSerial.IO;

namespace TTSerial.App;
public sealed class Session : IAsyncDisposable
{
    public SerialConnection Connection { get; } = new();
    public SampleBuffer Buffer { get; } = new();
    public ConcurrentQueue<WirePacket> DisplayPackets { get; } = new();
    private readonly Queue<WirePacket> wireHistory = new();
    private readonly object gate = new();
    private NumericLineParser parser = new(new());
    private readonly Stopwatch clock = new();
    private SessionRecorder? recorder;
    private long rx, tx, droppedDisplay;
    private int wireBytes;
    public long Rx => Interlocked.Read(ref rx);
    public long Tx => Interlocked.Read(ref tx);
    public long DroppedDisplay => Interlocked.Read(ref droppedDisplay);
    public long Rejected { get { lock (gate) return parser.RejectedLines; } }
    public string? Error { get; private set; }
    public string? RecordingError { get; private set; }
    public bool Recording { get { lock (gate) return recorder is { Failed: false }; } }
    public Session()
    {
        Connection.Packet += OnPacket; Connection.Fault += error => Error = error;
    }
    public async Task OpenAsync(SerialOptions options, ParserOptions protocol)
    {
        await CloseAsync();
        lock (gate) { Buffer.Clear(); parser = new(protocol); Error = null; RecordingError = null; rx = tx = droppedDisplay = 0; wireHistory.Clear(); wireBytes = 0; while (DisplayPackets.TryDequeue(out _)) { } clock.Restart(); }
        await Connection.OpenAsync(options);
    }
    private void OnPacket(WirePacket packet)
    {
        lock (gate)
        {
            SampleFrame[] samples = [];
            if (packet.Transmit) Interlocked.Add(ref tx, packet.Data.Length);
            else { Interlocked.Add(ref rx, packet.Data.Length); samples = parser.Feed(packet.Data, clock.Elapsed.TotalSeconds).ToArray(); Buffer.AddRange(samples); }
            wireHistory.Enqueue(packet); wireBytes += packet.Data.Length;
            while (wireBytes > 4 * 1024 * 1024 && wireHistory.Count > 0) wireBytes -= wireHistory.Dequeue().Data.Length;
            if (DisplayPackets.Count >= 256) { DisplayPackets.TryDequeue(out _); Interlocked.Increment(ref droppedDisplay); }
            DisplayPackets.Enqueue(packet);
            if (recorder != null && !recorder.Add(packet, samples)) RecordingError = recorder.Error ?? "录制失败。";
        }
    }
    public WirePacket[] WireSnapshot() { lock (gate) return wireHistory.ToArray(); }
    public void StartRecording(string directory) { lock (gate) { if (recorder != null) throw new InvalidOperationException("请先停止当前录制。"); RecordingError = null; recorder = new(directory); } }
    public async Task StopRecordingAsync() { SessionRecorder? old; lock (gate) { old = recorder; recorder = null; } if (old != null) { await old.DisposeAsync(); if (old.Failed) RecordingError = old.Error; } }
    public async Task CloseAsync() { await Connection.CloseAsync(); await StopRecordingAsync(); }
    public async ValueTask DisposeAsync() { await CloseAsync(); await Connection.DisposeAsync(); }
}
