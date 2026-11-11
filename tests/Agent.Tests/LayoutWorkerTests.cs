using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RhinoMagpie.Agent;

static class LayoutWorkerTests
{
    public static async Task<int> RunAsync(string package, string baseline)
    {
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }

        async Task Reject(Task<JsonObject> task, string message, string name)
        {
            try { await task; }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), name + ": " + error.Message);
                return;
            }
            throw new Exception("FAIL " + name + " returned a layout");
        }

        string brief = File.ReadAllText(Path.Combine(baseline, "examples", "valid-brief.json"));
        Task<JsonObject> Generate(string input, CancellationToken token = default) =>
            LayoutWorker.GenerateAsync(package, input, token);

        using (Stream resource = typeof(LayoutWorker).Assembly.GetManifestResourceStream(
            "RhinoMagpie.Agent.Resources.layout_baseline.py")!)
        using (Stream original = File.OpenRead(Path.Combine(baseline, "layout_baseline.py")))
            Check(resource is not null && SHA256.HashData(resource).SequenceEqual(SHA256.HashData(original)),
                "embedded layout script is an exact byte copy of the validated baseline");

        JsonObject success = await Generate(brief);
        Check(success.Count == 3 && new[] { "report", "project", "profile" }.All(name =>
                JsonNode.DeepEquals(success[name], JsonNode.Parse(File.ReadAllText(
                    Path.Combine(baseline, "evidence", "valid", name + ".json"))))),
            "exit 0 preserves all three baseline objects (32 candidates, 9 spaces)");
        Check(JsonNode.DeepEquals(success, await Generate(brief)), "repeated generation is deterministic");

        JsonObject infeasible = await Generate(File.ReadAllText(Path.Combine(baseline, "examples", "impossible-brief.json")));
        Check(infeasible.Count == 1 && JsonNode.DeepEquals(infeasible["report"], JsonNode.Parse(File.ReadAllText(
                Path.Combine(baseline, "evidence", "impossible", "report.json")))),
            "exit 2 returns only the complete infeasibility report (2 issues, 0 candidates)");

        await Reject(Generate("{}"), "invalid_input", "invalid brief is an input error");
        await Reject(Generate(brief.Replace("\"approved\": true", "\"approved\": false")), "invalid_input",
            "unapproved brief is rejected by the baseline");
        await Reject(Generate("{\"schemaVersion\":1,\"schemaVersion\":1}"), "invalid_input", "duplicate keys are rejected");
        await Reject(Generate("{broken"), "invalid_input", "malformed JSON is rejected");
        await Reject(Generate(" "), "brief JSON", "empty brief is rejected");
        await Reject(Generate(new string('x', 65537)), "64 KiB", "brief character limit is enforced");
        await Reject(Generate(new string('\u4e2d', 22000)), "64 KiB", "brief UTF-8 byte limit is enforced");
        await Reject(Generate("\ud800"), "Unicode", "invalid Unicode is rejected");
        await Reject(LayoutWorker.GenerateAsync(Path.Combine(package, "nonexistent"), brief, default),
            "full offline plugin bundle", "missing runtime is unavailable");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await Generate(brief, canceled.Token); throw new Exception("FAIL pre-canceled generation returned"); }
        catch (OperationCanceledException error)
        {
            Check(error.CancellationToken == canceled.Token, "pre-canceled generation preserves the caller token");
        }

        // Controlled scripts exercise the same isolated launcher and result reader,
        // without changing the verified package or the embedded baseline.
        var location = IfcRuntimeBundle.Resolve(package);
        var isolated = IfcRuntimeBundle.CreateStartInfo(location, []);
        Check(isolated.ArgumentList.Take(3).SequenceEqual(new[] { "-I", "-S", "-B" }) &&
            !isolated.UseShellExecute && isolated.WorkingDirectory == location.RootDirectory &&
            !isolated.Environment.ContainsKey("PYTHONPATH") && !isolated.Environment.ContainsKey("PYTHONHOME"),
            "layout uses the verified bundled interpreter and isolated launcher");
        var run = typeof(LayoutWorker).GetMethod("RunWorkerAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var scratch = Directory.CreateTempSubdirectory("miki-layout-test-");
        string script = Path.Combine(scratch.FullName, "probe.py");
        string output = Path.Combine(scratch.FullName, "result");
        string ready = Path.Combine(scratch.FullName, "pids.txt");
        Task<JsonObject> Probe(string source, TimeSpan? timeout = null, CancellationToken token = default)
        {
            File.WriteAllText(script, source);
            var start = IfcRuntimeBundle.CreateStartInfo(location, [output, ready]);
            start.ArgumentList[3] = script;
            return (Task<JsonObject>)run.Invoke(null, [start, output, timeout ?? TimeSpan.FromSeconds(10), token])!;
        }

        const string waiting = """
            import os, pathlib, subprocess, sys, time
            child = subprocess.Popen([sys.executable, '-I', '-S', '-B', '-c', 'import time; time.sleep(60)'])
            pending = pathlib.Path(sys.argv[2] + '.tmp')
            pending.write_text(f'{os.getpid()} {child.pid}', encoding='ascii')
            pending.replace(sys.argv[2])
            time.sleep(60)
            """;
        async Task<int[]> ReadPids()
        {
            for (int i = 0; i < 150; i++)
            {
                if (File.Exists(ready) && (await File.ReadAllTextAsync(ready)).Split(' ') is [var a, var b] &&
                    int.TryParse(a, out int parent) && int.TryParse(b, out int child))
                    return [parent, child];
                await Task.Delay(20);
            }
            throw new Exception("Probe did not start both processes.");
        }
        static async Task<bool> Exited(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(deadline.Token);
                return true;
            }
            catch (ArgumentException) { return true; }
            catch (OperationCanceledException) { return false; }
        }

        string WriteResult(string summary, int exit = 0, string report = "{}", string project = "{}", string profile = "{}",
            string mutation = "") =>
            "import pathlib, sys\np = pathlib.Path(sys.argv[1]); p.mkdir()\n" +
            $"(p / 'report.json').write_bytes({PythonBytes(report)})\n" +
            $"(p / 'project.json').write_bytes({PythonBytes(project)})\n" +
            $"(p / 'profile.json').write_bytes({PythonBytes(profile)})\n" +
            mutation + "\n" +
            $"print({PythonBytes(summary)}.decode('utf-8'))\nraise SystemExit({exit})";
        void ClearResult()
        {
            if (!Directory.Exists(output)) return;
            foreach (string name in new[] { "report.json", "project.json", "profile.json" })
                File.Delete(Path.Combine(output, name));
            Directory.Delete(output);
        }

        try
        {
            var timer = Stopwatch.StartNew();
            await Reject(Probe(waiting, TimeSpan.FromSeconds(2)), "timed out", "deadline rejects a running layout worker");
            Check(timer.Elapsed < TimeSpan.FromSeconds(6) && (await Task.WhenAll((await ReadPids()).Select(Exited))).All(x => x),
                "deadline terminates the layout process tree promptly");
            File.Delete(ready);

            using var cancel = new CancellationTokenSource();
            var running = Probe(waiting, token: cancel.Token);
            int[] pids = await ReadPids();
            timer.Restart();
            cancel.Cancel();
            try { await running; throw new Exception("FAIL canceled generation returned"); }
            catch (OperationCanceledException error)
            {
                Check(error.CancellationToken == cancel.Token && timer.Elapsed < TimeSpan.FromSeconds(6) &&
                    (await Task.WhenAll(pids.Select(Exited))).All(x => x),
                    "active cancellation preserves the token and terminates the process tree");
            }

            foreach (string channel in new[] { "stdout", "stderr" })
            {
                timer.Restart();
                await Reject(Probe($"import sys\nwhile True:\n    sys.{channel}.buffer.write(b'x' * 8192)\n    sys.{channel}.flush()"),
                    channel + " limit", "unbounded " + channel + " is rejected");
                Check(timer.Elapsed < TimeSpan.FromSeconds(6), channel + " overflow stops before the deadline");
            }

            await Reject(Probe("print('{}')"), "invalid report", "empty status is not a layout result");
            await Reject(Probe("print('not json')"), "invalid report", "non-JSON status is rejected");
            const string status = "{\"status\":\"feasible\",\"projectId\":\"Study-Hub\",\"candidateCount\":32,\"issueCodes\":[]}";
            await Reject(Probe($"print({PythonBytes(status)}.decode())"), "invalid report", "missing result files are rejected");
            string report = success["report"]!.ToJsonString();
            string project = success["project"]!.ToJsonString();
            string profile = success["profile"]!.ToJsonString();
            Check(JsonNode.DeepEquals(success, await Probe(WriteResult(status, report: report, project: project, profile: profile))),
                "controlled worker preserves the complete result envelope");
            ClearResult();
            foreach (string file in new[] { "report", "project", "profile" })
            {
                await Reject(Probe(WriteResult(status, report: report, project: project, profile: profile,
                    mutation: $"(p / '{file}.json').write_bytes(b'[]')")), "invalid report", file + " must be an object");
                ClearResult();
                await Reject(Probe(WriteResult(status, report: report, project: project, profile: profile,
                    mutation: $"(p / '{file}.json').write_bytes(b' ' * (1024 * 1024 + 1))")),
                    "limit", file + " file size limit is enforced");
                ClearResult();
            }
            await Reject(Probe(WriteResult(status.Replace("32", "31"), report: report, project: project, profile: profile)),
                "invalid report", "summary and report must agree");
            ClearResult();
            await Reject(Probe(WriteResult(status, exit: 2, report: report, project: project, profile: profile)),
                "invalid report", "exit code must agree with feasibility");
            ClearResult();
            await Reject(Probe(WriteResult(status, report: report, project: project.Replace("Study-Hub", "wrong"), profile: profile)),
                "invalid report", "project identity must agree with report");
            ClearResult();
            await Reject(Probe("print('{\"status\":\"io_error\",\"message\":\"disk full\"}')\nraise SystemExit(1)"),
                "io_error", "worker I/O errors remain operational errors");
        }
        finally
        {
            ClearResult();
            File.Delete(script);
            File.Delete(ready);
            File.Delete(ready + ".tmp");
            Directory.Delete(scratch.FullName);
        }
        return passed;
    }

    private static string PythonBytes(string text) =>
        "bytes.fromhex('" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)) + "')";
}
