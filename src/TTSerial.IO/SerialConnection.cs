using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace TTSerial.IO;

public sealed record SerialOptions(string Port = "演示设备", int Baud = 115200, int DataBits = 8, Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake Handshake = Handshake.None, bool Dtr = false, bool Rts = false);
public sealed record WirePacket(byte[] Data, bool Transmit, DateTimeOffset Timestamp);

/// <summary>One owner thread does all serial I/O. Bounded outgoing queue; closing never races a DataReceived handler.</summary>
public sealed class SerialConnection : IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Channel<byte[]>? outgoing;
    public event Action<WirePacket>? Packet;
    public event Action<string>? Fault;
    public bool Connected => worker is { IsCompleted: false } && connected;
    private volatile bool connected;
    public Task OpenAsync(SerialOptions options)
    {
        if (worker is { IsCompleted: false }) throw new InvalidOperationException("请先断开当前连接。");
        cancellation?.Dispose(); cancellation = new();
        outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellation.Token; var queue = outgoing;
        worker = Task.Factory.StartNew(() => Run(options, queue, started, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return started.Task;
    }
    public void Send(byte[] bytes)
    {
        if (!Connected) throw new InvalidOperationException("串口尚未连接。");
        if (bytes.Length == 0 || bytes.Length > 65536) throw new ArgumentException("单次发送长度应为 1–65536 字节。");
        if (outgoing?.Writer.TryWrite(bytes.ToArray()) != true) throw new IOException("发送队列已满，请降低发送频率。");
    }
    public async Task CloseAsync()
    {
        cancellation?.Cancel(); outgoing?.Writer.TryComplete();
        if (worker != null) await worker.ConfigureAwait(false);
        connected = false;
    }
    private void Run(SerialOptions options, Channel<byte[]> queue, TaskCompletionSource started, CancellationToken token)
    {
        SerialPort? port = null;
        try
        {
            bool demo = options.Port == "演示设备";
            if (!demo)
            {
                port = new(options.Port, options.Baud, options.Parity, options.DataBits, options.StopBits)
                { Handshake = options.Handshake, DtrEnable = options.Dtr, ReadTimeout = 100, WriteTimeout = 250, ReadBufferSize = 65536 };
                if (options.Handshake is not Handshake.RequestToSend and not Handshake.RequestToSendXOnXOff) port.RtsEnable = options.Rts;
                port.Open();
            }
            connected = true; started.TrySetResult();
            long sample = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); double nextDemo = 0;
            var readBuffer = new byte[16384];
            while (!token.IsCancellationRequested)
            {
                if (queue.Reader.TryRead(out var tx))
                {
                    port?.Write(tx, 0, tx.Length);
                    Packet?.Invoke(new(tx, true, DateTimeOffset.Now));
                    if (demo) Packet?.Invoke(new(Encoding.UTF8.GetBytes("# ECHO " + Encoding.UTF8.GetString(tx).TrimEnd() + "\r\n"), false, DateTimeOffset.Now));
                }
                if (demo && clock.Elapsed.TotalSeconds >= nextDemo)
                {
                    var text = new StringBuilder();
                    for (int n = 0; n < 20; n++, sample++)
                    {
                        double t = sample / 1000.0;
                        text.Append(CultureInfo.InvariantCulture, $"{1.65 + 1.1 * Math.Sin(t * Math.Tau * 2):F4},{1.65 + .7 * Math.Sin(t * Math.Tau * 3 + .8):F4},{1.5 + .6 * (Math.Sin(t * Math.Tau) > 0 ? 1 : -1):F4},{1.2 + .15 * Math.Sin(t * Math.Tau * 11):F4}\r\n");
                    }
                    Packet?.Invoke(new(Encoding.UTF8.GetBytes(text.ToString()), false, DateTimeOffset.Now));
                    nextDemo += .02;
                    if (clock.Elapsed.TotalSeconds - nextDemo > .5) nextDemo = clock.Elapsed.TotalSeconds;
                }
                else if (port != null && port.BytesToRead > 0)
                {
                    int count = port.Read(readBuffer, 0, Math.Min(port.BytesToRead, readBuffer.Length));
                    if (count > 0) Packet?.Invoke(new(readBuffer[..count], false, DateTimeOffset.Now));
                }
                token.WaitHandle.WaitOne(2);
            }
        }
        catch (Exception e) { started.TrySetException(e); if (!token.IsCancellationRequested) Fault?.Invoke(e.Message); }
        finally { connected = false; try { port?.Dispose(); } catch { } queue.Writer.TryComplete(); }
    }
    public async ValueTask DisposeAsync() { await CloseAsync(); cancellation?.Dispose(); }
}
