using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public static class PlanReviewContract
{
    public const string CheckVersion = "miki.plan-review/0.1";

    public static JsonObject NormalizeBrief(JsonObject profile, IEnumerable<string> knownSpaces)
    {
        if (profile.Count != 2 || profile["transitSpaces"] is not JsonArray transit ||
            profile["areaTargetsM2"] is not JsonObject targets)
            throw new InvalidOperationException("任务书只接受 transitSpaces 和 areaTargetsM2；未指定目标可用空对象。");
        var spaces = knownSpaces.ToHashSet(StringComparer.Ordinal);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        if (transit.Count > 250 || targets.Count > 250)
            throw new InvalidOperationException("一次平面任务书最多包含 250 个交通空间或面积目标。");
        foreach (var item in transit)
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) ||
                !spaces.Contains(id) || !ids.Add(id))
                throw new InvalidOperationException("交通空间须使用当前项目中不重复的空间 ID。");
        var ordered = new JsonObject();
        foreach (var item in targets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!spaces.Contains(item.Key) || item.Value is not JsonObject bounds || bounds.Count is < 1 or > 2 ||
                bounds.Any(pair => pair.Key is not ("min" or "max")))
                throw new InvalidOperationException("面积目标须引用当前空间 ID，且只指定平方米下限、上限或两者。");
            double Number(string key, double fallback)
            {
                if (!bounds.ContainsKey(key)) return fallback;
                if (bounds[key] is not JsonValue value || !value.TryGetValue<double>(out double number) ||
                    !double.IsFinite(number) || number < 0 || number > 1e13)
                    throw new InvalidOperationException("面积目标须为有效的非负平方米数值。");
                return number;
            }
            double min = Number("min", 0), max = Number("max", double.MaxValue);
            if (min > max) throw new InvalidOperationException("面积目标的下限不能大于上限。");
            var normalized = new JsonObject();
            if (bounds.ContainsKey("min")) normalized["min"] = min;
            if (bounds.ContainsKey("max")) normalized["max"] = max;
            ordered[item.Key] = normalized;
        }
        return new JsonObject
        {
            ["transitSpaces"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
            ["areaTargetsM2"] = ordered,
        };
    }

    public static string Fingerprint(string source, string normalizedBrief, string units, double tolerance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            new JsonArray(CheckVersion, source, normalizedBrief, units, tolerance).ToJsonString())));

    public static JsonObject Summarize(JsonObject report, string projectId, int revision)
    {
        if (report["check"]?.GetValue<string>() != CheckVersion || report["projectId"]?.GetValue<string>() != projectId ||
            report["revision"]?.GetValue<int>() != revision || report["passed"] is not JsonValue verdict ||
            !verdict.TryGetValue<bool>(out var passed) || report["rooms"] is not JsonObject rooms ||
            report["issues"] is not JsonArray issues || passed != (issues.Count == 0))
            throw new InvalidOperationException("平面检查返回的规则、版本或结果不一致。");
        int found = rooms.Count(pair => pair.Value?["toStair"] is JsonObject);
        if (passed && (rooms.Count == 0 || found != rooms.Count))
            throw new InvalidOperationException("平面检查未证明所有已列空间可达楼梯。");
        return new JsonObject
        {
            ["status"] = passed ? "passed" : "failed", ["projectId"] = projectId, ["revision"] = revision,
            ["issues"] = new JsonArray(issues.Take(8).Select(issue => issue?.DeepClone()).ToArray()),
            ["issuesOmitted"] = Math.Max(0, issues.Count - 8),
            ["stairRoutes"] = new JsonObject { ["found"] = found, ["total"] = rooms.Count },
            ["areaTargetsChecked"] = rooms.Count(pair => pair.Value?["areaTargetM2"] is JsonObject),
        };
    }
}
