using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed record ModelingRunOptions(
    bool Architectural = false,
    string Detail = "developed",
    string DocumentContext = "",
    bool ExistingGeometry = false,
    bool ExplicitScale = false,
    IReadOnlyList<ReferenceInput>? References = null,
    string Units = "");

public sealed record ReferenceInput(
    string Id, string Name, string Kind, string Path, double? Width = null, string? ImageData = null,
    string? Units = null)
{
    public ReferenceInput InUnits(string units) =>
        Width is not null && Units is not null && Units != units ? this with { Width = null } : this;
}

public sealed record WorkflowStep(string Id, string Title, string Acceptance, string Status);

public sealed class ProjectWorkflow
{
    public string Goal { get; private set; } = "";
    public string Basis { get; private set; } = "";
    public bool BasisConfirmed { get; private set; }
    public IReadOnlyList<WorkflowStep> Steps { get; private set; } = [];
    public bool AwaitingReview { get; private set; }
    public bool Accepted { get; private set; }
    private bool hasFreshReadback;
    private string? approvedBasis;
    private sealed record Operation(string Id, string Name, bool Modifying, bool Success, string[] Stages);
    private readonly List<Operation> operations = [];
    private readonly HashSet<string> failures = new(StringComparer.Ordinal);
    private int runStartIndex;
    public bool HasUnresolvedFailures => failures.Count > 0;
    private string basisUnits = "";

    public bool HasPending => Steps.Count == 0 || Steps.Any(step => step.Status != "complete");

    public void BeginRun(string units = "")
    {
        runStartIndex = operations.Count;
        if (BasisConfirmed && units.Length > 0 && basisUnits.Length > 0 && units != basisUnits)
        {
            BasisConfirmed = false;
            approvedBasis = null;
            Steps = Steps.Select(step => step with { Status = "pending" }).ToList();
        }
        AwaitingReview = false;
        Accepted = false;
        hasFreshReadback = false;
    }

    public void RecordBasisApproval(string note) => approvedBasis = note.Trim();
    public bool IsBasisApproved(string note) => approvedBasis is not null && approvedBasis == note.Trim();

    public static bool IsModifyingTool(string name, bool readOnly) =>
        !readOnly && name is not ("get_viewport_image" or "zoom_to_layer" or "zoom_to_object" or "set_camera" or "openbim_export_ifc");

    public string ObserveTool(string name, bool readOnly, bool success)
    {
        bool modifying = IsModifyingTool(name, readOnly);
        string id = "op-" + (operations.Count + 1);
        operations.Add(new Operation(id, name, modifying, success,
            Steps.Where(step => step.Status == "in_progress").Select(step => step.Id).ToArray()));
        if (modifying)
        {
            hasFreshReadback = false;
            if (!success) failures.Add(id);
        }
        if (success && name is "get_context" or "list_objects" or "openbim_inspect") hasFreshReadback = true;
        return id;
    }

    public void ResolveFailure(string failureId, string correctiveOperationId, string readbackOperationId,
        string verification)
    {
        var failed = operations.FirstOrDefault(operation => operation.Id == failureId);
        var correction = operations.FirstOrDefault(operation => operation.Id == correctiveOperationId);
        var readback = operations.FirstOrDefault(operation => operation.Id == readbackOperationId);
        if (failed is null || !failures.Contains(failureId) || correction is null || readback is null ||
            string.IsNullOrWhiteSpace(verification))
            throw new InvalidOperationException("请提供未解决的失败 ID、修正操作 ID、读回 ID 和实际验证说明。");
        int failureIndex = operations.IndexOf(failed);
        int correctionIndex = operations.IndexOf(correction);
        int readbackIndex = operations.IndexOf(readback);
        if (!correction.Success || !correction.Modifying || correctionIndex <= failureIndex ||
            RepairFamily(failed.Name) != RepairFamily(correction.Name) ||
            failed.Stages.Length != correction.Stages.Length ||
            !failed.Stages.All(stage => correction.Stages.Contains(stage, StringComparer.Ordinal)))
            throw new InvalidOperationException("修正操作必须在失败后成功执行，针对同一建模类型和计划阶段。无关操作不能核销失败。");
        if (!readback.Success || readbackIndex < runStartIndex ||
            readback.Name is not ("get_context" or "list_objects" or "openbim_inspect") ||
            readbackIndex <= correctionIndex || operations.Skip(readbackIndex + 1).Any(operation => operation.Modifying))
            throw new InvalidOperationException("需要修正之后、最近一次修改之后的成功 get_context、list_objects 或 openbim_inspect 读回。");
        failures.Remove(failureId);
    }

    private static string RepairFamily(string name) => name switch
    {
        "run_command" or "run_csharp" or "run_python" or "openbim_apply" => "geometry",
        _ => name,
    };

    public void SetBasis(JsonObject arguments, ModelingRunOptions options)
    {
        string source = arguments["source"]?.GetValue<string>() ?? "";
        string note = arguments["note"]?.GetValue<string>() ?? "";
        bool grounded = source switch
        {
            "existing_model" => options.ExistingGeometry,
            "user_dimensions" => options.ExplicitScale,
            "reference" => options.References?.Any(reference =>
                reference.Width > 0 && double.IsFinite(reference.Width.Value)) == true,
            "confirmed_assumption" => IsBasisApproved(note),
            _ => false,
        };
        if (!grounded || string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("尺度依据尚未确认。假设尺度必须经过专门确认，也可使用用户提供的明确尺寸、已定标参考图或现有模型。");
        Basis = note;
        basisUnits = options.Units;
        BasisConfirmed = true;
    }

    public void UpdatePlan(JsonObject arguments)
    {
        string goal = arguments["goal"]?.GetValue<string>() ?? Goal;
        var items = arguments["steps"] as JsonArray;
        if (string.IsNullOrWhiteSpace(goal) || items is not { Count: > 0 and <= 20 })
            throw new InvalidOperationException("请提供目标和 1–20 个有验收条件的步骤。");
        var steps = new List<WorkflowStep>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string id = item?["id"]?.GetValue<string>() ?? "";
            string title = item?["title"]?.GetValue<string>() ?? "";
            string acceptance = item?["acceptance"]?.GetValue<string>() ?? "";
            string status = item?["status"]?.GetValue<string>() ?? "pending";
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(acceptance) || status is not ("pending" or "in_progress" or "complete"))
                throw new InvalidOperationException("每个步骤需要唯一 ID、名称、验收条件和有效状态。");
            bool previouslyComplete = Steps.Any(step => step.Id == id && step.Status == "complete");
            var previous = Steps.FirstOrDefault(step => step.Id == id);
            if (previous is not null && (previous.Title != title || previous.Acceptance != acceptance))
                throw new InvalidOperationException("已有步骤的目标和验收条件不能自行删改。需要深化时请保留原项并新增步骤。");
            if (status == "complete" && !previouslyComplete &&
                (!BasisConfirmed || !hasFreshReadback || HasUnresolvedFailures))
                throw new InvalidOperationException("标记步骤完成前，需要已确认的尺度依据，以及最近一次修改后的 get_context、list_objects 或 openbim_inspect 读回。");
            steps.Add(new WorkflowStep(id, title, acceptance, status));
        }
        foreach (var previous in Steps)
            if (!ids.Contains(previous.Id)) steps.Add(previous);
        Goal = goal;
        Steps = steps;
        AwaitingReview = false;
        Accepted = false;
    }

    public string ContinuationPrompt()
    {
        if (HasUnresolvedFailures)
            return "存在尚未核销的建模失败：" + string.Join(", ", failures) +
                "。请根据错误完成相同阶段和类型的修正，重新 get_context/list_objects/openbim_inspect 读回后，调用 resolve_modeling_failure 逐项提交操作 ID 和验证说明。";
        if (!BasisConfirmed)
            return "建筑建模尚未具备确认过的平面和尺度依据。请检查实际单位，必要时向用户提问，调用 set_design_basis 后再修改几何。不要把单次答复当作任务完成。";
        if (Steps.Count == 0)
            return "请先调用 update_project_plan，列出从平面、体量到目标细度的步骤和逐项验收条件，再按计划执行。";
        if (!hasFreshReadback)
            return "当前修改之后还缺少几何读回。请使用 get_context、list_objects 或 openbim_inspect 检查实际结果，再更新步骤状态或提交验收。";
        string pending = string.Join("；", Steps.Where(step => step.Status != "complete")
            .Select(step => step.Title + "（" + step.Acceptance + "）"));
        return "计划仍有未完成步骤：" + pending +
            "。继续执行下一项，检查实际几何和视口，完成后更新计划。体量完成不能替代门窗、立面和细部的验收。";
    }

    public void RequestReview()
    {
        if (HasPending || !BasisConfirmed || !hasFreshReadback || HasUnresolvedFailures)
            throw new InvalidOperationException("仍有待办步骤或缺少最终几何读回。");
        AwaitingReview = true;
    }

    public void Accept()
    {
        if (!AwaitingReview) throw new InvalidOperationException("当前成果尚未进入待验收状态。");
        Accepted = true;
        AwaitingReview = false;
    }

    public JsonObject Snapshot() => new()
    {
        ["goal"] = Goal,
        ["basis"] = Basis,
        ["basisConfirmed"] = BasisConfirmed,
        ["awaitingReview"] = AwaitingReview,
        ["accepted"] = Accepted,
        ["unresolvedFailures"] = new JsonArray(failures.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
        ["steps"] = new JsonArray(Steps.Select(step => (JsonNode)new JsonObject
        {
            ["id"] = step.Id, ["title"] = step.Title, ["acceptance"] = step.Acceptance, ["status"] = step.Status,
        }).ToArray()),
    };

    public static JsonObject ResolutionToolDefinition() => JsonNode.Parse("""
        {"type":"function","function":{"name":"resolve_modeling_failure","description":"Resolve one outstanding modeling failure only after a successful corrective operation of the same family and plan stage, followed by fresh get_context/list_objects/openbim_inspect after all mutations. Use operation IDs from tool results and explain the actual verification. Unrelated success does not repair a failure.","parameters":{"type":"object","properties":{"failureId":{"type":"string"},"correctiveOperationId":{"type":"string"},"readbackOperationId":{"type":"string"},"verification":{"type":"string"}},"required":["failureId","correctiveOperationId","readbackOperationId","verification"]}}}
        """)!.AsObject();

    public static JsonArray ToolDefinitions() => JsonNode.Parse("""
        [
          {"type":"function","function":{"name":"set_design_basis","description":"Record scale and reference basis before architectural geometry changes. Use existing_model for actual existing objects, user_dimensions for explicit user lengths, reference for a scaled reference, or confirmed_assumption for proposed dimensions. The panel asks the user to approve the exact assumption note before accepting it, independently of other questions or auto-execute. State units, overall size and assumptions clearly.","parameters":{"type":"object","properties":{"source":{"type":"string","enum":["existing_model","user_dimensions","reference","confirmed_assumption"]},"note":{"type":"string"}},"required":["source","note"]}}},
          {"type":"function","function":{"name":"update_project_plan","description":"Create or update the full architectural task plan. Keep pending detail work visible. Every step requires an acceptance condition. A complete status requires fresh get_context/list_objects/openbim_inspect after the most recent mutation. Update after each stage, and continue until all requested stages are checked.","parameters":{"type":"object","properties":{"goal":{"type":"string"},"steps":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"acceptance":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","complete"]}},"required":["id","title","acceptance","status"]}}},"required":["goal","steps"]}}}
        ]
        """)!.AsArray();
}
