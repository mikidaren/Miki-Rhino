using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Miki.OpenBim;

public static partial class ProjectEngine
{
    public const long MaxGeneratedParts = 5000;

    private static readonly string[] Collections =
        ["levels", "grids", "spaces", "walls", "slabs", "types", "openings", "stairs"];

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            MaxDepth = 64
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static BuildingProject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("Project JSON must contain an object of at most 16 MiB characters.");
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            var project = document.RootElement.Deserialize<BuildingProject>(JsonOptions)
                ?? throw new InvalidOperationException("Project must be an object.");
            Validate(project);
            return project;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Invalid project JSON: {error.Message}", error);
        }
    }

    public static string Serialize(BuildingProject project)
    {
        Validate(project);
        return JsonSerializer.Serialize(project, JsonOptions);
    }

    public static void Validate(BuildingProject project) => ProjectValidation.Validate(project);

    public static long EstimateGeneratedParts(BuildingProject project)
    {
        if (project is null || project.Grids is null || project.Spaces is null || project.Walls is null
            || project.Slabs is null || project.Types is null || project.Openings is null || project.Stairs is null)
            throw new InvalidOperationException("Generated part estimates require a project and all counted collections.");
        long stairParts = 0;
        foreach (var stair in project.Stairs)
        {
            if (stair is null || stair.Risers < 0)
                throw new InvalidOperationException("Generated part estimates require stair records with nonnegative riser counts.");
            stairParts += (long)stair.Risers + 1L;
        }
        return project.Grids.LongLength + project.Spaces.LongLength
            + 2L * project.Walls.LongLength + 2L * project.Slabs.LongLength
            + 2L * project.Openings.LongLength + stairParts
            + 5L * project.Types.LongLength + 1L;
    }

    public static BuildingProject ApplyPatch(BuildingProject project, JsonObject patch)
    {
        Validate(project);
        if (patch is null) throw new InvalidOperationException("Patch must be an object.");
        if (patch.Count > 0 && project.Revision == int.MaxValue)
            throw new InvalidOperationException("Project revision cannot be incremented.");
        try
        {
            var root = JsonSerializer.SerializeToNode(project, JsonOptions)!.AsObject();
            foreach (var property in patch)
            {
                if (property.Key == "name")
                {
                    root["name"] = property.Value?.DeepClone();
                    continue;
                }
                if (!Collections.Contains(property.Key, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Unsupported patch property '{property.Key}'.");
                if (property.Value is not JsonArray changes || changes.Count > ProjectValidation.MaxParts)
                    throw new InvalidOperationException($"Patch '{property.Key}' must be a bounded array.");
                var target = root[property.Key]!.AsArray();
                var entries = target.ToDictionary(entry => entry!["id"]!.GetValue<string>(), entry => entry!.AsObject(), StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in changes)
                {
                    if (change is not JsonObject fields || fields["id"] is not JsonValue idValue
                        || !idValue.TryGetValue<string>(out var id) || !ProjectValidation.IsId(id))
                        throw new InvalidOperationException($"Every '{property.Key}' patch entry requires an ASCII id.");
                    if (!seen.Add(id)) throw new InvalidOperationException($"Duplicate patch id '{id}'.");
                    if (entries.TryGetValue(id, out var existing))
                    {
                        foreach (var field in fields) existing[field.Key] = field.Value?.DeepClone();
                    }
                    else
                    {
                        target.Add(fields.DeepClone());
                    }
                }
            }
            root["revision"] = project.Revision + (patch.Count == 0 ? 0 : 1);
            return Parse(root.ToJsonString(JsonOptions));
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"Invalid project patch: {error.Message}", error);
        }
    }

    public static string[] AffectedIds(BuildingProject before, BuildingProject after)
    {
        Validate(before);
        Validate(after);
        if (before.Id != after.Id) throw new InvalidOperationException("Affected projects must have the same identity.");
        var previous = JsonSerializer.SerializeToNode(before, JsonOptions)!.AsObject();
        var current = JsonSerializer.SerializeToNode(after, JsonOptions)!.AsObject();
        var changes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var collection in Collections)
        {
            var oldEntries = previous[collection]!.AsArray().ToDictionary(entry => entry!["id"]!.GetValue<string>(), StringComparer.Ordinal);
            var newEntries = current[collection]!.AsArray().ToDictionary(entry => entry!["id"]!.GetValue<string>(), StringComparer.Ordinal);
            changes[collection] = oldEntries.Keys.Concat(newEntries.Keys)
                .Where(id => !JsonNode.DeepEquals(oldEntries.GetValueOrDefault(id), newEntries.GetValueOrDefault(id)))
                .ToHashSet(StringComparer.Ordinal);
        }
        var affected = changes.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
        foreach (var project in new[] { before, after })
        {
            var dependentWalls = project.Walls
                .Where(wall => changes["walls"].Contains(wall.Id) || changes["levels"].Contains(wall.Level))
                .Select(wall => wall.Id).ToHashSet(StringComparer.Ordinal);
            affected.UnionWith(dependentWalls);
            affected.UnionWith(project.Spaces.Where(space => changes["levels"].Contains(space.Level)).Select(space => space.Id));
            affected.UnionWith(project.Slabs.Where(slab => changes["levels"].Contains(slab.Level)).Select(slab => slab.Id));
            affected.UnionWith(project.Stairs.Where(stair => changes["levels"].Contains(stair.From) || changes["levels"].Contains(stair.To)).Select(stair => stair.Id));
            foreach (var opening in project.Openings)
            {
                if (dependentWalls.Contains(opening.Wall) || changes["types"].Contains(opening.Type) || changes["openings"].Contains(opening.Id))
                    affected.Add(opening.Id);
                if (changes["types"].Contains(opening.Type) || changes["openings"].Contains(opening.Id))
                    affected.Add(opening.Wall);
            }
        }
        return affected.Order(StringComparer.Ordinal).ToArray();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException($"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
