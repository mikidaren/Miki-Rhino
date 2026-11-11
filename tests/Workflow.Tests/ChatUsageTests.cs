using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using RhinoMagpie;
using RhinoMagpie.Agent;

static class ChatUsageTests
{
    private const string Start = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}],\"usage\":null}\n\n";
    private const string Finish = "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n";
    private const string Done = "data: [DONE]\n\n";

    public static async Task<int> RunAsync(BridgeConfig config)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }

        var calls = new List<ChatUsage?>();
        using (var handler = new Handler(_ => Reply(HttpStatusCode.OK, Start + Finish + Usage + Done, "text/event-stream")))
        using (var http = new HttpClient(handler))
        {
            var deltas = new List<string>();
            var message = await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
                onText: deltas.Add, onUsage: calls.Add, requestUsage: true);
            Check(handler.Requests.Single()["stream"]!.GetValue<bool>() &&
                handler.Requests[0]["stream_options"]!["include_usage"]!.GetValue<bool>() &&
                calls.SequenceEqual(new[] { new ChatUsage(20, 3, 4) }) &&
                deltas.SequenceEqual(new[] { "hello" }) && message["content"]!.GetValue<string>() == "hello",
                "stream usage after finish_reason is captured without changing the assistant message or deltas");
        }

        calls.Clear();
        using (var handler = new Handler(_ => Reply(HttpStatusCode.OK,
            """
            {"choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":3000000000,"completion_tokens":0}}
            """)))
        using (var http = new HttpClient(handler))
        {
            await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None, onUsage: calls.Add);
            Check(!handler.Requests.Single().ContainsKey("stream_options") &&
                calls.SequenceEqual(new[] { new ChatUsage(3000000000, 0, null) }),
                "ordinary JSON reports provider counts with no stream options by default");
        }

        calls.Clear();
        using (var handler = new Handler(_ => Reply(HttpStatusCode.OK,
            """
            {"choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":"invalid","completion_tokens":2}}
            """)))
        using (var http = new HttpClient(handler))
        {
            var message = await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
                onUsage: calls.Add);
            Check(calls.Count == 1 && calls[0] is null && message["content"]!.GetValue<string>() == "hello",
                "invalid usage metadata leaves a successful message intact and reports unknown usage");
        }

        calls.Clear();
        using (var handler = new Handler(attempt => attempt == 1
            ? Reply(HttpStatusCode.BadRequest, """{"error":{"message":"stream_options.include_usage is not supported"}}""")
            : Reply(HttpStatusCode.OK, Start + Finish + Done, "text/event-stream")))
        using (var http = new HttpClient(handler))
        {
            await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
                onUsage: calls.Add, requestUsage: true);
            var firstRequest = handler.Requests[0].DeepClone().AsObject();
            Check(handler.Requests.Count == 2 && firstRequest.Remove("stream_options") &&
                JsonNode.DeepEquals(firstRequest, handler.Requests[1]) && calls.Count == 1 && calls[0] is null,
                "explicit option rejection retries once with the same payload minus stream options and reports unknown usage");
        }

        calls.Clear();
        using (var handler = new Handler(attempt => attempt == 1
            ? Reply(HttpStatusCode.BadRequest, "Unrecognized request argument supplied: stream_options.", "text/plain")
            : Reply(HttpStatusCode.OK, Start + Finish + Done, "text/event-stream")))
        using (var http = new HttpClient(handler))
        {
            await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
                onUsage: calls.Add, requestUsage: true);
            Check(handler.Requests.Count == 2 && calls.Count == 1 && calls[0] is null,
                "a standalone plain-text option rejection permits one fallback");
        }

        foreach (var (name, reply, attempts) in new (string, Func<HttpResponseMessage>, int)[]
        {
            ("ambiguous HTTP 400", () => Reply(HttpStatusCode.BadRequest,
                """{"error":{"param":"stream_options","message":"Invalid value"}}"""), 1),
            ("unrelated subfield rejection", () => Reply(HttpStatusCode.BadRequest,
                """{"error":{"message":"Unknown parameter: stream_options.other"}}"""), 1),
            ("non-400 option rejection", () => Reply(HttpStatusCode.InternalServerError,
                """{"error":{"message":"stream_options is unsupported"}}"""), 1),
            ("HTTP 400 with stream content type", () => Reply(HttpStatusCode.BadRequest,
                """{"error":{"message":"stream_options is unsupported"}}""", "text/event-stream"), 1),
            ("second explicit rejection", () => Reply(HttpStatusCode.BadRequest,
                """{"error":{"param":"stream_options","code":"unknown_parameter"}}"""), 2),
            ("stream error after usage", () => Reply(HttpStatusCode.OK,
                Start + Finish + Usage + "data: {\"error\":{\"message\":\"stream_options is unsupported\"}}\n\n" + Done,
                "text/event-stream"), 1),
            ("usage without finish_reason", () => Reply(HttpStatusCode.OK, Start + Usage + Done, "text/event-stream"), 1),
            ("truncated finish with usage", () => Reply(HttpStatusCode.OK,
                Start + Finish.Replace("stop", "length") + Usage + Done, "text/event-stream"), 1),
        })
        {
            calls.Clear();
            using var handler = new Handler(_ => reply());
            using var http = new HttpClient(handler);
            bool failed = false;
            try
            {
                await new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
                    onUsage: calls.Add, requestUsage: true);
            }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && handler.Requests.Count == attempts && calls.Count == 0,
                name + " stops after " + attempts + " attempt(s) and produces no usage callback");
        }

        Check(ChatUsage.Parse(JsonNode.Parse("""{"input_tokens":10,"output_tokens":2,"input_tokens_details":{"cached_tokens":0}}""")) ==
            new ChatUsage(10, 2, 0), "explicit zero cached tokens are retained");
        Check(ChatUsage.Parse(new JsonObject { ["prompt_tokens"] = 7, ["completion_tokens"] = 2L }) ==
            new ChatUsage(7, 2, null), "programmatically constructed integer counts are supported");
        Check(new string?[] { null, "[]", """{"total_tokens":2}""", """{"prompt_tokens":-1,"completion_tokens":1}""",
            """{"prompt_tokens":"1","completion_tokens":1}""", """{"prompt_tokens":1.5,"completion_tokens":1}""",
            """{"prompt_tokens":9223372036854775808,"completion_tokens":1}""",
            """{"prompt_tokens":1,"prompt_tokens":2,"completion_tokens":1}""",
            """{"prompt_tokens":1,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":2}}""" }
            .All(value => ChatUsage.Parse(value is null ? null : JsonNode.Parse(value)) is null),
            "missing, malformed, or out-of-range token counts are unknown");
        return passed;
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((await request.Content!.ReadFromJsonAsync<JsonObject>(token))!);
            return respond(Requests.Count);
        }
    }
}
