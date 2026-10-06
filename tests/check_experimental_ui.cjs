// Promoted Game UV workflow. Headless only; never launches Electron.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const { pathToFileURL } = require("node:url");

async function main() {
  const root = path.resolve(__dirname, "..");
  const modules = process.env.COURT_TEST_NODE_MODULES || path.join(process.env.USERPROFILE, ".cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules");
  const { chromium } = require(path.join(modules, "playwright"));
  const fixture = JSON.parse(execFileSync(path.join(root, "runtime/python/python.exe"), ["-B", "-c",
    "import json; from court_creator.backend import load_stock_state; print(json.dumps(load_stock_state()))"],
  { cwd: root, encoding: "utf8", maxBuffer: 20 * 1024 * 1024 }));
  const logoPath = path.join(root, "src/NBA2KCourtCreator/Assets/app-icon.png");
  const output = path.join(root, "outputs/experimental-ui-check");
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, channel: "chrome", args: ["--allow-file-access-from-files"] });
  try {
    const page = await browser.newPage({ viewport: { width: 1760, height: 920 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.addInitScript(({ fixture, logoPath }) => {
      localStorage.clear(); window.testLogoPath = logoPath;
      window.courtCreator = {
        appInfo: async () => ({ version: "test" }), recovery: async () => null,
        load: async () => fixture, autosave: project => { window.recovered = project; },
        render: async request => { window.lastExport = request; return { previewPath: request.outputPath }; },
        openLogoEditor: async request => { window.editorRequest = request; return { opened: true }; },
        onLogoEditorUpdate: callback => { window.editorUpdate = callback; },
        sampleColor: async () => { throw new Error("Native rows must not sample the PSD"); },
        importBaseStatus: async () => ({}), showItem: async () => {},
        chooseExportPng: async () => "test-export.png", chooseCurrentIffOutput: async () => "test-export.iff",
        exportCurrentIff: async request => { window.lastIffExport = request; return { outputPath: request.outputPath }; },
        confirmReplace: async () => true,
      };
    }, { fixture, logoPath });
    await page.goto(pathToFileURL(path.join(root, "electron/renderer/index.html")).href);
    await page.waitForFunction(() => state.geometry && nativePreview.image && !document.body.classList.contains("workspace-loading"));
    assert.equal(await page.evaluate(() => state.buildMode), "game-uv");
    assert.equal(await page.locator('[data-section="experimental"]').count(), 0);
    for (const selector of ["#openButton", "#openPsdButton", ".workspace-heading", ".inspector-tabs"]) assert.equal(await page.locator(selector).isVisible(), false);
    assert.equal(await page.locator(".sidebar .brand").count(), 0);
    assert.ok(await page.locator("#newButton").evaluate(el => Boolean(el.closest(".app-titlebar"))));
    await page.locator('[data-section="paint"]').click();
    assert.equal(await page.locator("#layersHost .layer-row:not(.group)").count(), 23);
    const row = id => page.locator(`#layersHost .layer-row[data-id="${id}"]`);
    const pixels = () => page.locator("#experimentalCanvas").evaluate(el => el.toDataURL());
    const initialPixels = await pixels();
    await row("paint-left").locator(".color-box").click();
    await page.locator("#colorEditorHex").fill("#2266CC");
    await page.locator("#colorEditorApply").click();
    await page.waitForTimeout(230);
    assert.notEqual(await pixels(), initialPixels);
    assert.equal(await page.evaluate(() => renderRequest().paintSettings["paint-left"].color), "#2266CC");
    await page.locator("#colorEditorClose").click();
    for (const id of ["two-point-left", "college-three", "high-school-three"]) {
      const before = await pixels();
      await row(id).locator(".visibility-toggle").click();
      await page.waitForTimeout(230);
      assert.notEqual(await pixels(), before, `${id} toggle did not paint`);
      assert.equal(await row(id).locator(".team-color-link").isVisible(), true);
      await row(id).locator(".visibility-toggle").click();
      assert.equal(await row(id).locator(".color-box").count(), 0);
    }
    await page.evaluate(() => {
      state.logos = Array.from({ length: 4 }, (_, index) => ({ id: `logo-${index}`, name: `Logo ${index}`,
        path: window.testLogoPath, x: 2700 + index * 600, y: 1400, width: 400, height: 400,
        rotation: index * 30, opacity: 100, visible: true, scaleLocked: true }));
      state.selectedLogoId = "logo-0"; renderLogos(); schedulePreview();
    });
    await page.waitForFunction(() => nativePreview.logoImages.size === 1);
    await page.waitForTimeout(80);
    const withLogos = await pixels();
    await page.locator("#browseFloorButton").click();
    await page.locator("#floorSearch").fill("Celtics");
    await page.locator(".floor-card").first().click();
    await page.waitForTimeout(400);
    assert.notEqual(await pixels(), withLogos, "Changing hardwood with logos failed");
    assert.equal(await page.evaluate(() => state.logos.length), 4);
    await page.locator('[data-section="logos"]').click();
    await page.locator("#openLogoEditorButton").click();
    assert.equal(await page.evaluate(() => window.editorRequest.buildMode), "game-uv");
    assert.equal(await page.evaluate(() => window.editorRequest.logoImages.length), 4);
    assert.equal(await page.evaluate(() => "templatePath" in window.editorRequest), false);
    await page.locator("#exportButton").click();
    assert.equal(await page.evaluate(() => window.lastExport.mappingMode), "game-uv");
    assert.equal(await page.evaluate(() => window.lastExport.logoImages.length), 4);
    await page.locator('[data-section="export"]').click();
    await page.locator("#exportIffButton").click();
    assert.equal(await page.evaluate(() => window.lastIffExport.buildMode), "game-uv");
    assert.equal(await page.evaluate(() => window.lastIffExport.paintSettings["paint-left"].color), "#2266CC");
    const snapshot = await page.evaluate(() => projectSnapshot());
    assert.equal(snapshot.version, 2); assert.equal(snapshot.templatePath, undefined);
    await page.evaluate(snapshot => loadWorkspace(null, snapshot), snapshot);
    assert.equal(await page.evaluate(() => state.logos.length), 4);
    assert.equal(await page.evaluate(() => selectedFloorLayer().displayName.includes("Celtics")), true);
    assert.equal(await page.evaluate(() => renderRequest().paintSettings["paint-left"].color), "#2266CC");
    await page.locator('[data-section="paint"]').click();
    for (const [width, height] of [[1760, 920], [1280, 720], [1000, 700]]) {
      await page.setViewportSize({ width, height });
      const bounds = await page.locator("#layersPanel").boundingBox();
      assert.ok(bounds.x + bounds.width <= width + 1 && bounds.y + bounds.height <= height + 1);
      assert.equal(await row("paint-left").locator(".team-color-link").isVisible(), true);
      await page.screenshot({ path: path.join(output, `${width}x${height}.png`) });
    }
    await page.evaluate(() => resetToDefault());
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(() => state.logos.length), 0);
    assert.equal(await page.evaluate(() => renderRequest().paintSettings["paint-left"].color), "#19583F");
    assert.equal(await page.evaluate(() => renderRequest().lineSettings["NBA_line_three_point_lowShape"].visible), true);
    assert.equal(await page.evaluate(() => renderRequest().lineSettings["college-three"].visible), false);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ promotedBuild: "pass", hiddenTemplates: "pass", chrome: "pass", rowControls: "pass",
      hardwoodWithFourLogos: "pass", nativeEditor: "pass", exportRequests: "pass", projectRoundTrip: "pass", newDefaults: "pass", responsive: "pass" }));
  } finally { await browser.close(); }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
