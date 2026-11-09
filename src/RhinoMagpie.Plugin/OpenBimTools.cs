using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Forms;
using Miki.OpenBim;
using Miki.OpenBim.Rhino;
using Rhino;
using RhinoMagpie.Agent;

namespace RhinoMagpie.Plugin;

public sealed class OpenBimTools(RhinoDoc document) : ILocalModelingTools
{
    private readonly uint documentSerial = document.RuntimeSerialNumber;

    private static string PluginDirectory => Path.GetDirectoryName(typeof(OpenBimTools).Assembly.Location)!;
    // Package contents change only on reinstall. Full integrity is checked again at export.
    public static bool IfcRuntimeAvailable { get; } = IfcRuntimeBundle.IsPresent(PluginDirectory);

    public JsonArray ListTools()
    {
        var tools = JsonNode.Parse("""
        [
          {"name":"openbim_catalog","description":"Inspect the local reusable lightweight BIM toolkit or one project's parameters. Request only the needed topic. No materials or construction-junction specifications are required. All source lengths are metres.","inputSchema":{"type":"object","properties":{"topic":{"type":"string","enum":["tools","template","project","levels","grids","spaces","walls","slabs","types","openings","stairs"]}},"additionalProperties":false},"annotations":{"readOnlyHint":true}},
          {"name":"openbim_apply","description":"Build exact native geometry or patch the current lightweight BIM project using the installed reusable toolkit. Supply exactly one of project, patch, or explicit template='two-storey'. Patches require expectedRevision from inspection. Shared type edits update matching Blocks and real host openings; floor edits affect dependent parts only. Do not use the demo template unless the user requests that example.","inputSchema":{"type":"object","properties":{"project":{"type":"object"},"patch":{"type":"object"},"template":{"type":"string","enum":["two-storey"]},"expectedRevision":{"type":"integer","minimum":0}},"additionalProperties":false},"annotations":{"readOnlyHint":false}},
          {"name":"openbim_inspect","description":"Read back and verify current native BIM geometry, exact solid volumes, through-openings, shared Blocks and source/geometry synchronization. Returns a compact summary; request ids only for specific part bounds. This is actual document readback, not a plan-status claim.","inputSchema":{"type":"object","properties":{"ids":{"type":"array","maxItems":32,"items":{"type":"string"}}},"additionalProperties":false},"annotations":{"readOnlyHint":true}},
          {"name":"openbim_export_ifc","description":"Export the synchronized current BIM project to a user-requested absolute .ifc path, reopen with IfcOpenShell and verify semantics and actual geometry. Uses the bundled offline runtime; no external Python or setup is needed. Never overwrites an existing file. Export is not a modeling repair.","inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false},"annotations":{"readOnlyHint":false}}
        ]
        """)!.AsArray();
        if (!IfcRuntimeAvailable)
        {
            var export = tools.First(item => item?["name"]?.GetValue<string>() == "openbim_export_ifc");
            tools.Remove(export);
        }
        return tools;
    }

    public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token)
    {
        try
        {
            JsonObject result = name == "openbim_export_ifc"
                ? await ExportAsync(arguments, token)
                : await OnUiAsync(() => name switch
                {
                    "openbim_catalog" => Catalog(arguments),
                    "openbim_apply" => Apply(arguments),
                    "openbim_inspect" => Compact(RhinoModel.Inspect(Current()), arguments),
                    _ => throw new InvalidOperationException("未知的简易 BIM 工具。"),
                }, token);
            return Text(result.ToJsonString());
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
            UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            var result = Text(error.Message);
            result["isError"] = true;
            return result;
        }
    }

    private JsonObject Catalog(JsonObject arguments)
    {
        Only(arguments, "topic");
        string topic = arguments["topic"]?.GetValue<string>() ?? "tools";
        if (topic == "tools")
            return new JsonObject
            {
                ["library"] = "Miki-GrasshopperWarehouse", ["contract"] = 1, ["units"] = "m",
                ["capabilities"] = "plan axes/spaces; straight walls with through-openings; rectangular shared door/window Blocks; slabs with voids; straight conceptual stairs; scoped patches" + (IfcRuntimeAvailable ? "; IFC4 export/readback" : ""),
                ["parameters"] = "No materials, wall build-ups or connection details required. Query topic=template or a collection name for its schema.",
                ["source"] = "https://github.com/mikidaren/Miki-GrasshopperWarehouse",
                ["ifcRuntimeConfigured"] = IfcRuntimeAvailable,
                ["ifcRuntime"] = IfcRuntimeAvailable ? "bundled-offline" : "unavailable",
            };
        if (topic == "template")
            return new JsonObject { ["template"] = "two-storey", ["sizeM"] = "12 x 8", ["storeys"] = 2,
                ["storeyHeightM"] = 3.2, ["wallThicknessM"] = .24,
                ["note"] = "Generic test example, not a user-approved architectural design. Explicitly request template='two-storey' to create it in a separate empty document." };
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["levels"] = "{id,elevation,height}", ["grids"] = "{id,start:[x,y],end:[x,y]}",
            ["spaces"] = "{id,level,name,boundary:[[x,y],...]}",
            ["walls"] = "{id,level,start:[x,y],end:[x,y],thickness,height?: follows level}",
            ["slabs"] = "{id,level,boundary:[[x,y],...],thickness,offset?:0,voids?:[[[x,y],...]]}",
            ["types"] = "{id,kind:window|door,form:rectangular,width,height,depth?:0.12}",
            ["openings"] = "{id,wall,type,offset:left edge along wall,sill:above level}",
            ["stairs"] = "{id,from:level,to:level,origin:[x,y],direction:[x,y],width,run,risers}",
        };
        if (topic != "project" && !schemas.ContainsKey(topic)) throw new InvalidOperationException("未知的参数集合。");
        var project = RhinoModel.ReadProject(Current());
        var result = new JsonObject { ["units"] = "m", ["project"] = project?.Id, ["revision"] = project?.Revision };
        if (topic == "project")
        {
            result["required"] = "{schemaVersion:1,id,name,revision:0,units:m,levels:[],grids:[],spaces:[],walls:[],slabs:[],types:[],openings:[],stairs:[]}";
            result["topics"] = new JsonArray(schemas.Keys.Select(key => (JsonNode)JsonValue.Create(key)!).ToArray());
        }
        else
        {
            result["schema"] = schemas[topic];
            if (project is not null) result[topic] = JsonNode.Parse(ProjectEngine.Serialize(project))![topic]!.DeepClone();
        }
        return result;
    }

    private JsonObject Apply(JsonObject arguments)
    {
        Only(arguments, "project", "patch", "template", "expectedRevision");
        string[] inputs = ["project", "patch", "template"];
        if (inputs.Count(key => arguments[key] is not null) != 1)
            throw new InvalidOperationException("请只提供 project、patch 或明确的 template 之一。");
        var current = RhinoModel.ReadProject(Current());
        BuildingProject next;
        if (arguments["patch"] is JsonObject patch)
        {
            if (current is null) throw new InvalidOperationException("当前文档尚无简易 BIM 项目。");
            if (arguments["expectedRevision"]?.GetValue<int>() != current.Revision)
                throw new InvalidOperationException("项目版本已变化，请先 openbim_inspect，再提交当前 expectedRevision。");
            next = ProjectEngine.ApplyPatch(current, patch);
        }
        else
        {
            if (current is not null) throw new InvalidOperationException("项目已存在，请用局部 patch，不覆盖整个项目。");
            if (arguments["template"] is not null)
            {
                if (arguments["template"]!.GetValue<string>() != "two-storey") throw new InvalidOperationException("未知模板。");
                if (Current().Objects.Count != 0) throw new InvalidOperationException("示例模板只在独立空文档创建，不混入已有模型。");
                next = ProjectEngine.CreateDemo();
            }
            else next = ProjectEngine.Parse(arguments["project"]!.ToJsonString());
        }
        return Compact(RhinoModel.Apply(Current(), next), new JsonObject());
    }

    private async Task<JsonObject> ExportAsync(JsonObject arguments, CancellationToken token)
    {
        Only(arguments, "path");
        string path = arguments["path"]?.GetValue<string>() ?? "";
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请提供用户要求的完整 .ifc 文件路径。");
        if (File.Exists(path)) throw new InvalidOperationException("目标文件已存在，请使用新文件名，未覆盖原文件。");
        string reportPath = path + ".validation.json";
        if (File.Exists(reportPath)) throw new InvalidOperationException("验证报告已存在，请使用新文件名。");
        var snapshot = await OnUiAsync(() =>
        {
            var verification = RhinoModel.Inspect(Current());
            return new JsonObject { ["source"] = RhinoModel.ReadProjectJson(Current()), ["revision"] = verification["revision"]!.DeepClone() };
        }, token);
        IfcRuntimeLocation runtime;
        try { runtime = IfcRuntimeBundle.Resolve(PluginDirectory); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException("内置 IFC 组件缺失或校验失败，请重新安装完整离线插件包，无需自行配置 Python。", error);
        }
        string temporary = Path.Combine(Path.GetTempPath(), "miki-openbim-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, snapshot["source"]!.GetValue<string>(), token);
            var start = IfcRuntimeBundle.CreateStartInfo(runtime,
                ["export", "--project", temporary, "--output", path, "--report", reportPath]);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 IFC 导出工具。");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw new InvalidOperationException("IFC 导出已停止或超时，请检查目标文件，尚未确认导出成功。");
            }
            string error = await stderr;
            string output = await stdout;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("IFC 导出或读回验证失败：" +
                    IfcRuntimeDiagnostics.DescribeWorkerFailure(output, error));
            }
            var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, token))?.AsObject()
                ?? throw new InvalidOperationException("IFC 验证报告无效。");
            if (report["verified"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("IFC 读回未通过，不能宣称导出完成。");
            bool unchanged = await OnUiAsync(() => RhinoModel.ReadProject(Current())?.Revision == snapshot["revision"]!.GetValue<int>(), token);
            return new JsonObject { ["verified"] = true, ["path"] = path, ["report"] = reportPath,
                ["exportedRevision"] = snapshot["revision"]!.DeepClone(), ["currentRevisionUnchanged"] = unchanged,
                ["note"] = unchanged ? "IFC4 已重新读取并验证几何及关系。" : "导出的是已记录的旧版本，当前项目已变化。" };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private RhinoDoc Current()
    {
        var current = RhinoDoc.FromRuntimeSerialNumber(documentSerial);
        if (current is null || RhinoDoc.ActiveDoc?.RuntimeSerialNumber != documentSerial)
            throw new InvalidOperationException("活动文档已切换或关闭，尚未执行此操作。");
        return current;
    }

    private Task<JsonObject> OnUiAsync(Func<JsonObject> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Instance.AsyncInvoke(() =>
        {
            try { token.ThrowIfCancellationRequested(); Current(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private Task<bool> OnUiAsync(Func<bool> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Instance.AsyncInvoke(() =>
        {
            try { token.ThrowIfCancellationRequested(); Current(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private static JsonObject Compact(JsonObject report, JsonObject arguments)
    {
        Only(arguments, "ids");
        var result = report.DeepClone().AsObject();
        result.Remove("volumesM3");
        result.Remove("bounds");
        if (arguments["ids"] is JsonArray ids)
        {
            if (ids.Count > 32) throw new InvalidOperationException("一次最多检查 32 个构件尺寸。");
            var selected = new JsonObject();
            foreach (var id in ids)
            {
                string key = id!.GetValue<string>();
                if (report["bounds"]?[key] is JsonNode bounds) selected[key] = bounds.DeepClone();
            }
            result["bounds"] = selected;
        }
        return result;
    }

    private static void Only(JsonObject arguments, params string[] allowed)
    {
        if (arguments.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("参数包含不支持的字段，请先查询工具目录。");
    }

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };
}
