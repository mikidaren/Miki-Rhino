# Replacing compatible runtime libraries

Python modules, `.pyd` files and DLLs are external, replaceable runtime files. The runtime manifest records their sizes and SHA-256 digests. A compatible replacement requires a regenerated manifest so the resolver can validate the changed bytes.

## Prepare a replacement

1. Work in an owned copy of the plugin's `openbim/runtime` directory. Keep the original installation available until the replacement passes validation.
2. Build or obtain the modified component with the required Windows x64 architecture, CPython 3.12 ABI, public interfaces and compatible dependent DLLs. Retain its corresponding sources, licence notices, modifications, patches and build/relinking material as applicable.
3. Replace the component's external files in the copied runtime. Update the wheel metadata and notices as appropriate. The current resolver expects the supported CPython 3.12.13 and IfcOpenShell 0.9.0.post1 bundle identities. A change to those supported identities also requires a corresponding resolver/builder update.
4. In the source checkout, prepare an updated provenance catalogue with the replacement's actual versions, source URLs, sizes and SHA-256 digests, native-file fingerprints and notices. Run `collect-ifc-licenses.py` with `--provenance` and `--source-cache`, taking the copied runtime and its copied site-packages as inputs. Choose a fresh output outside those inputs. Include that output as the replacement runtime's `licenses` directory.
5. Regenerate the runtime manifest after all library and notice changes. Preserve its supported `schemaVersion`, `platform`, `pythonVersion` and `ifcopenshellVersion`. Recompute every runtime file's relative path, size and SHA-256. Exclude the manifest itself.
6. Run the parent runtime verification and offline IFC export/reopen smoke tests with the modified bundle. Repackage the verified runtime into an owned installer/archive and retain the replacement provenance alongside it.

## Regenerate the manifest

The following PowerShell example uses the copied runtime interpreter and its standard library. Set `runtimeDirectory` to the owned replacement runtime. The manifest is written to a temporary file beside that directory and published by replacement after the complete inventory has been built.

```powershell
$runtimeDirectory = 'C:\owned-copy\openbim\runtime'
@'
import hashlib
import json
from pathlib import Path
import sys
import tempfile

runtime = Path(sys.argv[1]).resolve(strict=True)
manifest_path = runtime / "manifest.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
files = []
for path in sorted(runtime.rglob("*")):
    if not path.is_file() or path == manifest_path:
        continue
    if not path.resolve().is_relative_to(runtime):
        raise ValueError("Runtime file resolves outside the bundle")
    with path.open("rb") as stream:
        sha256 = hashlib.file_digest(stream, "sha256").hexdigest()
    files.append({"path": path.relative_to(runtime).as_posix(),
                  "size": path.stat().st_size, "sha256": sha256})
manifest["files"] = files
with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", suffix=".json",
                                 dir=runtime.parent, delete=False) as stream:
    temporary = Path(stream.name)
    json.dump(manifest, stream, indent=2)
    stream.write("\n")
try:
    temporary.replace(manifest_path)
finally:
    temporary.unlink(missing_ok=True)
'@ | & "$runtimeDirectory\python.exe" -I -S - $runtimeDirectory
```

Keep the interpreter and third-party libraries externally replaceable in the resulting package. The resolver validates the regenerated inventory. This procedure supplies a technical replacement path. The source-delivery and licence-review gate travels with the bundle until its findings have been resolved and the owner has authorized release.
