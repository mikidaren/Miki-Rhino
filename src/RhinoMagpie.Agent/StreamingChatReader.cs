using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public static class StreamingChatReader
{
    public static async Task<JsonObject> ReadAsync(
        Stream stream, Action<string>? onText, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        var eventData = new StringBuilder();
        string? finishReason = null;
        bool done = false;
        while (!done)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is not null && line.Length != 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0) eventData.Append('\n');
                    eventData.Append(line[5..].TrimStart());
                }
                continue;
            }
            if (eventData.Length > 0)
            {
                string data = eventData.ToString();
                eventData.Clear();
                if (data == "[DONE]") done = true;
                else
                {
                    var chunk = JsonNode.Parse(data)?.AsObject()
                        ?? throw new JsonException("Empty stream event.");
                    if (chunk["error"] is not null)
                        throw new InvalidOperationException("Magpie 流式请求中断，请检查模型和策略的用量记录。");
                    if (chunk["choices"] is JsonArray { Count: > 0 } choices)
                    {
                        var choice = choices[0]!;
                        if (choice["finish_reason"] is JsonValue reason)
                            finishReason = reason.GetValue<string>();
                        var delta = choice["delta"];
                        if (delta?["content"] is JsonValue content && content.TryGetValue<string>(out var value))
                        {
                            text.Append(value);
                            onText?.Invoke(value);
                        }
                        if (delta?["tool_calls"] is JsonArray fragments)
                            foreach (var fragment in fragments.OfType<JsonObject>())
                                AppendToolCall(calls, fragment);
                    }
                }
            }
            if (line is null) break;
        }
        if (finishReason is null)
            throw new InvalidOperationException("模型流在完成前中断，已收到的文字会保留，工具尚未执行。");
        if (finishReason is "length" or "content_filter")
            throw new InvalidOperationException("模型输出被截断，工具尚未执行，请缩小当前步骤后重试。");
        var result = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0)
            result["tool_calls"] = new JsonArray(calls.Values.Select(call => (JsonNode)call).ToArray());
        return result;
    }

    private static void AppendToolCall(SortedDictionary<int, JsonObject> calls, JsonObject fragment)
    {
        int index = fragment["index"]?.GetValue<int>()
            ?? throw new InvalidOperationException("模型返回了缺少索引的工具调用片段。");
        if (index < 0 || index > 127)
            throw new InvalidOperationException("模型返回了无效的工具调用索引。");
        if (!calls.TryGetValue(index, out var call))
        {
            call = new JsonObject
            {
                ["id"] = "", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" },
            };
            calls.Add(index, call);
        }
        if (fragment["id"] is JsonValue id)
        {
            string incoming = id.GetValue<string>();
            string current = call["id"]!.GetValue<string>();
            if (current.Length > 0 && current != incoming)
                throw new InvalidOperationException("工具调用标识在流式输出中发生变化，尚未执行。");
            call["id"] = incoming;
        }
        if (fragment["function"] is JsonObject function)
            foreach (string property in new[] { "name", "arguments" })
                if (function[property] is JsonValue part)
                    call["function"]![property] =
                        call["function"]![property]!.GetValue<string>() + part.GetValue<string>();
    }
}
