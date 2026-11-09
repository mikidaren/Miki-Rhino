import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { pathToFileURL, fileURLToPath } from "node:url";
import test from "node:test";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PANEL_SCROLL_PLAYWRIGHT || "playwright");
const panelUrl = pathToFileURL(fileURLToPath(new URL("../../src/RhinoMagpie.Plugin/Web/panel.html", import.meta.url))).href;
const lines = Array.from({ length: 90 }, (_, index) => `Detail line ${index} with readable execution output`).join("\n");

async function settle(page) {
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.waitForTimeout(100);
}

async function fixture(run) {
  const browser = await chromium.launch({ headless: true, ignoreDefaultArgs: ["--hide-scrollbars"] });
  try {
    const page = await browser.newPage({ viewport: { width: 480, height: 760 } });
    await page.addInitScript(() => { window.magpiePreviewSend = () => {}; });
    await page.goto(panelUrl);
    await page.evaluate(detail => {
      window.fixtureMessages = [
        ...Array.from({ length: 12 }, (_, index) => ({
          id: `message-${index}`, role: "assistant", text: `Reply ${index}\n` + "Reading history.\n".repeat(5), detail
        })),
        { id: "tool", type: "tool", text: "Streaming tool", state: "running", detail }
      ];
      window.magpie.applyState({
        documentId: "scroll-fixture", messages: window.fixtureMessages,
        models: [{ id: "test/model", label: "Test model" }], selectedModel: "test/model", canSend: true
      });
    }, lines);
    await settle(page);
    await run(page);
  } finally {
    await browser.close();
  }
}

async function metrics(page) {
  return page.locator("#viewport").evaluate(node => ({
    top: node.scrollTop, bottom: node.scrollHeight - node.clientHeight - node.scrollTop
  }));
}

async function update(page) {
  await page.evaluate(() => {
    window.fixtureMessages[11].text += "\nStream update";
    window.fixtureMessages[12].detail += "\nMore tool output";
    window.magpie.applyState({ messages: window.fixtureMessages, status: "Streaming" });
  });
  await settle(page);
}

async function wheel(page, selector, delta) {
  const bounds = await page.locator(selector).boundingBox();
  const viewport = await page.locator("#viewport").boundingBox();
  const top = Math.max(bounds.y, viewport.y);
  const bottom = Math.min(bounds.y + bounds.height, viewport.y + viewport.height);
  assert.ok(bottom > top, `${selector} must be visible before wheel input`);
  await page.mouse.move(bounds.x + 4, (top + bottom) / 2);
  await page.mouse.wheel(0, delta);
  await settle(page);
}

test("opening tool details pauses follow before the next streamed update", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await settle(page);
  const before = await metrics(page);
  assert.ok(before.bottom > 100);
  await update(page);
  const after = await metrics(page);
  assert.ok(Math.abs(after.top - before.top) <= 2, JSON.stringify({ before, after }));
  assert.equal(await page.locator(".tool").getAttribute("open"), "");
}));

test("nested tool wheel and edge overscroll leave the conversation still", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await settle(page);
  await page.locator(".tool-detail").hover();
  await settle(page);
  const before = await metrics(page);
  await wheel(page, ".tool-detail", 180);
  assert.ok(await page.locator(".tool-detail").evaluate(node => node.scrollTop) > 0);
  await page.locator(".tool-detail").evaluate(node => { node.scrollTop = node.scrollHeight; });
  await wheel(page, ".tool-detail", 500);
  await update(page);
  const after = await metrics(page);
  assert.ok(Math.abs(after.top - before.top) <= 2, JSON.stringify({ before, after }));
  assert.ok(await page.locator(".tool-detail").evaluate(node => node.scrollTop) > 100);
  await page.locator(".tool-detail").evaluate(node => { node.scrollTop = 0; });
  await wheel(page, ".tool-detail", -500);
  await update(page);
  const upperBoundary = await metrics(page);
  assert.ok(Math.abs(upperBoundary.top - before.top) <= 2, JSON.stringify({ before, upperBoundary }));
  assert.equal(await page.locator(".tool-detail").evaluate(node => node.scrollTop), 0);
}));

test("nested wheel at the conversation bottom does not resume follow", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await page.locator("#viewport").evaluate(node => { node.scrollTop = node.scrollHeight; });
  await settle(page);
  await wheel(page, ".tool-detail", 200);
  await update(page);
  assert.ok((await metrics(page)).bottom > 20);
}));

test("layout or programmatic bottom does not resume paused follow", async () => fixture(async page => {
  await wheel(page, "#viewport", -250);
  await page.locator("#viewport").evaluate(node => { node.scrollTop = node.scrollHeight; });
  await settle(page);
  await page.evaluate(() => {
    window.fixtureMessages.push({ id: "new", role: "assistant", text: "New streamed paragraph.\n".repeat(20) });
    window.magpie.applyState({ messages: window.fixtureMessages });
  });
  await settle(page);
  assert.ok((await metrics(page)).bottom > 200);
}));

test("upward wheel near the bottom immediately cancels queued follow", async () => fixture(async page => {
  await page.locator("#viewport").hover();
  await page.evaluate(() => window.magpie.applyState({ status: "Queued" }));
  await page.mouse.wheel(0, -20);
  await settle(page);
  const before = await metrics(page);
  await update(page);
  const after = await metrics(page);
  assert.ok(after.bottom >= 15, JSON.stringify({ before, after }));
}));

test("explicit jump and deliberate outer wheel to bottom resume follow", async () => fixture(async page => {
  await wheel(page, "#viewport", -350);
  await page.locator("#jump").click();
  await settle(page);
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
  await wheel(page, "#viewport", -350);
  await wheel(page, "#viewport", 10000);
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
}));

test("stream growth above the reading position preserves its visible anchor", async () => fixture(async page => {
  await wheel(page, "#viewport", -900);
  const before = await page.locator("#messages > article").evaluateAll(nodes => {
    const viewport = document.getElementById("viewport").getBoundingClientRect();
    const index = nodes.findIndex(node => node.getBoundingClientRect().bottom > viewport.top + 20);
    return { index, top: nodes[index].getBoundingClientRect().top };
  });
  await page.evaluate(() => {
    window.fixtureMessages[0].text += "\nEarlier stream growth.\n".repeat(10);
    window.magpie.applyState({ messages: window.fixtureMessages });
  });
  await settle(page);
  const after = await page.locator("#messages > article").nth(before.index).evaluate(node => node.getBoundingClientRect().top);
  assert.ok(Math.abs(after - before.top) <= 2, JSON.stringify({ before, after }));
}));

test("message details scroll independently and preserve query and local draft", async () => fixture(async page => {
  await wheel(page, "#viewport", -500);
  await page.locator("#messages > article").nth(10).locator("summary").click();
  await settle(page);
  const detail = page.locator("#messages > article").nth(10).locator("pre");
  assert.ok(await detail.evaluate(node => node.scrollHeight > node.clientHeight));
  await detail.hover();
  await settle(page);
  const outerBefore = await metrics(page);
  await detail.evaluate(node => { node.scrollTop = node.scrollHeight; });
  await page.mouse.wheel(0, 500);
  await settle(page);
  assert.ok(Math.abs((await metrics(page)).top - outerBefore.top) <= 2);
  await detail.evaluate(node => { node.scrollTop = 0; });
  await page.mouse.wheel(0, -500);
  await settle(page);
  assert.ok(Math.abs((await metrics(page)).top - outerBefore.top) <= 2);
  await page.mouse.wheel(0, 160);
  await settle(page);
  const nestedTop = await detail.evaluate(node => node.scrollTop);
  assert.ok(nestedTop > 0);
  await page.locator("#draft").fill("local unsent draft");
  await page.locator("#model-button").click();
  await page.locator("#model-search").fill("model");
  await update(page);
  assert.equal(await page.locator("#draft").inputValue(), "local unsent draft");
  assert.equal(await page.locator("#model-search").inputValue(), "model");
  assert.equal(await detail.locator("..").getAttribute("open"), "");
  assert.equal(await detail.evaluate(node => node.scrollTop), nestedTop);
}));

test("initial following survives ordinary stream updates and viewport resize", async () => fixture(async page => {
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
  await page.setViewportSize({ width: 390, height: 580 });
  await settle(page);
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
}));

test("resize clamping to bottom does not rearm paused follow", async () => fixture(async page => {
  await wheel(page, "#viewport", -200);
  await page.setViewportSize({ width: 480, height: 1500 });
  await settle(page);
  assert.ok((await metrics(page)).bottom <= 2);
  await page.evaluate(() => {
    window.fixtureMessages.push({ id: "resize-new", role: "assistant", text: "Additional content.\n".repeat(30) });
    window.magpie.applyState({ messages: window.fixtureMessages });
  });
  await settle(page);
  assert.ok((await metrics(page)).bottom > 200);
}));

test("keyboard scrolling pauses and deliberate End resumes following", async () => fixture(async page => {
  await page.locator("#viewport").focus();
  await page.keyboard.press("ArrowUp");
  await settle(page);
  await update(page);
  assert.ok((await metrics(page)).bottom > 20);
  await page.keyboard.press("End");
  await settle(page);
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
}));

test("keyboard disclosure expansion pauses following", async () => fixture(async page => {
  await page.locator(".tool summary").focus();
  await page.keyboard.press("Enter");
  await settle(page);
  const before = await metrics(page);
  await update(page);
  assert.equal(await page.locator(".tool").getAttribute("open"), "");
  assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
}));

test("wheel reading remains stable during repeated live updates", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await page.locator(".tool-detail").hover();
  await settle(page);
  const before = await metrics(page);
  await page.evaluate(() => {
    window.streamTimer = setInterval(() => {
      window.fixtureMessages[12].detail += "\nLive output";
      window.magpie.applyState({ messages: window.fixtureMessages });
    }, 20);
  });
  try {
    for (const delta of [100, 100, -80, 120]) {
      await page.mouse.wheel(0, delta);
      await settle(page);
      assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
    }
    assert.ok(await page.locator(".tool-detail").evaluate(node => node.scrollTop) > 0);
  } finally {
    await page.evaluate(() => clearInterval(window.streamTimer));
  }
}));

test("collapsing details keeps follow paused across subsequent expansion and updates", async () => fixture(async page => {
  const summary = page.locator(".tool summary");
  await summary.click();
  await settle(page);
  await summary.click();
  await settle(page);
  assert.ok((await metrics(page)).bottom <= 2);
  await summary.click();
  await settle(page);
  const before = await metrics(page);
  await update(page);
  assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
}));

test("late viewport preview load while reading does not resume follow", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await settle(page);
  const before = await metrics(page);
  await page.evaluate(() => {
    window.fixtureMessages[12].image = "data:image/svg+xml," + encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="400"><rect width="640" height="400" fill="blue"/></svg>'
    );
    window.magpie.applyState({ messages: window.fixtureMessages });
  });
  await page.locator(".tool-preview img").evaluate(image => image.decode());
  await settle(page);
  await update(page);
  assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
}));

test("dragging the outer scrollbar pauses and dragging back to bottom resumes", async () => fixture(async page => {
  const geometry = await page.locator("#viewport").evaluate(node => {
    const bounds = node.getBoundingClientRect();
    const thumbHeight = node.clientHeight * node.clientHeight / node.scrollHeight;
    return {
      x: bounds.right - 2, top: bounds.top, height: node.clientHeight,
      bottomThumbY: bounds.top + node.clientHeight - thumbHeight / 2,
      scrollbar: bounds.width - node.clientWidth
    };
  });
  await page.mouse.move(geometry.x, geometry.bottomThumbY);
  await page.mouse.down();
  await page.mouse.move(geometry.x, geometry.top + geometry.height / 2, { steps: 8 });
  await page.mouse.up();
  await settle(page);
  const before = await metrics(page);
  assert.ok(before.bottom > 100, JSON.stringify({ geometry, before }));
  await update(page);
  assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
  const thumbY = await page.locator("#viewport").evaluate(node => {
    const bounds = node.getBoundingClientRect();
    return bounds.top + node.scrollTop / node.scrollHeight * node.clientHeight +
      node.clientHeight * node.clientHeight / node.scrollHeight / 2;
  });
  await page.mouse.move(geometry.x, thumbY);
  await page.mouse.down();
  await page.mouse.move(geometry.x, geometry.top + geometry.height - 1, { steps: 8 });
  await page.mouse.up();
  await settle(page);
  await update(page);
  assert.ok((await metrics(page)).bottom <= 2);
}));

test("selected tool text survives appended streaming output", async () => fixture(async page => {
  await page.locator(".tool summary").click();
  await page.locator(".tool-detail").hover();
  await settle(page);
  await page.locator(".tool-detail").evaluate(node => {
    const range = document.createRange();
    range.setStart(node.firstChild, 0);
    range.setEnd(node.firstChild, 22);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  });
  const selected = await page.evaluate(() => window.getSelection().toString());
  assert.ok(selected.length > 0);
  await update(page);
  assert.equal(await page.evaluate(() => window.getSelection().toString()), selected);
}));

test("mouse text selection pauses follow and survives streamed text appends", async () => fixture(async page => {
  const message = page.locator("#messages > article").nth(11).locator(".message-text");
  await message.hover();
  const bounds = await message.boundingBox();
  await page.mouse.move(bounds.x + 3, bounds.y + 10);
  await page.mouse.down();
  await page.mouse.move(bounds.x + 80, bounds.y + 10, { steps: 6 });
  await page.mouse.up();
  await settle(page);
  const selected = await page.evaluate(() => window.getSelection().toString());
  assert.ok(selected.length > 0);
  const before = await metrics(page);
  await update(page);
  assert.equal(await page.evaluate(() => window.getSelection().toString()), selected);
  assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
}));

test("changing detail never sends a cached execution permission", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
    window.magpie.applyState({ autoTools: true, executionModeRevision: 17 });
  });
  await page.locator("#settings-button").click();
  await page.locator("#detail").selectOption("detailed");
  const options = await page.evaluate(() => window.sentActions.filter(action => action.action === "options"));
  assert.equal(options.length, 1);
  assert.equal(options[0].detail, "detailed");
  assert.equal(Object.hasOwn(options[0], "autoTools"), false);
  assert.equal(await page.evaluate(() => window.sentActions.some(action => action.action === "executionMode")), false);
}));

test("execution mode changes carry the current permission revision", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
    window.magpie.applyState({ autoTools: true, executionModeRevision: 17 });
  });
  await page.locator("#settings-button").click();
  await page.locator("#auto-tools").uncheck();
  const changes = await page.evaluate(() => window.sentActions.filter(action => action.action === "executionMode"));
  assert.equal(changes.length, 1);
  assert.equal(changes[0].autoTools, false);
  assert.equal(changes[0].revision, 17);
  await page.evaluate(() => window.magpie.applyState({ autoTools: false, executionModeRevision: 18 }));
  assert.equal(await page.locator("#execution-mode").textContent(), "逐次确认");
}));

test("Miki exposes native connection settings and the selected endpoint without a model catalog", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
    window.magpie.applyState({ canSend: false, endpoint: "https://owner.example/custom/v1", configuredModel: "owner/model", autoTools: true });
  });
  assert.equal(await page.locator("h1").textContent(), "Miki-Rhino");
  await page.locator("#settings-button").click();
  const button = page.getByRole("button", { name: "模型连接", exact: true });
  assert.equal(await button.count(), 1);
  assert.equal(await button.isEnabled(), true);
  assert.equal(await page.locator("#connection-endpoint").textContent(), "https://owner.example/custom/v1");
  await button.click();
  assert.deepEqual(await page.evaluate(() => window.sentActions), [{ action: "configureConnection", documentId: "scroll-fixture" }]);
  assert.equal(await page.locator("#execution-mode").textContent(), "YOLO");
}));

test("Miki describes provider-returned IDs without promising Magpie routing discovery", async () => fixture(async page => {
  await page.evaluate(() => window.magpie.applyState({
    models: [{ id: "group/manual-id", label: "Provider ID" }], selectedModel: "group/manual-id", catalogError: ""
  }));
  await page.locator("#model-button").click();
  assert.equal(await page.locator("#catalog-status").textContent(), "1 个模型 ID");
}));

test("offline IFC export dispatches without a model or send permission after catalog failure", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
    window.magpie.applyState({
      canSend: false, busy: false, canStop: false, models: [], selectedModel: "",
      catalogLoading: false, catalogError: "Magpie is offline", autoTools: true, executionModeRevision: 17,
      architectural: false, detail: "detailed"
    });
  });
  await page.locator("#settings-button").click();
  const button = page.getByRole("button", { name: "导出 IFC", exact: true });
  assert.equal(await button.count(), 1, "settings must expose the offline export action");
  assert.equal(await button.isEnabled(), true);
  await button.click();
  assert.deepEqual(await page.evaluate(() => window.sentActions), [
    { action: "exportIfc", documentId: "scroll-fixture" }
  ]);
  assert.equal(await page.locator("#execution-mode").textContent(), "YOLO");
  await page.locator("#settings-button").click();
  assert.equal(await page.locator("#auto-tools").isChecked(), true);
  assert.equal(await page.locator("#architectural").isChecked(), false);
  assert.equal(await page.locator("#detail").inputValue(), "detailed");
}));

for (const [name, activity] of [
  ["an AI turn", { busy: true, canStop: true, catalogLoading: false }],
  ["a model refresh", { busy: true, canStop: true, catalogLoading: true }],
  ["pending cancellation", { busy: true, canStop: false, catalogLoading: false }]
]) {
  test(`offline IFC export stays disabled during ${name} until the host releases its lock`, async () => fixture(async page => {
    await page.evaluate(activity => {
      window.sentActions = [];
      window.magpiePreviewSend = action => window.sentActions.push(action);
      window.magpie.applyState({ ...activity, canSend: false });
    }, activity);
    await page.locator("#settings-button").click();
    const button = page.getByRole("button", { name: "导出 IFC", exact: true });
    assert.equal(await button.count(), 1);
    assert.equal(await button.isDisabled(), true);
    await button.evaluate(node => node.dispatchEvent(new MouseEvent("click", { bubbles: true })));
    assert.deepEqual(await page.evaluate(() => window.sentActions), []);
    if (activity.canStop) {
      await page.locator("#send").click();
      assert.deepEqual(await page.evaluate(() => window.sentActions), [{ action: "stop" }]);
      await page.locator("#settings-button").click();
      assert.equal(await button.isDisabled(), true);
    }
    await page.evaluate(() => {
      window.sentActions = [];
      window.magpie.applyState({ busy: false, canStop: false, catalogLoading: false, canSend: false });
    });
    assert.equal(await button.isEnabled(), true);
    if (await page.locator("#settings").isHidden()) await page.locator("#settings-button").click();
    await button.click();
    assert.deepEqual(await page.evaluate(() => window.sentActions), [
      { action: "exportIfc", documentId: "scroll-fixture" }
    ]);
  }));
}

test("offline IFC export requires an active document", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
  });
  await page.locator("#settings-button").click();
  const button = page.getByRole("button", { name: "导出 IFC", exact: true });
  assert.equal(await button.count(), 1);
  for (const documentId of ["", "0"]) {
    await page.evaluate(documentId => window.magpie.applyState({ documentId, busy: false, canStop: false }), documentId);
    assert.equal(await button.isDisabled(), true);
    await button.evaluate(node => node.dispatchEvent(new MouseEvent("click", { bubbles: true })));
  }
  assert.deepEqual(await page.evaluate(() => window.sentActions), []);
}));

test("offline IFC export binds its action to the current document after switching", async () => fixture(async page => {
  await page.evaluate(() => {
    window.sentActions = [];
    window.magpiePreviewSend = action => window.sentActions.push(action);
    window.magpie.applyState({ canSend: false });
  });
  await page.locator("#settings-button").click();
  const button = page.getByRole("button", { name: "导出 IFC", exact: true });
  assert.equal(await button.count(), 1);
  await page.evaluate(() => window.magpie.applyState({ documentId: "second-document", draft: "second document draft" }));
  await button.click();
  assert.deepEqual(await page.evaluate(() => window.sentActions), [
    { action: "exportIfc", documentId: "second-document" }
  ]);
  assert.equal(await page.locator("#draft").inputValue(), "second document draft");
}));

for (const [state, title, detail, status] of [
  ["done", "IFC 导出完成", "IFC4 已重新读取并验证几何及关系。\nC:/exports/model.ifc", "done"],
  ["error", "IFC 导出失败", "目标文件已存在，请使用新文件名，未覆盖原文件。", "failed"]
]) {
  test(`offline IFC ${state} card preserves the reading position, draft and execution mode`, async () => fixture(async page => {
    await wheel(page, "#viewport", -500);
    await page.locator("#draft").fill("keep my unsent design request");
    await page.evaluate(() => window.magpie.applyState({ autoTools: true, executionModeRevision: 17, canSend: false }));
    await settle(page);
    const before = await metrics(page);
    await page.evaluate(() => {
      window.fixtureMessages.push({ id: "offline-ifc", type: "tool", text: "导出 IFC 并读回验证", state: "running" });
      window.magpie.applyState({ messages: window.fixtureMessages, busy: true, canStop: true });
    });
    await settle(page);
    await page.evaluate(({ state, title, detail }) => {
      Object.assign(window.fixtureMessages.at(-1), { state, text: title, detail });
      window.magpie.applyState({ messages: window.fixtureMessages, busy: false, canStop: false, canSend: false });
    }, { state, title, detail });
    await settle(page);
    const card = page.locator(".tool").last();
    assert.equal(await card.getAttribute("data-status"), status);
    assert.equal(await card.locator(".tool-name").textContent(), title);
    assert.equal(await card.locator(".tool-detail").textContent(), detail);
    assert.ok(Math.abs((await metrics(page)).top - before.top) <= 2);
    assert.equal(await page.locator("#draft").inputValue(), "keep my unsent design request");
    assert.equal(await page.locator("#execution-mode").textContent(), "YOLO");
  }));
}
