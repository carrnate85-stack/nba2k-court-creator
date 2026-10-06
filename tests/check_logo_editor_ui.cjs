// Exercises the Python editor HTML in a headless browser; never starts Electron.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

async function main() {
  const root = path.resolve(__dirname, "..");
  const modules = process.env.COURT_TEST_NODE_MODULES || path.join(process.env.USERPROFILE, ".cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules");
  const { chromium } = require(path.join(modules, "playwright"));
  const fixture = JSON.parse(execFileSync(path.join(root, "runtime/python/python.exe"), ["-B", "-c",
    "import json; from pathlib import Path; from tools.court_logo_web import HTML; from court_creator.experimental_lines import load_geometry,editor_guides; print(json.dumps({'html':HTML,'guides':editor_guides(load_geometry(Path.cwd()))}))"], { cwd: root, encoding: "utf8", maxBuffer: 10 * 1024 * 1024 }));
  const logo = path.join(root, "src/NBA2KCourtCreator/Assets/app-icon.png");
  const envelope = { revision: 1, returnRequested: false, project: { width: 8192, height: 4096,
    guides: fixture.guides, guideStatus: fixture.guides.alignment, selectedId: "logo", items: [
      { id: "logo", name: "Test Logo", path: logo, visible: true, x: 1600, y: 1400, width: 600, height: 400, rotation: 0, opacity: 100 },
    ] } };
  const saves = [];
  const browser = await chromium.launch({ headless: true, channel: "chrome" });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.route("http://court-editor.test/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.pathname === "/") return route.fulfill({ contentType: "text/html", body: fixture.html });
      if (url.pathname === "/studio-theme.js") return route.fulfill({ contentType: "text/javascript", body: fs.readFileSync(path.join(root, "electron/studio-theme.js")) });
      if (url.pathname === "/api/court") return route.fulfill({ contentType: "image/png", body: fs.readFileSync(path.join(root, "outputs/experimental-stock-lines-check.png")) });
      if (url.pathname.startsWith("/api/logo/")) return route.fulfill({ contentType: "image/png", body: fs.readFileSync(logo) });
      if (url.pathname === "/api/save") {
        const payload = request.postDataJSON();
        saves.push(payload);
        await new Promise(resolve => setTimeout(resolve, 80));
        envelope.project.items = payload.items; envelope.project.selectedId = payload.selectedId;
        envelope.revision++;
      }
      if (url.pathname === "/api/return") { envelope.returnRequested = true; envelope.revision++; }
      return route.fulfill({ contentType: "application/json", body: JSON.stringify(envelope) });
    });
    await page.goto("http://court-editor.test/");
    await page.waitForFunction(() => project && bg.complete && images.get("logo")?.complete && scale < 1);
    assert.equal(await page.locator("#showGuides").isChecked(), false);
    assert.equal(await page.locator("#snapGuides").isChecked(), false);
    assert.equal(await page.locator("#anchor option").count(), 17);
    const plainCanvas = await page.locator("#canvas").evaluate(el => el.toDataURL());
    await page.locator("#showGuides").check();
    await page.waitForTimeout(60);
    assert.notEqual(await page.locator("#canvas").evaluate(el => el.toDataURL()), plainCanvas);
    assert.equal(saves.length, 0, "Guides must not mutate project artwork");
    await page.locator("#snapGuides").check();
    const center = fixture.guides.center;
    const start = await page.evaluate(() => {
      const item = selected(), p = toScreen(item.x + item.width / 2, item.y + item.height / 2), box = canvas.getBoundingClientRect();
      return { x: p.x + box.x, y: p.y + box.y };
    });
    const target = await page.evaluate(center => {
      const p = toScreen(center.x + 20, center.y + 20), box = canvas.getBoundingClientRect();
      return { x: p.x + box.x, y: p.y + box.y };
    }, center);
    await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(target.x, target.y, { steps: 5 }); await page.mouse.up();
    await page.waitForFunction(() => savePromise === null);
    const position = await page.evaluate(() => ({ x: selected().x + selected().width / 2, y: selected().y + selected().height / 2 }));
    assert.ok(Math.abs(position.x - center.x) < .001 && Math.abs(position.y - center.y) < .001, JSON.stringify(position));
    assert.deepEqual(await page.evaluate(() => { const item=selected(); return snapPosition(item, item.x + 20, item.y + 20, true); }),
      await page.evaluate(() => ({ x: selected().x + 20, y: selected().y + 20 })));
    await page.locator("#anchor").selectOption("left-free-throw");
    await page.locator("#placeAnchor").click();
    await page.waitForFunction(() => savePromise === null);
    const anchor = fixture.guides.anchors.find(anchor => anchor.id === "left-free-throw");
    assert.equal(await page.evaluate(() => selected().x + selected().width / 2), anchor.x);
    await page.locator("#copyX").click();
    await page.waitForFunction(() => savePromise === null);
    assert.ok(Math.abs(await page.evaluate(() => selected().x + selected().width / 2) - (2 * center.x - anchor.x)) < .001);
    await page.evaluate(async () => {
      selected().x = 10; const first = save(); selected().x = 20; const second = save(); await Promise.all([first, second]);
    });
    assert.equal(saves.at(-2).items.at(-1).x, 10);
    assert.equal(saves.at(-1).items.at(-1).x, 20);
    assert.ok(saves.every(payload => !payload.guides && !payload.paintSettings), "Editor guides leaked into saved artwork");
    const output = path.join(root, "outputs/logo-editor-ui-check");
    fs.mkdirSync(output, { recursive: true });
    for (const [width, height] of [[1440, 900], [1000, 700], [800, 600]]) {
      await page.setViewportSize({ width, height }); await page.waitForTimeout(80);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), width);
      assert.ok(await page.locator("#stage").evaluate(el => el.clientWidth > 400 && el.clientHeight > 400));
      await page.screenshot({ path: path.join(output, `${width}x${height}.png`) });
    }
    await page.goto("http://court-editor.test/?theme=dark");
    await page.waitForFunction(() => project && bg.complete && scale < 1);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), "dark");
    assert.equal(await page.locator("#stage").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(13, 20, 29)");
    await page.screenshot({ path: path.join(output, "dark.png") });
    await page.locator("#done").click();
    await page.waitForFunction(() => document.getElementById("done").textContent === "Returned");
    assert.equal(envelope.returnRequested, true);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ anchors: 17, guides: "pass", pointerSnap: "pass", altBypass: "pass", anchorPlacement: "pass", courtMirror: "pass", queuedSaves: "pass", responsive: "pass", returnFlush: "pass" }));
  } finally { await browser.close(); }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
