using System.Globalization;
using System.Text;
using System.Threading.Channels;
using TTSerial.Core;

namespace TTSerial.IO;

public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly Channel<(WirePacket Packet, SampleFrame[] Samples)> queue = Channel.CreateBounded<(WirePacket, SampleFrame[])>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private volatile bool failed;
    public bool Failed => failed;
    public string? Error { get; private set; }
    public string DirectoryPath { get; }
    public SessionRecorder(string directory)
    {
        DirectoryPath = directory; Directory.CreateDirectory(directory);
        // Open before announcing recording so permission errors are synchronous.
        var raw = new StreamWriter(Path.Combine(directory, "wire.jsonl"), false, new UTF8Encoding(false));
        StreamWriter values;
        try { values = new(Path.Combine(directory, "samples.csv"), false, new UTF8Encoding(true)); }
        catch { raw.Dispose(); throw; }
        writer = Task.Run(async () =>
        {
            try
            {
                await values.WriteLineAsync("time_s,ch1,ch2,ch3,ch4,ch5,ch6,ch7,ch8,gap");
                await foreach (var batch in queue.Reader.ReadAllAsync())
                {
                    await raw.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { timestamp = batch.Packet.Timestamp, direction = batch.Packet.Transmit ? "TX" : "RX", hex = Convert.ToHexString(batch.Packet.Data) }));
                    foreach (var sample in batch.Samples) await values.WriteLineAsync(Csv(sample));
                }
            }
            catch (Exception e) { Error = e.Message; failed = true; queue.Writer.TryComplete(); }
            finally
            {
                try { await raw.DisposeAsync(); } catch (Exception e) { Error = e.Message; failed = true; }
                try { await values.DisposeAsync(); } catch (Exception e) { Error = e.Message; failed = true; }
            }
        });
    }
    public bool Add(WirePacket packet, SampleFrame[] samples)
    {
        if (failed) return false;
        if (queue.Writer.TryWrite((packet, samples))) return true;
        Error = "磁盘写入跟不上，录制队列已满；录制已停止。"; failed = true; queue.Writer.TryComplete(); return false;
    }
    public static string Csv(SampleFrame s) => s.Time.ToString("G17", CultureInfo.InvariantCulture) + "," + string.Join(',', Enumerable.Range(0, 8).Select(i => i < s.Values.Length ? s.Values[i].ToString("G17", CultureInfo.InvariantCulture) : "")) + "," + (s.Gap ? "1" : "0");
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer; }
}
