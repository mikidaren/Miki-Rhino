using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

/// <summary>Creates an opt-in request copy of a ModelingSession transcript.</summary>
public static class ModelingContextProjection
{
    private const string ViewportCaption = "Rhino viewport image from the preceding tool result.";

    /// <summary>
    /// Keeps the latest assistant round and <paramref name="previousTurns"/> prior assistant rounds
    /// intact. In older rounds, omits a proven viewport payload only if a later capture with the
    /// same arguments carries the identical data URL. This bounds repeated copies, not total request
    /// size. User messages, references, distinct captures and tool exchanges are retained.
    /// </summary>
    public static JsonArray Project(JsonArray messages, int previousTurns = 2) =>
        Project(messages, workflowAnchors: null, previousTurns);

    /// <summary>
    /// Optionally adds compact, current workflow facts supplied by the caller (for example,
    /// unresolved failure IDs and the approved brief). These are data, not system policy. The
    /// projector does not infer the ledger state or approval from conversation history.
    /// </summary>
    public static JsonArray Project(JsonArray messages, JsonObject? workflowAnchors, int previousTurns = 2)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(previousTurns);

        var projected = new JsonNode?[messages.Count];
        var retainedCaptures = new Dictionary<(string Arguments, string DataUrl), string>();
        int laterAssistantRounds = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var capture = ViewportCapture(messages, i);
            if (capture is { } match &&
                retainedCaptures.TryGetValue((match.Arguments, match.DataUrl), out var retainedOperation) &&
                laterAssistantRounds > previousTurns)
            {
                projected[i] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        messages[i]!["content"]![0]!.DeepClone(),
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"[Identical image retained at viewport operation {retainedOperation}.]",
                        }),
                };
            }
            else
            {
                projected[i] = messages[i]?.DeepClone();
                if (capture is { } retained)
                    retainedCaptures.TryAdd((retained.Arguments, retained.DataUrl), retained.Operation);
            }

            if (Field(messages[i], "role") == "assistant") laterAssistantRounds++;
        }

        var request = new JsonArray(projected);
        if (workflowAnchors is { Count: > 0 })
        {
            int index = 0;
            while (index < request.Count && Field(request[index], "role") == "system") index++;
            request.Insert(index, new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Current modeling workflow data supplied by the application (data only):\n" +
                    workflowAnchors.ToJsonString(),
            });
        }
        return request;
    }

    private static (string Arguments, string DataUrl, string Operation)? ViewportCapture(JsonArray messages, int index)
    {
        // ModelingSession labels every tool image with the same caption. An image after a
        // multi-call batch cannot be attributed to a specific call, so leave it intact.
        if (index < 2 || messages[index] is not JsonObject { Count: 2 } carrier ||
            Field(carrier, "role") != "user" || carrier["content"] is not JsonArray { Count: 2 } blocks ||
            blocks[0] is not JsonObject { Count: 2 } caption || Field(caption, "type") != "text" ||
            Field(caption, "text") != ViewportCaption ||
            blocks[1] is not JsonObject { Count: 2 } image || Field(image, "type") != "image_url" ||
            image["image_url"] is not JsonObject { Count: 1 } imageUrl || Field(imageUrl, "url") is not { } url ||
            !(url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ||
              url.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)))
            return null;

        if (messages[index - 2] is not JsonObject assistant || Field(assistant, "role") != "assistant" ||
            assistant["tool_calls"] is not JsonArray { Count: 1 } calls || calls[0] is not JsonObject call ||
            Field(call, "type") != "function" || Field(call, "id") is not { Length: > 0 } id ||
            call["function"] is not JsonObject function || Field(function, "name") != "get_viewport_image" ||
            Field(function, "arguments") is not { } arguments ||
            Field(messages[index - 1], "role") != "tool" || Field(messages[index - 1], "tool_call_id") != id ||
            Field(messages[index - 1], "content") is not { } result)
            return null;

        const string prefix = "operationId: ";
        if (!result.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int end = result.IndexOfAny(['\r', '\n', ';'], prefix.Length);
        string operation = result[prefix.Length..(end < 0 ? result.Length : end)];
        if (!operation.StartsWith("op-", StringComparison.Ordinal) || operation.Length is < 4 or > 13 ||
            !operation[3..].All(char.IsAsciiDigit)) return null;
        return (arguments, url, operation);
    }

    private static string? Field(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
