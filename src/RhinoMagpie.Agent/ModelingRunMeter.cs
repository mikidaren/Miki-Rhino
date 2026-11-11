using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public sealed record ModelingRunMetrics(
    int ModelCalls, int ModelResponses, int CallsWithUsage, int CallsWithCacheUsage,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens,
    long? ObservedInputTokens, long? ObservedOutputTokens,
    long MessageJsonBytes, long ToolSchemaJsonBytes,
    long WallTimeMs, long ModelTimeMs, long MeteringTimeMs, long? FirstTextMs,
    int ToolDispatches, int ToolErrors, int UserQuestions, int ApprovalChecks, int DeclinedApprovals,
    int ContinuationReminders, int RepairReminders, string Outcome)
{
    public int CallsWithoutUsage => ModelCalls - CallsWithUsage;
}

/// <summary>Accumulates counters only; no prompts, tool arguments, credentials or model text are stored.</summary>
internal sealed class ModelingRunMeter
{
    private readonly long started = Stopwatch.GetTimestamp();
    private int calls, responses, measured, cachedMeasured;
    private long input, output, cache, messageBytes, schemaBytes, modelTime, meteringTicks;
    private long? firstText;
    private bool tokenOverflow, cacheOverflow;

    public int ToolDispatches { get; set; }
    public int ToolErrors { get; set; }
    public int UserQuestions { get; set; }
    public int ApprovalChecks { get; set; }
    public int DeclinedApprovals { get; set; }
    public int ContinuationReminders { get; set; }
    public int RepairReminders { get; set; }

    public long StartCall(JsonArray messages, JsonArray tools)
    {
        long measurementStarted = Stopwatch.GetTimestamp();
        calls++;
        messageBytes += Encoding.UTF8.GetByteCount(messages.ToJsonString());
        schemaBytes += Encoding.UTF8.GetByteCount(tools.ToJsonString());
        long callStarted = Stopwatch.GetTimestamp();
        meteringTicks += callStarted - measurementStarted;
        return callStarted;
    }

    public void SeeText(string text)
    {
        if (text.Length != 0) firstText ??= (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public void FinishCall(long callStarted, bool returned, ChatUsage? usage)
    {
        modelTime += (long)Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds;
        if (returned) responses++;
        if (usage is not { InputTokens: >= 0, OutputTokens: >= 0 }) return;
        measured++;
        if (!tokenOverflow)
        {
            try { checked { input += usage.InputTokens; output += usage.OutputTokens; } }
            catch (OverflowException) { tokenOverflow = true; }
        }
        if (usage.CachedInputTokens is long cached && cached >= 0 && cached <= usage.InputTokens)
        {
            cachedMeasured++;
            if (!cacheOverflow)
            {
                try { cache = checked(cache + cached); }
                catch (OverflowException) { cacheOverflow = true; }
            }
        }
    }

    public ModelingRunMetrics Snapshot(string outcome) => new(calls, responses, measured, cachedMeasured,
        calls > 0 && measured == calls && !tokenOverflow ? input : null,
        calls > 0 && measured == calls && !tokenOverflow ? output : null,
        calls > 0 && cachedMeasured == calls && !cacheOverflow ? cache : null,
        measured > 0 && !tokenOverflow ? input : null, measured > 0 && !tokenOverflow ? output : null,
        messageBytes, schemaBytes, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, modelTime,
        (long)Stopwatch.GetElapsedTime(0, meteringTicks).TotalMilliseconds, firstText,
        ToolDispatches, ToolErrors, UserQuestions, ApprovalChecks, DeclinedApprovals,
        ContinuationReminders, RepairReminders, outcome);
}
