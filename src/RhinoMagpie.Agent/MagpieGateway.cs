using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed record ModelChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class MagpieGateway(HttpClient client)
{
    public const string Version = "0.1.0";

    public static string ConfigPath => ConnectionSettings.ConfigPath;

    public static string EnsureConfigurationDirectory(string? userProfile = null) =>
        Directory.CreateDirectory(ConfigurationDirectory(userProfile)).FullName;

    private static string ConfigurationDirectory(string? userProfile = null) => Path.Combine(
        userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "miki-rhino");

    public static BridgeConfig ReadConfig() => ConnectionSettings.Read();

    public async Task<IReadOnlyList<ModelChoice>> GetModelsAsync(BridgeConfig config, CancellationToken token)
    {
        config = config.Validate();
        using var request = Request(config, HttpMethod.Get, "/models");
        var result = await SendAsync(request, token);
        return (result["data"]?.AsArray() ?? throw new InvalidOperationException("所选服务返回的模型列表无效，可继续使用手动配置的模型 ID。"))
            .OfType<JsonObject>()
            .Where(model => model["tool_call"]?.GetValue<bool>() != false)
            .Select(model => new ModelChoice(model["id"]!.GetValue<string>(),
                model["magpie_label"]?.GetValue<string>() ??
                model["display_name"]?.GetValue<string>() ?? model["id"]!.GetValue<string>()))
            .ToList();
    }

    public async Task<JsonObject> CompleteAsync(
        BridgeConfig config, JsonArray messages, JsonArray tools, CancellationToken token,
        Action<string>? onText = null)
    {
        config = config.Validate();
        using var request = Request(config, HttpMethod.Post, "/chat/completions");
        request.Content = JsonContent.Create(new JsonObject
        {
            ["model"] = config.Model,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["stream"] = true,
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        JsonObject message;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            message = await StreamingChatReader.ReadAsync(stream, onText, token).ConfigureAwait(false);
        }
        else
        {
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(token).ConfigureAwait(false);
            if (body?["choices"]?[0]?["finish_reason"]?.GetValue<string>() is "length" or "content_filter")
                throw new InvalidOperationException("模型输出被截断，请缩小任务后重试。");
            message = body?["choices"]?[0]?["message"]?.DeepClone().AsObject()
                ?? throw new InvalidOperationException("所选模型服务未返回有效的聊天消息。");
        }
        NormalizeMessage(message);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            onText?.Invoke(message["content"]?.GetValue<string>() ?? "");
        return message;
    }

    private static void NormalizeMessage(JsonObject message)
    {
        if (message["content"] is JsonArray blocks)
            message["content"] = string.Join("\n", blocks.OfType<JsonObject>()
                .Where(block => block["type"]?.GetValue<string>() == "text")
                .Select(block => block["text"]?.GetValue<string>()));
        if (message["content"] is not null &&
            (message["content"] is not JsonValue content || !content.TryGetValue<string>(out _)))
            throw new InvalidOperationException("模型返回了无法识别的消息内容，请检查该模型的工具调用兼容性。");
        if (message["tool_calls"] is not null and not JsonArray)
            throw new InvalidOperationException("模型返回了无效的工具调用列表，尚未执行，请重新发送。");
        if (message["tool_calls"] is JsonArray calls)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                string? id = call?["id"]?.GetValue<string>();
                string? name = call?["function"]?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(name) ||
                    call?["function"]?["arguments"] is not JsonValue arguments ||
                    !arguments.TryGetValue<string>(out _))
                    throw new InvalidOperationException("模型返回了不完整或重复的工具调用，尚未执行，请重新发送。");
            }
        }
        if (message["tool_calls"] is not JsonArray { Count: > 0 } &&
            string.IsNullOrWhiteSpace(message["content"]?.GetValue<string>()))
            throw new InvalidOperationException("模型返回了空消息，请检查所选服务中的模型状态后重试。");
        message["role"] = "assistant";
    }

    private static HttpRequestMessage Request(BridgeConfig config, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, config.BaseUrl + path);
        if (config.ApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Headers.UserAgent.ParseAdd("MikiRhino/" + Version);
        return request;
    }

    private async Task<JsonObject> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        EnsureSuccess(response);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(token).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("所选模型服务返回了空响应。");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"所选模型服务 HTTP {(int)response.StatusCode}。请检查配置的端点、模型和权限。");
    }
}
