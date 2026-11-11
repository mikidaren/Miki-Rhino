using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception("FAIL " + name);
}

async Task RejectAsync<TException>(Func<Task> action, string name) where TException : Exception
{
    try { await action(); }
    catch (TException) { Check(true, name); return; }
    throw new Exception("FAIL " + name);
}

var config = new BridgeConfig("group/workflow-fixture", "http://localhost:3425", "fixture-secret");
var architectural = new ModelingRunOptions(Architectural: true);
var scaled = architectural with { ExplicitScale = true };

string fragmented = ": heartbeat\r\n" +
    Wire.Frame(new JsonObject { ["content"] = "冰雪🏰" }, multiline: true) +
    Wire.Frame(new JsonObject
    {
        ["tool_calls"] = new JsonArray(
            Wire.Fragment(1, "inspect", "list_", "{\"filter\":"),
            Wire.Fragment(0, "edit", "run_", "{\"script\":\"")),
    }) +
    Wire.Frame(new JsonObject { ["content"] = "建模" }) +
    Wire.Frame(new JsonObject
    {
        ["tool_calls"] = new JsonArray(
            Wire.Fragment(0, null, "csharp", "System.Console.WriteLine(\\\"冰雪\\\");\"}"),
            Wire.Fragment(1, null, "objects", "\"冰雪\"}")),
    }) +
    Wire.Finish("tool_calls");
using (var stream = new FragmentedStream(fragmented))
{
    var deltas = new List<string>();
    var result = await StreamingChatReader.ReadAsync(stream, deltas.Add, CancellationToken.None);
    Check(result["content"]!.GetValue<string>() == "冰雪🏰建模" &&
        deltas.SequenceEqual(new[] { "冰雪🏰", "建模" }),
        "UTF-8 split across byte reads preserves text, emoji and callback order");
    var calls = result["tool_calls"]!.AsArray();
    Check(calls.Count == 2 && calls[0]!["id"]!.GetValue<string>() == "edit" &&
        calls[1]!["id"]!.GetValue<string>() == "inspect",
        "interleaved tool fragments assemble by index rather than arrival order");
    Check(calls[0]!["function"]!["name"]!.GetValue<string>() == "run_csharp" &&
        JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>())!["script"]!
            .GetValue<string>() == "System.Console.WriteLine(\"冰雪\");" &&
        calls[1]!["function"]!["name"]!.GetValue<string>() == "list_objects" &&
        JsonNode.Parse(calls[1]!["function"]!["arguments"]!.GetValue<string>())!["filter"]!
            .GetValue<string>() == "冰雪",
        "CRLF and multi-line data frames preserve fragmented function names and JSON arguments");
    Check(stream.ReadCount > 100 && stream.LargestRead <= 3,
        "SSE fixture uses actual fragmented stream reads");
}

string textPrefix = Wire.Frame(new JsonObject { ["content"] = "正在检查" });
using (var stream = new FragmentedStream(textPrefix + Wire.Finish("stop"),
    Encoding.UTF8.GetByteCount(textPrefix)))
using (var fixture = new HttpFixture((_, _) => Wire.Response(stream)))
using (var http = new HttpClient(fixture))
{
    var firstText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = new MagpieGateway(http).CompleteAsync(config, [], [], CancellationToken.None,
        text => firstText.TrySetResult(text));
    try
    {
        Check(await firstText.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "正在检查" && !completion.IsCompleted,
            "gateway publishes text callback before the response completes");
        Check(fixture.ChatRequests.Single()["stream"]!.GetValue<bool>(),
            "gateway explicitly requests stream true");
    }
    finally { stream.Release(); }
    Check((await completion)["content"]!.GetValue<string>() == "正在检查",
        "gateway completes after the terminal frame is released");
}

string partialTool = Wire.Frame(new JsonObject { ["content"] = "准备修改" }) +
    Wire.Frame(new JsonObject
    {
        ["tool_calls"] = new JsonArray(Wire.Fragment(0, "partial", "run_csharp", "{\"script\":\"unfinished")),
    });
foreach (string ending in new[] { "", "data: [DONE]\r\n\r\n" })
{
    using var fixture = new HttpFixture((_, _) => Wire.Response(new FragmentedStream(partialTool + ending)));
    using var http = new HttpClient(fixture);
    var session = new ModelingSession();
    var deltas = new List<string>();
    int approvals = 0;
    await RejectAsync<InvalidOperationException>(() => session.RunAsync("Build", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint),
        (_, _) => { approvals++; return Task.FromResult(true); },
        (_, _) => Task.FromResult(""), (role, text) => { if (role == "assistant_delta") deltas.Add(text); },
        CancellationToken.None), "stream without finish_reason is refused at " + (ending == "" ? "EOF" : "[DONE]"));
    Check(fixture.Executed.Count == 0 && approvals == 0 && deltas.SequenceEqual(new[] { "准备修改" }) &&
        session.Outcome == "paused" && !session.ToolCompletionUnknown,
        "truncated tool arguments never reach approval or MCP and received text is retained");
}

foreach (string reason in new[] { "length", "content_filter" })
{
    using var fixture = new HttpFixture((_, _) => Wire.Response(new FragmentedStream(
        Wire.Calls(Wire.Call("complete-call", "run_csharp", new JsonObject { ["script"] = "valid" }),
            finish: false) + Wire.Finish(reason))));
    using var http = new HttpClient(fixture);
    await RejectAsync<InvalidOperationException>(() => new ModelingSession().RunAsync("Build", config,
        new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None),
        reason + " finish_reason refuses even a fully assembled tool call");
    Check(fixture.Executed.Count == 0, reason + " cannot execute partial work");
}

using (var stream = new FragmentedStream(
    Wire.Calls(Wire.Call("complete-but-unfinished", "run_csharp", new JsonObject { ["script"] = "valid" }),
        finish: false)))
{
    await RejectAsync<InvalidOperationException>(
        () => StreamingChatReader.ReadAsync(stream, null, CancellationToken.None),
        "EOF rejects a complete tool payload when finish_reason is absent");
}

using (var cancellation = new CancellationTokenSource())
using (var stream = new FragmentedStream(partialTool + Wire.Finish("tool_calls"),
    Encoding.UTF8.GetByteCount(partialTool)))
using (var fixture = new HttpFixture((turn, _) => turn == 0
    ? Wire.Response(stream) : Wire.Response(Wire.Text("Cancelled turn safely resumed."))))
using (var http = new HttpClient(fixture))
{
    var session = new ModelingSession();
    var completion = session.RunAsync("Build", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, cancellation.Token);
    await stream.Paused.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    await RejectAsync<OperationCanceledException>(() => completion,
        "cancellation interrupts an in-flight SSE read");
    Check(fixture.Executed.Count == 0 && session.Outcome == "paused" && !session.ToolCompletionUnknown,
        "cancelling fragmented model output dispatches no tool and leaves a resumable session");
    await session.RunAsync("Continue after cancellation", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
    Check(session.Outcome == "complete" && fixture.Executed.Count == 0 &&
        fixture.ChatRequests[1]["messages"]!.AsArray().All(message => message?["tool_calls"] is null),
        "cancelled partial tool fragments do not poison the resumed conversation");
}

var workflow = new ProjectWorkflow();
workflow.BeginRun();
foreach (string source in new[] { "existing_model", "user_dimensions", "reference", "confirmed_assumption" })
    Reject(() => workflow.SetBasis(Wire.Basis(source), architectural),
        "unknown scale rejects ungrounded " + source);
Reject(() => workflow.UpdatePlan(Wire.Plan("complete")), "completion requires a confirmed basis");
Check(!workflow.BasisConfirmed && workflow.Steps.Count == 0,
    "rejected basis and completion leave workflow state unchanged");
string approvedNote = Wire.Basis("confirmed_assumption")["note"]!.GetValue<string>();
var unrelatedApproval = new ProjectWorkflow();
unrelatedApproval.RecordBasisApproval("Use a flat roof.");
Reject(() => unrelatedApproval.SetBasis(Wire.Basis("confirmed_assumption"), architectural),
    "unrelated question consent cannot approve a scale assumption");
unrelatedApproval.RecordBasisApproval(approvedNote);
Check(unrelatedApproval.IsBasisApproved(approvedNote) &&
    !unrelatedApproval.IsBasisApproved(approvedNote + " Increase height to 6 m."),
    "approval lookup distinguishes the approved note from a changed assumption");
var changedAssumption = Wire.Basis("confirmed_assumption");
changedAssumption["note"] = approvedNote + " Increase height to 6 m.";
Reject(() => unrelatedApproval.SetBasis(changedAssumption, architectural),
    "basis approval authorizes only the exact approved note");
Check(!unrelatedApproval.BasisConfirmed, "rejected assumption changes leave basis unconfirmed");
workflow.RecordBasisApproval(approvedNote);
workflow.SetBasis(Wire.Basis("confirmed_assumption"), architectural);
Check(workflow.BasisConfirmed && workflow.Basis.Contains("12 m"),
    "exact-note approval permits the recorded scale assumption");
workflow.UpdatePlan(Wire.Plan("in_progress"));
Reject(() => workflow.Accept(), "Accept requires awaiting human review");
Reject(() => workflow.RequestReview(), "pending plan cannot enter human review");
workflow.ObserveTool("get_context", true, true);
workflow.ObserveTool("run_csharp", false, true);
Reject(() => workflow.UpdatePlan(Wire.Plan("complete")), "edit invalidates earlier readback");
workflow.ObserveTool("list_objects", true, false);
workflow.ObserveTool("get_viewport_image", true, true);
Reject(() => workflow.UpdatePlan(Wire.Plan("complete")),
    "failed readback and viewport image cannot complete an edited stage");
workflow.ObserveTool("list_objects", true, true);
workflow.UpdatePlan(Wire.Plan("complete"));
Check(!workflow.HasPending && !workflow.Accepted && !workflow.AwaitingReview,
    "fresh object readback permits completion without accepting the result");
workflow.ObserveTool("run_csharp", false, true);
Reject(() => workflow.RequestReview(), "final mutation also requires a fresh readback for review");
workflow.ObserveTool("get_context", true, true);
workflow.RequestReview();
Check(workflow.AwaitingReview && !workflow.Accepted, "completed stages await human review");
workflow.Accept();
Check(workflow.Accepted && !workflow.AwaitingReview &&
    workflow.Snapshot()["accepted"]!.GetValue<bool>(), "human Accept records acceptance");
workflow.BeginRun();
Check(workflow.BasisConfirmed && workflow.Steps.Single().Status == "complete" &&
    !workflow.Accepted && !workflow.AwaitingReview, "new run preserves basis and plan and resets review state");
Reject(() => workflow.RequestReview(), "new run requires a new readback");
Reject(() => workflow.SetBasis(changedAssumption, architectural),
    "a previous run's approval cannot authorize a different assumption");

var preservedPlan = new ProjectWorkflow();
preservedPlan.SetBasis(Wire.Basis("user_dimensions"), scaled);
preservedPlan.UpdatePlan(Wire.Plan("in_progress", "pending"));
preservedPlan.ObserveTool("get_context", true, true);
preservedPlan.UpdatePlan(Wire.Plan("complete"));
Check(preservedPlan.HasPending && preservedPlan.Steps.Count == 2 &&
    preservedPlan.Steps.Single(step => step.Id == "detail").Status == "pending",
    "partial plan update preserves omitted outstanding stages");
Reject(() => preservedPlan.RequestReview(), "omitting an outstanding stage cannot reach review");
foreach (string property in new[] { "title", "acceptance" })
{
    foreach (string stage in new[] { "mass", "detail" })
    {
        string before = preservedPlan.Snapshot().ToJsonString();
        var changedPlan = Wire.Plan("complete", "pending");
        var changedStep = changedPlan["steps"]!.AsArray().Single(step => step!["id"]!.GetValue<string>() == stage)!;
        changedStep[property] = "Reduced requirement";
        Reject(() => preservedPlan.UpdatePlan(changedPlan),
            "existing " + stage + " stage rejects silently changed " + property);
        Check(preservedPlan.Snapshot().ToJsonString() == before,
            "rejected " + stage + " " + property + " change leaves plan intact");
    }
}

var failedOperation = new ProjectWorkflow();
failedOperation.SetBasis(Wire.Basis("user_dimensions"), scaled);
failedOperation.UpdatePlan(Wire.Plan("in_progress"));
failedOperation.ObserveTool("get_context", true, true);
string failedCommandId = failedOperation.ObserveTool("run_command", false, false);
Reject(() => failedOperation.UpdatePlan(Wire.Plan("complete")),
    "failed modifying run_command invalidates an earlier successful readback");
failedOperation.ObserveTool("list_objects", true, true);
Reject(() => failedOperation.UpdatePlan(Wire.Plan("complete")),
    "readback alone cannot resolve a failed modifying operation");
failedOperation.BeginRun();
failedOperation.ObserveTool("get_context", true, true);
Reject(() => failedOperation.UpdatePlan(Wire.Plan("complete")),
    "unresolved modifying failure survives a new run and readback");
string correctedCommandId = failedOperation.ObserveTool("run_command", false, true);
Reject(() => failedOperation.UpdatePlan(Wire.Plan("complete")),
    "successful modifying repair still requires a subsequent readback");
string commandReadbackId = failedOperation.ObserveTool("list_objects", true, true);
Check(failedOperation.HasUnresolvedFailures,
    "successful correction and readback retain the failure until explicit resolution");
Reject(() => failedOperation.UpdatePlan(Wire.Plan("complete")),
    "corrected geometry cannot complete a stage before explicit failure resolution");
failedOperation.ResolveFailure(failedCommandId, correctedCommandId, commandReadbackId,
    "Verified the repaired command produced the required dimensions and object count.");
failedOperation.UpdatePlan(Wire.Plan("complete"));
failedOperation.RequestReview();
Check(!failedOperation.HasUnresolvedFailures && failedOperation.AwaitingReview,
    "explicit resolution links failure, corrective operation and fresh readback before review");
failedOperation.ObserveTool("run_command", false, false);
failedOperation.ObserveTool("get_context", true, true);
Reject(() => failedOperation.RequestReview(),
    "failed edit blocks renewed review even when plan was already complete");

ProjectWorkflow LedgerWorkflow()
{
    var ledger = new ProjectWorkflow();
    ledger.SetBasis(Wire.Basis("user_dimensions"), scaled);
    ledger.UpdatePlan(Wire.Plan("in_progress", "pending"));
    return ledger;
}

var materialLedger = LedgerWorkflow();
string facadeFailureId = materialLedger.ObserveTool("run_csharp", false, false);
string materialCorrectionId = materialLedger.ObserveTool("set_layer_material", false, true);
string materialReadbackId = materialLedger.ObserveTool("list_objects", true, true);
Check(materialLedger.HasUnresolvedFailures,
    "unrelated successful material operation cannot clear a failed facade operation");
Reject(() => materialLedger.ResolveFailure(facadeFailureId, materialCorrectionId, materialReadbackId,
    "The layer material was changed."), "wrong-family correction cannot resolve a geometry failure");
Reject(() => materialLedger.UpdatePlan(Wire.Plan("complete", "pending")),
    "material success and readback cannot complete the failed geometry stage");
string facadeCorrectionId = materialLedger.ObserveTool("run_python", false, true);
string facadeReadbackId = materialLedger.ObserveTool("get_context", true, true);
materialLedger.ResolveFailure(facadeFailureId, facadeCorrectionId, facadeReadbackId,
    "Verified the repaired facade geometry and opening dimensions.");
Check(!materialLedger.HasUnresolvedFailures,
    "compatible geometry script family can explicitly resolve the failed facade operation");

var staleLedger = LedgerWorkflow();
string preFailureCorrectionId = staleLedger.ObserveTool("run_python", false, true);
string preFailureReadbackId = staleLedger.ObserveTool("get_context", true, true);
string staleFailureId = staleLedger.ObserveTool("run_command", false, false);
string preCorrectionReadbackId = staleLedger.ObserveTool("list_objects", true, true);
Reject(() => staleLedger.ResolveFailure(staleFailureId, preFailureCorrectionId, preCorrectionReadbackId,
    "A script before the failure succeeded."), "correction before the failed operation cannot resolve it");
string staleCorrectionId = staleLedger.ObserveTool("run_csharp", false, true);
Reject(() => staleLedger.ResolveFailure(staleFailureId, staleCorrectionId, preFailureReadbackId,
    "Old context looked correct."), "readback before the failed operation cannot verify its correction");
Reject(() => staleLedger.ResolveFailure(staleFailureId, staleCorrectionId, preCorrectionReadbackId,
    "Context before the correction looked correct."), "readback before correction cannot resolve a failure");
string obsoleteReadbackId = staleLedger.ObserveTool("list_objects", true, true);
string latestCorrectionId = staleLedger.ObserveTool("run_python", false, true);
Reject(() => staleLedger.ResolveFailure(staleFailureId, staleCorrectionId, obsoleteReadbackId,
    "An earlier readback looked correct."), "readback before the latest mutation is stale for failure resolution");
string unsuccessfulReadbackId = staleLedger.ObserveTool("get_context", true, false);
Reject(() => staleLedger.ResolveFailure(staleFailureId, latestCorrectionId, unsuccessfulReadbackId,
    "Readback failed."), "unsuccessful readback cannot verify failure resolution");
Check(staleLedger.HasUnresolvedFailures,
    "rejected stale and failed readbacks preserve the outstanding failure");
string latestReadbackId = staleLedger.ObserveTool("list_objects", true, true);
staleLedger.ResolveFailure(staleFailureId, latestCorrectionId, latestReadbackId,
    "Latest successful readback confirms corrected dimensions after all mutations.");
Check(!staleLedger.HasUnresolvedFailures, "fresh readback after the latest mutation permits explicit resolution");

var stageLedger = LedgerWorkflow();
string stageFailureId = stageLedger.ObserveTool("run_csharp", false, false);
stageLedger.UpdatePlan(Wire.Plan("pending", "in_progress"));
string otherStageCorrectionId = stageLedger.ObserveTool("run_python", false, true);
string otherStageReadbackId = stageLedger.ObserveTool("list_objects", true, true);
Reject(() => stageLedger.ResolveFailure(stageFailureId, otherStageCorrectionId, otherStageReadbackId,
    "Facade openings were checked."), "correction in another active stage cannot resolve the failed stage");
Check(stageLedger.HasUnresolvedFailures, "wrong-stage resolution retains the original failure");
stageLedger.UpdatePlan(Wire.Plan("in_progress", "pending"));
string matchingStageCorrectionId = stageLedger.ObserveTool("run_command", false, true);
string matchingStageReadbackId = stageLedger.ObserveTool("get_context", true, true);
stageLedger.ResolveFailure(stageFailureId, matchingStageCorrectionId, matchingStageReadbackId,
    "Building mass corrected and checked in its original active stage.");
Check(!stageLedger.HasUnresolvedFailures, "correction and readback in the original stage resolve its failure");

var independentLedger = LedgerWorkflow();
string firstFailureId = independentLedger.ObserveTool("run_csharp", false, false);
string secondFailureId = independentLedger.ObserveTool("run_command", false, false);
string firstCorrectionId = independentLedger.ObserveTool("run_python", false, true);
string firstReadbackId = independentLedger.ObserveTool("list_objects", true, true);
Check(new[] { firstFailureId, secondFailureId, firstCorrectionId, firstReadbackId }
    .SequenceEqual(new[] { "op-1", "op-2", "op-3", "op-4" }) &&
    independentLedger.Snapshot()["unresolvedFailures"]!.AsArray()
        .Select(item => item!.GetValue<string>()).Order().SequenceEqual(new[] { firstFailureId, secondFailureId }),
    "observed operations receive stable sequential ledger IDs");
independentLedger.ResolveFailure(firstFailureId, firstCorrectionId, firstReadbackId,
    "Verified the first failed operation was corrected.");
Check(independentLedger.HasUnresolvedFailures &&
    independentLedger.Snapshot()["unresolvedFailures"]!.AsArray().Single()!.GetValue<string>() == secondFailureId,
    "resolving one failure leaves the second failure outstanding");
Reject(() => independentLedger.UpdatePlan(Wire.Plan("complete", "pending")),
    "one resolved failure cannot complete a stage with another outstanding failure");
Reject(() => independentLedger.ResolveFailure(firstFailureId, firstCorrectionId, firstReadbackId,
    "Repeat the first resolution."), "resolution must name an outstanding failed operation");
string secondCorrectionId = independentLedger.ObserveTool("run_csharp", false, true);
string secondReadbackId = independentLedger.ObserveTool("get_context", true, true);
independentLedger.ResolveFailure(secondFailureId, secondCorrectionId, secondReadbackId,
    "Verified the second failed operation was independently corrected.");
Check(!independentLedger.HasUnresolvedFailures &&
    independentLedger.Snapshot()["unresolvedFailures"]!.AsArray().Count == 0,
    "two failures require independent explicit resolutions");

foreach (string name in new[] { "get_viewport_image", "zoom_to_layer", "zoom_to_object", "set_camera" })
{
    foreach (bool success in new[] { true, false })
    {
        var viewWorkflow = new ProjectWorkflow();
        viewWorkflow.SetBasis(Wire.Basis("user_dimensions"), scaled);
        viewWorkflow.UpdatePlan(Wire.Plan("in_progress"));
        viewWorkflow.ObserveTool("get_context", true, true);
        viewWorkflow.ObserveTool(name, false, success);
        viewWorkflow.UpdatePlan(Wire.Plan("complete"));
        viewWorkflow.RequestReview();
        Check(!ProjectWorkflow.IsModifyingTool(name, false) && viewWorkflow.AwaitingReview,
            name + (success ? " success" : " failure") + " preserves geometric readback without creating a modifying failure");
    }
}

foreach (var basisCase in new[]
{
    (Source: "existing_model", Options: architectural with { ExistingGeometry = true }),
    (Source: "user_dimensions", Options: scaled),
    (Source: "reference", Options: architectural with
    {
        References = new[] { new ReferenceInput("plan", "Plan", "image", "unused.png", Width: 12) },
    }),
})
{
    var grounded = new ProjectWorkflow();
    grounded.SetBasis(Wire.Basis(basisCase.Source), basisCase.Options);
    Check(grounded.BasisConfirmed, basisCase.Source + " accepts its supplied scale evidence");
}
foreach (double? width in new double?[] { null, 0, -1 })
{
    var unscaled = architectural with
    {
        References = new[] { new ReferenceInput("plan", "Plan", "image", "unused.png", Width: width) },
    };
    Reject(() => new ProjectWorkflow().SetBasis(Wire.Basis("reference"), unscaled),
        "reference width " + (width?.ToString() ?? "unknown") + " cannot ground scale");
}
Reject(() => new ProjectWorkflow().SetBasis(new JsonObject { ["source"] = "user_dimensions", ["note"] = " " },
    scaled), "grounded scale still requires an explanatory note");

var millimetreReference = new ReferenceInput("first", "Millimetre plan", "image", "first.png",
    Width: 12000, ImageData: "data:image/png;base64,fixture", Units: "Millimeters");
var metreReference = new ReferenceInput("second", "Metre elevation", "image", "second.png",
    Width: 8, Units: "Meters");
var importedReferences = new List<ReferenceInput> { millimetreReference };
var firstAtImport = importedReferences[0].InUnits("Millimeters");
Check(firstAtImport.Width == 12000, "reference width remains usable in its original millimetre units");
var firstInMetres = importedReferences[0].InUnits("Meters");
Check(firstInMetres.Width is null && firstInMetres.Id == millimetreReference.Id &&
    firstInMetres.Path == millimetreReference.Path && firstInMetres.ImageData == millimetreReference.ImageData,
    "millimetre-to-metre unit change clears reference width and preserves reference identity and image");
importedReferences.Add(metreReference);
var referencesInMetres = importedReferences.Select(reference => reference.InUnits("Meters")).ToArray();
Check(referencesInMetres[0].Width is null && referencesInMetres[1].Width == 8,
    "second reference imported in metres cannot revalidate the first millimetre reference");
Check(importedReferences[0] == millimetreReference && firstAtImport.Width == 12000 &&
    importedReferences[0].InUnits("Millimeters").Width == 12000 &&
    importedReferences[1].InUnits("Millimeters").Width is null,
    "per-reference unit filtering preserves original records and treats each import independently");
var metreOptions = architectural with { Units = "Meters", References = new[] { firstInMetres } };
Reject(() => new ProjectWorkflow().SetBasis(Wire.Basis("reference"), metreOptions),
    "invalidated millimetre width cannot supply metre scale evidence");
var referenceWorkflow = new ProjectWorkflow();
referenceWorkflow.SetBasis(Wire.Basis("reference"), metreOptions with { References = referencesInMetres });
Check(referenceWorkflow.BasisConfirmed, "matching second reference supplies valid metre scale evidence");

var changedUnits = new ProjectWorkflow();
changedUnits.BeginRun("Millimeters");
var millimetreBasis = Wire.Basis("confirmed_assumption");
millimetreBasis["note"] = "Millimetre units; approved 12000 x 8000 plan, 3000 height.";
changedUnits.RecordBasisApproval(millimetreBasis["note"]!.GetValue<string>());
changedUnits.SetBasis(millimetreBasis, architectural with { Units = "Millimeters" });
changedUnits.ObserveTool("list_objects", true, true);
changedUnits.UpdatePlan(Wire.Plan("complete", "complete"));
changedUnits.RequestReview();
changedUnits.Accept();
changedUnits.BeginRun("Meters");
Check(!changedUnits.BasisConfirmed && changedUnits.HasPending &&
    changedUnits.Steps.All(step => step.Status == "pending") &&
    !changedUnits.AwaitingReview && !changedUnits.Accepted,
    "BeginRun unit change invalidates scale basis, completed stages and prior acceptance");
Reject(() => changedUnits.RequestReview(), "changed document units require renewed basis and checks");
Reject(() => changedUnits.SetBasis(millimetreBasis, architectural with { Units = "Meters" }),
    "unit change invalidates prior exact-note assumption approval");
Check(!changedUnits.IsBasisApproved(millimetreBasis["note"]!.GetValue<string>()),
    "approval lookup reflects unit-change invalidation");

bool unrelatedAnswered = false;
bool dimensionsAnswered = false;
using (var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
{
    0 => Wire.Calls(Wire.Call("premature", "run_csharp", new JsonObject { ["script"] = "premature" })),
    1 => Wire.Calls(Wire.Call("ungrounded", "set_design_basis", Wire.Basis("confirmed_assumption"))),
    2 => Wire.Calls(Wire.Call("question", "ask_user", new JsonObject
    {
        ["questions"] = new JsonArray(new JsonObject
        {
            ["question"] = "Use a flat roof?", ["options"] = new JsonArray("Yes", "Revise"),
        }),
    })),
    3 => Wire.Calls(Wire.Call("unrelated-consent", "set_design_basis", Wire.Basis("confirmed_assumption")),
        Wire.Call("still-premature", "run_csharp", new JsonObject { ["script"] = "unapproved dimensions" })),
    4 => Wire.Calls(Wire.Call("dimensions-question", "ask_user", new JsonObject
        {
            ["questions"] = new JsonArray(new JsonObject
            {
                ["question"] = "Use 12 m by 8 m?", ["options"] = new JsonArray("Yes", "Revise"),
            }),
        }),
        Wire.Call("basis", "set_design_basis", Wire.Basis("confirmed_assumption")),
        Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
        Wire.Call("build", "run_csharp", new JsonObject { ["script"] = "approved dimensions" }),
        Wire.Call("premature-complete", "update_project_plan", Wire.Plan("complete")),
        Wire.Call("readback", "list_objects", new JsonObject()),
        Wire.Call("checked", "update_project_plan", Wire.Plan("complete"))),
    5 => Wire.Text("Ready for review."),
    _ => throw new Exception("Unexpected model round"),
})))
using (var http = new HttpClient(fixture))
{
    var session = new ModelingSession();
    var approvals = new List<string>();
    async Task RunUnknownScale(string prompt) => await session.RunAsync(prompt, config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint),
        (call, _) =>
        {
            approvals.Add(call.Name);
            if (call.Name == "confirm_design_basis")
            {
                Check(!call.ReadOnly && call.Arguments["note"]!.GetValue<string>() == approvedNote,
                    "scale assumption requires explicit approval of the exact note");
                return Task.FromResult(dimensionsAnswered);
            }
            Check(dimensionsAnswered && session.Workflow.BasisConfirmed,
                "MCP approval occurs only after answer and basis confirmation");
            return Task.FromResult(true);
        },
        (question, _) =>
        {
            Check(fixture.Executed.Count == 0 && !session.Workflow.BasisConfirmed,
                "unknown-scale session asks before any mutation");
            if (question.ToJsonString().Contains("flat roof"))
            {
                unrelatedAnswered = true;
                return Task.FromResult("Yes, use a flat roof.");
            }
            dimensionsAnswered = true;
            return Task.FromResult("Yes, use 12 m by 8 m in metre units.");
        }, (_, _) => { }, CancellationToken.None, architectural);
    await RunUnknownScale("Design a building without supplied dimensions");
    Check(session.Outcome == "paused" && !session.Workflow.BasisConfirmed && fixture.Executed.Count == 0,
        "declining initial scale confirmation pauses before mutation");
    await RunUnknownScale("Choose a roof before confirming dimensions");
    Check(unrelatedAnswered && !dimensionsAnswered && session.Outcome == "paused" &&
        !session.Workflow.BasisConfirmed && fixture.Executed.Count == 0,
        "unrelated question consent still requires separate dimensional approval");
    await RunUnknownScale("Confirm the proposed dimensions and continue");
    Check(fixture.Executed.Select(call => call.Name).SequenceEqual(new[] { "run_csharp", "list_objects" }) &&
        approvals.SequenceEqual(new[]
        {
            "confirm_design_basis", "confirm_design_basis", "confirm_design_basis", "run_csharp", "list_objects",
        }),
        "premature mutation is refused before approval and never dispatched");
    Check(Wire.ToolResult(fixture.ChatRequests[1], "premature").StartsWith("ERROR") &&
        Wire.ToolResult(fixture.ChatRequests[2], "ungrounded").StartsWith("ERROR"),
        "model receives errors for mutation without basis and assumption without an answer");
    Check(unrelatedAnswered && Wire.ToolResult(fixture.ChatRequests[4], "unrelated-consent").StartsWith("ERROR") &&
        Wire.ToolResult(fixture.ChatRequests[4], "still-premature").StartsWith("ERROR"),
        "unrelated ask_user consent cannot bypass explicit basis approval or permit mutation");
    Check(Wire.ToolResult(fixture.ChatRequests[5], "premature-complete").StartsWith("ERROR") &&
        !Wire.ToolResult(fixture.ChatRequests[5], "checked").StartsWith("ERROR"),
        "session refuses completed stage before readback and accepts it after readback");
    Check(session.Outcome == "needs_review" && session.Workflow.AwaitingReview && !session.Workflow.Accepted,
        "architectural session finishes in awaiting-review state");
    session.Workflow.Accept();
    Check(session.Workflow.Accepted, "architectural session supports explicit human Accept");
}

var pendingWorkflow = new ProjectWorkflow();
pendingWorkflow.SetBasis(Wire.Basis("user_dimensions"), scaled);
pendingWorkflow.UpdatePlan(Wire.Plan("in_progress", "pending"));
pendingWorkflow.BeginRun();
Check(pendingWorkflow.HasPending && pendingWorkflow.Steps[1].Status == "pending",
    "pending plan survives a new run before readback");
pendingWorkflow.ObserveTool("get_context", true, true);
Check(pendingWorkflow.HasPending && pendingWorkflow.ContinuationPrompt().Contains("Facade openings") &&
    pendingWorkflow.ContinuationPrompt().Contains("Two named openings"),
    "pending plan and acceptance criteria survive continuation");

using (var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
{
    0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
        Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress", "pending")),
        Wire.Call("mass", "run_csharp", new JsonObject { ["script"] = "mass" }),
        Wire.Call("mass-readback", "get_context", new JsonObject()),
        Wire.Call("mass-complete", "update_project_plan", Wire.Plan("complete"))),
    >= 1 and <= 5 => Wire.Text("The mass is finished."),
    6 => Wire.Calls(Wire.Call("detail", "run_csharp", new JsonObject { ["script"] = "openings" }),
        Wire.Call("detail-readback", "list_objects", new JsonObject()),
        Wire.Call("detail-complete", "update_project_plan", Wire.Plan("complete", "complete"))),
    7 => Wire.Text("Openings checked. Please review."),
    _ => throw new Exception("Unexpected model round"),
})))
using (var http = new HttpClient(fixture))
{
    var session = new ModelingSession();
    var gateway = new MagpieGateway(http);
    var mcp = new RhinoMcpClient(http, HttpFixture.McpEndpoint);
    await session.RunAsync("Build mass and openings", config, gateway, mcp,
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { },
        CancellationToken.None, scaled);
    Check(session.Outcome == "paused" && session.Workflow.HasPending &&
        session.Workflow.Steps[0].Status == "complete" && session.Workflow.Steps[1].Status == "pending" &&
        !session.Workflow.AwaitingReview, "repeated no-tool finals preserve pending details instead of completing");
    Check(session.Workflow.Steps[1].Title == "Facade openings" &&
        session.Workflow.Steps[1].Acceptance == "Two named openings",
        "session partial plan update preserves omitted pending stage and original requirements");
    Check(fixture.ChatRequests.Skip(2).Take(4).All(request =>
        request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>().Contains("Facade openings") &&
        request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>().Contains("Two named openings")),
        "session nudges continuation with the pending stage and its acceptance criterion");
    await session.RunAsync("Continue pending openings", config, gateway, mcp,
        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { },
        CancellationToken.None, scaled);
    Check(session.Outcome == "needs_review" && !session.Workflow.HasPending &&
        session.Workflow.AwaitingReview && !session.Workflow.Accepted &&
        fixture.Executed.Select(call => call.Name).SequenceEqual(
            new[] { "run_csharp", "get_context", "run_csharp", "list_objects" }),
        "next run continues the saved plan through edits, readback and human review");
}

int commandAttempts = 0;
using (var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
{
    0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
        Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
        Wire.Call("stale-readback", "get_context", new JsonObject()),
        Wire.Call("failed-command", "run_command", new JsonObject { ["command"] = "_FixtureEdit" }),
        Wire.Call("stale-complete", "update_project_plan", Wire.Plan("complete"))),
    1 => Wire.Text("The command failed, but the previous geometry looked complete."),
    2 => Wire.Calls(Wire.Call("unrepaired-readback", "list_objects", new JsonObject()),
        Wire.Call("unrepaired-complete", "update_project_plan", Wire.Plan("complete"))),
    3 => Wire.Calls(Wire.Call("repaired-command", "run_command", new JsonObject { ["command"] = "_FixtureRepair" }),
        Wire.Call("repair-before-readback", "update_project_plan", Wire.Plan("complete")),
        Wire.Call("repaired-readback", "list_objects", new JsonObject()),
        Wire.Call("repair-before-resolution", "update_project_plan", Wire.Plan("complete")),
        Wire.Call("resolved-command", "resolve_modeling_failure",
            Wire.Resolution("op-2", "op-4", "op-5")),
        Wire.Call("repaired-complete", "update_project_plan", Wire.Plan("complete"))),
    4 => Wire.Text("Repaired geometry has been read back. Please review."),
    _ => throw new Exception("Failed modifying operation did not remain recoverable"),
})))
using (var http = new HttpClient(fixture))
{
    fixture.ToolResult = call => call.Name == "run_command" && ++commandAttempts == 1
        ? Wire.Result("Command changed some geometry before failing.", isError: true)
        : Wire.Result("Done");
    var session = new ModelingSession();
    await session.RunAsync("Build and check the building", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled);
    Check(Wire.ToolResult(fixture.ChatRequests[1], "failed-command").StartsWith("ERROR") &&
        Wire.ToolResult(fixture.ChatRequests[1], "stale-complete").StartsWith("ERROR"),
        "session rejects stale completion after a dispatched modifying command fails");
    Check(fixture.ChatRequests.Count == 5 &&
        fixture.ChatRequests[2]["messages"]!.AsArray().Last()!["role"]!.GetValue<string>() == "user",
        "failed run_command followed by no-tool final continues instead of entering review");
    Check(Wire.ToolResult(fixture.ChatRequests[3], "unrepaired-complete").StartsWith("ERROR") &&
        Wire.ToolResult(fixture.ChatRequests[4], "repair-before-readback").StartsWith("ERROR") &&
        Wire.ToolResult(fixture.ChatRequests[4], "repair-before-resolution").StartsWith("ERROR"),
        "session requires successful modifying repair, subsequent readback and explicit resolution");
    Check(fixture.Executed.Select(call => call.Name).SequenceEqual(
        new[] { "get_context", "run_command", "list_objects", "run_command", "list_objects" }) &&
        !Wire.ToolResult(fixture.ChatRequests[4], "repaired-complete").StartsWith("ERROR") &&
        session.Outcome == "needs_review" && session.Workflow.AwaitingReview,
        "successful command repair and fresh readback allow the session to reach human review");
    Check(Wire.ToolResult(fixture.ChatRequests[1], "failed-command").Contains("op-2") &&
        Wire.ToolResult(fixture.ChatRequests[4], "repaired-command").Contains("op-4") &&
        Wire.ToolResult(fixture.ChatRequests[4], "repaired-readback").Contains("op-5") &&
        Wire.ToolResult(fixture.ChatRequests[4], "resolved-command").StartsWith("Failure resolved.") &&
        !session.Workflow.HasUnresolvedFailures,
        "native operation IDs are returned to the model and explicitly linked to failure resolution");
}

using (var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
{
    0 => Wire.Calls(Wire.Call("failed-facade", "run_csharp", new JsonObject { ["script"] = "failed facade" })),
    1 => Wire.Calls(Wire.Call("material", "set_layer_material", new JsonObject()),
        Wire.Call("material-readback", "list_objects", new JsonObject()),
        Wire.Call("wrong-family-resolution", "resolve_modeling_failure", Wire.Resolution("op-1", "op-2", "op-3"))),
    2 => Wire.Text("The material was updated. Finished."),
    3 => Wire.Calls(Wire.Call("corrected-facade", "run_python", new JsonObject { ["script"] = "corrected facade" }),
        Wire.Call("facade-readback", "get_context", new JsonObject()),
        Wire.Call("resolved-facade", "resolve_modeling_failure", Wire.Resolution("op-1", "op-4", "op-5"))),
    4 => Wire.Text("Facade corrected and verified."),
    _ => throw new Exception("Nonarchitectural failure resolution did not finish"),
})))
using (var http = new HttpClient(fixture))
{
    fixture.ToolResult = call => call.Name == "run_csharp"
        ? Wire.Result("Facade operation failed.", isError: true) : Wire.Result("Done");
    var session = new ModelingSession();
    await session.RunAsync("Repair the facade", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
    Check(fixture.ChatRequests.All(request => request["tools"]!.AsArray().Any(tool =>
        tool!["function"]!["name"]!.GetValue<string>() == "resolve_modeling_failure")),
        "explicit failure-resolution tool is advertised outside architectural mode");
    var recoveryNudge = fixture.ChatRequests[3]["messages"]!.AsArray().Last()!;
    string recoveryInstructions = recoveryNudge["content"]!.GetValue<string>();
    Check(Wire.ToolResult(fixture.ChatRequests[2], "wrong-family-resolution").StartsWith("ERROR") &&
        recoveryNudge["role"]!.GetValue<string>() == "user" &&
        recoveryInstructions.Contains("resolve_modeling_failure") && recoveryInstructions.Contains("op-1"),
        "unrelated material success and no-tool final cannot bypass a facade failure outside architectural mode");
    Check(Wire.ToolResult(fixture.ChatRequests[4], "resolved-facade").StartsWith("Failure resolved.") &&
        fixture.Executed.Select(call => call.Name).SequenceEqual(
            new[] { "run_csharp", "set_layer_material", "list_objects", "run_python", "get_context" }) &&
        session.Outcome == "complete" && !session.Workflow.HasUnresolvedFailures,
        "nonarchitectural session completes only after compatible correction, readback and explicit resolution");
}

foreach (bool architecturalMode in new[] { false, true })
{
    using var fixture = new HttpFixture((turn, _) =>
    {
        if (turn > 3) throw new Exception("Unresolved error exceeded the two-reminder limit");
        return Wire.Response(turn == 0
            ? architecturalMode
                ? Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
                    Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
                    Wire.Call("failed", "run_command", new JsonObject { ["command"] = "_FixtureEdit" }))
                : Wire.Calls(Wire.Call("failed", "run_command", new JsonObject { ["command"] = "_FixtureEdit" }))
            : Wire.Text("The command failed. No corrected tool call is available."));
    });
    fixture.ToolResult = _ => Wire.Result("Modifying command failed.", isError: true);
    using var http = new HttpClient(fixture);
    var session = new ModelingSession();
    var diagnostics = new List<string>();
    await session.RunAsync("Build the building", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""),
        (role, text) => { if (role == "诊断") diagnostics.Add(text); },
        CancellationToken.None, scaled with { Architectural = architecturalMode });
    Check(fixture.ChatRequests.Count == 4 && diagnostics.Count(text => text.Contains("继续修正")) == 2,
        "architectural=" + architecturalMode + " unresolved modifying failure receives exactly two repair reminders");
    Check(session.Outcome == "paused" && !session.Workflow.AwaitingReview && !session.Workflow.Accepted &&
        fixture.Executed.Single().Name == "run_command",
        "architectural=" + architecturalMode + " repeated no-tool finals pause without repeating the failed mutation");
}

foreach (var failure in new[]
{
    (Name: "reserved keyword base",
        Script: "double base = 3; System.Console.WriteLine(base);",
        Error: "CS1001: Identifier expected. Invalid expression term 'base'.",
        Guidance: "baseHeight"),
    (Name: "Layer FindName return-index",
        Script: "int layerIndex = __rhino_doc__.Layers.FindName(\"Walls\");",
        Error: "CS0029: Cannot implicitly convert type 'Rhino.DocObjects.Layer' to 'int'.",
        Guidance: "layer?.Index"),
    (Name: "top-level return",
        Script: "return new { created = 1 };",
        Error: "CS0127: Since the entry point returns void, a return keyword must not be followed by an object expression.",
        Guidance: "System.Console.WriteLine"),
})
{
    string corrected = """
        double baseHeight = 3;
        var layer = __rhino_doc__.Layers.FindName("Walls");
        int layerIndex = layer?.Index ?? __rhino_doc__.Layers.Add("Walls", System.Drawing.Color.Black);
        var box = new Rhino.Geometry.Box(Rhino.Geometry.Plane.WorldXY,
            new Rhino.Geometry.Interval(0, 12), new Rhino.Geometry.Interval(0, 8),
            new Rhino.Geometry.Interval(0, baseHeight));
        var attributes = new Rhino.DocObjects.ObjectAttributes { Name = "Fixture mass", LayerIndex = layerIndex };
        var objectId = __rhino_doc__.Objects.AddBrep(box.ToBrep(), attributes);
        System.Console.WriteLine(objectId);
        """;
    using var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
    {
        0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
            Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
            Wire.Call("before", "get_context", new JsonObject()),
            Wire.Call("bad-script", "run_csharp", new JsonObject { ["script"] = failure.Script })),
        1 => Wire.Text("I could not compile the script."),
        2 => Wire.Calls(Wire.Call("fixed-script", "run_csharp", new JsonObject { ["script"] = corrected }),
            Wire.Call("after", "list_objects", new JsonObject()),
            Wire.Call("resolve-script", "resolve_modeling_failure", Wire.Resolution("op-2", "op-3", "op-4")),
            Wire.Call("checked", "update_project_plan", Wire.Plan("complete"))),
        3 => Wire.Text("Geometry read back. Ready for review."),
        _ => throw new Exception("Session failed to terminate after repair"),
    }));
    fixture.ToolResult = call => call.Name == "run_csharp" &&
        call.Arguments["script"]!.GetValue<string>() == failure.Script
        ? Wire.Result(failure.Error, isError: true)
        : Wire.Result(call.Name == "list_objects" ? "Fixture mass; 12 x 8 x 3 m; count=1" : "Done");
    using var http = new HttpClient(fixture);
    var session = new ModelingSession();
    var reports = new List<(string Role, string Text)>();
    await session.RunAsync("Build a 12 m by 8 m building, height 3 m", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (role, text) => reports.Add((role, text)),
        CancellationToken.None, scaled);
    string errorResult = Wire.ToolResult(fixture.ChatRequests[1], "bad-script");
    Check(errorResult.StartsWith("ERROR") && errorResult.Contains(failure.Error) &&
        errorResult.Contains(failure.Guidance), failure.Name + " isError retains error and actionable repair guidance");
    var nudge = fixture.ChatRequests[2]["messages"]!.AsArray().Last()!;
    Check(nudge["role"]!.GetValue<string>() == "user" &&
        nudge["content"]!.GetValue<string>().Contains("resolve_modeling_failure") &&
        nudge["content"]!.GetValue<string>().Contains("op-2") &&
        reports.Any(report => report.Role == "诊断" && report.Text.Contains("继续修正")),
        failure.Name + " no-tool final triggers a repair nudge");
    Check(fixture.Executed.Select(call => call.Name).SequenceEqual(
        new[] { "get_context", "run_csharp", "run_csharp", "list_objects" }) &&
        fixture.Executed[2].Arguments["script"]!.GetValue<string>() == corrected &&
        Wire.ToolResult(fixture.ChatRequests[3], "after").Contains("12 x 8 x 3 m"),
        failure.Name + " executes corrected script and fresh readback before finishing");
    Check(fixture.ChatRequests.All(request => request["stream"]!.GetValue<bool>()) &&
        reports.Any(report => report.Role == "assistant_delta") &&
        Wire.ToolResult(fixture.ChatRequests[3], "resolve-script").StartsWith("Failure resolved.") &&
        !session.Workflow.HasUnresolvedFailures &&
        session.Outcome == "needs_review" && session.Workflow.AwaitingReview && !session.Workflow.Accepted,
        failure.Name + " recovery streams output and reaches human review");
}

var reviewFailures = new List<Exception>();
async Task ReviewRegression(string name, Func<Task> regression)
{
    try { await regression(); }
    catch (Exception error)
    {
        reviewFailures.Add(error);
        Console.WriteLine("FAIL " + name + " - " + error.Message);
    }
}

foreach (bool inspectAfterBegin in new[] { false, true })
{
    await ReviewRegression("readback from a previous run; new context=" + inspectAfterBegin, () =>
    {
        var resumedLedger = LedgerWorkflow();
        string failureId = resumedLedger.ObserveTool("run_csharp", false, false);
        string correctionId = resumedLedger.ObserveTool("run_python", false, true);
        string oldReadbackId = resumedLedger.ObserveTool("list_objects", true, true);
        resumedLedger.BeginRun();
        string? currentReadbackId = inspectAfterBegin
            ? resumedLedger.ObserveTool("get_context", true, true) : null;
        Reject(() => resumedLedger.ResolveFailure(failureId, correctionId, oldReadbackId,
            "Use the readback saved before continuing."),
            "BeginRun rejects a previous run's readback ID; new context=" + inspectAfterBegin);
        Check(resumedLedger.HasUnresolvedFailures,
            "rejected previous-run readback preserves the outstanding failure; new context=" + inspectAfterBegin);
        currentReadbackId ??= resumedLedger.ObserveTool("list_objects", true, true);
        resumedLedger.ResolveFailure(failureId, correctionId, currentReadbackId,
            "Current-run readback verifies the earlier correction after resuming.");
        Check(!resumedLedger.HasUnresolvedFailures,
            "current-run readback can verify the preserved correction; new context=" + inspectAfterBegin);
        return Task.CompletedTask;
    });
}

await ReviewRegression("verbose native results preserve ledger metadata", async () =>
{
    using var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
    {
        0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
            Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
            Wire.Call("verbose-failure", "run_csharp", new JsonObject { ["script"] = "broken" })),
        1 => Wire.Calls(Wire.Call("verbose-correction", "run_python", new JsonObject { ["script"] = "corrected" }),
            Wire.Call("verbose-readback", "list_objects", new JsonObject())),
        2 => Wire.Calls(Wire.Call("verbose-resolution", "resolve_modeling_failure",
                Wire.Resolution("op-1", "op-2", "op-3")),
            Wire.Call("verbose-complete", "update_project_plan", Wire.Plan("complete"))),
        3 => Wire.Text("Verbose results were verified. Please review."),
        _ => throw new Exception("Verbose-result repair failed to finish"),
    }));
    string verboseOutput = new('x', 70000);
    fixture.ToolResult = call => Wire.Result(call.Name + "\n" + verboseOutput,
        isError: call.Name == "run_csharp");
    using var http = new HttpClient(fixture);
    var session = new ModelingSession();
    await session.RunAsync("Repair and inspect geometry with verbose native output", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled);
    string failedResult = Wire.ToolResult(fixture.ChatRequests[1], "verbose-failure");
    string correctedResult = Wire.ToolResult(fixture.ChatRequests[2], "verbose-correction");
    string readbackResult = Wire.ToolResult(fixture.ChatRequests[2], "verbose-readback");
    Check(new[] { failedResult, correctedResult, readbackResult }.All(text => text.Contains("[Result truncated.")),
        "session truncates failure, correction and readback output exceeding 64K");
    var missingMetadata = new List<string>();
    if (!failedResult.Contains("operationId: op-1") || !failedResult.Contains("failureId: op-1"))
        missingMetadata.Add("failed operationId/failureId");
    if (!correctedResult.Contains("operationId: op-2")) missingMetadata.Add("corrective operationId");
    if (!readbackResult.Contains("operationId: op-3")) missingMetadata.Add("readback operationId");
    Check(missingMetadata.Count == 0,
        "truncated native results retain ledger IDs; missing " +
        (missingMetadata.Count == 0 ? "none" : string.Join(", ", missingMetadata)));
    Check(Wire.ToolResult(fixture.ChatRequests[3], "verbose-resolution").StartsWith("Failure resolved.") &&
        session.Outcome == "needs_review" && !session.Workflow.HasUnresolvedFailures,
        "verbose-output session explicitly resolves failure before review");
});

if (reviewFailures.Count > 0)
    throw new AggregateException("Review regressions failed.", reviewFailures);
passed += await LocalToolTests.RunAsync(config);
passed += await ChatUsageTests.RunAsync(config);
passed += ModelingContextProjectionTests.Run();
{
    var managed = new ProjectWorkflow();
    managed.BeginRun("Meters");
    managed.SetBasis(new JsonObject { ["source"] = "user_dimensions", ["note"] = "12 m × 8 m" }, scaled);
    JsonObject Plan(string status) => JsonNode.Parse($$"""
        {"goal":"Verify the plan","steps":[{"id":"plan","title":"Plan","acceptance":"Routes and areas","status":"{{status}}"}]}
        """)!.AsObject();
    managed.UpdatePlan(Plan("in_progress"));
    managed.SetPlanReview(new JsonObject { ["status"] = "pending", ["revision"] = 0 });
    managed.ObserveTool("get_context", true, true);
    Reject(() => managed.UpdatePlan(Plan("complete")), "generic readback cannot finish a managed project without plan evidence");
    managed.SetPlanReview(new JsonObject { ["status"] = "failed", ["revision"] = 0 });
    Reject(managed.RequestReview, "failed plan review never grants acceptance");
    managed.SetPlanReview(new JsonObject { ["status"] = "passed", ["revision"] = 0 });
    managed.ObserveTool("openbim_plan_review", true, true);
    managed.UpdatePlan(Plan("complete"));
    managed.RequestReview();
    managed.Accept();
    Check(managed.Accepted, "current passing plan evidence and human acceptance can close the managed workflow");
    managed.ObserveTool("openbim_apply", false, true);
    managed.ObserveTool("get_context", true, true);
    Reject(managed.RequestReview, "after an edit a generic readback does not resurrect prior plan evidence");
    Check(!managed.Accepted && managed.Snapshot()["planReview"]?["status"]?.GetValue<string>() == "stale",
        "edit invalidates visible managed acceptance and flags evidence as stale");
    managed.SetPlanReview(new JsonObject { ["status"] = "unavailable" });
    Reject(managed.RequestReview, "missing plan runtime fails closed for a managed project");
    managed.SetPlanReview(null);
    Reject(managed.RequestReview, "undoing an entire building cannot reuse its previous plan completion");
    Check(!managed.RequiresPlanReview && managed.HasPending, "an actually empty document can return to ordinary CAD");
    var restored = new ProjectWorkflow();
    restored.RestoreCheckpoint(managed.ExportCheckpoint());
    Check(restored.Goal == managed.Goal && restored.Basis == managed.Basis && restored.HasPending &&
        !restored.BasisConfirmed && !restored.Accepted && !restored.AwaitingReview,
        "reopening restores the task and basis notes but requires fresh evidence and scale confirmation");
    Reject(restored.RequestReview, "restored notes cannot replay human acceptance");
    var longPlan = Plan("pending");
    longPlan["steps"]![0]!["acceptance"] = new string('a', 2048);
    var lengths = new ProjectWorkflow();
    lengths.UpdatePlan(longPlan);
    var lengthReplay = new ProjectWorkflow();
    lengthReplay.RestoreCheckpoint(lengths.ExportCheckpoint());
    Check(lengthReplay.Steps.Single().Acceptance.Length == 2048, "valid boundary-length checkpoint round-trips");
    longPlan["steps"]![0]!["acceptance"] = new string('a', 2049);
    Reject(() => new ProjectWorkflow().UpdatePlan(longPlan), "unrestorable checkpoint length is rejected at entry");
}

using (var directFixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
    ? Wire.Calls(Wire.Call("apply", "openbim_apply", new JsonObject()),
        Wire.Call("apply-layout", "openbim_apply_layout", new JsonObject()),
        Wire.Call("generic", "run_csharp", new JsonObject { ["script"] = "unapproved write" }),
        Wire.Call("read", "get_context", new JsonObject()))
    : Wire.Text("Inspected."))))
using (var directHttp = new HttpClient(directFixture))
{
    var managedTools = new ManagedFixtureTools(managed: true);
    var session = new ModelingSession();
    await session.RunAsync("Inspect", config, new MagpieGateway(directHttp),
        new RhinoMcpClient(directHttp, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, localTools: managedTools);
    Check(managedTools.Called.Count == 0 && directFixture.Executed.Select(x => x.Name).SequenceEqual(["get_context"]),
        "direct CAD mode cannot dispatch local or generic writes against a managed building");
}
using (var deniedFixture = new HttpFixture((_, _) => Wire.Response(Wire.Calls(
    Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
    Wire.Call("apply-layout", "openbim_apply_layout", new JsonObject()),
    Wire.Call("bypass", "openbim_apply", new JsonObject())))))
using (var deniedHttp = new HttpClient(deniedFixture))
{
    var managedTools = new ManagedFixtureTools(managed: false);
    var session = new ModelingSession();
    await session.RunAsync("Build", config, new MagpieGateway(deniedHttp),
        new RhinoMcpClient(deniedHttp, HttpFixture.McpEndpoint), (call, _) => Task.FromResult(call.Name != "openbim_apply_layout"),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled, managedTools);
    Check(session.Outcome == "paused" && managedTools.Called.Count == 0,
        "declined candidate cannot fall through to a later raw apply in the same batch");
}
using (var loopFixture = new HttpFixture((turn, _) => Wire.Response(turn switch
{
    0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
        Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
        Wire.Call("candidate", "openbim_layout_candidate", new JsonObject())),
    1 => Wire.Calls(Wire.Call("apply", "openbim_apply_layout", new JsonObject { ["candidateId"] = "test-draft" }),
        Wire.Call("inspect", "get_context", new JsonObject()),
        Wire.Call("premature", "update_project_plan", Wire.Plan("complete"))),
    2 => Wire.Calls(Wire.Call("verify", "openbim_plan_review", new JsonObject { ["expectedRevision"] = 0 }),
        Wire.Call("complete", "update_project_plan", Wire.Plan("complete"))),
    _ => Wire.Text("Current plan checked; awaiting review."),
})))
using (var loopHttp = new HttpClient(loopFixture))
{
    var pipeline = new ManagedFixtureTools(managed: false, simulatePipeline: true);
    var session = new ModelingSession();
    await session.RunAsync("Model a two-storey brief", config, new MagpieGateway(loopHttp),
        new RhinoMcpClient(loopHttp, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled, pipeline);
    Check(pipeline.Called.SequenceEqual(["openbim_layout_candidate", "openbim_apply_layout", "openbim_plan_review"]) &&
        Wire.ToolResult(loopFixture.ChatRequests[2], "premature").Contains("平面尚未通过") &&
        session.Outcome == "needs_review" && !session.Workflow.Accepted,
        "streamed candidate workflow needs trusted native plan evidence before human review");
}

string WithUsage(string response) => response.Replace("data: [DONE]",
    "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":19,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":1}}}\r\n\r\ndata: [DONE]");

using (var metered = new HttpFixture((turn, _) => Wire.Response(turn == 0
    ? WithUsage(Wire.Calls(Wire.Call("inspect", "get_context", new JsonObject())))
    : Wire.Text("Inspected"))))
using (var meteredHttp = new HttpClient(metered))
{
    var session = new ModelingSession();
    await session.RunAsync("Inspect", config, new MagpieGateway(meteredHttp),
        new RhinoMcpClient(meteredHttp, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, new(MeasureRun: true));
    Check(session.LastRunMetrics is { ModelCalls: 2, ModelResponses: 2, CallsWithUsage: 1,
        CallsWithoutUsage: 1, InputTokens: null, OutputTokens: null, CachedInputTokens: null,
        ObservedInputTokens: 19, ObservedOutputTokens: 2, ToolDispatches: 1,
        ToolErrors: 0, MeteringTimeMs: >= 0, FirstTextMs: not null, Outcome: "complete" },
        "meter retains observed usage but cannot claim complete totals for a partially measured run");
}

async Task<ModelingRunMetrics> ReplayRepeatedViews(bool compact)
{
    const int views = 6;
    using var fixture = new HttpFixture((turn, _) => Wire.Response(WithUsage(turn < views
        ? Wire.Calls(Wire.Call($"view-{turn}", "get_viewport_image", new JsonObject()))
        : Wire.Text("Inspected"))));
    fixture.ExtraTools.Add(new JsonObject
    {
        ["name"] = "get_viewport_image", ["description"] = "capture the viewport",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
    });
    fixture.ToolResult = _ => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "image", ["mimeType"] = "image/png", ["data"] = new string('A', 8192),
        }),
    };
    using var http = new HttpClient(fixture);
    var session = new ModelingSession();
    await session.RunAsync("Inspect repeatedly", config, new MagpieGateway(http),
        new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
        (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None,
        new(MeasureRun: true, CompactContext: compact));
    return session.LastRunMetrics ?? throw new Exception("Replay did not produce metrics");
}

var fullReplay = await ReplayRepeatedViews(false);
var compactReplay = await ReplayRepeatedViews(true);
Check(fullReplay is { ModelCalls: 7, ToolDispatches: 6, CallsWithUsage: 7, InputTokens: 133, OutputTokens: 14,
    CachedInputTokens: 7, Outcome: "complete" } &&
    compactReplay.ModelCalls == fullReplay.ModelCalls && compactReplay.ToolDispatches == fullReplay.ToolDispatches &&
    compactReplay.ToolSchemaJsonBytes == fullReplay.ToolSchemaJsonBytes && compactReplay.Outcome == fullReplay.Outcome &&
    compactReplay.MessageJsonBytes < fullReplay.MessageJsonBytes,
    $"same replay execution with fewer message JSON bytes: {fullReplay.MessageJsonBytes} -> {compactReplay.MessageJsonBytes}");

Console.WriteLine($"{passed} workflow checks passed");

sealed class ManagedFixtureTools(bool managed, bool simulatePipeline = false) : ILocalModelingTools, IPlanReviewTools
{
    public List<string> Called { get; } = [];
    private string? status = managed ? "pending" : null;
    public JsonArray ListTools() => new(new[] { "openbim_apply", "openbim_apply_layout", "openbim_layout_candidate", "openbim_plan_review" }.Select(name => (JsonNode)new JsonObject
    {
        ["name"] = name, ["description"] = "managed write", ["inputSchema"] = new JsonObject { ["type"] = "object" },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = name is "openbim_layout_candidate" or "openbim_plan_review" },
    }).ToArray());
    public Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token)
    {
        Called.Add(name);
        if (simulatePipeline)
        {
            if (name == "openbim_apply_layout") status = "pending";
            if (name == "openbim_plan_review") status = "passed";
            return Task.FromResult(Wire.Result("local call completed"));
        }
        return Task.FromResult(Wire.Result("unexpected write"));
    }
    public Task<JsonObject?> ReadPlanReviewAsync(bool verifyNative, CancellationToken token) =>
        Task.FromResult<JsonObject?>(status is not null ? new JsonObject { ["status"] = status } : null);
}

static class Wire
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Frame(JsonObject delta, string? finishReason = null, bool multiline = false)
    {
        string choice = new JsonObject { ["delta"] = delta, ["finish_reason"] = finishReason }
            .ToJsonString(JsonOptions);
        return multiline
            ? "event: message\r\nid: fixture\r\ndata: {\"choices\": [\r\ndata: " + choice + "\r\ndata: ]}\r\n\r\n"
            : "data: {\"choices\":[" + choice + "]}\r\n\r\n";
    }

    public static string Finish(string reason) =>
        Frame(new JsonObject(), reason) + "data: [DONE]\r\n\r\n";

    public static string Text(string text) => Frame(new JsonObject { ["content"] = text }) + Finish("stop");

    public static JsonObject Fragment(int index, string? id, string name, string arguments)
    {
        var fragment = new JsonObject
        {
            ["index"] = index,
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        };
        if (id is not null) fragment["id"] = id;
        return fragment;
    }

    public static JsonObject Call(string id, string name, JsonObject arguments) =>
        new() { ["id"] = id, ["name"] = name, ["arguments"] = arguments };

    public static string Calls(params JsonObject[] calls) => Calls(calls, true);

    public static string Calls(JsonObject call, bool finish) => Calls(new[] { call }, finish);

    private static string Calls(JsonObject[] calls, bool finish)
    {
        var fragments = new JsonArray();
        for (int index = 0; index < calls.Length; index++)
            fragments.Add(Fragment(index, calls[index]["id"]!.GetValue<string>(),
                calls[index]["name"]!.GetValue<string>(), calls[index]["arguments"]!.ToJsonString(JsonOptions)));
        return Frame(new JsonObject { ["tool_calls"] = fragments }) + (finish ? Finish("tool_calls") : "");
    }

    public static JsonObject Basis(string source) => new()
    {
        ["source"] = source, ["note"] = "Metre units; approved 12 m x 8 m plan, 3 m height.",
    };

    public static JsonObject Resolution(string failureId, string correctiveOperationId, string readbackOperationId) => new()
    {
        ["failureId"] = failureId, ["correctiveOperationId"] = correctiveOperationId,
        ["readbackOperationId"] = readbackOperationId,
        ["verification"] = "Readback confirms the corrected geometry meets the stage dimensions and object count.",
    };

    public static JsonObject Plan(string massStatus, string? detailStatus = null)
    {
        var steps = new JsonArray(new JsonObject
        {
            ["id"] = "mass", ["title"] = "Building mass", ["acceptance"] = "Read back 12 x 8 x 3 m",
            ["status"] = massStatus,
        });
        if (detailStatus is not null)
            steps.Add(new JsonObject
            {
                ["id"] = "detail", ["title"] = "Facade openings", ["acceptance"] = "Two named openings",
                ["status"] = detailStatus,
            });
        return new JsonObject { ["goal"] = "Checked building", ["steps"] = steps };
    }

    public static JsonObject Result(string text, bool isError = false) => new()
    {
        ["isError"] = isError,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    public static string ToolResult(JsonObject request, string id) => request["messages"]!.AsArray()
        .Single(message => message?["role"]?.GetValue<string>() == "tool" &&
            message["tool_call_id"]?.GetValue<string>() == id)!["content"]!.GetValue<string>();

    public static HttpResponseMessage Response(string data) => Response(new FragmentedStream(data));

    public static HttpResponseMessage Response(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

sealed class HttpFixture(Func<int, JsonObject, HttpResponseMessage> chat) : HttpMessageHandler
{
    public static Uri McpEndpoint { get; } = new("http://localhost:10500/agent");
    public List<JsonObject> ChatRequests { get; } = [];
    public List<ToolInvocation> Executed { get; } = [];
    public JsonArray ExtraTools { get; } = [];
    public Func<ToolInvocation, JsonObject> ToolResult { get; set; } = _ => Wire.Result("Done");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        if (request.RequestUri!.AbsolutePath == "/v1/chat/completions")
        {
            ChatRequests.Add(body);
            return chat(ChatRequests.Count - 1, body);
        }
        string method = body["method"]!.GetValue<string>();
        if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.NoContent);
        JsonObject result;
        if (method == "initialize") result = new JsonObject { ["protocolVersion"] = "2025-03-26" };
        else if (method == "tools/list")
        {
            var tools = new JsonArray();
            foreach (string name in new[]
            {
                "get_context", "list_objects", "run_csharp", "run_python", "run_command", "set_layer_material", "ask_user",
            })
                tools.Add(new JsonObject
                {
                    ["name"] = name, ["description"] = name,
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    ["annotations"] = new JsonObject
                    {
                        ["readOnlyHint"] = name is "get_context" or "list_objects",
                    },
                });
            foreach (var tool in ExtraTools) tools.Add(tool?.DeepClone());
            result = new JsonObject { ["tools"] = tools };
        }
        else if (method == "tools/call")
        {
            string name = body["params"]!["name"]!.GetValue<string>();
            var invocation = new ToolInvocation(name, body["params"]!["arguments"]!.DeepClone().AsObject(),
                name is "get_context" or "list_objects");
            Executed.Add(invocation);
            result = ToolResult(invocation);
        }
        else throw new Exception("Unexpected MCP method " + method);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result,
            }),
        };
    }
}

sealed class FragmentedStream(string data, int? pauseAt = null) : Stream
{
    private readonly byte[] bytes = Encoding.UTF8.GetBytes(data);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int position;
    public int ReadCount { get; private set; }
    public int LargestRead { get; private set; }
    public Task Paused => paused.Task;
    public void Release() => release.TrySetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => bytes.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        if (pauseAt.HasValue && position >= pauseAt.Value && !release.Task.IsCompleted)
        {
            paused.TrySetResult();
            await release.Task.WaitAsync(token);
        }
        int count = Math.Min(buffer.Length, Math.Min(1 + ReadCount % 3, bytes.Length - position));
        if (pauseAt.HasValue && position < pauseAt.Value) count = Math.Min(count, pauseAt.Value - position);
        bytes.AsMemory(position, count).CopyTo(buffer);
        position += count;
        ReadCount++;
        LargestRead = Math.Max(LargestRead, count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
