using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RhinoMagpie.Agent;

public record IfcRuntimeLocation(string RootDirectory, string PythonPath, string WorkerPath,
    string PythonVersion, string IfcVersion);

public static class IfcRuntimeBundle
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] RequiredFiles =
    [
        "python.exe", "python312.dll", "python312._pth", "msvcp140.dll", "Lib/encodings/__init__.py",
        "Lib/site-packages/ifcopenshell/__init__.py",
    ];

    public static IfcRuntimeLocation Resolve(string pluginDirectory) => ResolveCore(pluginDirectory, verifyIntegrity: true);

    private static IfcRuntimeLocation ResolveCore(string pluginDirectory, bool verifyIntegrity)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
            string openBimDirectory = Path.Combine(Path.GetFullPath(pluginDirectory), "openbim");
            string root = Path.Combine(openBimDirectory, "runtime");
            string manifestPath = Path.Combine(root, "manifest.json");
            CheckPath(manifestPath);
            var manifest = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(manifestPath), ManifestOptions);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Platform != "win-x64" ||
                manifest.PythonVersion != "3.12.13" || manifest.IfcopenshellVersion != "0.9.0.post1" ||
                manifest.Files is not { Length: > 0 })
                throw new InvalidDataException("The runtime manifest does not match the supported bundle.");

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Files)
            {
                if (entry is null || entry.Size is null or < 0)
                    throw new InvalidDataException("A runtime file entry is incomplete.");
                string filePath = RuntimeFilePath(root, entry.Path);
                if (!paths.Add(entry.Path!))
                    throw new InvalidDataException("The runtime manifest contains duplicate file paths.");
                CheckDigest(entry.Sha256);
                if (verifyIntegrity) VerifyFile(filePath, entry.Size, entry.Sha256);
            }

            foreach (string required in RequiredFiles)
            {
                if (!paths.Contains(required))
                    throw new InvalidDataException("A critical runtime file is absent from the manifest.");
                CheckPath(RuntimeFilePath(root, required));
            }

            string snapshotPath = Path.Combine(openBimDirectory, "snapshot.json");
            CheckPath(snapshotPath);
            var snapshot = JsonSerializer.Deserialize<SourceSnapshot>(File.ReadAllText(snapshotPath), ManifestOptions);
            if (snapshot?.Files is null || snapshot.Files.Any(entry => entry is null))
                throw new InvalidDataException("The worker source snapshot is incomplete.");
            var workers = snapshot.Files.Where(entry => string.Equals(entry!.Path,
                "python/miki_ifc.py", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (workers.Length != 1)
                throw new InvalidDataException("The worker source snapshot must identify exactly one IFC worker.");
            string workerPath = Path.Combine(openBimDirectory, "miki_ifc.py");
            CheckDigest(workers[0]!.Sha256);
            CheckPath(workerPath);
            if (verifyIntegrity) VerifyFile(workerPath, null, workers[0]!.Sha256);

            return new IfcRuntimeLocation(root, Path.Combine(root, "python.exe"), workerPath,
                manifest.PythonVersion, manifest.IfcopenshellVersion);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "The bundled IFC runtime is missing or damaged. Please reinstall the full plugin bundle.", error);
        }
    }

    public static bool IsPresent(string pluginDirectory)
    {
        try { ResolveCore(pluginDirectory, verifyIntegrity: false); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static ProcessStartInfo CreateStartInfo(IfcRuntimeLocation location, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo
        {
            FileName = location.PythonPath,
            WorkingDirectory = location.RootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-S");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(location.WorkerPath);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        start.Environment.Clear();
        foreach (string name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) start.Environment[name] = value;
        }
        var privatePath = new List<string> { location.RootDirectory, Path.Combine(location.RootDirectory, "DLLs") };
        if (start.Environment.TryGetValue("SystemRoot", out string? windowsRoot) ||
            start.Environment.TryGetValue("WINDIR", out windowsRoot))
            privatePath.Add(Path.Combine(windowsRoot!, "System32"));
        start.Environment["PATH"] = string.Join(Path.PathSeparator, privatePath);
        return start;
    }

    private static string RuntimeFilePath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            relative.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => char.IsControl(character) || "<>:\"\\|?*".Contains(character))))
            throw new InvalidDataException("A runtime manifest path is not a relative forward-slash file path.");
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CheckPath(string path)
    {
        string? current = path;
        bool directory = false;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException("A bundle path is a link, reparse point or unexpected file type.");
            directory = true;
            current = Path.GetDirectoryName(current);
        }
    }

    private static void CheckDigest(string? sha256)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("A bundle file digest is invalid.");
    }

    private static void VerifyFile(string path, long? size, string? sha256)
    {
        CheckPath(path);
        using var stream = File.OpenRead(path);
        if ((size is not null && stream.Length != size) ||
            !Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A bundle file does not match its recorded size or SHA-256 digest.");
    }

    private sealed record BundleManifest(int SchemaVersion, string? Platform, string? PythonVersion,
        string? IfcopenshellVersion, ManifestFile?[]? Files);

    private sealed record ManifestFile(string? Path, long? Size, string? Sha256);

    private sealed record SourceSnapshot(SnapshotFile?[]? Files);

    private sealed record SnapshotFile(string? Path, string? Sha256);
}
