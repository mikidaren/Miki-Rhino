using System.Text.Json.Nodes;
using Miki.OpenBim;
using Miki.OpenBim.Rhino;
using Rhino;
using RhinoMagpie.Agent;

namespace RhinoMagpie.Plugin;

public sealed partial class OpenBimTools
{
    private const string LayoutBriefKey = "layoutBrief.v1";
    private sealed record LayoutDraft(string Id, string Brief, BuildingProject Project, JsonObject Profile, JsonObject Summary);
    private static readonly Dictionary<uint, LayoutDraft> LayoutDrafts = [];

    private static JsonObject LayoutSchema() => new()
    {
        ["available"] = IfcRuntimeAvailable,
        ["scope"] = "Two rectangular storeys, same outline, one row of rooms on a single corridor; one straight aligned stair. No exterior doors, windows or railings are inferred.",
        ["schema"] = "{schemaVersion:1,id,name,envelope:{widthM,depthM},corridor:{pattern:'all_rooms_direct',widthM},stair:{startXM,widthM,runM,risers},levels:[{heightM,spaces:[{id,name,areaM2:{min,target,max}}],requiredAdjacencies:[[spaceId,spaceId],...]}]}",
        ["units"] = "metres, 0.1 m grid; area min/target/max in m2; 1–5 spaces per floor; adjacency means shared partition, not a connecting door",
        ["fixedDefaults"] = "0.20 m wall, 0.90 m door opening, 1.50 m minimum room width, 2.00 m minimum depth, 0.90 m stair bypass. These are solver inputs, not code compliance.",
    };

    private async Task<JsonObject> StageLayoutAsync(JsonObject arguments, CancellationToken token)
    {
        Only(arguments, "brief");
        if (!IfcRuntimeAvailable) throw new InvalidOperationException("当前安装包没有离线平面求解运行时。");
        var brief = arguments["brief"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("请提供两层建筑的结构化任务书。");
        if (brief.ContainsKey("approved")) throw new InvalidOperationException("不要在参数里声明审批。候选生成后由用户确认。");
        string requested = brief.ToJsonString();
        brief["approved"] = true; // Worker input contract; the user gate is ApplyStagedLayout.
        await OnUiAsync(() => { RequireEmptyLayoutDocument(); LayoutDrafts.Remove(documentSerial); return true; }, token);
        var result = await LayoutWorker.GenerateAsync(PluginDirectory, brief.ToJsonString(), token);
        var report = result["report"]?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("平面求解器没有返回诊断。");
        if (result["project"] is null) return report;
        var project = ProjectEngine.Parse(result["project"]!.ToJsonString());
        if (project.Revision != 0 || project.Id != brief["id"]?.GetValue<string>())
            throw new InvalidOperationException("候选的项目 ID 或初始版本不匹配。");
        var profile = PlanReviewContract.NormalizeBrief(result["profile"]?.AsObject()
            ?? throw new InvalidOperationException("候选缺少检查任务书。"), project.Spaces.Select(space => space.Id));
        var reviewProfile = profile.DeepClone().AsObject();
        reviewProfile["layout"] = JsonNode.Parse(requested);
        var checkedReport = PlanReviewContract.Summarize(await PlanReviewWorker.ReviewAsync(PluginDirectory,
            ProjectEngine.Serialize(project), reviewProfile.ToJsonString(), token), project.Id, project.Revision);
        if (checkedReport["status"]?.GetValue<string>() != "passed")
            return new JsonObject { ["status"] = "candidate_failed_plan_review", ["review"] = checkedReport };
        var summary = new JsonObject
        {
            ["candidateId"] = Guid.NewGuid().ToString("N"), ["projectId"] = project.Id,
            ["candidateCount"] = report["candidateCount"]?.DeepClone(),
            ["selected"] = report["candidates"] is JsonArray { Count: > 0 } candidates ? candidates[0]?.DeepClone() : null,
            ["sourcePlanReview"] = checkedReport,
            ["note"] = "尚未建模。确认并应用候选后，再检查 Rhino 实体。",
        };
        return await OnUiAsync(() =>
        {
            RequireEmptyLayoutDocument();
            LayoutDrafts[documentSerial] = new LayoutDraft(summary["candidateId"]!.GetValue<string>(), requested,
                project, profile, summary);
            return summary.DeepClone().AsObject();
        }, token);
    }

    internal JsonObject DescribeLayoutDraft(JsonObject arguments)
    {
        var draft = RequireDraft(arguments);
        var review = draft.Summary.DeepClone().AsObject();
        review["brief"] = JsonNode.Parse(draft.Brief);
        review["fixedDefaults"] = LayoutSchema()["fixedDefaults"]!.DeepClone();
        return review;
    }

    private JsonObject ApplyStagedLayout(JsonObject arguments)
    {
        var draft = RequireDraft(arguments);
        var doc = RequireEmptyLayoutDocument();
        var native = RhinoModel.Apply(doc, draft.Project);
        SetPlanBrief(new JsonObject { ["expectedRevision"] = 0, ["profile"] = draft.Profile.DeepClone() });
        doc.Strings.SetString(ReviewSection, LayoutBriefKey, draft.Brief);
        doc.Modified = true;
        LayoutDrafts.Remove(documentSerial);
        return Compact(native, new JsonObject());
    }

    private async Task<JsonObject> ReviseLayoutBriefAsync(JsonObject arguments, CancellationToken token)
    {
        Only(arguments, "expectedRevision", "layout");
        if (!IfcRuntimeAvailable) throw new InvalidOperationException("当前安装包没有离线任务书验证运行时。");
        var revised = arguments["layout"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("请提供完整的修订版任务书。");
        if (revised.ContainsKey("approved")) throw new InvalidOperationException("审批由用户确认，不能写在参数中。");
        string text = revised.ToJsonString();
        var captured = await OnUiAsync(() =>
        {
            var doc = Current();
            var project = RequireCurrentProject(doc, arguments);
            if (ReadReviewProfile(doc, project)?["layout"] is not JsonObject || revised["id"]?.GetValue<string>() != project.Id)
                throw new InvalidOperationException("修订任务书须关联当前由求解器生成的项目。");
            return new JsonObject { ["source"] = ProjectEngine.Serialize(project),
                ["previous"] = doc.Strings.GetValue(ReviewSection, LayoutBriefKey) };
        }, token);
        revised["approved"] = true;
        var result = await LayoutWorker.GenerateAsync(PluginDirectory, revised.ToJsonString(), token);
        if (result["profile"] is not JsonObject derived)
            return new JsonObject { ["briefConfirmed"] = false, ["diagnostic"] = result["report"]?.DeepClone() };
        return await OnUiAsync(() =>
        {
            var doc = Current();
            var project = RequireCurrentProject(doc, arguments);
            if (captured["source"]!.GetValue<string>() != ProjectEngine.Serialize(project) ||
                captured["previous"]?.GetValue<string>() != doc.Strings.GetValue(ReviewSection, LayoutBriefKey))
                throw new InvalidOperationException("修订过程中项目或原任务书已变化，请重新确认。");
            var summary = SetPlanBrief(new JsonObject
            {
                ["expectedRevision"] = project.Revision, ["profile"] = derived.DeepClone(),
            });
            doc.Strings.SetString(ReviewSection, LayoutBriefKey, text);
            CurrentReports.Remove(documentSerial);
            doc.Modified = true;
            summary["needsNewReview"] = true;
            return summary;
        }, token);
    }

    private LayoutDraft RequireDraft(JsonObject arguments)
    {
        Only(arguments, "candidateId");
        if (arguments["candidateId"] is not JsonValue token || !token.TryGetValue<string>(out var id) ||
            !LayoutDrafts.TryGetValue(documentSerial, out var draft) || draft.Id != id)
            throw new InvalidOperationException("候选不存在或已被替换，请重新生成。");
        return draft;
    }

    private RhinoDoc RequireEmptyLayoutDocument()
    {
        var doc = Current();
        if (ModelObjects(doc).Any() || RhinoModel.ReadProject(doc) is not null)
            throw new InvalidOperationException("布局求解只适用于独立空白文档，不覆盖已有项目。");
        return doc;
    }
}
