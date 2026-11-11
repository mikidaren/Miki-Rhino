using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public interface ILocalModelingTools
{
    JsonArray ListTools();
    Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token);
}

/// <summary>Trusted document-side check. Tool arguments and model messages cannot supply this state.</summary>
public interface IPlanReviewTools
{
    Task<JsonObject?> ReadPlanReviewAsync(bool verifyNative, CancellationToken token);
}
