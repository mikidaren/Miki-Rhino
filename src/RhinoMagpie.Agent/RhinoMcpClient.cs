using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed class McpRpcException() : Exception("Rhino MCP 拒绝了调用，请检查 Rhino 命令历史。");

public sealed class RhinoMcpClient(HttpClient client, Uri endpoint)
{
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
        using var response = await client.SendAsync(notification, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonArray> ListToolsAsync(CancellationToken token)
    {
        var tools = new JsonArray();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await RpcAsync("tools/list", parameters, token);
            foreach (var tool in result["tools"]?.AsArray() ?? [])
                tools.Add(tool?.DeepClone());
            cursor = result["nextCursor"]?.GetValue<string>();
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
        var id = Interlocked.Increment(ref requestId);
        using var request = CreateRequest(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        });
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Rhino MCP HTTP {(int)response.StatusCode}。请重新连接当前文档。");
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            sessionId = sessions.First();
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        JsonObject? body;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = null;
            foreach (var block in text.Replace("\r\n", "\n").Split("\n\n"))
            {
                string data = string.Join("\n", block.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].TrimStart()));
                if (string.IsNullOrWhiteSpace(data)) continue;
                var candidate = JsonNode.Parse(data)?.AsObject();
                if (candidate?["id"]?.ToString() == id.ToString()) body = candidate;
            }
        }
        else body = JsonNode.Parse(text)?.AsObject();
        if (body is null || body["id"]?.ToString() != id.ToString())
            throw new InvalidOperationException("Rhino MCP 响应编号不匹配。");
        if (body["error"] is not null)
            throw new McpRpcException();
        return body["result"]?.AsObject() ?? throw new InvalidOperationException("Rhino MCP 响应无效。");
    }
}
