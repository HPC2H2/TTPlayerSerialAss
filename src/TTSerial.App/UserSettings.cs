using System.IO;
using System.Text.Json;
using TTSerial.Core;
using TTSerial.IO;

namespace TTSerial.App;
public sealed record CommandPreset(string Name, string Text, bool Hex = false);
public sealed record SavedChannel(bool Enabled = true, string Name = "", double Scale = 1, double Offset = 0, string Unit = "V");
public sealed class UserSettings
{
    public SerialOptions Serial { get; set; } = new();
    public ParserOptions Protocol { get; set; } = new();
    public string Encoding { get; set; } = "UTF-8";
    public string? SkinPath { get; set; }
    public List<string> ImportedSkins { get; set; } = [];
    public List<CommandPreset> Presets { get; set; } = [new("查询状态", "GET_STATUS"), new("读取 ADC", "READ_ADC"), new("设置采样率", "SET_RATE 1000")];
    public SavedChannel[] Channels { get; set; } = [];
    public double Width { get; set; } = 1140;
    public double Height { get; set; } = 780;
    private static string FileName => Path.Combine(App.DataDirectory, "settings.json");
    public static UserSettings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FileName)) ?? new();
            value.Serial ??= new(); value.Protocol ??= new(); value.Channels ??= []; value.Presets ??= []; value.ImportedSkins ??= [];
            _ = new NumericLineParser(value.Protocol);
            return value;
        }
        catch { return new(); }
    }
    public void Save()
    {
        var temp = FileName + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, FileName, true);
    }
}
