using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed class McpRpcException() : Exception("Rhino MCP 拒绝了调用，请检查 Rhino 命令历史。");

public sealed class RhinoMcpClient(HttpClient client, Uri endpoint, TimeSpan? requestTimeout = null)
{
    private const int MaxReplySize = 16 * 1024 * 1024;
    private readonly TimeSpan timeout = requestTimeout ?? TimeSpan.FromMinutes(2);
    private long requestId;
    private string? sessionId;

    public async Task InitializeAsync(CancellationToken token)
    {
        await RpcAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "RhinoMagpie", ["version"] = MagpieGateway.Version },
        }, token);
        using var notification = CreateRequest(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized",
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var response = await client.SendAsync(notification, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonArray> ListToolsAsync(CancellationToken token)
    {
        var tools = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        const int MaxPages = 64;
        int pages = 0;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await RpcAsync("tools/list", parameters, token);
            foreach (var tool in result["tools"]?.AsArray() ?? [])
                tools.Add(tool?.DeepClone());
            cursor = result["nextCursor"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(cursor) && (++pages >= MaxPages || !seen.Add(cursor)))
                throw new InvalidOperationException("Rhino MCP 工具分页重复或超出限制。");
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }

    public Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token) =>
        RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }, token);

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonObject> RpcAsync(string method, JsonObject parameters, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        token = deadline.Token;
        var id = Interlocked.Increment(ref requestId);
        using var request = CreateRequest(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Rhino MCP HTTP {(int)response.StatusCode}。请重新连接当前文档。");
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            sessionId = sessions.First();
        JsonObject? body = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? await ReadEventResultAsync(response.Content, id, token).ConfigureAwait(false)
            : await ReadJsonResultAsync(response.Content, token).ConfigureAwait(false);
        if (body is null || !IsReply(body, id))
            throw new InvalidOperationException("Rhino MCP 响应编号不匹配。");
        if (body["error"] is not null)
            throw new McpRpcException();
        return body["result"]?.AsObject() ?? throw new InvalidOperationException("Rhino MCP 响应无效。");
    }

    private static bool IsReply(JsonObject candidate, long expectedId) =>
        candidate["jsonrpc"]?.ToString() == "2.0" &&
        candidate["id"] is JsonValue value && value.TryGetValue<long>(out long id) && id == expectedId &&
        !candidate.ContainsKey("method") && (candidate.ContainsKey("result") != candidate.ContainsKey("error"));

    private static async Task<JsonObject?> ReadJsonResultAsync(HttpContent content, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (json.Length + read > MaxReplySize) throw new InvalidOperationException("Rhino MCP 响应过大。");
            json.Write(buffer, 0, read);
        }
        return JsonNode.Parse(json.GetBuffer().AsSpan(0, (int)json.Length)) as JsonObject;
    }

    private static async Task<JsonObject?> ReadEventResultAsync(HttpContent content, long expectedId, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        long consumed = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) return null;
            consumed += line.Length + 1;
            if (consumed > MaxReplySize) throw new InvalidOperationException("Rhino MCP 响应过大。");
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (JsonNode.Parse(data.ToString()) is JsonObject candidate &&
                        IsReply(candidate, expectedId)) return candidate;
                    data.Clear();
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (data.Length > 0) data.Append('\n');
                data.Append(value.StartsWith(' ') ? value[1..] : value);
            }
        }
    }
}
