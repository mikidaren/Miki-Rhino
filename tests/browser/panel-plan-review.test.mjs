import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PANEL_SCROLL_PLAYWRIGHT || "playwright");
const panelUrl = new URL("../../src/RhinoMagpie.Plugin/Web/panel.html", import.meta.url).href;

async function fixture(run) {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.PANEL_TEST_CHROMIUM_EXECUTABLE ? { executablePath: process.env.PANEL_TEST_CHROMIUM_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 300, height: 560 } });
    await page.addInitScript(() => {
      window.actions = [];
      window.eto = { postMessage: value => window.actions.push(JSON.parse(value)) };
    });
    await page.goto(panelUrl);
    await run(page);
  } finally {
    await browser.close();
  }
}

function apply(page, state) {
  return page.evaluate(state => window.magpie.applyState(state), state);
}

function review(page, planReview) {
  return apply(page, { documentId: "42", plan: { goal: "两层平面", planReview } });
}

test("plan review distinguishes missing evidence, workflow states and checked coverage", () => fixture(async page => {
  const card = page.getByRole("region", { name: "平面检查", exact: true });
  const status = card.getByRole("status");
  await review(page);
  assert.match(await status.textContent({ timeout: 2000 }), /暂无检查结果/);
  assert.match(await card.innerText(), /楼板范围.*重叠.*楼梯.*面积/s);
  for (const [value, label] of [
    ["needs_brief", /补充任务书/], ["pending", /待检查/],
    ["unavailable", /暂不可用/], ["stale", /已过期/],
  ]) {
    await review(page, { status: value });
    assert.match(await status.textContent(), label);
  }
  await review(page, { status: "passed", issues: [] });
  assert.doesNotMatch(await status.textContent(), /通过/);
  assert.match(await card.innerText(), /面积目标[^\n]*未报告/);
  await review(page, { status: "passed", stairRoutes: { found: 0, total: 0 }, areaTargetsChecked: 0 });
  assert.doesNotMatch(await status.textContent(), /通过/);
  assert.match(await card.innerText(), /面积目标[^\n]*未检查/);
  await review(page, { status: "passed", stairRoutes: { found: 6, total: 6 }, areaTargetsChecked: 1 });
  assert.match(await status.textContent(), /已检查项通过/);
  assert.match(await card.innerText(), /6\s*\/\s*6/);
  assert.match(await card.innerText(), /面积目标[^\n]*已检查 1 项/);
  await review(page, { status: "passed", stairRoutes: { found: 6, total: 6 }, issuesOmitted: 2 });
  assert.doesNotMatch(await status.textContent(), /通过/);
  assert.match(await card.innerText(), /另有 2 项问题未显示/);
}));

test("issue details are reachable by keyboard without losing position during host updates", () => fixture(async page => {
  const card = page.getByRole("region", { name: "平面检查", exact: true });
  const data = {
    status: "failed", projectId: "TWO-STOREY", revision: 0, stairRoutes: { found: 0, total: 4 },
    areaTargetsChecked: 0, issuesOmitted: 3,
    issues: [
      { code: "ROOM_OVERLAP", ids: ["L1-ROOM-1", "L1-ROOM-2"], areaM2: 0.025 },
      { code: "AREA_BELOW_MIN", ids: ["L1-ROOM-1"], actualM2: 4.5, limitM2: 8 },
      ...Array.from({ length: 6 }, (_, i) => ({ code: "NO_STAIR_ROUTE", ids: [`ROOM-${i}`] })),
    ],
  };
  await review(page, data);
  assert.match(await card.getByRole("status").textContent({ timeout: 2000 }), /未通过/);
  assert.match(await card.innerText(), /TWO-STOREY.*版本 0/);
  assert.match(await card.innerText(), /0\s*\/\s*4/);
  assert.match(await card.innerText(), /另有 3 项问题未显示/);

  const scroller = page.getByRole("region", { name: "阶段计划内容" });
  await page.locator("#plan > summary").focus();
  await page.keyboard.press("Tab");
  assert.equal(await scroller.evaluate(node => node === document.activeElement), true);
  await page.keyboard.press("Tab");
  const disclosure = card.locator("summary");
  assert.equal(await disclosure.evaluate(node => node === document.activeElement), true);
  await page.keyboard.press("Enter");
  const issues = await card.getByRole("listitem").allTextContents();
  assert.equal(issues.length, 8);
  assert.match(issues[0], /房间重叠.*L1-ROOM-1.*L1-ROOM-2.*0\.025\s*m²/s);
  assert.match(issues[1], /面积.*下限.*4\.5\s*m².*8\s*m²/s);
  assert.ok(await scroller.evaluate(node => node.scrollHeight > node.clientHeight && node.clientHeight <= 220));

  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("End");
  await page.waitForFunction(() => {
    const node = document.activeElement;
    return node.scrollTop > 0 && node.scrollHeight - node.clientHeight - node.scrollTop <= 2;
  });
  const before = await scroller.evaluate(node => node.scrollTop);
  await review(page, data);
  assert.equal(await scroller.evaluate(node => node === document.activeElement && node.scrollTop), before);
  assert.equal(await card.getByRole("listitem").count(), 8);
}));

test("check action uses the current document and ignores activation when unavailable", () => fixture(async page => {
  const check = page.getByRole("button", { name: "检查平面", exact: true });
  const actions = () => page.evaluate(() => window.actions.filter(item => item.action === "checkPlan"));
  assert.equal(await check.isDisabled({ timeout: 2000 }), true);
  await apply(page, { documentId: "0" });
  assert.equal(await check.isDisabled(), true);
  await apply(page, { documentId: "42", busy: true });
  assert.equal(await check.isDisabled(), true);
  await check.press("Enter");
  assert.deepEqual(await actions(), []);

  await apply(page, { architectural: false, busy: false });
  assert.equal(await check.count(), 0);
  await apply(page, { architectural: true });
  await check.press("Space");
  assert.deepEqual(await actions(), [{ action: "checkPlan", documentId: "42" }]);
  await review(page, { status: "failed", projectId: "PREVIOUS" });
  await apply(page, { documentId: "43" });
  assert.equal(await page.getByRole("region", { name: "平面检查", exact: true }).count(), 0);
  await check.press("Enter");
  assert.deepEqual(await actions(), [
    { action: "checkPlan", documentId: "42" }, { action: "checkPlan", documentId: "43" },
  ]);
}));
