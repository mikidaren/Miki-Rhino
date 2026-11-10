using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RhinoMagpie.Agent;

static class McpTransportTests
{
    public static async Task<int> RunAsync()
    {
        using var keepAlive = new HttpClient(new Handler((id, _) =>
        {
            var eventStream = new OpenStream(
                ": keepalive\r\n\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\r\n\r\n" +
                $"data: {{\"jsonrpc\":\"2.0\",\r\ndata: \"id\":{id},\"result\":{{\"ok\":true}}}}\r\n\r\n");
            return Sse(eventStream);
        }));
        var client = new RhinoMcpClient(keepAlive, new Uri("http://localhost:10500/agent"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if ((await client.CallToolAsync("get_context", new(), deadline.Token))["ok"]?.GetValue<bool>() != true)
            throw new Exception("MCP did not return the matching SSE event before EOF.");
        Console.WriteLine("PASS open SSE response returns on matching event and skips notifications");

        using var pending = new HttpClient(new Handler((_, _) => Sse(new OpenStream(": waiting\n\n"))));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await new RhinoMcpClient(pending, new Uri("http://localhost:10500/agent"))
                .CallToolAsync("get_context", new(), cancel.Token);
            throw new Exception("MCP ignored cancellation while waiting for an event.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Console.WriteLine("PASS waiting SSE call observes cancellation");

        try
        {
            await new RhinoMcpClient(pending, new Uri("http://localhost:10500/agent"), TimeSpan.FromMilliseconds(150))
                .CallToolAsync("run_csharp", new(), CancellationToken.None);
            throw new Exception("MCP ignored the response deadline after receiving headers.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS MCP enforces response deadline even without a caller token");

        int requests = 0;
        using var paging = new HttpClient(new Handler((id, _) =>
        {
            if (++requests > 2) throw new Exception("Unexpected third call on a repeated cursor.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[],\"nextCursor\":\"same\"}}}}",
                    Encoding.UTF8, "application/json")
            };
        }));
        try
        {
            await new RhinoMcpClient(paging, new Uri("http://localhost:10500/agent")).ListToolsAsync(default);
            throw new Exception("MCP accepted repeated pagination cursor.");
        }
        catch (InvalidOperationException) when (requests == 2) { }
        Console.WriteLine("PASS duplicate MCP cursor fails without another request");
        return 4;
    }

    private static HttpResponseMessage Sse(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } },
    };

    private sealed class Handler(Func<long, JsonObject, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            return handle(body["id"]!.GetValue<long>(), body);
        }
    }

    private sealed class OpenStream(string initial) : MemoryStream(Encoding.UTF8.GetBytes(initial))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, token);
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    }
}
