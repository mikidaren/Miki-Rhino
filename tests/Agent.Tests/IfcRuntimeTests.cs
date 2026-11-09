using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RhinoMagpie.Agent;

static class IfcRuntimeTests
{
    public static int Run()
    {
        int passed = 0;
        int skipped = 0;
        var failures = new List<Exception>();

        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }

        void Scenario(string name, Action test)
        {
            try { test(); }
            catch (Exception error)
            {
                failures.Add(new Exception(name, error));
                Console.WriteLine("FAIL IFC scenario " + name + " - " + error.Message);
            }
        }

        void Reject(string pluginDirectory, string name, bool catalogAvailable = false)
        {
            Check(IfcRuntimeBundle.IsPresent(pluginDirectory) == catalogAvailable,
                name + (catalogAvailable ? " stays available to the metadata-only catalog" : " is unavailable"));
            try { IfcRuntimeBundle.Resolve(pluginDirectory); }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("reinstall", StringComparison.OrdinalIgnoreCase) &&
                    error.Message.Contains("full plugin bundle", StringComparison.OrdinalIgnoreCase) &&
                    new[] { "setup", "pip", "download" }.All(word =>
                        !error.Message.Contains(word, StringComparison.OrdinalIgnoreCase)),
                    name + " gives full-bundle reinstall guidance");
                return;
            }
            throw new Exception("FAIL " + name + " was accepted");
        }

        Scenario("fixed portable bundle", () =>
        {
            using var fixture = new RuntimeFixture();
            var location = IfcRuntimeBundle.Resolve(fixture.PluginDirectory);
            Check(location.RootDirectory == fixture.RuntimeDirectory &&
                location.PythonPath == fixture.RuntimeFile("python.exe") &&
                location.WorkerPath == fixture.WorkerPath,
                "runtime and worker resolve inside a plugin path containing spaces and Unicode");
            Check(location.PythonVersion == "3.12.13" && location.IfcVersion == "0.9.0.post1",
                "resolved versions match the pinned manifest");
            Check(IfcRuntimeBundle.IsPresent(fixture.PluginDirectory), "a complete bundle is available");
            Check(IfcRuntimeBundle.Resolve(Path.Combine(fixture.PluginDirectory, ".")) == location,
                "relative path segments normalize to the same fixed bundle");
            Check(IfcRuntimeBundle.Resolve(fixture.PluginDirectory + Path.DirectorySeparatorChar) == location,
                "a trailing plugin directory separator preserves the fixed location");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, fixture.PluginDirectory);
            Check(IfcRuntimeBundle.Resolve(relative) == location, "relative plugin directories resolve to absolute paths");
            File.WriteAllText(Path.Combine(fixture.PluginDirectory, "openbim-runtime.json"), "{broken legacy config");
            Check(IfcRuntimeBundle.Resolve(fixture.PluginDirectory) == location,
                "a legacy external-runtime configuration cannot change a valid fixed bundle");
        });

        Scenario("no external fallback", () =>
        {
            using var fixture = new RuntimeFixture();
            string alternate = Path.Combine(fixture.DirectoryPath, "external runtime");
            Directory.Move(fixture.RuntimeDirectory, alternate);
            File.WriteAllText(Path.Combine(fixture.PluginDirectory, "openbim-runtime.json"),
                new JsonObject { ["python"] = Path.Combine(alternate, "python.exe"),
                    ["worker"] = fixture.WorkerPath }.ToJsonString());
            Reject(fixture.PluginDirectory, "a missing fixed runtime with a legacy configuration");
        });

        foreach (string relative in RuntimeFixture.RuntimePaths.Concat(new[]
        {
            "manifest.json", "../miki_ifc.py", "../snapshot.json",
        }))
        {
            Scenario("missing " + relative, () =>
            {
                using var fixture = new RuntimeFixture();
                File.Delete(fixture.RuntimeFile(relative));
                Reject(fixture.PluginDirectory, "missing " + relative,
                    RuntimeFixture.RuntimePaths.Contains(relative) && !RuntimeFixture.CriticalPaths.Contains(relative));
            });
        }

        foreach (string relative in RuntimeFixture.RuntimePaths)
        {
            Scenario("corrupted " + relative, () =>
            {
                using var fixture = new RuntimeFixture();
                byte[] bytes = File.ReadAllBytes(fixture.RuntimeFile(relative));
                bytes[0] ^= 1;
                File.WriteAllBytes(fixture.RuntimeFile(relative), bytes);
                Reject(fixture.PluginDirectory, "same-size corruption of " + relative, catalogAvailable: true);
            });
        }

        foreach (string relative in RuntimeFixture.CriticalPaths)
        {
            Scenario("undeclared " + relative, () =>
            {
                using var fixture = new RuntimeFixture();
                fixture.Files.Remove(fixture.Files.Single(entry => entry!["path"]!.GetValue<string>() == relative));
                fixture.SaveManifest();
                Reject(fixture.PluginDirectory, "an existing but undeclared critical file " + relative);
            });
        }

        foreach (string invalid in new[] { "{broken", "null", "[]", "", "{}" })
        {
            Scenario("malformed manifest " + invalid, () =>
            {
                using var fixture = new RuntimeFixture();
                File.WriteAllText(fixture.ManifestPath, invalid);
                Reject(fixture.PluginDirectory, "malformed manifest " + invalid);
            });
        }

        foreach (var (property, invalid) in new (string, JsonNode?)[]
        {
            ("schemaVersion", JsonValue.Create(0)), ("schemaVersion", JsonValue.Create(2)),
            ("schemaVersion", JsonValue.Create("1")), ("schemaVersion", null),
            ("platform", JsonValue.Create("linux-x64")), ("platform", JsonValue.Create("win-arm64")),
            ("platform", JsonValue.Create("WIN-X64")), ("platform", null),
            ("pythonVersion", JsonValue.Create("3.12.12")), ("pythonVersion", JsonValue.Create("3.13.0")),
            ("pythonVersion", null), ("ifcopenshellVersion", JsonValue.Create("0.9.0")),
            ("ifcopenshellVersion", JsonValue.Create("0.9.0.post2")), ("ifcopenshellVersion", null),
            ("files", null), ("files", new JsonArray()), ("files", new JsonArray((JsonNode?)null)),
            ("files", new JsonObject()),
        })
        {
            string name = property + " = " + (invalid?.ToJsonString() ?? "null");
            Scenario("unsupported manifest " + name, () =>
            {
                using var fixture = new RuntimeFixture();
                fixture.Manifest[property] = invalid?.DeepClone();
                fixture.SaveManifest();
                Reject(fixture.PluginDirectory, "unsupported manifest " + name);
            });
        }

        foreach (var (property, invalid) in new (string, JsonNode?)[]
        {
            ("size", null), ("size", JsonValue.Create(-1)), ("size", JsonValue.Create(999999)),
            ("size", JsonValue.Create("16")), ("size", JsonValue.Create(1.5)),
            ("sha256", null), ("sha256", JsonValue.Create("not-a-sha256")),
            ("sha256", JsonValue.Create(new string('g', 64))),
            ("sha256", JsonValue.Create(new string('0', 64))),
            ("path", null), ("path", JsonValue.Create(42)),
        })
        {
            string name = property + " = " + (invalid?.ToJsonString() ?? "null");
            Scenario("invalid file metadata " + name, () =>
            {
                using var fixture = new RuntimeFixture();
                fixture.Files[0]![property] = invalid?.DeepClone();
                fixture.SaveManifest();
                bool wellFormed = (property == "size" && invalid?.ToJsonString() == "999999") ||
                    (property == "sha256" && invalid?.GetValue<string>() == new string('0', 64));
                Reject(fixture.PluginDirectory, "invalid file metadata " + name, catalogAvailable: wellFormed);
            });
        }

        Scenario("missing size property", () =>
        {
            using var fixture = new RuntimeFixture();
            File.WriteAllBytes(fixture.RuntimeFile("python.exe"), []);
            fixture.RefreshEntry("python.exe");
            fixture.Files[0]!.AsObject().Remove("size");
            fixture.SaveManifest();
            Reject(fixture.PluginDirectory, "a missing size even for an empty file");
        });

        Scenario("uppercase digests", () =>
        {
            using var fixture = new RuntimeFixture();
            foreach (var entry in fixture.Files)
                entry!["sha256"] = entry["sha256"]!.GetValue<string>().ToUpperInvariant();
            fixture.SaveManifest();
            Check(IfcRuntimeBundle.IsPresent(fixture.PluginDirectory) &&
                IfcRuntimeBundle.Resolve(fixture.PluginDirectory).PythonPath == fixture.RuntimeFile("python.exe"),
                "hexadecimal digest case does not change integrity");
        });

        foreach (string duplicate in new[] { "python.exe", "PYTHON.EXE" })
        {
            Scenario("duplicate " + duplicate, () =>
            {
                using var fixture = new RuntimeFixture();
                byte[] bytes = File.ReadAllBytes(fixture.RuntimeFile("python.exe"));
                File.WriteAllBytes(fixture.RuntimeFile(duplicate), bytes);
                var entry = fixture.Files[0]!.DeepClone();
                entry["path"] = duplicate;
                fixture.Files.Add(entry);
                fixture.SaveManifest();
                Reject(fixture.PluginDirectory, "a duplicate manifest path " + duplicate);
            });
        }

        foreach (string invalid in new[]
        {
            "../outside.bin", "Lib/../../outside.bin", "./python.exe", "Lib/../python.exe",
            "/python.exe", "C:/python.exe", "C:\\python.exe", "C:python.exe",
            "\\\\server\\share\\python.exe", "//server/share/python.exe", "Lib\\..\\python.exe",
            "Lib//encodings/__init__.py", "python.exe/", "python.exe.", "python.exe ",
            "python.exe:stream", "", " ", "manifest.json",
        })
        {
            Scenario("unsafe manifest path " + invalid, () =>
            {
                using var fixture = new RuntimeFixture();
                File.WriteAllBytes(Path.Combine(fixture.PluginDirectory, "openbim", "outside.bin"),
                    File.ReadAllBytes(fixture.RuntimeFile("python.exe")));
                var entry = fixture.Files[0]!.DeepClone();
                entry["path"] = invalid;
                fixture.Files.Add(entry);
                fixture.SaveManifest();
                Reject(fixture.PluginDirectory, "unsafe manifest path " + invalid);
            });
        }

        Scenario("worker snapshot corruption", () =>
        {
            using var fixture = new RuntimeFixture();
            byte[] bytes = File.ReadAllBytes(fixture.WorkerPath);
            bytes[0] ^= 1;
            File.WriteAllBytes(fixture.WorkerPath, bytes);
            Reject(fixture.PluginDirectory, "a worker that differs from its sibling source snapshot", catalogAvailable: true);
        });

        foreach (string invalid in new[]
        {
            "{broken", "null", "{}", """{"files":null}""", """{"files":[null]}""",
            """{"files":[]}""", """{"files":[{"path":"python/miki_ifc.py","sha256":"invalid"}]}""",
        })
        {
            Scenario("invalid snapshot " + invalid, () =>
            {
                using var fixture = new RuntimeFixture();
                File.WriteAllText(fixture.SnapshotPath, invalid);
                Reject(fixture.PluginDirectory, "invalid snapshot " + invalid);
            });
        }

        Scenario("duplicate snapshot worker", () =>
        {
            using var fixture = new RuntimeFixture();
            var snapshot = JsonNode.Parse(File.ReadAllText(fixture.SnapshotPath))!.AsObject();
            var entries = snapshot["files"]!.AsArray();
            var duplicate = entries.Last()!.DeepClone();
            duplicate["path"] = "PYTHON/MIKI_IFC.PY";
            entries.Add(duplicate);
            File.WriteAllText(fixture.SnapshotPath, snapshot.ToJsonString());
            Reject(fixture.PluginDirectory, "ambiguous case-insensitive worker entries in the snapshot");
        });

        foreach (string? invalid in new string?[] { null, "", " ", "\0invalid" })
            Scenario("invalid plugin directory", () => Reject(invalid!, "an invalid plugin directory"));

        Scenario("missing plugin directory", () =>
        {
            using var fixture = new RuntimeFixture();
            Reject(Path.Combine(fixture.DirectoryPath, "missing plugin"), "a nonexistent plugin directory");
        });

        foreach (string relative in new[] { "", "openbim", "openbim/runtime", "openbim/runtime/DLLs" })
        {
            Scenario("linked directory " + relative, () =>
            {
                using var fixture = new RuntimeFixture();
                string link = Path.Combine(fixture.PluginDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                string target = Path.Combine(fixture.DirectoryPath, "real directory");
                Directory.Move(link, target);
                try
                {
                    try { Directory.CreateSymbolicLink(link, target); }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                    {
                        Console.WriteLine("SKIP linked directory " + relative + " - " + error.Message);
                        skipped++;
                        return;
                    }
                    Reject(fixture.PluginDirectory, "a symlink or reparse directory " + relative,
                        catalogAvailable: relative.EndsWith("/DLLs", StringComparison.Ordinal));
                }
                finally
                {
                    if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
                }
            });
        }

        Scenario("linked plugin ancestor", () =>
        {
            using var fixture = new RuntimeFixture();
            string alias = Path.Combine(fixture.DirectoryPath, "linked ancestor");
            try
            {
                try { Directory.CreateSymbolicLink(alias, fixture.DirectoryPath); }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    Console.WriteLine("SKIP linked plugin ancestor - " + error.Message);
                    skipped++;
                    return;
                }
                Reject(Path.Combine(alias, Path.GetFileName(fixture.PluginDirectory)), "a symlink above the plugin directory");
            }
            finally
            {
                if (new DirectoryInfo(alias).LinkTarget is not null) Directory.Delete(alias);
            }
        });

        foreach (string relative in new[]
        {
            "python.exe", "manifest.json", "../miki_ifc.py", "../snapshot.json",
            "Lib/site-packages/ifcopenshell/_ifcopenshell_wrapper.pyd",
        })
        {
            Scenario("linked file " + relative, () =>
            {
                using var fixture = new RuntimeFixture();
                string link = fixture.RuntimeFile(relative);
                string target = Path.Combine(fixture.DirectoryPath, "real file");
                File.Move(link, target);
                try
                {
                    try { File.CreateSymbolicLink(link, target); }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                    {
                        Console.WriteLine("SKIP linked file " + relative + " - " + error.Message);
                        skipped++;
                        return;
                    }
                    Reject(fixture.PluginDirectory, "a symlink or reparse file " + relative,
                        catalogAvailable: relative.EndsWith(".pyd", StringComparison.Ordinal));
                }
                finally
                {
                    if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
                }
            });
        }

        Scenario("isolated start info", () =>
        {
            using var fixture = new RuntimeFixture();
            var location = IfcRuntimeBundle.Resolve(fixture.PluginDirectory);
            string windowsRoot = Path.Combine(fixture.DirectoryPath, "mock Windows");
            string alternateRoot = Path.Combine(fixture.DirectoryPath, "other Windows");
            string temp = Path.Combine(fixture.DirectoryPath, "temporary files");
            var ambient = new Dictionary<string, string?>
            {
                ["SystemRoot"] = windowsRoot, ["WINDIR"] = alternateRoot, ["TEMP"] = temp, ["TMP"] = temp,
                ["PATH"] = "unusable-parent-path", ["PYTHONHOME"] = "ambient-python-home",
                ["PYTHONPATH"] = "ambient-python-path", ["VIRTUAL_ENV"] = "ambient-venv",
                ["PYTHONDONTWRITEBYTECODE"] = "0",
                ["PYTHONPYCACHEPREFIX"] = Path.Combine(fixture.DirectoryPath, "developer bytecode cache"),
                ["OPENAI_API_KEY"] = "private-provider-sentinel", ["ANTHROPIC_API_KEY"] = "private-provider-sentinel",
                ["MAGPIE_API_KEY"] = "private-provider-sentinel", ["AZURE_OPENAI_API_KEY"] = "private-provider-sentinel",
                ["UNRELATED_PARENT_VARIABLE"] = "unrelated-parent-sentinel",
            };
            using var environment = new EnvironmentFixture(ambient);
            string[] arguments = ["export", "--project", "a path/中文 \"project\".json", "--output", "x;$(inert).ifc"];
            var start = IfcRuntimeBundle.CreateStartInfo(location, arguments.Select(argument => argument));
            Check(start.FileName == location.PythonPath && start.WorkingDirectory == fixture.RuntimeDirectory,
                "launch uses the private interpreter and the runtime working directory");
            Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardInput &&
                start.RedirectStandardOutput && start.RedirectStandardError,
                "launch disables the shell and window and redirects all three streams");
            Check(start.ArgumentList.SequenceEqual(new[] { "-I", "-S", "-B", location.WorkerPath }.Concat(arguments)) &&
                start.Arguments == "", "isolated launch preserves individual worker arguments without shell quoting");
            Check(start.ArgumentList.Take(3).SequenceEqual(new[] { "-I", "-S", "-B" }) &&
                !start.Environment.Keys.Any(name => name.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)),
                "CLI flags enforce isolation, disabled site initialization and disabled bytecode writes with a cleared Python environment");
            string expectedPath = string.Join(Path.PathSeparator, fixture.RuntimeDirectory,
                fixture.RuntimeFile("DLLs"), Path.Combine(windowsRoot, "System32"));
            Check(start.Environment["PATH"] == expectedPath,
                "PATH contains only the runtime, its DLLs and System32 derived from the supplied system root");
            Check(start.Environment.Count == 5 && start.Environment["SystemRoot"] == windowsRoot &&
                start.Environment["WINDIR"] == alternateRoot && start.Environment["TEMP"] == temp &&
                start.Environment["TMP"] == temp, "only system roots, temporary directories and the private PATH survive");
            Check(ambient.Where(entry => entry.Key is not ("SystemRoot" or "WINDIR" or "TEMP" or "TMP" or "PATH"))
                .All(entry => !start.Environment.ContainsKey(entry.Key)),
                "provider credentials, Python settings and arbitrary parent variables are absent");
            Check(ambient.All(entry => Environment.GetEnvironmentVariable(entry.Key) == entry.Value),
                "launch construction leaves the parent environment intact");
        });

        foreach (bool hasWindowsRoot in new[] { true, false })
        {
            Scenario("optional system environment " + hasWindowsRoot, () =>
            {
                using var fixture = new RuntimeFixture();
                var location = IfcRuntimeBundle.Resolve(fixture.PluginDirectory);
                string? windowsRoot = hasWindowsRoot ? Path.Combine(fixture.DirectoryPath, "fallback Windows") : null;
                using var environment = new EnvironmentFixture(new Dictionary<string, string?>
                {
                    ["SystemRoot"] = null, ["WINDIR"] = windowsRoot, ["TEMP"] = null, ["TMP"] = null,
                });
                var start = IfcRuntimeBundle.CreateStartInfo(location, []);
                var expected = new List<string> { fixture.RuntimeDirectory, fixture.RuntimeFile("DLLs") };
                if (windowsRoot is not null) expected.Add(Path.Combine(windowsRoot, "System32"));
                Check(start.Environment["PATH"] == string.Join(Path.PathSeparator, expected) &&
                    start.Environment.Count == (hasWindowsRoot ? 2 : 1),
                    hasWindowsRoot ? "WINDIR supplies System32 when SystemRoot is absent" :
                        "absent system and temp variables add no host-specific paths or inherited variables");
                Check(start.ArgumentList.SequenceEqual(new[] { "-I", "-S", "-B", location.WorkerPath }),
                    "an empty worker argument sequence preserves isolation and disabled bytecode writes");
            });
        }

        Console.WriteLine($"{passed} IFC runtime checks passed; {skipped} symlink scenarios skipped; {failures.Count} scenarios failed");
        if (failures.Count > 0) throw new AggregateException("IFC runtime checks failed", failures);
        return passed;
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public static readonly string[] CriticalPaths =
        [
            "python.exe", "python312.dll", "python312._pth", "msvcp140.dll", "Lib/encodings/__init__.py",
            "Lib/site-packages/ifcopenshell/__init__.py",
        ];

        public static readonly string[] RuntimePaths =
        [
            .. CriticalPaths, "DLLs/private.dll", "Lib/site-packages/ifcopenshell/_ifcopenshell_wrapper.pyd",
            "Lib/site-packages/dependency/file with spaces.py",
        ];

        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "rhino ifc tests " + Guid.NewGuid().ToString("N"));
        public string PluginDirectory => Path.Combine(DirectoryPath, "插件 bundle");
        public string RuntimeDirectory => Path.Combine(PluginDirectory, "openbim", "runtime");
        public string WorkerPath => Path.Combine(PluginDirectory, "openbim", "miki_ifc.py");
        public string SnapshotPath => Path.Combine(PluginDirectory, "openbim", "snapshot.json");
        public string ManifestPath => RuntimeFile("manifest.json");
        public JsonArray Files { get; } = new();
        public JsonObject Manifest { get; }

        public RuntimeFixture()
        {
            foreach (string relative in RuntimePaths)
            {
                string path = RuntimeFile(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relative == "python312._pth" ? ".\nDLLs\nLib\nLib/site-packages\n" :
                    "fake bundle content " + relative);
                Files.Add(new JsonObject { ["path"] = relative });
                RefreshEntry(relative);
            }
            File.WriteAllText(WorkerPath, "print('fake IFC worker, never executed')\n");
            File.WriteAllText(SnapshotPath, new JsonObject
            {
                ["files"] = new JsonArray(
                    new JsonObject { ["path"] = "src/source.cs", ["sha256"] = new string('0', 64) },
                    new JsonObject { ["path"] = "python/miki_ifc.py", ["sha256"] = Hash(WorkerPath) }),
            }.ToJsonString());
            Manifest = new JsonObject
            {
                ["schemaVersion"] = 1, ["platform"] = "win-x64", ["pythonVersion"] = "3.12.13",
                ["ifcopenshellVersion"] = "0.9.0.post1", ["files"] = Files,
            };
            SaveManifest();
        }

        public string RuntimeFile(string relative) => Path.Combine(RuntimeDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

        public void RefreshEntry(string relative)
        {
            var entry = Files.Single(entry => entry!["path"]!.GetValue<string>() == relative)!;
            entry["size"] = new FileInfo(RuntimeFile(relative)).Length;
            entry["sha256"] = Hash(RuntimeFile(relative));
        }

        public void SaveManifest() => File.WriteAllText(ManifestPath, Manifest.ToJsonString());

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public void Dispose()
        {
            string fullPath = Path.GetFullPath(DirectoryPath);
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(fullPath) != temporaryRoot || !Path.GetFileName(fullPath).StartsWith("rhino ifc tests "))
                throw new InvalidOperationException("Unexpected test cleanup directory.");
            Directory.Delete(fullPath, true);
        }
    }

    private sealed class EnvironmentFixture : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        public EnvironmentFixture(Dictionary<string, string?> values)
        {
            original = values.ToDictionary(entry => entry.Key, entry => Environment.GetEnvironmentVariable(entry.Key));
            foreach (var entry in values) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }

        public void Dispose()
        {
            foreach (var entry in original) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
