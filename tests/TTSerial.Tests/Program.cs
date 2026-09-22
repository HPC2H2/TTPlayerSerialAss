using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using TTSerial.Core;
using TTSerial.IO;

int passed = 0, failed = 0;
void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection."); }
void Test(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } }
async Task AsyncTest(string name, Func<Task> action) { try { await action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } }
string root = args.FirstOrDefault() ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

Test("All byte boundaries preserve numeric frames", () =>
{
    var bytes = Encoding.UTF8.GetBytes("1.25,-2,3e-2\r\n4.5,6,7\n");
    for (int split = 0; split <= bytes.Length; split++)
    {
        var parser = new NumericLineParser(new(TimeSource: TimeSource.FixedRate, SampleRate: 2000));
        var frames = parser.Feed(bytes.AsSpan(0, split), 0).Concat(parser.Feed(bytes.AsSpan(split), 1)).ToArray();
        Assert(frames.Length == 2 && frames[0].Values[0] == 1.25 && frames[0].Values[2] == .03 && frames[1].Time == .0005, "Split " + split);
    }
});
Test("Bad lines create a gap and consume a fixed-rate slot", () =>
{
    var parser = new NumericLineParser(new(TimeSource: TimeSource.FixedRate, SampleRate: 1000));
    var frames = parser.Feed(Encoding.ASCII.GetBytes("1,2\nNaN,2\n3,4,5\n5,6\n"), 0);
    Assert(frames.Count == 2 && parser.RejectedLines == 2 && frames[1].Gap && frames[1].Time == .003, "Bad lines / gaps");
});
Test("Long line is bounded and parser recovers", () =>
{
    var parser = new NumericLineParser(new(TimeSource: TimeSource.FixedRate));
    var frames = parser.Feed(Encoding.ASCII.GetBytes(new string('1', 100000) + "\n2\n"), 0);
    Assert(frames.Count == 1 && frames[0].Values[0] == 2 && frames[0].Gap && frames[0].Time == .001 && parser.RejectedLines == 1, "Long line recovery");
});
Test("Device timestamp and explicit fields reject backward time", () =>
{
    var parser = new NumericLineParser(new(TimeSource: TimeSource.DeviceMilliseconds, Fields: [2, 1]));
    var frames = parser.Feed(Encoding.ASCII.GetBytes("1000,10,20\n1002,11,21\n1001,12,22\n1003,13,23\n"), 2);
    Assert(frames.Count == 3 && Math.Abs(frames[1].Time - .002) < 1e-9 && frames[0].Values.SequenceEqual(new double[] { 20, 10 }) && frames[2].Gap, "Timestamp / field mapping");
    Throws(() => new NumericLineParser(new(TimeSource: TimeSource.DeviceMilliseconds, Fields: [0])));
    Throws(() => new NumericLineParser(new(Fields: [1, 1])));
});
Test("Whitespace and arrival timestamps", () =>
{
    var frames = new NumericLineParser(new(Delimiter: ' ')).Feed(Encoding.ASCII.GetBytes("1  2\t3\n"), 12.5);
    Assert(frames.Count == 1 && frames[0].Time == 12.5 && frames[0].Values.Length == 3, "Whitespace mode");
});
Test("Circular history order, eviction, range query", () =>
{
    var buffer = new SampleBuffer(4); buffer.AddRange(Enumerable.Range(0, 8).Select(i => new SampleFrame(i, [i])));
    Assert(buffer.Count == 4 && buffer.Evicted == 4 && buffer.Range == (4, 7), "Bounded history");
    Assert(buffer.Snapshot().Select(s => s.Time).SequenceEqual(new double[] { 4, 5, 6, 7 }), "Order");
    Assert(buffer.Snapshot(5.1, 5.9).Select(s => s.Time).SequenceEqual(new double[] { 5, 6 }), "Adjacent points");
    Throws(() => buffer.AddRange([new(1, [1])])); buffer.Clear(); Assert(buffer.Count == 0, "Clear");
});
Test("Send codec preserves exact bytes", () =>
{
    Assert(SendCodec.Encode("01 03\n00 ff", true, "\r\n", Encoding.UTF8).SequenceEqual(new byte[] { 1, 3, 0, 255 }), "HEX output");
    Assert(Encoding.UTF8.GetString(SendCodec.Encode("读取", false, "\r\n", Encoding.UTF8)) == "读取\r\n", "Text output");
    Throws(() => SendCodec.Encode("ABC", true, "", Encoding.UTF8)); Throws(() => SendCodec.Encode("0x01", true, "", Encoding.UTF8));
});
Test("Legacy XML handles literals and isolates windows", () =>
{
    var xml = LegacyXml.Parse("<?xml version='1.0'?><skin name='A & B'><player_window><play position=1,2,3,4 /></player_window><mini_window><play position=5,6,7,8 /></mini_window></skin>");
    Assert(xml.Attribute("name")?.Value == "A & B" && xml.Element("player_window")?.Element("play")?.Attribute("position")?.Value == "1,2,3,4", "Legacy attributes");
    Throws(() => LegacyXml.Parse("<!DOCTYPE skin SYSTEM 'file:///secret'><skin/>")); Throws(() => LegacyXml.Parse("<skin><x></skin>"));
});
Test("All bundled SKN archives parse with independent main windows", () =>
{
    var files = Directory.GetFiles(Path.Combine(root, "resources/skins"), "*.skn"); Assert(files.Length >= 69, "Skin count");
    var errors = new List<string>();
    foreach (var file in files) try { var skin = SkinReader.Load(file); Assert(skin.Player.Elements.Count > 5 && skin.Asset(skin.Player.Definition.Get("image")) != null, "Main window"); } catch (Exception e) { errors.Add(Path.GetFileName(file) + ": " + e.Message); }
    Assert(errors.Count == 0, string.Join("\n", errors)); Console.WriteLine($"  {files.Length} SKN archives checked");
});
Test("ZIP path traversal rejected before extraction", () =>
{
    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".skn");
    try { using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("../escape").Open())) writer.Write("test"); Throws(() => SkinReader.Load(file)); } finally { File.Delete(file); }
});
await AsyncTest("Repeated connect, asynchronous TX echo, disconnect", async () =>
{
    await using var connection = new SerialConnection(); var packets = new ConcurrentQueue<WirePacket>(); connection.Packet += packets.Enqueue;
    for (int cycle = 0; cycle < 3; cycle++)
    {
        await connection.OpenAsync(new()); connection.Send(Encoding.ASCII.GetBytes("PING\r\n"));
        var timeout = DateTime.UtcNow.AddSeconds(4);
        while (!packets.Any(p => p.Transmit) && DateTime.UtcNow < timeout) await Task.Delay(10);
        await connection.CloseAsync(); Assert(!connection.Connected, "Disconnect");
        Assert(packets.Any(p => p.Transmit && Encoding.ASCII.GetString(p.Data) == "PING\r\n"), "TX");
        Assert(packets.Any(p => !p.Transmit && Encoding.ASCII.GetString(p.Data).Contains("ECHO PING")), "RX echo"); packets.Clear();
    }
    Throws(() => connection.Send([1]));
});
await AsyncTest("Recorder drains raw bytes and invariant CSV", async () =>
{
    var folder = Path.Combine(root, "artifacts", "recorder-test-" + Guid.NewGuid().ToString("N"));
    await using (var recorder = new SessionRecorder(folder))
    {
        for (int i = 0; i < 250; i++) Assert(recorder.Add(new([0, 255, 13, 10], i % 2 == 0, DateTimeOffset.Now), [new(i / 1000.0, [1.25, 3.5], i == 5)]), "Queue enqueue");
        await recorder.DisposeAsync(); Assert(!recorder.Failed, recorder.Error ?? "Recorder failed");
    }
    Assert(File.ReadAllLines(Path.Combine(folder, "wire.jsonl")).Length == 250, "Raw rows");
    var rows = File.ReadAllLines(Path.Combine(folder, "samples.csv")); Assert(rows.Length == 251 && rows[1].StartsWith("0,1.25,3.5,"), "CSV rows");
});
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
