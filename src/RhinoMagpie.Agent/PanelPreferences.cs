using System.Text.Json;

namespace RhinoMagpie.Agent;

public sealed record PanelPreferences(bool AutoExecute = false)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(MagpieGateway.ConfigPath)!, "panel-preferences.json");

    public static PanelPreferences Read(string? path = null)
    {
        path ??= DefaultPath;
        return File.Exists(path)
            ? JsonSerializer.Deserialize<PanelPreferences>(File.ReadAllText(path), Options)
                ?? throw new JsonException("执行模式偏好不是有效的 JSON 对象。")
            : new PanelPreferences();
    }

    public void Save(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, Options));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
