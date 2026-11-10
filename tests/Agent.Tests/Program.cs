using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RhinoMagpie;
using RhinoMagpie.Agent;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}

if (args.FirstOrDefault() == "--live")
{
    string endpoint = args[1];
    string prompt = File.ReadAllText(args[2]);
    var config = MagpieGateway.ReadConfig();
    if (args.Length > 3) config = config with { Model = args[3] };
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(8),
    };
    using var mcpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    Console.WriteLine("MODEL " + config.Model);
    await new ModelingSession().RunAsync(prompt, config, new MagpieGateway(client),
        new RhinoMcpClient(mcpClient, new Uri(endpoint)),
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult("Use the explicitly requested test dimensions."),
        (role, text) => Console.WriteLine(role + "\n" + text), CancellationToken.None);
    return;
}

var fixtureConfig = new BridgeConfig("group/custom-routing", "http://localhost:3425", "fixture-secret");
var handler = new FixtureHandler();
using var http = new HttpClient(handler);
var gateway = new MagpieGateway(http);
var mcp = new RhinoMcpClient(http, new Uri("http://localhost:10500/agent"));
var session = new ModelingSession();
var approvals = new List<ToolInvocation>();
await session.RunAsync("Build test geometry", fixtureConfig, gateway, mcp,
    (call, _) => { approvals.Add(call); return Task.FromResult(true); },
    (_, _) => Task.FromResult("answer"), (_, _) => { }, CancellationToken.None);
string modelingInstructions = handler.ChatRequests[0]["messages"]![0]!["content"]!.GetValue<string>();
Check(modelingInstructions.Contains("NURBS") && modelingInstructions.Contains("closed Brep") &&
    modelingInstructions.Contains("Extrusion"), "default modeling instructions prefer editable curves, surfaces and native solids");
Check(modelingInstructions.Contains("construction curves") && modelingInstructions.Contains("explicitly requests Mesh/SubD"),
    "geometry defaults retain construction curves and allow explicit mesh requests");
Check(handler.Models.All(model => model == "group/custom-routing"), "group ID preserved for every request");
Check(handler.Authorization.All(value => value == "Bearer fixture-secret"), "gateway authorization is sent");
Check(handler.Executed.SequenceEqual(new[] { "get_viewport_image", "run_python" }), "MCP tools execute sequentially");
Check(approvals.Count == 2 && approvals[0].ReadOnly && !approvals[1].ReadOnly, "MCP annotation controls approval");
var continuation = handler.ChatRequests[1]["messages"]!.AsArray();
Check(continuation[3]!["role"]!.GetValue<string>() == "tool" &&
      continuation[4]!["role"]!.GetValue<string>() == "tool" &&
      continuation[5]!["role"]!.GetValue<string>() == "user", "all tool responses precede image content");
Check(continuation[5]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>().StartsWith("data:image/png;base64,"),
    "viewport image forwarded to model");
Check(handler.ChatRequests[0]["tools"]!.AsArray().All(tool =>
    ModelingSession.IsAllowedTool(tool!["function"]!["name"]!.GetValue<string>())), "internal and close tools excluded");

handler.FinalOnly = true;
await session.RunAsync("Continue", fixtureConfig with { Model = "auto/new-strategy", ApiKey = "new-fixture-key" },
    gateway, mcp, (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""),
    (_, _) => { }, CancellationToken.None);
Check(handler.Models.Last() == "auto/new-strategy", "strategy change applies on next turn");
Check(handler.Authorization.Last() == "Bearer new-fixture-key", "rotated credential applies on next turn");
Check(handler.ChatRequests.Last()["messages"]!.AsArray().Count > 6, "conversation history continues");

var deniedHandler = new FixtureHandler();
using var deniedHttp = new HttpClient(deniedHandler);
await new ModelingSession().RunAsync("Build", fixtureConfig, new MagpieGateway(deniedHttp),
    new RhinoMcpClient(deniedHttp, new Uri("http://localhost:10500/agent")),
    (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
Check(deniedHandler.Executed.Count == 0, "declined tools do not reach Rhino");
Check(deniedHandler.ChatRequests[1].ToJsonString().Contains("user declined"), "denial is returned to the model");

using var cancellation = new CancellationTokenSource();
var cancelHandler = new FixtureHandler { AfterTool = () => cancellation.Cancel() };
using var cancelHttp = new HttpClient(cancelHandler);
var cancelSession = new ModelingSession();
try
{
    await cancelSession.RunAsync("Build", fixtureConfig, new MagpieGateway(cancelHttp),
        new RhinoMcpClient(cancelHttp, new Uri("http://localhost:10500/agent")),
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, cancellation.Token);
    throw new Exception("Cancellation ignored");
}
catch (OperationCanceledException) { Check(true, "cancellation stops before dispatching the next tool"); }
cancelHandler.FinalOnly = true;
cancelHandler.AfterTool = null;
await cancelSession.RunAsync("Inspect before continuing", fixtureConfig, new MagpieGateway(cancelHttp),
    new RhinoMcpClient(cancelHttp, new Uri("http://localhost:10500/agent")),
    (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
var resumed = cancelHandler.ChatRequests.Last()["messages"]!.AsArray();
Check(resumed.Count(message => message?["role"]?.GetValue<string>() == "tool") == 2,
    "interrupted turn retains a result for every pending tool call");
Check(resumed.ToJsonString().Contains("Turn interrupted"), "interruption is recorded for the model");

var errorHandler = new FixtureHandler { FailGateway = true };
using var errorHttp = new HttpClient(errorHandler);
try
{
    await new MagpieGateway(errorHttp).CompleteAsync(fixtureConfig, [], [], CancellationToken.None);
    throw new Exception("HTTP error ignored");
}
catch (InvalidOperationException error)
{
    Check(error.Message.Contains("401") && !error.Message.Contains("fixture-secret"),
        "gateway HTTP failure is reported without echoing its body or credential");
}
var brokenHandler = new FixtureHandler { FailTool = true };
using var brokenHttp = new HttpClient(brokenHandler);
var brokenSession = new ModelingSession();
for (int attempt = 0; attempt < 2; attempt++)
{
    try
    {
        await brokenSession.RunAsync("Build", fixtureConfig, new MagpieGateway(brokenHttp),
            new RhinoMcpClient(brokenHttp, new Uri("http://localhost:10500/agent")),
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
        throw new Exception("Unconfirmed tool completion was ignored");
    }
    catch (UnconfirmedToolException)
    {
        Check(brokenSession.ToolCompletionUnknown, "unconfirmed tool completion blocks the session");
    }
}
Check(brokenHandler.ChatRequests.Count == 1, "blocked session cannot dispatch a retry or another model request");

foreach (string failure in new[] { "invalid-json", "wrong-id", "http-error" })
{
    var protocolHandler = new FixtureHandler { ToolFailure = failure };
    using var protocolHttp = new HttpClient(protocolHandler);
    var protocolSession = new ModelingSession();
    try
    {
        await protocolSession.RunAsync("Build", fixtureConfig, new MagpieGateway(protocolHttp),
            new RhinoMcpClient(protocolHttp, new Uri("http://localhost:10500/agent")),
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
        throw new Exception("Unknown MCP result was ignored");
    }
    catch (UnconfirmedToolException)
    {
        Check(protocolSession.ToolCompletionUnknown && protocolHandler.Executed.Count == 1,
            failure + " blocks further tools when completion is unknown");
    }
}

var refusedHandler = new FixtureHandler { ToolFailure = "rpc-error" };
using var refusedHttp = new HttpClient(refusedHandler);
var refusedSession = new ModelingSession();
await refusedSession.RunAsync("Inspect", fixtureConfig, new MagpieGateway(refusedHttp),
    new RhinoMcpClient(refusedHttp, new Uri("http://localhost:10500/agent")),
    (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
Check(!refusedSession.ToolCompletionUnknown && refusedSession.Outcome == "paused" &&
    refusedHandler.ChatRequests.Count is >= 2 and <= 5,
    "explicit MCP refusal is known, explained and kept paused until the failed change is repaired");

var blockHandler = new FixtureHandler
{
    FirstMessage = JsonNode.Parse("""{"role":"assistant","content":[{"type":"text","text":"可读的回答"}]}""")!.AsObject(),
};
using var blockHttp = new HttpClient(blockHandler);
var blockMessage = await new MagpieGateway(blockHttp).CompleteAsync(fixtureConfig, [], [], CancellationToken.None);
Check(blockMessage["content"]!.GetValue<string>() == "可读的回答", "text content blocks display as readable model output");

foreach (string invalid in new[]
{
    """{"role":"assistant","content":null,"tool_calls":[{"id":"same","function":{"name":"run_python","arguments":"{}"}},{"id":"same","function":{"name":"run_python","arguments":"{}"}}]}""",
    """{"role":"assistant","content":null,"tool_calls":[{"function":{"name":"run_python","arguments":"{}"}}]}""",
    """{"role":"assistant","content":null,"tool_calls":{"id":"bad"}}""",
    """{"role":"assistant","content":""}""",
})
{
    var invalidHandler = new FixtureHandler { FirstMessage = JsonNode.Parse(invalid)!.AsObject() };
    using var invalidHttp = new HttpClient(invalidHandler);
    var invalidSession = new ModelingSession();
    try
    {
        await invalidSession.RunAsync("Build", fixtureConfig, new MagpieGateway(invalidHttp),
            new RhinoMcpClient(invalidHttp, new Uri("http://localhost:10500/agent")),
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
        throw new Exception("Malformed model response was accepted");
    }
    catch (InvalidOperationException)
    {
        Check(invalidHandler.Executed.Count == 0, "invalid model response cannot execute geometry tools");
    }
    invalidHandler.FinalOnly = true;
    await invalidSession.RunAsync("Try again", fixtureConfig, new MagpieGateway(invalidHttp),
        new RhinoMcpClient(invalidHttp, new Uri("http://localhost:10500/agent")),
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
    Check(invalidHandler.ChatRequests.Last()["messages"]!.AsArray().All(message =>
        message?["role"]?.GetValue<string>() != "assistant" || message["tool_calls"] is null),
        "rejected response does not poison the next conversation turn");
}

var questions = UserQuestion.Parse(JsonNode.Parse("""
    {"questions":[
      {"question":"选择屋顶","options":["平屋顶","坡屋顶","平屋顶"]},
      {"question":"选择材料","options":["木材","钢材"],"multiSelect":true}
    ]}
    """)!.AsObject());
Check(questions.Count == 2 && questions[0].Options.Count == 2 && !questions[0].AllowMultiple &&
    questions[1].AllowMultiple, "user questions preserve readable choices and multiple-selection behavior");
try
{
    UserQuestion.Parse(JsonNode.Parse("""{"questions":[{"question":"材料","options":[" "]}]}""")!.AsObject());
    throw new Exception("Blank choices accepted");
}
catch (InvalidOperationException) { Check(true, "empty question choices are rejected before opening a dialog"); }

string freshProfile = Path.Combine(Path.GetTempPath(), "rhinomagpie-install-" + Guid.NewGuid().ToString("N"));
try
{
    string directory = MagpieGateway.EnsureConfigurationDirectory(freshProfile);
    Check(Directory.Exists(directory) && directory == Path.Combine(freshProfile, ".config", "miki-rhino"),
        "fresh plugin startup creates the Magpie detection directory without an installer script");
    Check(Directory.GetFiles(directory).Length == 0, "startup creates no credential or placeholder config");
    string settings = Path.Combine(directory, "settings.json");
    File.WriteAllText(settings, """{"model":"group/existing","apiKey":"fixture-private","keep":{"setting":1}}""");
    MagpieGateway.EnsureConfigurationDirectory(freshProfile);
    Check(File.ReadAllText(settings).Contains("fixture-private") &&
        File.ReadAllText(settings).Contains("\"keep\""), "repeated startup preserves existing connection settings");
}
finally
{
    if (Directory.Exists(freshProfile)) Directory.Delete(freshProfile, true);
}
var failureLedger = new ProjectWorkflow();
failureLedger.SetBasis(new JsonObject { ["source"] = "user_dimensions", ["note"] = "12m x 8m" },
    new ModelingRunOptions(ExplicitScale: true));
failureLedger.ObserveTool("run_csharp", false, false);
failureLedger.ObserveTool("set_layer_material", false, true);
failureLedger.ObserveTool("list_objects", true, true);
bool maskedFailureRejected = false;
try
{
    failureLedger.UpdatePlan(JsonNode.Parse("""
        {"goal":"Facade","steps":[{"id":"facade","title":"Openings","acceptance":"Openings verified","status":"complete"}]}
        """)!.AsObject());
}
catch (InvalidOperationException) { maskedFailureRejected = true; }
Check(maskedFailureRejected, "unrelated material success cannot mask a failed geometry operation");
string preferencesTestDirectory = Path.Combine(Path.GetTempPath(), "rhino-execution-mode-" + Guid.NewGuid().ToString("N"));
string preferencesTestPath = Path.Combine(preferencesTestDirectory, "panel-preferences.json");
try
{
    Check(!PanelPreferences.Read(preferencesTestPath).AutoExecute, "fresh profiles default to tool confirmation");
    new PanelPreferences(true).Save(preferencesTestPath);
    Check(PanelPreferences.Read(preferencesTestPath).AutoExecute, "YOLO persists across new preference instances");
    new PanelPreferences(false).Save(preferencesTestPath);
    Check(!PanelPreferences.Read(preferencesTestPath).AutoExecute, "switching back to confirmation persists");
    Check(Directory.GetFiles(preferencesTestDirectory).Length == 1, "saving execution mode leaves no temporary files");
    File.WriteAllText(preferencesTestPath, "{broken");
    try
    {
        PanelPreferences.Read(preferencesTestPath);
        throw new Exception("Invalid execution-mode settings accepted");
    }
    catch (System.Text.Json.JsonException) { Check(true, "malformed preferences never implicitly enable YOLO"); }
}
finally
{
    if (File.Exists(preferencesTestPath)) File.Delete(preferencesTestPath);
    if (Directory.Exists(preferencesTestDirectory)) Directory.Delete(preferencesTestDirectory);
}
passed += IfcRuntimeTests.Run();
passed += IfcDiagnosticTests.Run();
passed += await McpTransportTests.RunAsync();
Console.WriteLine($"{passed} agent checks passed");

sealed class FixtureHandler : HttpMessageHandler
{
    public List<string> Models { get; } = [];
    public List<string> Authorization { get; } = [];
    public List<JsonObject> ChatRequests { get; } = [];
    public List<string> Executed { get; } = [];
    public bool FinalOnly { get; set; }
    public bool FailGateway { get; set; }
    public bool FailTool { get; set; }
    public Action? AfterTool { get; set; }
    public string? ToolFailure { get; set; }
    public JsonObject? FirstMessage { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        if (request.RequestUri!.AbsolutePath == "/v1/chat/completions")
        {
            Models.Add(body["model"]!.GetValue<string>());
            Authorization.Add(request.Headers.Authorization!.ToString());
            ChatRequests.Add(body);
            if (FailGateway)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("fixture-secret") };
            JsonObject message = ChatRequests.Count == 1 && FirstMessage is not null
                ? FirstMessage.DeepClone().AsObject()
                : ChatRequests.Count > 1 || FinalOnly
                ? new() { ["role"] = "assistant", ["content"] = "Verified." }
                : JsonNode.Parse("""
                {"role":"assistant","content":null,"tool_calls":[
                  {"id":"image","type":"function","function":{"name":"get_viewport_image","arguments":"{}"}},
                  {"id":"build","type":"function","function":{"name":"run_python","arguments":"{\"script\":\"test\"}"}}
                ]}
                """)!.AsObject();
            return Response(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = "stop" }),
            });
        }
        string method = body["method"]!.GetValue<string>();
        if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.NoContent);
        JsonObject result;
        if (method == "initialize")
            result = new JsonObject { ["protocolVersion"] = "2025-03-26" };
        else if (method == "tools/list")
        {
            var tools = new JsonArray();
            foreach (string name in new[] { "get_viewport_image", "run_python", "_router_quit_app", "close_doc" })
                tools.Add(new JsonObject
                {
                    ["name"] = name, ["description"] = name,
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = name == "get_viewport_image" },
                });
            result = new JsonObject { ["tools"] = tools };
        }
        else
        {
            string name = body["params"]!["name"]!.GetValue<string>();
            Executed.Add(name);
            if (FailTool) throw new HttpRequestException("Fixture connection lost after dispatch.");
            if (ToolFailure == "invalid-json")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{invalid") };
            if (ToolFailure == "wrong-id")
                return Response(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 999999, ["result"] = new JsonObject() });
            if (ToolFailure == "http-error")
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            if (ToolFailure == "rpc-error")
                return Response(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = body["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Refused before execution" },
                });
            AfterTool?.Invoke();
            result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Done" }),
            };
            if (name == "get_viewport_image")
                result["content"]!.AsArray().Add(new JsonObject
                {
                    ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "iVBORw==",
                });
        }
        return Response(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result });
    }

    private static HttpResponseMessage Response(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body),
    };
}
