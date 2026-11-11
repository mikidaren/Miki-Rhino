using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using RhinoMagpie.Agent;

static class PlanReviewWorkerTests
{
    public static async Task<int> RunAsync(string package, string review, string originalProject)
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
            throw new Exception("FAIL " + name + " returned a review report");
        }

        string project = File.ReadAllText(Path.Combine(review, "connected-project.json"));
        string profile = File.ReadAllText(Path.Combine(review, "connected-profile.json"));
        Task<JsonObject> Review(string p, string b, CancellationToken token = default) =>
            PlanReviewWorker.ReviewAsync(package, p, b, token);

        JsonObject success = await Review(project, profile);
        Check(success["passed"]?.GetValue<bool>() == true &&
            JsonNode.DeepEquals(success, JsonNode.Parse(File.ReadAllText(Path.Combine(review, "connected-report.json")))),
            "exit 0 preserves the complete connected plan report (6 rooms, 0 issues)");

        JsonObject failure = await Review(File.ReadAllText(originalProject), "{}");
        Check(failure["passed"]?.GetValue<bool>() == false &&
            JsonNode.DeepEquals(failure, JsonNode.Parse(File.ReadAllText(Path.Combine(review, "original-report.json")))),
            "exit 1 preserves the complete failing plan report (4 rooms, 6 issues)");

        await Reject(Review(project, "{\"transitSpaces\":[\"UNKNOWN\"]}"), "invalid_plan_profile", "invalid profile is an input error");
        await Reject(Review("{}", profile), "invalid_project", "invalid project is an input error");
        await Reject(PlanReviewWorker.ReviewAsync(Path.Combine(package, "nonexistent"), project, profile, default),
            "full offline plugin bundle", "missing runtime is unavailable");

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        try
        {
            await Review(project, profile, alreadyCanceled.Token);
            throw new Exception("FAIL pre-canceled review did not cancel");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == alreadyCanceled.Token)
        {
            Check(true, "pre-canceled review keeps the caller token");
        }

        // Use the bundled interpreter with controlled probes for transport faults. These
        // go through the real launcher path without editing the package or worker resource.
        var location = IfcRuntimeBundle.Resolve(package);
        var runWorker = typeof(PlanReviewWorker).GetMethod("RunWorkerAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var scratch = Directory.CreateTempSubdirectory("miki-plan-review-test-");
        string script = Path.Combine(scratch.FullName, "probe.py");
        string ready = Path.Combine(scratch.FullName, "pids.txt");
        Task<JsonObject> Probe(string source, TimeSpan timeout, CancellationToken token = default)
        {
            File.WriteAllText(script, source);
            var start = IfcRuntimeBundle.CreateStartInfo(location, [ready]);
            start.ArgumentList[3] = script;
            return (Task<JsonObject>)runWorker.Invoke(null, [start, timeout, token])!;
        }

        const string waiting = """
            import os, pathlib, subprocess, sys, time
            child = subprocess.Popen([sys.executable, '-I', '-S', '-B', '-c', 'import time; time.sleep(60)'])
            pending = pathlib.Path(sys.argv[1] + '.tmp')
            pending.write_text(f'{os.getpid()} {child.pid}', encoding='ascii')
            pending.replace(sys.argv[1])
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

        try
        {
            var timeout = TimeSpan.FromSeconds(2);
            var timer = Stopwatch.StartNew();
            await Reject(Probe(waiting, timeout), "timed out", "deadline rejects a running worker");
            Check(timer.Elapsed < TimeSpan.FromSeconds(6) && (await Task.WhenAll((await ReadPids()).Select(Exited))).All(x => x),
                "deadline returns promptly and terminates the process tree");
            File.Delete(ready);

            using var cancel = new CancellationTokenSource();
            var running = Probe(waiting, TimeSpan.FromSeconds(15), cancel.Token);
            int[] pids = await ReadPids();
            timer.Restart();
            cancel.Cancel();
            try { await running; throw new Exception("FAIL canceled review returned a report"); }
            catch (OperationCanceledException error)
            {
                Check(error.CancellationToken == cancel.Token && timer.Elapsed < TimeSpan.FromSeconds(6) &&
                    (await Task.WhenAll(pids.Select(Exited))).All(x => x),
                    "caller cancellation keeps the token and terminates the running process tree");
            }

            foreach (string channel in new[] { "stdout", "stderr" })
            {
                timer.Restart();
                await Reject(Probe($"import sys\nwhile True:\n    sys.{channel}.buffer.write(b'x' * 8192)\n    sys.{channel}.flush()",
                    TimeSpan.FromSeconds(15)), channel + " limit", "unbounded " + channel + " is rejected");
                Check(timer.Elapsed < TimeSpan.FromSeconds(6), channel + " overflow stops the worker before its deadline");
            }

            await Reject(Probe("print('{}')\nraise SystemExit(1)", timeout), "invalid report", "exit 1 without a review is unavailable");
            await Reject(Probe("print('{\"passed\":false,\"error\":\"runtime_missing\",\"message\":\"install dependencies\"}')\nraise SystemExit(2)",
                timeout), "full offline plugin bundle", "worker dependency failure is unavailable");
        }
        finally
        {
            File.Delete(script);
            File.Delete(ready);
            File.Delete(ready + ".tmp");
            Directory.Delete(scratch.FullName);
        }

        return passed;
    }
}
