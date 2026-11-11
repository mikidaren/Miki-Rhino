using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public static class LayoutWorker
{
    private const string Script = "layout_baseline.py";
    private const string ResourceName = "RhinoMagpie.Agent.Resources." + Script;
    private const string Family = "two_storey_single_loaded_corridor/1";
    private const string Unavailable = "The bundled layout generator is unavailable. Reinstall the full offline plugin bundle.";
    private const int BriefLimit = 64 * 1024;
    private const int FileLimit = 1024 * 1024;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Returns the baseline report and, when feasible, project/profile data. Does not modify Rhino geometry.</summary>
    public static async Task<JsonObject> GenerateAsync(string pluginDirectory, string briefJson, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateBrief(briefJson);
        using Stream script = typeof(LayoutWorker).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(Unavailable + " The embedded layout script is missing.");
        IfcRuntimeLocation runtime;
        try
        {
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
            scratch = Directory.CreateTempSubdirectory("miki-layout-");
            string scriptPath = Path.Combine(scratch.FullName, Script);
            string briefPath = Path.Combine(scratch.FullName, "brief.json");
            string output = Path.Combine(scratch.FullName, "result"); // CLI requires a fresh directory.
            await using (var destination = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                await script.CopyToAsync(destination, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(briefPath, briefJson, Utf8, token).ConfigureAwait(false);

            var start = IfcRuntimeBundle.CreateStartInfo(runtime, ["--brief", briefPath, "--out-dir", output]);
            start.ArgumentList[3] = scriptPath;
            return await RunWorkerAsync(start, output, WorkerTimeout, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not prepare the local layout generator. Check access to the plugin and temporary directory.", error);
        }
        finally
        {
            if (scratch is not null) DeleteScratch(scratch.FullName);
        }
    }

    private static void ValidateBrief(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Layout generation requires brief JSON.");
            if (json.Length > BriefLimit || Utf8.GetByteCount(json) > BriefLimit)
                throw new InvalidOperationException("Layout brief JSON exceeds its 64 KiB limit.");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidOperationException("Layout brief JSON contains invalid Unicode.", error);
        }
    }

    private static async Task<JsonObject> RunWorkerAsync(ProcessStartInfo start, string output, TimeSpan timeout,
        CancellationToken token)
    {
        var result = await PlanReviewWorker.RunBoundedWorkerAsync(start, timeout, token, "layout generation", Unavailable)
            .ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(result.Stdout) is not JsonObject summary) throw InvalidReport(result.ExitCode, result.Stderr);
            string? status = Text(summary["status"]);
            if (result.ExitCode == 1 && status is "invalid_input" or "io_error")
                throw new InvalidOperationException($"Layout generation failed ({status}): " +
                    Clip(Text(summary["message"]) ?? "Check the brief and temporary directory."));

            bool feasible = result.ExitCode == 0 && status == "feasible";
            if (!feasible && !(result.ExitCode == 2 && status == "infeasible"))
                throw InvalidReport(result.ExitCode, result.Stderr);

            JsonObject report = await ReadObjectAsync(Path.Combine(output, "report.json"), token).ConfigureAwait(false);
            string? projectId = Text(report["projectId"]);
            if (Text(report["family"]) != Family || Text(report["status"]) != status || string.IsNullOrEmpty(projectId) ||
                Text(summary["projectId"]) != projectId ||
                report["candidateCount"] is not JsonValue count || !count.TryGetValue<int>(out int candidates) ||
                candidates < 0 || candidates > 14400 || !JsonNode.DeepEquals(count, summary["candidateCount"]) ||
                report["issues"] is not JsonArray issues || report["candidates"] is not JsonArray choices ||
                summary["issueCodes"] is not JsonArray codes || codes.Count != issues.Count)
                throw InvalidReport(result.ExitCode, result.Stderr);
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i] is not JsonObject issue || string.IsNullOrEmpty(Text(issue["code"])) ||
                    Text(codes[i]) != Text(issue["code"]))
                    throw InvalidReport(result.ExitCode, result.Stderr);
            }

            var envelope = new JsonObject { ["report"] = report };
            if (!feasible)
            {
                if (candidates != 0 || choices.Count != 0 || issues.Count == 0 ||
                    File.Exists(Path.Combine(output, "project.json")) || File.Exists(Path.Combine(output, "profile.json")))
                    throw InvalidReport(result.ExitCode, result.Stderr);
                return envelope;
            }
            if (candidates == 0 || choices.Count != Math.Min(candidates, 3) || issues.Count != 0 ||
                choices.Any(choice => choice is not JsonObject))
                throw InvalidReport(result.ExitCode, result.Stderr);

            JsonObject project = await ReadObjectAsync(Path.Combine(output, "project.json"), token).ConfigureAwait(false);
            JsonObject profile = await ReadObjectAsync(Path.Combine(output, "profile.json"), token).ConfigureAwait(false);
            if (Text(project["id"]) != projectId || Text(project["units"]) != "m" ||
                project["schemaVersion"] is not JsonValue schema || !schema.TryGetValue<int>(out int version) || version != 1 ||
                project["levels"] is not JsonArray { Count: 2 } || project["spaces"] is not JsonArray ||
                profile["transitSpaces"] is not JsonArray || profile["areaTargetsM2"] is not JsonObject)
                throw InvalidReport(result.ExitCode, result.Stderr);
            envelope["project"] = project;
            envelope["profile"] = profile;
            return envelope;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw InvalidReport(result.ExitCode, result.Stderr, error);
        }
    }

    private static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        long length = source.Length;
        if (length is <= 0 or > FileLimit)
            throw new InvalidOperationException($"Layout result {Path.GetFileName(path)} exceeds its size limit or is empty (limit {FileLimit} bytes).");
        byte[] bytes = new byte[length];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Layout output must be an object.");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static string Clip(string text) => text[..Math.Min(text.Length, 600)].Trim();

    private static InvalidOperationException InvalidReport(int exitCode, byte[] stderr, Exception? inner = null)
    {
        string detail = Clip(Encoding.UTF8.GetString(stderr));
        return new InvalidOperationException($"{Unavailable} The worker returned an invalid report (exit {exitCode})." +
            (detail.Length > 0 ? " " + detail : ""), inner);
    }

    private static void DeleteScratch(string directory)
    {
        string output = Path.Combine(directory, "result");
        foreach (string path in new[] { Path.Combine(directory, Script), Path.Combine(directory, "brief.json"),
            Path.Combine(output, "report.json"), Path.Combine(output, "project.json"), Path.Combine(output, "profile.json") })
        {
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (string path in new[] { output, directory })
        {
            try { Directory.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
