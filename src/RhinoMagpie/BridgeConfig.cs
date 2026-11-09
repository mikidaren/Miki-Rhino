using System.Text.Json;

namespace RhinoMagpie;

public sealed record BridgeConfig(string Model, string BaseUrl, string ApiKey)
{
    public static BridgeConfig Parse(string json)
    {
        BridgeConfig config;
        try
        {
            config = JsonSerializer.Deserialize<BridgeConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Miki-Rhino 连接配置为空。");
        }
        catch (JsonException)
        {
            throw new ArgumentException("Miki-Rhino 连接配置不是有效 JSON。");
        }
        return config.Validate();
    }

    public BridgeConfig Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsControl) ||
            (ApiKey ?? "").Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("请填写有效模型 ID 和 API key。无密钥的本机服务可留空 API key。");
        if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl.Any(char.IsControl) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            (uri.Scheme == "http" && !uri.IsLoopback) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请填写 OpenAI-compatible API 基地址。远程地址使用 HTTPS，本机可使用 HTTP，地址中不含凭据、查询或片段。");
        string endpoint = uri.AbsolutePath == "/" ? uri.GetLeftPart(UriPartial.Authority) + "/v1" :
            uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return this with { Model = Model.Trim(), BaseUrl = endpoint, ApiKey = ApiKey ?? "" };
    }

    public override string ToString() => $"Miki-Rhino connection ({BaseUrl}, {Model})";
}
