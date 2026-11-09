import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PANEL_SCROLL_PLAYWRIGHT || "playwright");
const source = readFileSync(new URL("../../src/RhinoMagpie.Plugin/NativeAiIntegration.cs", import.meta.url), "utf8");
const mikiScript = source.match(/private const string EntryScript = """\s*\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
const magpieScript = readFileSync(new URL("../fixtures/magpie-native-entry.js", import.meta.url), "utf8");
const scripts = { miki: mikiScript, magpie: magpieScript };
const urls = { miki: "rhino-miki://native-entry/fixture", magpie: "rhino-magpie://native-entry/fixture" };

async function inject(page, name, url = urls[name]) {
  await page.evaluate(script => { window.eval(script); }, scripts[name]
    .replaceAll("__ENTRY_URL__", JSON.stringify(url)).replaceAll("__ENABLED__", "true"));
}

async function fixture(run) {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.setContent('<header class="header"><button class="agent-chip" aria-expanded="true">Native agent</button></header><div class="popover" role="menu"><div class="menu-sep"></div></div><button class="send">Send</button>');
    await page.evaluate(() => { window.openedEntries = []; window.open = url => window.openedEntries.push(url); });
    await run(page);
  } finally { await browser.close(); }
}

for (const order of [["magpie", "miki"], ["miki", "magpie"]]) {
  test(`native entries coexist and Miki cleanup preserves Magpie in ${order.join(" then ")} order`, async () => fixture(async page => {
    for (const name of order) await inject(page, name);
    assert.equal(await page.locator('[data-rhino-magpie-entry="menu"]').count(), 1);
    assert.equal(await page.locator('[data-rhino-miki-entry="menu"]').count(), 1);
    await page.getByRole("button", { name: "Miki-Rhino", exact: true }).click();
    await page.getByRole("button", { name: "Magpie", exact: true }).click();
    assert.deepEqual(await page.evaluate(() => window.openedEntries), [urls.miki, urls.magpie]);
    await page.evaluate(() => window.__rhinoMikiNativeEntry.dispose());
    assert.equal(await page.locator("[data-rhino-miki-entry]").count(), 0);
    assert.equal(await page.locator('[data-rhino-magpie-entry="menu"]').count(), 1);
    await page.getByRole("button", { name: "Magpie", exact: true }).click();
    assert.equal((await page.evaluate(() => window.openedEntries)).at(-1), urls.magpie);
  }));
}

test("native busy detection blocks both entries and independent availability affects only Miki", async () => fixture(async page => {
  await inject(page, "magpie");
  await inject(page, "miki");
  await page.evaluate(() => document.querySelector(".send").classList.add("stop"));
  await page.waitForTimeout(30);
  for (const name of ["miki", "magpie"]) {
    const button = page.locator(`[data-rhino-${name}-entry="menu"]`);
    assert.equal(await button.isDisabled(), true);
    await button.evaluate(node => node.dispatchEvent(new MouseEvent("click", { bubbles: true })));
  }
  assert.deepEqual(await page.evaluate(() => window.openedEntries), []);
  await page.evaluate(() => {
    document.querySelector(".send").classList.remove("stop");
    window.__rhinoMikiNativeEntry.refresh(false);
  });
  await page.waitForTimeout(30);
  assert.equal(await page.locator('[data-rhino-miki-entry="menu"]').isDisabled(), true);
  assert.equal(await page.locator('[data-rhino-magpie-entry="menu"]').isEnabled(), true);
}));

test("Miki reinjection replaces only its own menu entry and URL", async () => fixture(async page => {
  await inject(page, "magpie");
  await inject(page, "miki");
  const replacement = "rhino-miki://native-entry/replacement";
  await inject(page, "miki", replacement);
  assert.equal(await page.locator("[data-rhino-miki-entry]").count(), 1);
  assert.equal(await page.locator("[data-rhino-magpie-entry]").count(), 1);
  await page.getByRole("button", { name: "Miki-Rhino", exact: true }).click();
  assert.deepEqual(await page.evaluate(() => window.openedEntries), [replacement]);
}));
