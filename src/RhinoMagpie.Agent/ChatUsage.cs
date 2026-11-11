using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed record ChatUsage(long InputTokens, long OutputTokens, long? CachedInputTokens)
{
    public static ChatUsage? Parse(JsonNode? usage)
    {
        if (usage is not JsonObject counts) return null;
        try { return ParseCounts(counts); }
        catch (ArgumentException) { return null; } // JsonObject can reject duplicate fields on first access.
    }

    private static ChatUsage? ParseCounts(JsonObject counts)
    {
        bool chat = counts.ContainsKey("prompt_tokens") || counts.ContainsKey("completion_tokens");
        if (!TryCount(counts[chat ? "prompt_tokens" : "input_tokens"], out long input) ||
            !TryCount(counts[chat ? "completion_tokens" : "output_tokens"], out long output))
            return null;

        long? cached = null;
        if (counts[chat ? "prompt_tokens_details" : "input_tokens_details"] is JsonNode details)
        {
            if (details is not JsonObject tokenDetails) return null;
            if (tokenDetails["cached_tokens"] is JsonNode value)
            {
                if (!TryCount(value, out long count) || count > input) return null;
                cached = count;
            }
        }
        return new ChatUsage(input, output, cached);
    }

    private static bool TryCount(JsonNode? node, out long count)
    {
        count = 0;
        if (node is not JsonValue value || !value.TryGetValue<object>(out var raw)) return false;
        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                return number.TryGetInt64(out count) && count >= 0;
            case byte or sbyte or short or ushort or int or uint or long:
                count = Convert.ToInt64(raw);
                return count >= 0;
            case ulong unsigned when unsigned <= long.MaxValue:
                count = (long)unsigned;
                return true;
            default:
                return false;
        }
    }
}
