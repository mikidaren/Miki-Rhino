using System.Text.Json;
using System.Text.Json.Nodes;
using Miki.OpenBim;
using Miki.OpenBim.Rhino;
using Rhino;
using RhinoMagpie.Agent;

namespace RhinoMagpie.Plugin;

public sealed partial class OpenBimTools
{
    private const string ReviewSection = "MikiRhino.ArchitecturalPlan";
    private const string BriefKey = "brief.v1";
    private const string ReportKey = "lastReview.v1";
    private const string CheckpointKey = "workflow.v1";
    // A reopened file may display its last report, but cannot attest to itself.
    // This evidence is produced only by the trusted local runner in this open document.
    private sealed record LivePlanReport(string Fingerprint, JsonObject Summary, UnitSystem Units, double Tolerance);
    private static readonly Dictionary<uint, LivePlanReport> CurrentReports = [];

    static OpenBimTools()
    {
        RhinoDoc.CloseDocument += (_, e) =>
        {
            CurrentReports.Remove(e.DocumentSerialNumber);
            LayoutDrafts.Remove(e.DocumentSerialNumber);
        };
        RhinoDoc.AddRhinoObject += (_, e) => InvalidateObject(e.TheObject?.Document, e.TheObject?.Attributes);
        RhinoDoc.DeleteRhinoObject += (_, e) => InvalidateObject(e.TheObject?.Document, e.TheObject?.Attributes);
        RhinoDoc.UndeleteRhinoObject += (_, e) => InvalidateObject(e.TheObject?.Document, e.TheObject?.Attributes);
        RhinoDoc.ReplaceRhinoObject += (_, e) =>
        {
            InvalidateObject(e.Document, e.OldRhinoObject?.Attributes);
            InvalidateObject(e.Document, e.NewRhinoObject?.Attributes);
        };
        RhinoDoc.ModifyObjectAttributes += (_, e) =>
        {
            InvalidateObject(e.Document, e.OldAttributes);
            InvalidateObject(e.Document, e.NewAttributes);
        };
        RhinoDoc.InstanceDefinitionTableEvent += (_, e) =>
        {
            if (e.Document is { } doc) CurrentReports.Remove(doc.RuntimeSerialNumber);
        };
    }

    private static void InvalidateObject(RhinoDoc? doc, Rhino.DocObjects.ObjectAttributes? attributes)
    {
        if (doc is not null && attributes?.GetUserString("miki.kind") is { Length: > 0 })
            CurrentReports.Remove(doc.RuntimeSerialNumber);
    }

    internal static bool HasLivePlanReport(RhinoDoc doc)
    {
        if (!CurrentReports.TryGetValue(doc.RuntimeSerialNumber, out var report)) return false;
        if (report.Units == doc.ModelUnitSystem && report.Tolerance == doc.ModelAbsoluteTolerance) return true;
        CurrentReports.Remove(doc.RuntimeSerialNumber);
        return false;
    }

    public async Task<JsonObject?> ReadPlanReviewAsync(bool verifyNative, CancellationToken token)
    {
        var wrapper = await OnUiAsync(() => new JsonObject { ["review"] = ReadPlanReview(verifyNative) }, token);
        return wrapper["review"]?.DeepClone().AsObject();
    }

    internal JsonObject? LoadWorkflowCheckpoint()
    {
        var doc = Current();
        var project = RhinoModel.ReadProject(doc);
        var text = doc.Strings.GetValue(ReviewSection, CheckpointKey);
        if (project is null || string.IsNullOrWhiteSpace(text) || text.Length > 128 * 1024) return null;
        if (JsonNode.Parse(text) is not JsonObject saved || saved["projectId"]?.GetValue<string>() != project.Id)
            return null;
        return saved["workflow"]?.DeepClone().AsObject();
    }

    internal void SaveWorkflowCheckpoint(ProjectWorkflow workflow)
    {
        if (workflow.Steps.Count == 0 && workflow.Basis.Length == 0) return;
        var doc = Current();
        var project = RhinoModel.ReadProject(doc);
        if (project is null) return;
        string text = new JsonObject { ["projectId"] = project.Id, ["workflow"] = workflow.ExportCheckpoint() }.ToJsonString();
        if (text.Length <= 128 * 1024 && doc.Strings.GetValue(ReviewSection, CheckpointKey) != text)
        {
            doc.Strings.SetString(ReviewSection, CheckpointKey, text);
            doc.Modified = true;
        }
    }

    internal JsonObject? ReadPlanReview(bool verifyNative)
    {
        var doc = Current();
        var project = RhinoModel.ReadProject(doc);
        if (project is null)
            return ModelObjects(doc).Any(obj =>
                obj.Attributes.GetUserString("miki.project") is { Length: > 0 })
                ? new JsonObject { ["status"] = "stale", ["briefConfirmed"] = false }
                : null;
        var status = Status(project, "pending");
        var brief = ReadReviewProfile(doc, project);
        status["briefConfirmed"] = brief is not null;
        if (!IfcRuntimeAvailable) status["status"] = "unavailable";
        else if (brief is null) status["status"] = "needs_brief";
        else
        {
            var fingerprint = Fingerprint(doc, project, brief);
            if (CurrentReports.TryGetValue(documentSerial, out var evidence))
            {
                bool valid = evidence.Fingerprint == fingerprint;
                if (valid && verifyNative)
                {
                    try { EnsureNative(doc, project); }
                    catch (InvalidOperationException) { valid = false; }
                }
                if (valid) return WithBrief(evidence.Summary);
                CurrentReports.Remove(documentSerial);
            }
            if (doc.Strings.GetValue(ReviewSection, ReportKey) is { Length: > 0 })
                status["status"] = "stale";
        }
        return status;
    }

    private JsonObject SetPlanBrief(JsonObject arguments)
    {
        Only(arguments, "profile", "expectedRevision");
        var doc = Current();
        var project = RequireCurrentProject(doc, arguments);
        var profile = PlanReviewContract.NormalizeBrief(arguments["profile"] as JsonObject
            ?? throw new InvalidOperationException("请输入平面任务书参数。"), project.Spaces.Select(s => s.Id));
        if (!JsonNode.DeepEquals(ReadBrief(doc, project), profile))
        {
            doc.Strings.SetString(ReviewSection, BriefKey, new JsonObject
            {
                ["projectId"] = project.Id,
                ["profile"] = profile.DeepClone(),
            }.ToJsonString());
            doc.Modified = true;
            CurrentReports.Remove(documentSerial);
        }
        return new JsonObject { ["briefConfirmed"] = true, ["projectId"] = project.Id, ["revision"] = project.Revision,
            ["profile"] = profile };
    }

    private async Task<JsonObject> ReviewPlanAsync(JsonObject arguments, CancellationToken token)
    {
        Only(arguments, "expectedRevision");
        if (!IfcRuntimeAvailable)
            throw new InvalidOperationException("这个安装包没有离线平面检查运行时。可以继续建模，暂不能通过平面验收。");
        var snapshot = await OnUiAsync(() =>
        {
            var doc = Current();
            var project = RequireCurrentProject(doc, arguments);
            var brief = ReadReviewProfile(doc, project) ?? throw new InvalidOperationException("先确认当前项目的平面任务书。");
            EnsureNative(doc, project);
            CurrentReports.Remove(documentSerial);
            return new JsonObject { ["projectId"] = project.Id, ["revision"] = project.Revision,
                ["source"] = ProjectEngine.Serialize(project), ["profile"] = brief.ToJsonString(),
                ["fingerprint"] = Fingerprint(doc, project, brief) };
        }, token);
        var report = await PlanReviewWorker.ReviewAsync(PluginDirectory, snapshot["source"]!.GetValue<string>(),
            snapshot["profile"]!.GetValue<string>(), token);
        var summary = PlanReviewContract.Summarize(report, snapshot["projectId"]!.GetValue<string>(),
            snapshot["revision"]!.GetValue<int>());
        return await OnUiAsync(() =>
        {
            var doc = Current();
            var project = RequireCurrentProject(doc, arguments);
            var brief = ReadReviewProfile(doc, project);
            string fingerprint = snapshot["fingerprint"]!.GetValue<string>();
            if (brief is null || Fingerprint(doc, project, brief) != fingerprint)
                throw new InvalidOperationException("检查过程中模型或任务书已变化，请重新检查当前版本。");
            EnsureNative(doc, project);
            CurrentReports[documentSerial] = new LivePlanReport(fingerprint, summary, doc.ModelUnitSystem, doc.ModelAbsoluteTolerance);
            string lastReport = summary.ToJsonString();
            if (doc.Strings.GetValue(ReviewSection, ReportKey) != lastReport)
            {
                doc.Strings.SetString(ReviewSection, ReportKey, lastReport);
                doc.Modified = true;
            }
            return WithBrief(summary);
        }, token);
    }

    private static JsonObject WithBrief(JsonObject summary)
    {
        var copy = summary.DeepClone().AsObject();
        copy["briefConfirmed"] = true;
        return copy;
    }

    private static JsonObject Status(BuildingProject project, string status) => new()
    {
        ["status"] = status, ["projectId"] = project.Id, ["revision"] = project.Revision,
    };

    private static BuildingProject RequireCurrentProject(RhinoDoc doc, JsonObject arguments)
    {
        var project = RhinoModel.ReadProject(doc) ?? throw new InvalidOperationException("当前文档没有简易 BIM 项目。");
        if (arguments["expectedRevision"] is not JsonValue value || !value.TryGetValue<int>(out var revision) ||
            project.Revision != revision)
            throw new InvalidOperationException("方案已更新，请读回当前版本再重试。");
        return project;
    }

    private static JsonObject? ReadBrief(RhinoDoc doc, BuildingProject project)
    {
        string? saved = doc.Strings.GetValue(ReviewSection, BriefKey);
        if (string.IsNullOrWhiteSpace(saved) || saved.Length > 128 * 1024) return null;
        try
        {
            if (JsonNode.Parse(saved) is not JsonObject node || node["projectId"]?.GetValue<string>() != project.Id ||
                node["profile"] is not JsonObject profile) return null;
            return PlanReviewContract.NormalizeBrief(profile, project.Spaces.Select(s => s.Id));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private JsonObject CurrentPlanBrief()
    {
        var doc = Current();
        var project = RhinoModel.ReadProject(doc);
        if (project is null) return new JsonObject { ["briefConfirmed"] = false };
        var profile = ReadReviewProfile(doc, project);
        return new JsonObject { ["projectId"] = project.Id, ["revision"] = project.Revision,
            ["briefConfirmed"] = profile is not null, ["profile"] = profile };
    }

    private static JsonObject? ReadReviewProfile(RhinoDoc doc, BuildingProject project)
    {
        var profile = ReadBrief(doc, project);
        if (profile is null) return null;
        var saved = doc.Strings.GetValue(ReviewSection, LayoutBriefKey);
        if (string.IsNullOrWhiteSpace(saved))
            return project.Levels.Any(level => level.Id.StartsWith("LB.", StringComparison.Ordinal)) ? null : profile;
        if (saved.Length > 128 * 1024) return null;
        try
        {
            if (JsonNode.Parse(saved) is not JsonObject layout || layout["id"]?.GetValue<string>() != project.Id)
                return null;
            profile["layout"] = layout;
            return profile;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static string Fingerprint(RhinoDoc doc, BuildingProject project, JsonObject brief) =>
        PlanReviewContract.Fingerprint(ProjectEngine.Serialize(project), brief.ToJsonString(),
            doc.ModelUnitSystem.ToString(), doc.ModelAbsoluteTolerance);

    private static void EnsureNative(RhinoDoc doc, BuildingProject project)
    {
        var inspected = RhinoModel.Inspect(doc);
        if (inspected["verified"]?.GetValue<bool>() != true || inspected["project"]?.GetValue<string>() != project.Id ||
            inspected["revision"]?.GetValue<int>() != project.Revision)
            throw new InvalidOperationException("当前 Rhino 实体与源方案不一致，不能验收。");
    }

    private static IEnumerable<Rhino.DocObjects.RhinoObject> ModelObjects(RhinoDoc doc) => doc.Objects.GetObjectList(
        new Rhino.DocObjects.ObjectEnumeratorSettings
        {
            NormalObjects = true, HiddenObjects = true, LockedObjects = true, ReferenceObjects = false,
        });
}
