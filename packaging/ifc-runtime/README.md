# Offline IFC runtime licence and source evidence

The collector gathers the private CPython base's actual `LICENSE.txt`, wheel licence files, source-derived missing notices, package metadata and native-file hashes. It records unresolved source relationships in a blocked public-release gate. Any source-distribution commitment requires separate owner authorization.

## Build integration

Run from the repository root with a build interpreter supporting Python 3.11 or newer. The collector uses the standard library and reads the supplied runtime files without importing their packages or executing their interpreter.

```powershell
python scripts/collect-ifc-licenses.py --python-base BASE --site-packages SITE --output OUT
```

Use the staged copied interpreter directory for `BASE` and its staged `Lib/site-packages` for `SITE`. Choose a fresh `OUT` outside both input directories. Copy the complete resulting directory to `openbim/runtime/licenses` before the parent builder computes the runtime manifest. This makes the native-file inventory describe the actual staged payload, including the builder's pruning decisions.

For the current workspace, the default source cache is `work/ifc-source-cache` under the workspace that contains `outputs/rhino9-magpie`. A different build checkout can supply an explicit cache.

```powershell
python scripts/collect-ifc-licenses.py --python-base BASE --site-packages SITE --output OUT --source-cache CACHE
```

The checked-in `source-artifacts.json` records each cache filename, exact acquisition URL, byte size, SHA-256, acquisition status and artifact kind. Populate a fresh build cache from those URLs and verify size and SHA-256 before collection. All 25 recorded artifacts were acquired and digest-verified during this audit. Their combined size is 442491391 bytes. They comprise 23 source, build-recipe, submodule or data archives and two upstream licence-text files. The archives remain in the build cache, separate from the runtime notices.

Collection performs no network access. Missing archives, mismatched archive/member digests, changed interpreter or wheel fingerprints, unmatched package versions, missing declared licence files, unsafe evidence paths and nonempty outputs fail with exit code 1. Validation completes before output publication. Output is written through a sibling staging directory and published by rename. Failed writes leave the requested output unpublished. Exit code 0 means evidence collection succeeded. `publicReleaseGate.status` remains `blocked`.

The optional `--provenance` argument accepts an explicitly reviewed replacement catalogue. Its source-artifact file resolves relative to that catalogue. Source-cache contents must match the catalogue's actual digests.

## Output

- `components.json` contains 10 primary components, 24 native or submodule records, copied-text hashes, package metadata, actual native-file hashes, source acquisition records, evidence limitations and the release blockers.
- `THIRD-PARTY-NOTICES.txt` indexes component versions, declared or audited licence expressions, exact source URLs and the copied texts. `licenseReferences` in the JSON connect native components to their owning wheel's bundled notices.
- `texts/` preserves the actual licence and copyright bytes, including CRLF, BOM, nested licence directories and declared author files.
- `metadata/` preserves each installed distribution's actual `METADATA`.
- `PUBLIC-RELEASE-GATE.md` carries the unresolved redistribution and corresponding-source findings.
- `LIBRARY-REPLACEMENT.md` provides the external-library replacement and manifest regeneration procedure.

The actual unpruned base and tested site-packages produced 64 licence texts and 159 native-file hashes. A pruned runtime has its own native-file count. The collector has no legal-compliance pass mode.

## Primary components

| Component | Version | Recorded terms and actual evidence |
| --- | --- | --- |
| CPython | 3.12.13 | Actual base `LICENSE.txt`, historical Python terms, third-party notices, source `Doc/license.rst` and Microsoft CRT notice |
| IfcOpenShell | 0.9.0.post1 | Metadata LGPL-3.0-or-later, source `COPYING` and `COPYING.LESSER` |
| isodate | 0.7.2 | BSD-3-Clause, legacy dist-info `LICENSE` |
| lark | 1.3.1 | MIT, dist-info `licenses/LICENSE` |
| numpy | 2.5.4 | All 17 declared licence files, including OpenBLAS/LAPACK/GCC binary notices and runtime exception |
| pyparsing | 3.3.3 | MIT, dist-info `licenses/LICENSE` |
| python-dateutil | 4.0.0 | Actual Apache/BSD text and authors file, with the BSD-only metadata declaration separately preserved |
| shapely | 2.2.0 | BSD text, GEOS LGPL text and Windows Microsoft runtime notice |
| typing-extensions | 4.16.0 | PSF declaration and complete historical Python licence text |
| tzdata | 2026.6 | Apache wrapper notices and separate IANA 2026f public-domain notice |

## Exact IFC source

The installed native wrapper reports `0.9.0post1+b5e509b`. The commit resolves to `b5e509b223e1ab2c7eb4641211ea05209a2dfa3e`. The acquired commit archive is 136968032 bytes with SHA-256 `9cd664dfe790be1aec2ef59c95004692a0774e2217e0ee3e96a173ae2f0d08c1`. Both missing IFC licence texts are copied from this digest-verified archive. [Exact source archive](https://codeload.github.com/IfcOpenShell/IfcOpenShell/tar.gz/b5e509b223e1ab2c7eb4641211ea05209a2dfa3e)

The exact Git tree supplies these additional shipped or compiled submodule sources. Their archives were also acquired and are listed with full URLs and digests in `source-artifacts.json`.

- `src/ifcopenshell-python/ifcopenshell/mvd` resolves to `83c12fa494b9d6a5a370d8525dc1f76aa5c89d8d`. Its LGPL 3 text is collected.
- `src/ifcopenshell-python/ifcopenshell/simple_spf` resolves to `9400d243d880dace57490949d74ab1932ce99a09`. Its LGPL 2.1 text is collected.
- `src/svgfill/3rdparty/svgpp` resolves to `5f1870aa7b757718ff5f86bdfb55966fa4f217f9`. Its Boost licence text is collected.

The PyPI Windows Python 3.12 wheel is identified as `ifcopenshell-0.9.0.post1-py312-none-win_amd64.whl`, 42827327 bytes, SHA-256 `290acc68c564b5393785deb67818191481181eda8c409c141fd37081fb3361f0`. Its exact URL is recorded in `provenance.json`. The installed wheel's internal `WHEEL` advertises `py3-none-any`. A complete comparison against the published wheel archive remains an open provenance task. The collector pins the audited installed metadata, wrapper and principal dependency binaries individually.

## Native dependency findings

PE import-table inspection of the installed DLLs and `.pyd` files confirmed direct OCCT, Manifold, GEOS and OpenBLAS imports. The inventory also contains both CGAL kernel DLLs. Import tables identify dynamic dependencies. The source recipes and static linkage still require reconciliation with the published binary build.

The build-side import audit is retained as `work/ifc-source-cache/native-binary-audit.json`. It records installed wheel-native paths, sizes, SHA-256 digests and imported DLL names. The collector's output inventories the supplied files without loading them. Candidate recipe versions and unestablished static dependencies are explicitly labelled in `provenance.json`.

| Component | Actual observation | Source and notice acquisition |
| --- | --- | --- |
| OpenCASCADE | `TKernel.dll` file/product version 7.8.1, matching IFC recipe | OCCT V7_8_1 source, LGPL 2.1 text and OCCT exception acquired. Preserve IFC `win/patches/V7_8_1.patch` and recipe changes too. |
| Boost | `boost_container-vc143-mt-x64-1_92.dll`, recipe 1.92.0 | Actual Boost licence acquired. Complete Boost source remains uncached. |
| CGAL | `ifcopenshell_geometry_kernel_cgal.dll` and `cgalsimple.dll`, recipe candidate 5.5.5 | Complete v5.5.5 source and GPL/LGPL/BSL/commercial-option texts acquired. Nef header explicitly uses GPL/commercial terms. |
| MPIR | Recipe pins `5e0c2061af105c151970d41c8394ce956f77e455` | Source, GPL and LGPL texts acquired. Exact static linkage remains unresolved. |
| MPFR | Recipe selects mutable `Andrej730/mpfr` branch `ifcopenshell` and applies a patch | Observed branch head `9f637c103431faa235f1aad7e35b639d3aa635ee` archived with GPL/LGPL texts. Wheel-time revision remains unresolved. |
| Manifold | `manifold.dll` is imported by the manifold kernel, recipe candidate 3.2.1 | v3.2.1 source and Apache text acquired. Exact build-cache linkage remains unresolved. |
| OpenCOLLADA | DAE serializer is present, recipe pins `064a60b65c2c31b94f013820856bc84fb1937cc6` | Source and StreamWriter/LibXML/Expat notices acquired. Linked optional module selection remains unresolved. |
| nlohmann/json | Recipe downloads 3.6.1 single header | Actual MIT text acquired. Complete header source remains uncached. |
| Eigen | Recipe selects 3.3.9 | GitLab retrieval did not complete. Exact source and notices remain a release blocker. |
| GEOS | `shapely.geos_version_string` reports 3.14.1, two bundled DLLs | Exact 3.14.1 source and LGPL text acquired, wheel build relationship pending. |
| OpenBLAS/LAPACK/GCC | NumPy configuration reports scipy-openblas 0.3.34.106.0 | Preserve NumPy's complete binary notice, GPL and GCC exception. Exact source/compiler revisions and exception applicability remain pending. |
| Extra CPython | `ifcopenshell/python312.dll` reports 3.12.1 | Exact 3.12.1 source and historical Python licence acquired. Runtime builder owns its loading/replacement decision. |
| Private Python dependencies | OpenSSL 3.5.6, SQLite 3.50.4, zlib 1.3.1 and Expat 2.7.4 observed at runtime | Actual CPython/source/standalone-build notices collected. Libffi version, Windows Expat source relationship and static dependencies need reconciliation. |
| Microsoft VC runtime | Base `vcruntime140*.dll` and renamed NumPy/Shapely `msvcp140*.dll` | Actual CPython Microsoft CRT notice and Shapely Windows notice retained. Applicable redistribution entitlement and terms require owner review. |

The standalone build tag `20260510` resolves to `f0ae464f527498d6c4b13f19977447d603cd5edd`. Its source recipe is acquired. The generic dependency dictionary and Windows-specific build path differ in places, including libffi and Expat. Those differences are carried into the release gate.

## Public binary release gate

The installed wheel and source snapshot provide concrete provenance evidence. Upstream Windows CI obtains native dependency caches from `IfcOpenShell/build-outputs` using a secret token. The exact cache revision, corresponding build inputs and static-linking material for the audited wheel remain unestablished. The complete blocker list is maintained in `provenance.json` and copied into each output.

Before public binary distribution, the owner needs a reviewed source-delivery method, corresponding sources/build scripts/patches for the relevant binaries, any required relinking objects or installation information, applicable modification/reverse-engineering permissions, and the completed notice inventory. CGAL's GPL/commercial terms, MPFR's mutable source branch, Eigen acquisition, GCC exception applicability and Microsoft runtime terms require specific resolution. The acquired archives support that work. Metadata, source URLs and passing collector tests establish evidence-collection behavior.

## Verification

```powershell
python -B -m unittest discover -s tests/packaging -p test_license_collection.py -v
```

The tests run the CLI on independent temporary fixtures and cover byte preservation, nested notices/authors, missing licences, unmatched versions, source-record validation, interpreter fingerprints, path traversal, supplemental-text digests, source-archive/member digests, deterministic outputs, output preservation and failed-write publication. The real collection was also repeated and compared byte-for-byte. Every copied licence digest and each of the eight dependency sdist URL/digest/size tuples were independently checked against the downloaded artifacts and PyPI metadata.
