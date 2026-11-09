using System.Text.Json;
using System.Text.Json.Nodes;
using RhinoMagpie;
using RhinoMagpie.Agent;

static class LocalToolTests
{
    public static async Task<int> RunAsync(BridgeConfig config)
    {
        int passed = 0;
        var failures = new List<Exception>();
        var architectural = new ModelingRunOptions(Architectural: true);
        var scaled = architectural with { ExplicitScale = true };

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

        async Task Scenario(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception error)
            {
                failures.Add(new Exception(name, error));
                Console.WriteLine("FAIL local scenario " + name + " - " + error.Message);
            }
        }

        await Scenario("local dispatch and MCP definitions", async () =>
        {
            var project = new JsonObject { ["project"] = new JsonObject { ["id"] = "building", ["revision"] = 1 } };
            using var fixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
                ? Wire.Calls(Wire.Call("catalog", "openbim_catalog", new JsonObject()),
                    Wire.Call("apply", "openbim_apply", project),
                    Wire.Call("inspect", "openbim_inspect", new JsonObject()),
                    Wire.Call("export", "openbim_export_ifc", new JsonObject { ["path"] = "building.ifc" }),
                    Wire.Call("native", "get_context", new JsonObject()))
                : Wire.Text("Checked.")));
            using var http = new HttpClient(fixture);
            var sharedResult = Wire.Result("{\"revision\":1,\"counts\":{\"walls\":4},\"checks\":{\"solids\":true}}");
            sharedResult.Remove("isError");
            sharedResult["structuredContent"] = new JsonObject { ["geometry"] = "NATIVE_BREP_SENTINEL" };
            var local = new LocalToolsFixture { Handler = (_, _, _) => Task.FromResult(sharedResult) };
            local.Definitions[1]!["implementation"] = "NATIVE_SOURCE_SENTINEL";
            var approvals = new List<ToolInvocation>();
            var reports = new List<(string Role, string Text)>();
            var session = new ModelingSession();
            await session.RunAsync("Apply the project, inspect and export", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                (call, _) => { approvals.Add(call); return Task.FromResult(true); },
                (_, _) => Task.FromResult(""), (role, text) => reports.Add((role, text)),
                CancellationToken.None, localTools: local);
            Check(local.Executed.Select(call => call.Name).SequenceEqual(new[]
                { "openbim_catalog", "openbim_apply", "openbim_inspect", "openbim_export_ifc" }) &&
                fixture.Executed.Select(call => call.Name).SequenceEqual(new[] { "get_context" }),
                "local tools execute sequentially without native MCP forwarding");
            Check(approvals.Select(call => call.ReadOnly).SequenceEqual(new[] { true, false, true, false, true }) &&
                JsonNode.DeepEquals(local.Executed[1].Arguments, project) &&
                local.Executed[3].Arguments["path"]!.GetValue<string>() == "building.ifc",
                "local annotations and original arguments reach the shared approval and dispatch path");
            var tools = fixture.ChatRequests[0]["tools"]!.AsArray();
            var advertised = tools.Single(tool => tool!["function"]!["name"]!.GetValue<string>() == "openbim_apply")!["function"]!;
            Check(JsonNode.DeepEquals(advertised["parameters"], local.Definitions[1]!["inputSchema"]) &&
                advertised["description"]!.GetValue<string>() == "Apply an explicit project or patch." &&
                tools.Count(tool => tool!["function"]!["name"]!.GetValue<string>() == "get_context") == 1,
                "raw local MCP schemas merge with native function definitions");
            Check(new[] { "catalog", "apply", "inspect", "export", "native" }.Select((id, index) =>
                Wire.ToolResult(fixture.ChatRequests[1], id).Contains("operationId: op-" + (index + 1))).All(found => found) &&
                reports.Count(item => item.Role == "工具") == 5 && reports.Count(item => item.Role == "工具结果") == 5 &&
                session.Outcome == "complete" && !session.Workflow.HasUnresolvedFailures,
                "local results share native reporting and stable operation ledger IDs");
            Check(sharedResult["content"]!.AsArray().Count == 1 &&
                Wire.ToolResult(fixture.ChatRequests[1], "apply").Contains("\"walls\":4"),
                "compact local results reach model history without mutating service-owned responses");
            Check(fixture.ChatRequests.All(request => !request.ToJsonString().Contains("NATIVE_SOURCE_SENTINEL") &&
                !request.ToJsonString().Contains("NATIVE_BREP_SENTINEL")),
                "service implementation and native geometry metadata stay outside model requests and history");
        });

        foreach (string name in new[] { "openbim_apply", "openbim_export_ifc" })
        foreach (bool architecturalMode in new[] { false, true })
        {
            await Scenario("approval denial " + name + " architectural=" + architecturalMode, async () =>
            {
                using var fixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
                    ? Wire.Calls(Wire.Call("denied", name, new JsonObject())) : Wire.Text("Stopped.")));
                using var http = new HttpClient(fixture);
                var local = new LocalToolsFixture();
                var session = new ModelingSession();
                if (architecturalMode && name == "openbim_apply")
                    session.Workflow.SetBasis(Wire.Basis("user_dimensions"), scaled);
                var approvals = new List<ToolInvocation>();
                var results = new List<string>();
                await session.RunAsync("Make the requested change", config, new MagpieGateway(http),
                    new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                    (call, _) => { approvals.Add(call); return Task.FromResult(false); },
                    (_, _) => Task.FromResult(""), (role, text) => { if (role == "工具结果") results.Add(text); },
                    CancellationToken.None, architecturalMode ? scaled : null, local);
                Check(approvals.Count == 1 && approvals[0].Name == name && !approvals[0].ReadOnly &&
                    local.Executed.Count == 0 && fixture.Executed.Count == 0 && results.Single().StartsWith("ERROR"),
                    name + " denial prevents dispatch in architectural=" + architecturalMode);
                Check(!session.ToolCompletionUnknown && !session.Workflow.HasUnresolvedFailures &&
                    session.Outcome == (architecturalMode ? "paused" : "complete"),
                    name + " denial preserves approval-mode stopping and creates no operation failure");
            });
        }

        await Scenario("architectural basis and file export", async () =>
        {
            using var fixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
                ? Wire.Calls(Wire.Call("premature", "openbim_apply", new JsonObject()),
                    Wire.Call("catalog", "openbim_catalog", new JsonObject()),
                    Wire.Call("initial-inspect", "openbim_inspect", new JsonObject()),
                    Wire.Call("export", "openbim_export_ifc", new JsonObject { ["path"] = "existing.ifc" }),
                    Wire.Call("basis", "set_design_basis", Wire.Basis("confirmed_assumption")),
                    Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
                    Wire.Call("apply", "openbim_apply", new JsonObject()),
                    Wire.Call("inspect", "openbim_inspect", new JsonObject()),
                    Wire.Call("complete", "update_project_plan", Wire.Plan("complete")))
                : Wire.Text("Please review.")));
            using var http = new HttpClient(fixture);
            var local = new LocalToolsFixture();
            var session = new ModelingSession();
            var approvals = new List<ToolInvocation>();
            await session.RunAsync("Use the proposed dimensions", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                (call, _) => { approvals.Add(call); return Task.FromResult(true); },
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, architectural, local);
            Check(Wire.ToolResult(fixture.ChatRequests[1], "premature").StartsWith("ERROR") &&
                local.Executed.Count(call => call.Name == "openbim_apply") == 1 && fixture.Executed.Count == 0,
                "architectural local apply is refused before approval when basis is missing");
            Check(approvals.Select(call => call.Name).SequenceEqual(new[]
                { "openbim_catalog", "openbim_inspect", "openbim_export_ifc", "confirm_design_basis", "openbim_apply", "openbim_inspect" }) &&
                approvals.Single(call => call.Name == "confirm_design_basis").Arguments["note"]!.GetValue<string>() ==
                    Wire.Basis("confirmed_assumption")["note"]!.GetValue<string>() &&
                !approvals.Single(call => call.Name == "openbim_export_ifc").ReadOnly,
                "export requires write approval without scale gating and auto-execution still confirms the exact scale note");
            Check(session.Workflow.BasisConfirmed && session.Outcome == "needs_review" &&
                session.Workflow.AwaitingReview && !Wire.ToolResult(fixture.ChatRequests[1], "complete").StartsWith("ERROR"),
                "successful local geometry inspection supplies architectural completion readback");
        });

        await Scenario("denied scale assumption", async () =>
        {
            using var fixture = new HttpFixture((_, _) => Wire.Response(Wire.Calls(
                Wire.Call("basis", "set_design_basis", Wire.Basis("confirmed_assumption")),
                Wire.Call("apply", "openbim_apply", new JsonObject()))));
            using var http = new HttpClient(fixture);
            var local = new LocalToolsFixture();
            var session = new ModelingSession();
            var approvals = new List<string>();
            await session.RunAsync("Use assumed dimensions", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                (call, _) => { approvals.Add(call.Name); return Task.FromResult(call.Name != "confirm_design_basis"); },
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, architectural, local);
            Check(approvals.SequenceEqual(new[] { "confirm_design_basis" }) && local.Executed.Count == 0 &&
                !session.Workflow.BasisConfirmed && session.Outcome == "paused",
                "denying the exact scale note stops local apply even when other tools would auto-execute");
        });

        await Scenario("trusted successful local inspection", async () =>
        {
            using var fixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
                ? Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
                    Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
                    Wire.Call("apply", "openbim_apply", new JsonObject()),
                    Wire.Call("catalog", "openbim_catalog", new JsonObject()),
                    Wire.Call("after-catalog", "update_project_plan", Wire.Plan("complete")),
                    Wire.Call("failed-inspect", "openbim_inspect", new JsonObject()),
                    Wire.Call("after-failed-inspect", "update_project_plan", Wire.Plan("complete")),
                    Wire.Call("inspect", "openbim_inspect", new JsonObject()),
                    Wire.Call("after-inspect", "update_project_plan", Wire.Plan("complete")))
                : Wire.Text("Please review.")));
            using var http = new HttpClient(fixture);
            int inspections = 0;
            var local = new LocalToolsFixture
            {
                Handler = (name, _, _) => Task.FromResult(name == "openbim_inspect" && ++inspections == 1
                    ? Wire.Result("Geometry validation failed.", true) : Wire.Result("Native solids validated.")),
            };
            var session = new ModelingSession();
            await session.RunAsync("Build and validate", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled, local);
            Check(Wire.ToolResult(fixture.ChatRequests[1], "after-catalog").StartsWith("ERROR") &&
                Wire.ToolResult(fixture.ChatRequests[1], "failed-inspect").StartsWith("ERROR") &&
                Wire.ToolResult(fixture.ChatRequests[1], "after-failed-inspect").StartsWith("ERROR"),
                "catalog and failed local inspection cannot manufacture fresh geometry readback");
            Check(!Wire.ToolResult(fixture.ChatRequests[1], "after-inspect").StartsWith("ERROR") &&
                session.Outcome == "needs_review" && !session.Workflow.HasUnresolvedFailures,
                "only successful local inspection enables completion after local apply");
        });

        foreach (var pair in new[]
        {
            (Failed: "run_csharp", Correction: "openbim_apply"),
            (Failed: "run_python", Correction: "openbim_apply"),
            (Failed: "run_command", Correction: "openbim_apply"),
            (Failed: "openbim_apply", Correction: "run_python"),
            (Failed: "openbim_apply", Correction: "openbim_apply"),
        })
        {
            await Scenario("geometry repair " + pair.Failed + " via " + pair.Correction, async () =>
            {
                using var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
                {
                    0 => Wire.Calls(Wire.Call("basis", "set_design_basis", Wire.Basis("user_dimensions")),
                        Wire.Call("plan", "update_project_plan", Wire.Plan("in_progress")),
                        Wire.Call("failed", pair.Failed, new JsonObject())),
                    1 => Wire.Calls(Wire.Call("export", "openbim_export_ifc", new JsonObject()),
                        Wire.Call("catalog", "openbim_catalog", new JsonObject()),
                        Wire.Call("unrepaired-inspect", "openbim_inspect", new JsonObject()),
                        Wire.Call("export-resolution", "resolve_modeling_failure", Wire.Resolution("op-1", "op-2", "op-4")),
                        Wire.Call("catalog-resolution", "resolve_modeling_failure", Wire.Resolution("op-1", "op-3", "op-4")),
                        Wire.Call("unrepaired-complete", "update_project_plan", Wire.Plan("complete"))),
                    2 => Wire.Calls(Wire.Call("correction", pair.Correction, new JsonObject()),
                        Wire.Call("inspect", "openbim_inspect", new JsonObject()),
                        Wire.Call("resolution", "resolve_modeling_failure", Wire.Resolution("op-1", "op-5", "op-6")),
                        Wire.Call("complete", "update_project_plan", Wire.Plan("complete"))),
                    3 => Wire.Text("Corrected and checked. Please review."),
                    _ => throw new Exception("Local repair failed to reach review."),
                }));
                using var http = new HttpClient(fixture);
                bool firstGeometryCall = true;
                JsonObject Result(string name)
                {
                    if (name == pair.Failed && firstGeometryCall)
                    {
                        firstGeometryCall = false;
                        return Wire.Result("Geometry operation failed.", true);
                    }
                    return Wire.Result("Checked.");
                }
                fixture.ToolResult = call => Result(call.Name);
                var local = new LocalToolsFixture { Handler = (name, _, _) => Task.FromResult(Result(name)) };
                var session = new ModelingSession();
                await session.RunAsync("Repair geometry", config, new MagpieGateway(http),
                    new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                    (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, scaled, local);
                Check(Wire.ToolResult(fixture.ChatRequests[1], "failed").Contains("outstanding failureId: op-1") &&
                    new[] { "export-resolution", "catalog-resolution", "unrepaired-complete" }.All(id =>
                        Wire.ToolResult(fixture.ChatRequests[2], id).StartsWith("ERROR")),
                    pair.Failed + " failure survives unrelated export, catalog and inspection");
                Check(Wire.ToolResult(fixture.ChatRequests[3], "correction").Contains("operationId: op-5") &&
                    Wire.ToolResult(fixture.ChatRequests[3], "inspect").Contains("operationId: op-6") &&
                    Wire.ToolResult(fixture.ChatRequests[3], "resolution").StartsWith("Failure resolved.") &&
                    session.Outcome == "needs_review" && !session.Workflow.HasUnresolvedFailures,
                    pair.Correction + " shares the geometry repair family with " + pair.Failed + " and uses inspected proof");
            });
        }

        foreach (string validation in new[] { "isError", "argument", "json", "invalid-operation" })
        {
            await Scenario("known local validation " + validation, async () =>
            {
                using var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
                {
                    0 => Wire.Calls(Wire.Call("failed", "openbim_apply", new JsonObject())),
                    1 => Wire.Calls(Wire.Call("correction", "openbim_apply", new JsonObject()),
                        Wire.Call("inspect", "openbim_inspect", new JsonObject()),
                        Wire.Call("resolution", "resolve_modeling_failure", Wire.Resolution("op-1", "op-2", "op-3"))),
                    2 => Wire.Text("Corrected."),
                    _ => throw new Exception("Known validation failure could not be repaired."),
                }));
                using var http = new HttpClient(fixture);
                int attempts = 0;
                var local = new LocalToolsFixture
                {
                    Handler = (name, _, _) =>
                    {
                        if (name != "openbim_apply" || ++attempts > 1) return Task.FromResult(Wire.Result("Checked."));
                        return validation switch
                        {
                            "argument" => throw new ArgumentException("Known project validation failed."),
                            "json" => throw new JsonException("Known project validation failed."),
                            "invalid-operation" => throw new InvalidOperationException("Known project validation failed."),
                            _ => Task.FromResult(Wire.Result("Known project validation failed." + new string('x', 70000), true)),
                        };
                    },
                };
                var session = new ModelingSession();
                await session.RunAsync("Apply a valid project", config, new MagpieGateway(http),
                    new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                    (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, localTools: local);
                string error = Wire.ToolResult(fixture.ChatRequests[1], "failed");
                Check(error.StartsWith("ERROR") && error.Contains("operationId: op-1") &&
                    error.Contains("failureId: op-1") && error.Contains("Known project validation failed.") &&
                    !session.ToolCompletionUnknown && fixture.Executed.Count == 0,
                    validation + " local validation remains a known tool failure with repair ledger metadata");
                Check(Wire.ToolResult(fixture.ChatRequests[2], "resolution").StartsWith("Failure resolved.") &&
                    session.Outcome == "complete" && !session.Workflow.HasUnresolvedFailures,
                    validation + " local validation supports corrective dispatch, fresh inspection and explicit resolution");
                if (validation == "isError")
                    Check(error.Contains("[Result truncated.") && error.Contains("failureId: op-1"),
                        "verbose local failure output retains metadata before the 64K truncation");
            });
        }

        await Scenario("cancellation waits for local dispatch", async () =>
        {
            using var fixture = new HttpFixture((turn, _) => Wire.Response(turn == 0
                ? Wire.Calls(Wire.Call("apply", "openbim_apply", new JsonObject()),
                    Wire.Call("pending", "openbim_catalog", new JsonObject())) : Wire.Text("Resumed.")));
            using var http = new HttpClient(fixture);
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var local = new LocalToolsFixture
            {
                Handler = (_, _, token) => { entered.TrySetResult(token); return completed.Task; },
            };
            var session = new ModelingSession();
            var run = session.RunAsync("Apply and inspect", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(""), (_, _) => { }, cancellation.Token, localTools: local);
            try
            {
                var dispatchedToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                Check(!run.IsCompleted && !dispatchedToken.CanBeCanceled,
                    "cancellation waits for an already-dispatched local operation using an uncancelled token");
                await RejectAsync<InvalidOperationException>(() => session.RunAsync("Concurrent retry", config,
                    new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                    (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { },
                    CancellationToken.None, localTools: local), "dispatched local work retains the session gate until completion");
            }
            finally { completed.TrySetResult(Wire.Result("Applied.")); }
            await RejectAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)),
                "cancellation stops the next local dispatch after the first outcome is known");
            Check(local.Executed.Count == 1 && !session.ToolCompletionUnknown && session.Outcome == "paused",
                "known local completion survives cancellation without dispatching pending calls");
            await session.RunAsync("Continue", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, localTools: local);
            Check(Wire.ToolResult(fixture.ChatRequests[1], "apply").Contains("operationId: op-1") &&
                Wire.ToolResult(fixture.ChatRequests[1], "pending").Contains("Turn interrupted") &&
                session.Outcome == "complete" && fixture.Executed.Count == 0,
                "resuming after local cancellation preserves completed and interrupted tool history");
        });

        await Scenario("cancellation at approval", async () =>
        {
            using var fixture = new HttpFixture((_, _) => Wire.Response(Wire.Calls(
                Wire.Call("apply", "openbim_apply", new JsonObject()))));
            using var http = new HttpClient(fixture);
            using var cancellation = new CancellationTokenSource();
            var local = new LocalToolsFixture();
            var session = new ModelingSession();
            await RejectAsync<OperationCanceledException>(() => session.RunAsync("Apply", config,
                new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                (_, _) => { cancellation.Cancel(); return Task.FromResult(true); },
                (_, _) => Task.FromResult(""), (_, _) => { }, cancellation.Token, localTools: local),
                "cancellation immediately after local approval stops before dispatch");
            Check(local.Executed.Count == 0 && !session.ToolCompletionUnknown && !session.Workflow.HasUnresolvedFailures,
                "cancelling local approval creates no operation or unknown-outcome latch");
        });

        foreach (string name in new[] { "openbim_apply", "openbim_inspect", "openbim_export_ifc" })
        {
            await Scenario("unknown local outcome " + name, async () =>
            {
                using var fixture = new HttpFixture((_, _) => Wire.Response(Wire.Calls(
                    Wire.Call("unknown", name, new JsonObject()), Wire.Call("pending", "openbim_catalog", new JsonObject()))));
                using var http = new HttpClient(fixture);
                var local = new LocalToolsFixture { Handler = (_, _, _) => throw new UnconfirmedToolException() };
                var session = new ModelingSession();
                for (int attempt = 0; attempt < 2; attempt++)
                    await RejectAsync<UnconfirmedToolException>(() => session.RunAsync("Apply", config,
                        new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { },
                        CancellationToken.None, localTools: local), name + " unconfirmed outcome refuses run " + (attempt + 1));
                Check(session.ToolCompletionUnknown && session.Outcome == "paused" && local.Executed.Count == 1 &&
                    fixture.ChatRequests.Count == 1 && fixture.Executed.Count == 0,
                    name + " unknown outcome latches before pending tools, model requests or retry");
            });
        }

        foreach (string failure in new[] { "http", "cancelled", "json", "invalid-operation" })
        {
            await Scenario("native unknown outcome with local tools " + failure, async () =>
            {
                using var fixture = new HttpFixture((_, _) => Wire.Response(Wire.Calls(
                    Wire.Call("native", "run_csharp", new JsonObject()), Wire.Call("pending", "openbim_apply", new JsonObject()))));
                fixture.ToolResult = _ => throw (failure switch
                {
                    "http" => new HttpRequestException("Disconnected."),
                    "cancelled" => new TaskCanceledException("Disconnected."),
                    "json" => new JsonException("Disconnected."),
                    _ => new InvalidOperationException("Disconnected."),
                });
                using var http = new HttpClient(fixture);
                var local = new LocalToolsFixture();
                var session = new ModelingSession();
                for (int attempt = 0; attempt < 2; attempt++)
                    await RejectAsync<UnconfirmedToolException>(() => session.RunAsync("Apply", config,
                        new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                        (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(""), (_, _) => { },
                        CancellationToken.None, localTools: local), failure + " native outcome refuses run " + (attempt + 1));
                Check(session.ToolCompletionUnknown && fixture.Executed.Count == 1 && local.Executed.Count == 0 &&
                    fixture.ChatRequests.Count == 1,
                    failure + " native transport error retains its latch when local tools are registered");
            });
        }

        foreach (string collision in new[]
        {
            "run_csharp", "ask_user", "set_design_basis", "update_project_plan", "resolve_modeling_failure",
            "_hidden", "Openbim_apply", "openbim_", "duplicate-local", "duplicate-native", "reserved-native",
            "missing-schema", "non-object",
        })
        {
            await Scenario("registration rejection " + collision, async () =>
            {
                using var fixture = new HttpFixture((_, _) => Wire.Response(Wire.Text("No tools needed.")));
                using var http = new HttpClient(fixture);
                var local = new LocalToolsFixture();
                if (collision == "duplicate-local") local.Definitions.Add(local.Definitions[1]!.DeepClone());
                else if (collision == "duplicate-native") fixture.ExtraTools.Add(local.Definitions[1]!.DeepClone());
                else if (collision == "reserved-native") fixture.ExtraTools.Add(LocalToolsFixture.Definition("resolve_modeling_failure", true));
                else if (collision == "missing-schema") local.Definitions[1]!.AsObject().Remove("inputSchema");
                else if (collision == "non-object") local.Definitions.Add("invalid definition");
                else local.Definitions.Add(LocalToolsFixture.Definition(collision, true));
                var session = new ModelingSession();
                int approvals = 0;
                await RejectAsync<InvalidOperationException>(() => session.RunAsync("Apply", config,
                    new MagpieGateway(http), new RhinoMcpClient(http, HttpFixture.McpEndpoint),
                    (_, _) => { approvals++; return Task.FromResult(true); },
                    (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, localTools: local),
                    collision + " is rejected during local registration");
                Check(fixture.ChatRequests.Count == 0 && fixture.Executed.Count == 0 && local.Executed.Count == 0 &&
                    approvals == 0 && !session.ToolCompletionUnknown,
                    collision + " rejection precedes model requests and dispatch without poisoning the session");
                fixture.ExtraTools.Clear();
                await session.RunAsync("Continue without local tools", config, new MagpieGateway(http),
                    new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                    (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
            });
        }

        await Scenario("local tool removal between runs", async () =>
        {
            using var fixture = new HttpFixture((turn, _) => Wire.Response(turn switch
            {
                0 => Wire.Calls(Wire.Call("registered", "openbim_catalog", new JsonObject())),
                1 => Wire.Text("Catalog read."),
                2 => Wire.Calls(Wire.Call("removed", "openbim_apply", new JsonObject())),
                _ => Wire.Text("Unavailable."),
            }));
            using var http = new HttpClient(fixture);
            var local = new LocalToolsFixture();
            var session = new ModelingSession();
            await session.RunAsync("Inspect", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None, localTools: local);
            await session.RunAsync("Apply", config, new MagpieGateway(http),
                new RhinoMcpClient(http, HttpFixture.McpEndpoint), (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(""), (_, _) => { }, CancellationToken.None);
            Check(local.Executed.Count == 1 && fixture.Executed.Count == 0 &&
                Wire.ToolResult(fixture.ChatRequests[3], "removed").StartsWith("ERROR") &&
                fixture.ChatRequests[2]["tools"]!.AsArray().All(tool =>
                    !tool!["function"]!["name"]!.GetValue<string>().StartsWith("openbim_", StringComparison.Ordinal)),
                "a new run without local tools cannot dispatch stale local definitions through native MCP");
        });

        await Scenario("export and catalog preserve geometric freshness", () =>
        {
            foreach (bool success in new[] { false, true })
            {
                var workflow = new ProjectWorkflow();
                workflow.SetBasis(Wire.Basis("user_dimensions"), scaled);
                workflow.UpdatePlan(Wire.Plan("in_progress"));
                workflow.ObserveTool("openbim_inspect", true, true);
                workflow.ObserveTool("openbim_export_ifc", false, success);
                workflow.ObserveTool("openbim_catalog", true, success);
                workflow.UpdatePlan(Wire.Plan("complete"));
                workflow.RequestReview();
                Check(!ProjectWorkflow.IsModifyingTool("openbim_export_ifc", false) &&
                    !workflow.HasUnresolvedFailures && workflow.AwaitingReview,
                    "export success=" + success + " preserves inspected geometry freshness and creates no geometric failure");
                workflow.ObserveTool("openbim_apply", false, true);
                workflow.ObserveTool("openbim_catalog", true, true);
                workflow.ObserveTool("openbim_export_ifc", false, true);
                Reject(workflow.RequestReview, "apply invalidates local inspection while catalog/export supply no fresh readback");
            }
            return Task.CompletedTask;
        });

        await Scenario("local inspection freshness and repair stage", () =>
        {
            var workflow = new ProjectWorkflow();
            workflow.SetBasis(Wire.Basis("user_dimensions"), scaled);
            workflow.UpdatePlan(Wire.Plan("in_progress", "pending"));
            string failed = workflow.ObserveTool("openbim_apply", false, false);
            string corrected = workflow.ObserveTool("run_csharp", false, true);
            string previousReadback = workflow.ObserveTool("openbim_inspect", true, true);
            workflow.BeginRun();
            workflow.ObserveTool("get_context", true, true);
            Reject(() => workflow.ResolveFailure(failed, corrected, previousReadback, "Old inspection."),
                "a previous-run local inspection cannot resolve failure even with current native context");
            string currentReadback = workflow.ObserveTool("openbim_inspect", true, true);
            workflow.ObserveTool("run_python", false, true);
            Reject(() => workflow.ResolveFailure(failed, corrected, currentReadback, "Stale inspection."),
                "native geometry changes invalidate local inspection used as resolution proof");
            string failedReadback = workflow.ObserveTool("openbim_inspect", true, false);
            Reject(() => workflow.ResolveFailure(failed, corrected, failedReadback, "Failed inspection."),
                "failed local inspection cannot resolve a geometry failure");
            workflow.UpdatePlan(Wire.Plan("pending", "in_progress"));
            string wrongStage = workflow.ObserveTool("openbim_apply", false, true);
            string wrongStageReadback = workflow.ObserveTool("openbim_inspect", true, true);
            Reject(() => workflow.ResolveFailure(failed, wrongStage, wrongStageReadback, "Another stage."),
                "local apply in another plan stage cannot repair the failed geometry stage");
            workflow.UpdatePlan(Wire.Plan("in_progress", "pending"));
            string finalCorrection = workflow.ObserveTool("openbim_apply", false, true);
            string finalReadback = workflow.ObserveTool("openbim_inspect", true, true);
            workflow.ResolveFailure(failed, finalCorrection, finalReadback, "Current native solids checked in the original stage.");
            Check(!workflow.HasUnresolvedFailures, "same-stage local correction and current inspection explicitly resolve failure");
            return Task.CompletedTask;
        });

        if (failures.Count > 0) throw new AggregateException("Local-tool regressions failed.", failures);
        return passed;
    }
}

sealed class LocalToolsFixture : ILocalModelingTools
{
    public JsonArray Definitions { get; } = new(
        Definition("openbim_catalog", true), Definition("openbim_apply", false),
        Definition("openbim_inspect", true), Definition("openbim_export_ifc", false));
    public List<ToolInvocation> Executed { get; } = [];
    public Func<string, JsonObject, CancellationToken, Task<JsonObject>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(Wire.Result("Native geometry checks passed; revision=1; affectedIds=[wall-1]."));

    public JsonArray ListTools() => Definitions;

    public Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token)
    {
        Executed.Add(new ToolInvocation(name, arguments.DeepClone().AsObject(),
            name is "openbim_catalog" or "openbim_inspect"));
        return Handler(name, arguments, token);
    }

    public static JsonObject Definition(string name, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = name == "openbim_apply" ? "Apply an explicit project or patch." : name,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["project"] = new JsonObject { ["type"] = "object" },
                ["patch"] = new JsonObject { ["type"] = "object" },
                ["path"] = new JsonObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
    };
}
