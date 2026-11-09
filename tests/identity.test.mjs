import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";

const read = path => readFileSync(new URL("../" + path, import.meta.url), "utf8");

test("Miki owns distinct plugin, panel and command identities", () => {
  const plugin = read("src/RhinoMagpie.Plugin/MagpiePlugin.cs");
  const panel = read("src/RhinoMagpie.Plugin/MagpiePanel.cs");
  assert.match(plugin, /assembly: Guid\("20e04364-aa79-4c4c-9d51-830af0ec16c7"\)/);
  assert.match(panel, /Guid\("e73857e0-e6fe-4390-a509-99a7997f45bb"\)/);
  assert.match(plugin, /EnglishName => "MikiRhino"/);
  assert.match(plugin, /EnglishName => "MikiRhinoStandalone"/);
  assert.ok(!plugin.includes("ad8574d5-1868-455f-915f-c93780651477"));
  assert.ok(!panel.includes("b2a952a2-34e6-4c81-9846-c152dad14abb"));
});

test("Miki assemblies and native resource names are isolated from the installed product", () => {
  for (const [path, assembly] of [
    ["src/RhinoMagpie.Plugin/RhinoMagpie.Plugin.csproj", "MikiRhino.Plugin"],
    ["src/RhinoMagpie.Agent/RhinoMagpie.Agent.csproj", "MikiRhino.Agent"],
    ["vendor/openbim/src/Miki.OpenBim/Miki.OpenBim.csproj", "MikiRhino.OpenBim"],
    ["vendor/openbim/src/Miki.OpenBim.Rhino/Miki.OpenBim.Rhino.csproj", "MikiRhino.OpenBim.Rhino"]
  ]) assert.ok(read(path).includes(`<AssemblyName>${assembly}</AssemblyName>`), assembly);
  assert.ok(read("src/RhinoMagpie.Plugin/PanelWebView.cs").includes('"MikiRhino.Plugin.Web.panel.html"'));
});

test("the visible product and the native entry use Miki identities", () => {
  const html = read("src/RhinoMagpie.Plugin/Web/panel.html");
  const native = read("src/RhinoMagpie.Plugin/NativeAiIntegration.cs");
  assert.ok(html.includes("<h1>Miki-Rhino</h1>"));
  assert.ok(native.includes("__rhinoMikiNativeEntry"));
  assert.ok(native.includes("data-rhino-miki-entry"));
  assert.ok(native.includes("rhino-miki://native-entry/"));
  assert.ok(!native.includes("__rhinoMagpieNativeEntry"));
});

test("Sponsors metadata identifies the owner and documents the unconfirmed collection status", () => {
  assert.equal(read(".github/FUNDING.yml").trim(), "github: mikidaren");
  const documentation = read("README.md");
  assert.ok(documentation.includes("重定向"));
  assert.ok(documentation.includes("尚未确认"));
});

test("public restores use nuget.org while cache-only operation is explicitly optional", () => {
  assert.ok(read("NuGet.Config").includes("https://api.nuget.org/v3/index.json"));
  const builder = read("scripts/build-local.ps1");
  assert.ok(builder.includes("[switch]$Offline"));
  const documentation = read("README.md");
  assert.ok(documentation.includes("自动发现"));
  assert.ok(documentation.includes("完整自动组列表"));
});

test("public CI restores pinned dependencies and runs source checks without distributing IFC binaries", () => {
  assert.ok(existsSync(new URL("../.github/workflows/source-ci.yml", import.meta.url)), "public source CI must exist");
  const workflow = read(".github/workflows/source-ci.yml");
  assert.ok(workflow.includes("windows-latest"));
  assert.ok(workflow.includes("actions/setup-dotnet@v4"));
  assert.ok(workflow.includes("actions/setup-node@v4"));
  assert.ok(workflow.includes("--locked-mode"));
  for (const suite of ["Connection.Tests", "Agent.Tests", "Workflow.Tests"]) assert.ok(workflow.includes(suite));
  assert.ok(workflow.includes("tests/browser/*.test.mjs"));
  assert.ok(workflow.includes("scripts/build-local.ps1"));
  assert.ok(!workflow.includes("upload-artifact"));
  assert.ok(!workflow.includes("-IfcRuntimePath"));
});
