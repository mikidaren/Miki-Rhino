using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public interface ILocalModelingTools
{
    JsonArray ListTools();
    Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken token);
}
