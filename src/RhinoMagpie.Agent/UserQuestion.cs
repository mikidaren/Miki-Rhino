using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed record UserQuestion(string Text, IReadOnlyList<string> Options, bool AllowMultiple)
{
    public static IReadOnlyList<UserQuestion> Parse(JsonObject arguments)
    {
        var questions = arguments["questions"] as JsonArray;
        if (questions is not { Count: > 0 })
            throw new InvalidOperationException("Ask at least one question with its choices.");
        var result = new List<UserQuestion>();
        foreach (var question in questions)
        {
            string? text = question?["question"]?.GetValue<string>();
            var options = (question?["options"] as JsonArray)?.Select(option => option?.GetValue<string>())
                .Where(option => !string.IsNullOrWhiteSpace(option)).Cast<string>()
                .Distinct(StringComparer.Ordinal).ToList();
            if (string.IsNullOrWhiteSpace(text) || options is not { Count: > 0 })
                throw new InvalidOperationException("Each question needs nonblank text and at least one choice.");
            result.Add(new UserQuestion(text, options, question?["multiSelect"]?.GetValue<bool>() == true));
        }
        return result;
    }
}
