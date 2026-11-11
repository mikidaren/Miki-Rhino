using System.Text.Json.Nodes;
using RhinoMagpie.Agent;

static class ModelingContextProjectionTests
{
    private const string Caption = "Rhino viewport image from the preceding tool result.";
    private const string ImageA = "data:image/png;base64,AAAA";
    private const string ImageB = "data:image/png;base64,BBBB";

    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }

        var history = new JsonArray(
            Message("system", "Initial modeling policy"),
            Message("user", "Approved design: keep the east stair"),
            ImageMessage("User reference: east stair; kind=drawing; width in document units=2400.", ImageA),
            Calls(Call("failure", "run_csharp")),
            Result("failure", "ERROR\noperationId: op-1; outstanding failureId: op-1"));
        int first = Capture(history, "capture-1", ImageA);
        history.Add(Message("user", "Continue the approved design"));
        int second = Capture(history, "capture-2", ImageA);
        int third = Capture(history, "capture-3", ImageA);
        int current = Capture(history, "capture-4", ImageA);
        var original = history.DeepClone();

        var projected = ModelingContextProjection.Project(history);
        Check(!HasImage(projected[first]) && HasImage(projected[second]) && HasImage(projected[third]) &&
            HasImage(projected[current]), "default retains the current and previous two model rounds within repeated tool exchanges");
        Check(projected.Count == history.Count && Enumerable.Range(0, history.Count).Where(i => i != first)
            .All(i => JsonNode.DeepEquals(history[i], projected[i])) && projected[first]?["role"]?.GetValue<string>() == "user" &&
            projected[first]?["content"]?[0]?["text"]?.GetValue<string>() == Caption,
            "policy, brief, reference, failure IDs, call/response pairs and message positions survive unchanged");
        Check(JsonNode.DeepEquals(history, original), "projection does not edit the source transcript");
        projected[0]!["content"] = "edited copy";
        Check(JsonNode.DeepEquals(history, original), "projected messages do not share mutable nodes with the source");

        projected = ModelingContextProjection.Project(history, previousTurns: 0);
        Check(!HasImage(projected[first]) && !HasImage(projected[second]) && !HasImage(projected[third]) &&
            HasImage(projected[current]) && JsonNode.DeepEquals(projected, ModelingContextProjection.Project(projected, 0)),
            "zero previous rounds still protects the latest image and reprojection is stable");

        var alternating = new JsonArray();
        int oldA = Capture(alternating, "old-a", ImageA);
        int oldB = Capture(alternating, "old-b", ImageB);
        int newA = Capture(alternating, "new-a", ImageA);
        int newB = Capture(alternating, "new-b", ImageB);
        Capture(alternating, "current", "data:image/png;base64,CCCC");
        var alternatingCopy = ModelingContextProjection.Project(alternating);
        Check(alternatingCopy[oldA]?["content"]?[1]?["text"]?.GetValue<string>() ==
                "[Identical image retained at viewport operation op-7.]" && HasImage(alternatingCopy[newA]) &&
            alternatingCopy[oldB]?["content"]?[1]?["text"]?.GetValue<string>() ==
                "[Identical image retained at viewport operation op-10.]" && HasImage(alternatingCopy[newB]),
            "interleaved duplicate images point to their own retained operation IDs");

        var mixed = new JsonArray(Message("user", "Compare the original viewport against the current model"));
        int distinct = Capture(mixed, "before", ImageB);
        int otherView = Capture(mixed, "other-view", ImageA, "{\"view\":\"Top\"}");
        mixed.Add(Calls(Call("generic", "openbim_inspect")));
        mixed.Add(Result("generic"));
        mixed.Add(ImageMessage(Caption, ImageA));
        mixed.Add(Calls(Call("parallel-view"), Call("parallel-other", "openbim_inspect")));
        mixed.Add(Result("parallel-view"));
        mixed.Add(Result("parallel-other"));
        mixed.Add(ImageMessage(Caption, ImageA));
        mixed.Add(Calls(Call("wrong-id")));
        mixed.Add(Result("mismatched-response"));
        mixed.Add(ImageMessage(Caption, ImageA));
        mixed.Add(Calls(Call("failed-view")));
        mixed.Add(Result("failed-view", "ERROR\nCapture failed"));
        mixed.Add(ImageMessage(Caption, ImageA));
        Capture(mixed, "current-view", ImageA);
        Check(HasImage(mixed[distinct]) && HasImage(mixed[otherView]) &&
            JsonNode.DeepEquals(mixed, ModelingContextProjection.Project(mixed, 0)),
            "different bytes or view arguments, non-viewport tools, ambiguous batches, mismatched IDs and failed captures are retained");

        var anchors = new JsonObject
        {
            ["unresolvedFailures"] = new JsonArray(new JsonObject { ["failureId"] = "op-1", ["stage"] = "openings" }),
            ["approvedBrief"] = new JsonObject { ["requirement"] = "keep the east stair" },
        };
        var withAnchors = ModelingContextProjection.Project(history, anchors);
        string anchorContent = withAnchors[1]!["content"]!.GetValue<string>();
        int payloadStart = anchorContent.IndexOf('\n');
        Check(withAnchors.Count == history.Count + 1 && JsonNode.DeepEquals(withAnchors[0], history[0]) &&
            withAnchors[1]!["role"]!.GetValue<string>() == "user" && payloadStart >= 0 &&
            JsonNode.DeepEquals(JsonNode.Parse(anchorContent[(payloadStart + 1)..]), anchors) &&
            JsonNode.DeepEquals(withAnchors[^1], history[^1]),
            "caller supplies structured workflow anchors without changing the policy or current exchange");

        string payload = "data:image/png;base64," + Convert.ToBase64String(new byte[192 * 1024]);
        var repeated = new JsonArray(Message("user", "Inspect the viewport"));
        for (int i = 0; i < 8; i++) Capture(repeated, "repeat-" + i, payload);
        var compact = ModelingContextProjection.Project(repeated);
        int fullBytes = repeated.ToJsonString().Length; // ASCII fixture: characters equal UTF-8 bytes.
        int compactBytes = compact.ToJsonString().Length;
        Check(compact.Count(HasImage) == 3 && fullBytes - compactBytes > 1_300_000,
            "eight repeated 256 KiB payloads retain three copies and remove over 1.3 MB from the request");
        Console.WriteLine($"Synthetic message JSON: {fullBytes:N0} -> {compactBytes:N0} bytes");
        return passed;
    }

    private static JsonObject Message(string role, JsonNode content) => new() { ["role"] = role, ["content"] = content };

    private static JsonObject ImageMessage(string caption, string url) => Message("user", new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = caption },
        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } }));

    private static JsonObject Call(string id, string name = "get_viewport_image", string arguments = "{}") => new()
    {
        ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    };

    private static JsonObject Calls(params JsonObject[] calls) => new()
    {
        ["role"] = "assistant", ["tool_calls"] = new JsonArray(calls),
    };

    private static JsonObject Result(string id, string content = "operationId: op-1") => new()
    {
        ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content,
    };

    private static int Capture(JsonArray messages, string id, string url, string arguments = "{}")
    {
        int operation = messages.Count + 1;
        messages.Add(Calls(Call(id, arguments: arguments)));
        messages.Add(Result(id, $"operationId: op-{operation}\nCaptured"));
        messages.Add(ImageMessage(Caption, url));
        return messages.Count - 1;
    }

    private static bool HasImage(JsonNode? message) => message?["content"] is JsonArray content &&
        content.Any(block => block?["type"]?.GetValue<string>() == "image_url");
}
