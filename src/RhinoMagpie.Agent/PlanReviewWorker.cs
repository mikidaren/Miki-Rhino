using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public static class PlanReviewWorker
{
    private const string ReviewScript = "miki_plan_review.py";
    private const string IfcScript = "miki_ifc.py";
    private const string ResourceName = "RhinoMagpie.Agent.Resources." + ReviewScript;
    // The exact Warehouse helper verified with this review script and shipped in the IFC bundle.
    private const string IfcScriptSha256 = "40BAAC01B8867D1C1D08F9808E9D6221A7651FDE27301418B64A1DDC02A4EE40";
    private const string Unavailable = "The bundled plan review is unavailable. Reinstall the full offline plugin bundle.";
    private const int MiB = 1024 * 1024;
    private const int StdoutLimit = 4 * MiB;
    private const int StderrLimit = 16 * 1024;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<JsonObject> ReviewAsync(string pluginDirectory, string projectJson, string profileJson,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateInput(projectJson, 10 * MiB, "project");
        ValidateInput(profileJson, MiB, "profile");

        using Stream reviewScript = typeof(PlanReviewWorker).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(Unavailable + " The embedded review script is missing.");
        IfcRuntimeLocation runtime;
        try
        {
            // Manifest verification reads the runtime files; keep it off the calling thread.
            runtime = await Task.Run(() => IfcRuntimeBundle.Resolve(pluginDirectory), token).WaitAsync(token)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(Unavailable, error);
        }

        token.ThrowIfCancellationRequested();
        DirectoryInfo? scratch = null;
        try
        {
            scratch = Directory.CreateTempSubdirectory("miki-plan-review-");
            string reviewPath = Path.Combine(scratch.FullName, ReviewScript);
            string projectPath = Path.Combine(scratch.FullName, "project.json");
            string profilePath = Path.Combine(scratch.FullName, "profile.json");
            await using (var destination = new FileStream(reviewPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                await reviewScript.CopyToAsync(destination, token).ConfigureAwait(false);

            await StageIfcScriptAsync(runtime.WorkerPath, Path.Combine(scratch.FullName, IfcScript), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(projectPath, projectJson, Utf8, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(profilePath, profileJson, Utf8, token).ConfigureAwait(false);

            var start = IfcRuntimeBundle.CreateStartInfo(runtime, ["--project", projectPath, "--profile", profilePath]);
            start.ArgumentList[3] = reviewPath; // Keep the validated interpreter, flags, working directory and environment.
            return await RunWorkerAsync(start, WorkerTimeout, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not prepare the local plan review. Check access to the plugin and temporary directory.", error);
        }
        finally
        {
            if (scratch is not null) DeleteScratch(scratch.FullName);
        }
    }

    private static void ValidateInput(string json, int maxBytes, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"Plan review requires {name} JSON.");
            if (json.Length > maxBytes || Utf8.GetByteCount(json) > maxBytes)
                throw new InvalidOperationException($"Plan review {name} JSON exceeds {maxBytes / MiB} MiB.");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidOperationException($"Plan review {name} JSON contains invalid Unicode.", error);
        }
    }

    private static async Task StageIfcScriptAsync(string sourcePath, string destinationPath, CancellationToken token)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        long length = source.Length;
        if (length is <= 0 or > MiB)
            throw new InvalidOperationException(Unavailable + " The bundled IFC helper has an unexpected size.");
        byte[] bytes = new byte[length];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        // Verify the actual bytes we will run, even if the source changed after Resolve.
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(IfcScriptSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(Unavailable + " The bundled IFC helper does not match the review script.");
        await File.WriteAllBytesAsync(destinationPath, bytes, token).ConfigureAwait(false);
    }

    private static async Task<JsonObject> RunWorkerAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
    {
        var result = await RunBoundedWorkerAsync(start, timeout, token, "plan review", Unavailable).ConfigureAwait(false);
        return ReadReport(result.ExitCode, result.Stdout, result.Stderr);
    }

    internal static async Task<(int ExitCode, byte[] Stdout, byte[] Stderr)> RunBoundedWorkerAsync(
        ProcessStartInfo start, TimeSpan timeout, CancellationToken token, string name, string unavailable)
    {
        token.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        bool started = false;
        bool drained = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException(unavailable + " The worker could not be started.");
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, StdoutLimit, "stdout", name, stop.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, StderrLimit, "stderr", name, stop.Token);
            List<Task> pending = [stdout, stderr, process.WaitForExitAsync(stop.Token)];
            // Observe both drains even when the first failure aborts the worker.
            _ = Task.WhenAll(pending).ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(stop.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }

            drained = true;
            stop.Token.ThrowIfCancellationRequested();
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"The bundled {name} timed out after {timeout.TotalSeconds:g} seconds. Retry with a smaller project.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(unavailable + " Could not start or read from the worker process.", error);
        }
        finally
        {
            if (started && !drained)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException) { }
                try { process.WaitForExit(2000); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, string channel, string name, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit)
                throw new InvalidOperationException($"The bundled {name} exceeded its {channel} limit of {limit} bytes. Retry with a smaller project.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static JsonObject ReadReport(int exitCode, byte[] stdout, byte[] stderr)
    {
        try
        {
            if (JsonNode.Parse(stdout) is JsonObject result)
            {
                if (exitCode is 0 or 1 && Text(result["check"]) == "miki.plan-review/0.1" &&
                    result["passed"] is JsonValue flag && flag.TryGetValue<bool>(out bool passed) && passed == (exitCode == 0) &&
                    !string.IsNullOrEmpty(Text(result["projectId"])) &&
                    result["revision"] is JsonValue revision && revision.TryGetValue<int>(out int number) && number >= 0 &&
                    result["rooms"] is JsonObject && result["issues"] is JsonArray &&
                    result["doorLinks"] is JsonArray && result["stairLinks"] is JsonArray && result["unlinkedDoors"] is JsonArray)
                    return result;

                if (exitCode == 2)
                {
                    string? code = Text(result["error"]);
                    if (code is "runtime_missing" or "runtime_version")
                        throw new InvalidOperationException(Unavailable + $" Worker diagnostic: {code}.");
                    if (code is "invalid_project" or "invalid_plan_profile" or "invalid_input")
                        throw new InvalidOperationException($"Plan review rejected the project or profile ({code}): " +
                            Clip(Text(result["message"]) ?? "Check the input JSON."));
                }
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw InvalidReport(exitCode, stderr, error);
        }
        throw InvalidReport(exitCode, stderr);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static string Clip(string value) => value[..Math.Min(value.Length, 600)].Trim();

    private static InvalidOperationException InvalidReport(int exitCode, byte[] stderr, Exception? inner = null)
    {
        string detail = Clip(Encoding.UTF8.GetString(stderr));
        return new InvalidOperationException($"{Unavailable} The worker returned an invalid report (exit {exitCode})." +
            (detail.Length > 0 ? " " + detail : ""), inner);
    }

    private static void DeleteScratch(string directory)
    {
        // Only remove the files created by this call, and keep the original diagnostic if cleanup fails.
        foreach (string file in new[] { "project.json", "profile.json", ReviewScript, IfcScript })
        {
            try { File.Delete(Path.Combine(directory, file)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
