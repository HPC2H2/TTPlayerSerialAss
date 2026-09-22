using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace TTSerial.Core;

public readonly record struct SkinRect(double X, double Y, double Width, double Height)
{
    public static SkinRect Parse(string? text)
    {
        var p = text?.Split(',').Select(v => double.TryParse(v.Trim(), CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && Math.Abs(n) <= 65536 ? n : 0).ToArray();
        return p is { Length: 4 } ? new(p[0], p[1], p[2] - p[0], p[3] - p[1]) : default;
    }
}
public sealed record SkinElement(string Name, Dictionary<string, string> Attributes)
{
    public string Get(string key, string fallback = "") => Attributes.GetValueOrDefault(key, fallback);
    public SkinRect Rect => SkinRect.Parse(Get("position"));
}
public sealed record SkinWindow(SkinElement Definition, Dictionary<string, SkinElement> Elements);
public sealed class SkinDocument
{
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Source { get; init; }
    public string TransparentColor { get; init; } = "#ff00ff";
    public required Dictionary<string, SkinWindow> Windows { get; init; }
    public required Dictionary<string, byte[]> Assets { get; init; }
    public Dictionary<string, string> Colors { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Asset(string name) => Assets.GetValueOrDefault(name.Replace('\\', '/'));
    public SkinWindow Player => Windows["player_window"];
}

public static class SkinReader
{
    public static SkinDocument Load(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("皮肤包超过 64 MB。");
        using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false, Encoding.GetEncoding(936));
        if (archive.Entries.Count > 2048) throw new InvalidDataException("皮肤文件数量过多。");
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains("..")) throw new InvalidDataException("皮肤包含无效路径。");
            if (name.EndsWith('/')) continue;
            if (entry.Length > 16 * 1024 * 1024 || (total += entry.Length) > 96 * 1024 * 1024) throw new InvalidDataException("皮肤展开后过大。");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = source.Read(chunk)) > 0)
            {
                if (buffer.Length + read > entry.Length || buffer.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("皮肤资源展开长度异常。");
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length != entry.Length) throw new InvalidDataException("皮肤资源不完整。");
            assets.TryAdd(name, buffer.ToArray());
        }
        var xmlPath = assets.Keys.FirstOrDefault(n => Path.GetFileName(n).Equals("Skin.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("缺少 Skin.xml。");
        var prefix = xmlPath[..^"Skin.xml".Length];
        if (prefix.Length > 0) assets = assets.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.OrdinalIgnoreCase);
        var root = LegacyXml.Parse(Decode(assets["Skin.xml"]));
        if (!root.Name.LocalName.Equals("skin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("根节点不是 skin。");
        var windows = new Dictionary<string, SkinWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in root.Elements().Where(e => e.Name.LocalName.EndsWith("_window", StringComparison.OrdinalIgnoreCase)))
        {
            var definition = Element(w);
            var elements = new Dictionary<string, SkinElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in w.Elements()) elements[e.Name.LocalName] = Element(e);
            windows[w.Name.LocalName] = new(definition, elements);
        }
        if (!windows.TryGetValue("player_window", out var player) || !assets.ContainsKey(player.Definition.Get("image"))) throw new InvalidDataException("缺少主窗口或主窗口背景。");
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "Lyric.xml", "Playlist.xml", "Visual.xml" })
        {
            if (!assets.TryGetValue(file, out var bytes)) continue;
            try { foreach (var attr in LegacyXml.Parse(Decode(bytes)).DescendantsAndSelf().Attributes()) if (attr.Value.StartsWith('#')) colors[attr.Name.LocalName] = attr.Value; }
            catch (InvalidDataException) { /* Auxiliary settings may be absent in old skins. */ }
        }
        return new SkinDocument { Name = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(path), Author = root.Attribute("author")?.Value ?? "", Source = path,
            TransparentColor = root.Attribute("transparent_color")?.Value ?? "#ff00ff", Windows = windows, Assets = assets, Colors = colors };
    }
    private static SkinElement Element(XElement e) => new(e.Name.LocalName, e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase));
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length > 1 && ((bytes[0] == 255 && bytes[1] == 254) || (bytes[0] == 254 && bytes[1] == 255)))
            return (bytes[0] == 255 ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes).TrimStart('\uFEFF');
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(54936).GetString(bytes); }
    }
}

/// <summary>Bounded legacy attribute tokenizer. Accepts unquoted values and literal ampersands; never resolves external entities.</summary>
public static class LegacyXml
{
    public static XElement Parse(string text)
    {
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("皮肤不支持 DTD 或外部实体。");
        var stack = new Stack<XElement>();
        XElement? root = null;
        int i = 0, nodes = 0;
        while (i < text.Length)
        {
            if (text[i++] != '<') continue;
            if (text.AsSpan(i).StartsWith("!--")) { var end = text.IndexOf("-->", i, StringComparison.Ordinal); if (end < 0) throw new InvalidDataException("未结束的注释。"); i = end + 3; continue; }
            if (i < text.Length && text[i] == '?') { var end = text.IndexOf("?>", i, StringComparison.Ordinal); if (end < 0) throw new InvalidDataException("XML 声明不完整。"); i = end + 2; continue; }
            bool closing = i < text.Length && text[i] == '/'; if (closing) i++;
            Skip(); string name = Name();
            if (name.Length == 0) throw new InvalidDataException("无效 XML 节点。");
            if (closing)
            {
                Skip(); if (i >= text.Length || text[i++] != '>' || stack.Count == 0 || !stack.Peek().Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("皮肤节点未正确闭合。");
                stack.Pop(); continue;
            }
            if (++nodes > 10000 || stack.Count > 32) throw new InvalidDataException("皮肤 XML 结构过大。");
            var element = new XElement(name);
            bool selfClosing = false, ended = false;
            while (i < text.Length)
            {
                Skip(); if (i >= text.Length) break;
                if (text[i] == '>') { i++; ended = true; break; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>') { i += 2; selfClosing = ended = true; break; }
                string key = Name(); Skip();
                if (key.Length == 0 || i >= text.Length || text[i++] != '=') throw new InvalidDataException("无效的皮肤属性。");
                Skip(); if (i >= text.Length) throw new InvalidDataException("缺少属性值。");
                char quote = text[i]; string value;
                if (quote is '\'' or '"')
                {
                    int start = ++i; while (i < text.Length && text[i] != quote) i++;
                    if (i == text.Length) throw new InvalidDataException("属性引号未闭合。");
                    value = text[start..i++];
                }
                else { int start = i; while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>' && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')) i++; value = text[start..i]; }
                element.SetAttributeValue(key, WebUtility.HtmlDecode(value));
            }
            if (!ended) throw new InvalidDataException("XML 节点不完整。");
            if (stack.Count > 0) stack.Peek().Add(element); else if (root == null) root = element; else throw new InvalidDataException("多个 XML 根节点。");
            if (!selfClosing) stack.Push(element);
        }
        if (stack.Count != 0 || root == null) throw new InvalidDataException("XML 未完整结束。");
        return root;
        void Skip() { while (i < text.Length && char.IsWhiteSpace(text[i])) i++; }
        string Name() { int start = i; while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ':' or '-' or '.')) i++; return text[start..i]; }
    }
}
