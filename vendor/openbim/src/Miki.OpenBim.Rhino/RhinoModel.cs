using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Miki.OpenBim.Rhino;

public static class RhinoModel
{
    private const string Section = "MikiOpenBim";
    private const string ProjectKey = "miki.project";
    private const string PartKey = "miki.part";
    private const string SourceKey = "miki.source";

    public static string ApplyJson(RhinoDoc document, string json) =>
        Apply(document, ProjectEngine.Parse(json)).ToJsonString();

    public static string PatchJson(RhinoDoc document, string patch) => Apply(document,
        ProjectEngine.ApplyPatch(ReadProject(document) ?? throw new InvalidOperationException("文档尚未建立简易 BIM 项目。"),
            JsonNode.Parse(patch)?.AsObject() ?? throw new InvalidOperationException("补丁必须是 JSON 对象。"))).ToJsonString();

    public static string InspectJson(RhinoDoc document) => Inspect(document).ToJsonString();

    public static string ReadProjectJson(RhinoDoc document) =>
        ProjectEngine.Serialize(ReadProject(document) ?? throw new InvalidOperationException("文档没有简易 BIM 项目。"));

    public static BuildingProject? ReadProject(RhinoDoc document)
    {
        var states = Objects(document).Where(item => item.Attributes.GetUserString("miki.kind") == "state").ToArray();
        if (states.Length > 1) throw new InvalidOperationException("文档有多个 BIM 状态记录，请先检查，尚未修改。");
        string? json = states.SingleOrDefault()?.Attributes.GetUserString(SourceKey);
        return json is null ? null : ProjectEngine.Parse(json);
    }

    public static JsonObject Apply(RhinoDoc document, BuildingProject project)
    {
        ProjectEngine.Validate(project);
        var previous = ReadProject(document);
        if (previous is not null && previous.Id != project.Id)
            throw new InvalidOperationException("当前文档已有另一个简易 BIM 项目，请使用独立文档。");
        var existing = Managed(document, project.Id);
        if (previous is null && existing.Count > 0)
            throw new InvalidOperationException("检测到缺少源记录的 BIM 对象，请先恢复源数据，不自动覆盖。");
        if (previous is not null) RequireSynchronized(document, previous);
        double scale = Scale(document);
        double[] featureSizes = project.Walls.Select(wall => wall.Thickness)
            .Concat(project.Slabs.Select(slab => slab.Thickness)).ToArray();
        if (featureSizes.Length > 0 && document.ModelAbsoluteTolerance > featureSizes.Min() * scale / 20)
            throw new InvalidOperationException("当前公差相对墙或楼板厚度过大，请自行确认并设置公差后继续；插件不会修改公差。");
        var parts = NativeGeometry.Build(project, scale, document.ModelAbsoluteTolerance);
        var affected = previous is null ? parts.Select(part => part.SourceId).ToHashSet(StringComparer.Ordinal)
            : ProjectEngine.AffectedIds(previous, project).ToHashSet(StringComparer.Ordinal);
        var definitions = new Dictionary<string, InstanceDefinition>(StringComparer.Ordinal);
        foreach (var type in project.Types)
        {
            string name = DefinitionName(project.Id, type.Id);
            var definition = document.InstanceDefinitions.Find(name);
            var geometry = NativeGeometry.Block(type, scale);
            if (definition is null)
            {
                if (previous is not null && previous.Types.Any(item => item.Id == type.Id))
                    throw new InvalidOperationException("共享 Block 定义缺失：" + type.Id);
            }
            else if (previous?.Types.Any(item => item.Id == type.Id) != true)
                throw new InvalidOperationException("同名 Block 已存在但不属于本项目状态：" + name);
            definitions[type.Id] = definition!;
            if (geometry.Any(item => !item.IsValid)) throw new InvalidOperationException("Block 几何无效：" + type.Id);
        }
        foreach (var type in project.Types)
        {
            var definition = definitions[type.Id];
            if (definition is null)
            {
                int index = document.InstanceDefinitions.Add(DefinitionName(project.Id, type.Id),
                    "Miki lightweight architectural type", Point3d.Origin, NativeGeometry.Block(type, scale));
                if (index < 0) throw new InvalidOperationException("无法新增共享 Block：" + type.Id);
                definitions[type.Id] = document.InstanceDefinitions[index];
            }
            else if (previous is not null && ProjectEngine.Serialize(previous) != ProjectEngine.Serialize(project) &&
                JsonSerializer.Serialize(previous.Types.Single(item => item.Id == type.Id), ProjectEngine.JsonOptions) !=
                JsonSerializer.Serialize(type, ProjectEngine.JsonOptions))
            {
                if (!document.InstanceDefinitions.ModifyGeometry(definition.Index, NativeGeometry.Block(type, scale),
                    definition.GetObjects().Select(item => item.Attributes.Duplicate())))
                    throw new InvalidOperationException("无法修改共享 Block：" + type.Id);
            }
        }
        int created = 0;
        int updated = 0;
        foreach (var part in parts)
        {
            if (!affected.Contains(part.SourceId) && existing.ContainsKey(part.Id)) continue;
            existing.TryGetValue(part.Id, out var current);
            var attributes = Attributes(document, project.Id, part);
            if (current is not null)
            {
                var preserved = current.Attributes.Duplicate();
                if (document.Layers[preserved.LayerIndex].Id.ToString() == preserved.GetUserString("miki.layer"))
                    preserved.LayerIndex = attributes.LayerIndex;
                foreach (string key in new[] { ProjectKey, PartKey, "miki.id", "miki.kind", "miki.level", "miki.type", "miki.host", "miki.layer" })
                    preserved.SetUserString(key, attributes.GetUserString(key));
                attributes = preserved;
            }
            if (current is null)
            {
                Guid id = part.Type is not null
                    ? document.Objects.AddInstanceObject(definitions[part.Type].Index, part.Placement, attributes)
                    : document.Objects.Add(part.Geometry!, attributes);
                if (id == Guid.Empty) throw new InvalidOperationException("无法创建原生对象：" + part.Id);
                created++;
            }
            else
            {
                bool sameInstance = part.Type is not null && current is InstanceObject instance &&
                    instance.InstanceDefinition.Id == definitions[part.Type].Id && instance.InstanceXform.Equals(part.Placement);
                GeometryBase? replacement = part.Type is not null && !sameInstance
                    ? new InstanceReferenceGeometry(definitions[part.Type].Id, part.Placement) : part.Geometry;
                if (replacement is not null && !document.Objects.Replace(current.Id, replacement, true))
                    throw new InvalidOperationException("无法更新原生对象：" + part.Id);
                if (!document.Objects.ModifyAttributes(current.Id, attributes, true))
                    throw new InvalidOperationException("无法更新构件关联：" + part.Id);
                updated++;
            }
        }
        var desired = parts.Select(part => part.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in existing.Where(pair => !desired.Contains(pair.Key)))
            if (!document.Objects.Delete(obsolete.Value.Id, true))
                throw new InvalidOperationException("无法清理已替换构件：" + obsolete.Key);
        StoreState(document, project);
        document.Views.Redraw();
        var report = Inspect(document);
        report["created"] = created;
        report["updated"] = updated;
        report["affectedIds"] = new JsonArray(affected.Order().Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        return report;
    }

    public static JsonObject Inspect(RhinoDoc document)
    {
        var project = ReadProject(document) ?? throw new InvalidOperationException("文档没有简易 BIM 项目。");
        RequireSynchronized(document, project);
        double scale = Scale(document);
        var parts = NativeGeometry.Build(project, scale, document.ModelAbsoluteTolerance);
        var actual = Managed(document, project.Id);
        var checks = new JsonArray();
        var dimensions = new JsonObject();
        var volumes = new JsonObject();
        double epsilon = Math.Max(document.ModelAbsoluteTolerance * 2 / scale, 1e-6);
        foreach (var part in parts)
        {
            if (!actual.TryGetValue(part.Id, out var item)) throw new InvalidOperationException("构件缺失：" + part.Id);
            if (!item.Geometry.IsValid || item.Geometry is Mesh or SubD)
                throw new InvalidOperationException("原生精确几何检查未通过：" + part.Id);
            GeometryBase expected = part.Geometry!;
            BoundingBox expectedBox;
            if (part.Type is not null)
            {
                if (item is not InstanceObject instance || instance.InstanceDefinition.Name != DefinitionName(project.Id, part.Type))
                    throw new InvalidOperationException("共享 Block 引用错误：" + part.Id);
                expectedBox = BoundingBox.Empty;
                foreach (var geometry in NativeGeometry.Block(project.Types.Single(type => type.Id == part.Type), scale))
                {
                    geometry.Transform(part.Placement);
                    expectedBox.Union(geometry.GetBoundingBox(true));
                }
            }
            else expectedBox = expected.GetBoundingBox(true);
            var box = item.Geometry.GetBoundingBox(true);
            if (box.Min.DistanceTo(expectedBox.Min) / scale > epsilon || box.Max.DistanceTo(expectedBox.Max) / scale > epsilon)
                throw new InvalidOperationException("实际尺寸与项目参数不一致：" + part.Id);
            if (part.ExpectedVolume > 0)
            {
                double volume = item is InstanceObject instance
                    ? instance.InstanceDefinition.GetObjects().Sum(child => NativeGeometry.Volume(child.Geometry))
                    : NativeGeometry.Volume(item.Geometry);
                volume /= Math.Pow(scale, 3);
                if (item.Geometry is Brep brep && !brep.IsSolid || !double.IsFinite(volume) ||
                    Math.Abs(volume - part.ExpectedVolume) > Math.Max(part.ExpectedVolume * 1e-6, 1e-8))
                    throw new InvalidOperationException("实际实体体积或穿洞检查未通过：" + part.Id);
                volumes[part.Id] = volume;
            }
            if (part.Kind == "wall" && item.Geometry is Brep wallBrep)
            {
                var wall = project.Walls.Single(candidate => candidate.Id == part.SourceId);
                var level = project.Levels.Single(candidate => candidate.Id == wall.Level);
                var direction = new Vector3d(wall.End[0] - wall.Start[0], wall.End[1] - wall.Start[1], 0);
                direction.Unitize();
                foreach (var opening in project.Openings.Where(candidate => candidate.Wall == wall.Id))
                {
                    var type = project.Types.Single(candidate => candidate.Id == opening.Type);
                    var center = new Point3d((wall.Start[0] + direction.X * (opening.Offset + type.Width / 2)) * scale,
                        (wall.Start[1] + direction.Y * (opening.Offset + type.Width / 2)) * scale,
                        (level.Elevation + opening.Sill + type.Height / 2) * scale);
                    if (wallBrep.IsPointInside(center, document.ModelAbsoluteTolerance, false))
                        throw new InvalidOperationException("洞口中心仍被墙体占据：" + opening.Id);
                }
            }
            if (part.Kind is "wall" or "slab" or "window" or "door")
                dimensions[part.Id] = new JsonObject { ["min"] = Coordinates(box.Min, scale), ["max"] = Coordinates(box.Max, scale),
                    ["objectId"] = item.Id.ToString(), ["kind"] = part.Kind, ["type"] = part.Type };
        }
        checks.Add("native geometry valid; no Mesh/SubD");
        checks.Add("wall/slab solids and through-opening volumes match parameters");
        checks.Add("plan curves, stair parts and instance bounds match current revision");
        checks.Add("shared Block types and managed-object fingerprints match");
        return new JsonObject
        {
            ["verified"] = true, ["project"] = project.Id, ["revision"] = project.Revision,
            ["units"] = document.ModelUnitSystem.ToString(), ["sourceUnits"] = "m",
            ["toleranceMeters"] = document.ModelAbsoluteTolerance / scale,
            ["levels"] = project.Levels.Length, ["walls"] = project.Walls.Length,
            ["openings"] = project.Openings.Length, ["stairs"] = project.Stairs.Length,
            ["managedObjects"] = actual.Count, ["blockTypes"] = project.Types.Length,
            ["blockInstances"] = parts.Count(part => part.Type is not null),
            ["checks"] = checks, ["bounds"] = dimensions, ["volumesM3"] = volumes,
        };
    }

    private static ObjectAttributes Attributes(RhinoDoc document, string project, NativePart part)
    {
        string layerName = LayerName(project, part.Kind, part.Level);
        int layerIndex = document.Layers.FindByFullPath(layerName, -1);
        if (layerIndex < 0) layerIndex = document.Layers.Add(layerName,
            part.Kind.Contains("plan") || part.Kind == "grid" ? System.Drawing.Color.SlateGray : System.Drawing.Color.LightSteelBlue);
        if (layerIndex < 0) throw new InvalidOperationException("无法建立 BIM 图层。");
        var attributes = new ObjectAttributes { Name = "MIKI:" + project + ":" + part.Id, LayerIndex = layerIndex };
        attributes.SetUserString(ProjectKey, project);
        attributes.SetUserString(PartKey, part.Id);
        attributes.SetUserString("miki.id", part.SourceId);
        attributes.SetUserString("miki.kind", part.Kind);
        attributes.SetUserString("miki.level", part.Level);
        attributes.SetUserString("miki.layer", document.Layers[layerIndex].Id.ToString());
        if (part.Type is not null) attributes.SetUserString("miki.type", part.Type);
        if (part.Host is not null) attributes.SetUserString("miki.host", part.Host);
        return attributes;
    }

    private static void RequireSynchronized(RhinoDoc document, BuildingProject project)
    {
        var state = Objects(document).Single(item => item.Attributes.GetUserString("miki.kind") == "state");
        if (state.Attributes.GetUserString("miki.units") != document.ModelUnitSystem.ToString())
            throw new InvalidOperationException("文档单位已改变，需确认并迁移项目，尚未自动换算已有对象。");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(state.Attributes.GetUserString("miki.stamps") ?? "{}")!;
        var actual = Stamps(document, project);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            throw new InvalidOperationException("BIM 对象或共享 Block 被手动修改、删除或撤销，源数据和几何不一致。请恢复后再继续，插件不会覆盖这些修改。");
    }

    private static Dictionary<string, string> Stamps(RhinoDoc document, BuildingProject project)
    {
        var stamps = Managed(document, project.Id).ToDictionary(pair => pair.Key, pair =>
            pair.Value.Id + ":" + GeometryStamp(pair.Value) + ":" +
            pair.Value.Attributes.GetUserString("miki.id") + ":" + pair.Value.Attributes.GetUserString("miki.type") + ":" +
            pair.Value.Attributes.GetUserString("miki.host") + ":" + pair.Value.Attributes.GetUserString("miki.layer"), StringComparer.Ordinal);
        foreach (var type in project.Types)
        {
            var definition = document.InstanceDefinitions.Find(DefinitionName(project.Id, type.Id))
                ?? throw new InvalidOperationException("共享 Block 缺失：" + type.Id);
            stamps["definition:" + type.Id] = definition.Id + ":" + string.Join(",", definition.GetObjects()
                .Select(item => item.Geometry.DataCRC(0).ToString(CultureInfo.InvariantCulture)));
        }
        return stamps;
    }

    private static string GeometryStamp(RhinoObject item)
    {
        if (item is not InstanceObject instance) return item.Geometry.DataCRC(0).ToString(CultureInfo.InvariantCulture);
        var transform = instance.InstanceXform;
        return "instance:" + instance.InstanceDefinition.Id + ":" + string.Join(",", Enumerable.Range(0, 16)
            .Select(index => transform[index / 4, index % 4].ToString("R", CultureInfo.InvariantCulture)));
    }

    private static void StoreState(RhinoDoc document, BuildingProject project)
    {
        var state = Objects(document).SingleOrDefault(item => item.Attributes.GetUserString("miki.kind") == "state");
        var attributes = state?.Attributes.Duplicate() ?? new ObjectAttributes { Name = "MIKI:" + project.Id + ":state", Visible = false };
        string source = ProjectEngine.Serialize(project);
        attributes.SetUserString(ProjectKey, project.Id);
        attributes.SetUserString("miki.kind", "state");
        attributes.SetUserString(SourceKey, source);
        attributes.SetUserString("miki.units", document.ModelUnitSystem.ToString());
        attributes.SetUserString("miki.stamps", JsonSerializer.Serialize(Stamps(document, project)));
        bool stored = state is null ? document.Objects.AddPoint(Point3d.Origin, attributes) != Guid.Empty
            : document.Objects.ModifyAttributes(state.Id, attributes, true);
        if (!stored) throw new InvalidOperationException("无法保存可撤销的 BIM 源状态。");
        document.Strings.SetString(Section, "project", source);
    }

    private static IEnumerable<RhinoObject> Objects(RhinoDoc document) => document.Objects.GetObjectList(
        new ObjectEnumeratorSettings { NormalObjects = true, HiddenObjects = true, LockedObjects = true, ReferenceObjects = false });

    private static Dictionary<string, RhinoObject> Managed(RhinoDoc document, string project) => Objects(document)
        .Where(item => item.Attributes.GetUserString(ProjectKey) == project && item.Attributes.GetUserString(PartKey) is not null)
        .ToDictionary(item => item.Attributes.GetUserString(PartKey)!, StringComparer.Ordinal);

    private static string LayerName(string project, string kind, string level) =>
        "Miki-" + project + "-" + (kind.Contains("plan") || kind == "grid" ? "Plan-" : "Model-") + level;

    private static string DefinitionName(string project, string type) => "MIKI_" + type[..Math.Min(type.Length, 24)] + "_" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(project + "\0" + type)));

    private static double Scale(RhinoDoc document)
    {
        if (document.ModelUnitSystem is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            throw new InvalidOperationException("请先设置明确的 Rhino 长度单位。");
        return RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
    }

    private static JsonArray Coordinates(Point3d point, double scale) => new(point.X / scale, point.Y / scale, point.Z / scale);
}
