// Studio chrome and catalog checks use a headless browser, never Electron.
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
    "import json; from court_creator import backend as b; print(json.dumps({'workspace':b.load_stock_state(),'experimental':b.experimental_state()}))"],
  { cwd: root, encoding: "utf8", maxBuffer: 20 * 1024 * 1024 }));
  const output = path.join(root, "outputs/studio-ui-check");
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, channel: "chrome", args: ["--allow-file-access-from-files"] });
  try {
    const page = await browser.newPage({ viewport: { width: 1760, height: 920 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.addInitScript(fixture => {
      if (!sessionStorage.getItem("themeTestStarted")) {
        localStorage.clear(); sessionStorage.setItem("themeTestStarted", "1");
      }
      window.courtCreator = {
        appInfo: async () => ({ version: "test" }), recovery: async () => null,
        setStudioTheme: async name => { window.nativeTheme = name; },
        load: async () => fixture.workspace,
        render: async request => { window.lastPreview = request; return { previewPath: fixture.workspace.previewPath }; },
        experimental: async () => fixture.experimental, sampleColor: async () => ({ color: [255, 255, 255] }),
        autosave: () => {}, importBaseStatus: async () => ({}), onLogoEditorUpdate: () => {},
      };
    }, fixture);
    await page.goto(pathToFileURL(path.join(root, "electron/renderer/index.html")).href);
    await page.waitForFunction(() => state.geometry && !document.body.classList.contains("workspace-loading"));
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), "light");
    assert.equal(await page.locator("body").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(236, 237, 239)");
    assert.equal(await page.locator("#exportButton").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(48, 49, 58)");
    assert.equal(await page.locator("#previewStage").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(225, 227, 231)");
    assert.equal(await page.locator("#floorCatalog").evaluate(el => el.open), false);
    const layout = await page.locator(".layout").boundingBox(), preview = await page.locator("#previewCard").boundingBox();
    assert.ok(preview.width >= layout.width - 2, "Floor preview did not reclaim catalog space");
    const artwork = JSON.stringify(await page.evaluate(() => ({ visibility: state.visibility, colorOverrides: state.colorOverrides, logos: state.logos })));
    await page.screenshot({ path: path.join(output, "floors-light.png") });

    await page.locator("#browseFloorButton").click();
    assert.equal(await page.locator("#floorCatalog").evaluate(el => el.open), true);
    await page.locator("#floorSearch").fill("Celtics");
    assert.ok(await page.locator(".floor-card").count() > 0);
    await page.locator("#floorListButton").click();
    assert.ok(await page.locator("#floorGallery").evaluate(el => el.classList.contains("list-view")));
    await page.locator("#floorGridButton").click();
    await page.screenshot({ path: path.join(output, "catalog-light.png") });
    await page.keyboard.press("Escape");
    assert.equal(await page.locator("#floorCatalog").evaluate(el => el.open), false);
    assert.equal(await page.evaluate(() => document.activeElement.id), "browseFloorButton");

    await page.getByRole("button", { name: "Switch to dark theme", exact: true }).click();
    assert.equal(await page.evaluate(() => window.nativeTheme), "dark");
    assert.equal(await page.locator("body").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(11, 17, 25)");
    assert.equal(JSON.stringify(await page.evaluate(() => ({ visibility: state.visibility, colorOverrides: state.colorOverrides, logos: state.logos }))), artwork);
    await page.reload();
    await page.waitForFunction(() => state.geometry && !document.body.classList.contains("workspace-loading"));
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), "dark");
    await page.screenshot({ path: path.join(output, "floors-dark.png") });
    await page.locator("#browseFloorButton").click();
    await page.locator("#floorSearch").fill("Celtics");
    await page.screenshot({ path: path.join(output, "catalog-dark.png") });
    await page.locator(".floor-card").first().click();
    assert.equal(await page.locator("#floorCatalog").evaluate(el => el.open), false);
    assert.ok(await page.evaluate(() => selectedFloorLayer().displayName.includes("Celtics")));
    await page.waitForFunction(() => nativePreview.image);

    await page.getByRole("button", { name: "Switch to light theme", exact: true }).click();
    await page.locator('[data-section="paint"]').click();
    await page.locator("#browseFloorButton").click();
    await page.locator("#closeFloorCatalogButton").click();
    assert.equal(await page.evaluate(() => state.section), "paint");
    const firstColor = page.locator("#layersHost .color-box").first();
    await firstColor.click();
    assert.equal(await page.locator("#colorEditorHandle").evaluate(el => getComputedStyle(el).backgroundColor), "rgb(247, 247, 249)");
    await page.screenshot({ path: path.join(output, "paint-color-window.png") });
    await page.locator("#colorEditorClose").click();
    const colorSelection = await page.evaluate(() => state.selectedLayerId);
    await page.locator("#browseFloorButton").click();
    await page.locator(".floor-card").first().click();
    assert.equal(await page.evaluate(() => state.selectedLayerId), colorSelection);
    await page.locator("#browseFloorButton").click();
    await page.evaluate(() => {
      window.courtCreator.chooseFloorImage = async () => "catalog-test.png";
      window.courtCreator.addFloor = async () => {
        const source = state.customFloorImages[0];
        return { layer: { id: "catalog-custom-test", name: "Catalog Test Floor", kind: "pixel",
          parent_id: state.layers.find(isCourtFloorGroup).id },
        image: { ...source, id: "catalog-custom-test", name: "Catalog Test Floor" } };
      };
    });
    await page.locator("#addFloorButton").click();
    await page.waitForFunction(() => state.floorFilter === "custom" && state.layersById.has("catalog-custom-test"));
    assert.equal(await page.locator('.floor-card[data-id="catalog-custom-test"]').count(), 1);
    assert.equal(await page.locator("#floorSearch").inputValue(), "");
    await page.keyboard.press("Escape");

    for (const [width, height] of [[1760, 920], [1280, 760], [1000, 700]]) {
      await page.setViewportSize({ width, height });
      for (const section of ["floors", "paint", "logos", "import", "export"]) {
        await page.locator(`button.nav[data-section="${section}"]`).click();
        await page.waitForTimeout(80);
        const panel = await page.locator(".layout").boundingBox();
        assert.ok(panel.x >= 0 && panel.x + panel.width <= width + 1 && panel.y + panel.height <= height + 1);
        const shell = section === "import" ? "#importPreviewStage" : "#previewShell";
        const imageBox = await page.locator(shell).boundingBox(), stage = await page.locator("#previewStage").boundingBox();
        assert.ok(imageBox.width > 100 && imageBox.height > 50, `${section} preview is blank`);
        assert.ok(Math.abs(imageBox.width / imageBox.height - 2) < .01, `${section} aspect ratio was changed`);
        assert.ok(imageBox.x >= stage.x - 1 && imageBox.y >= stage.y - 1
          && imageBox.x + imageBox.width <= stage.x + stage.width + 1
          && imageBox.y + imageBox.height <= stage.y + stage.height + 1, `${section} preview is cropped`);
      }
      await page.locator('button.nav[data-section="floors"]').click();
      await page.locator("#browseFloorButton").click();
      const dialog = await page.locator("#floorCatalog").boundingBox();
      assert.ok(dialog.x >= 0 && dialog.y >= 0 && dialog.x + dialog.width <= width + 1 && dialog.y + dialog.height <= height + 1);
      const gallery = await page.locator("#floorGallery").evaluate(el => ({ height: el.clientHeight, overflow: getComputedStyle(el).overflowY }));
      assert.ok(gallery.height > 100 && gallery.overflow === "auto");
      await page.screenshot({ path: path.join(output, `catalog-${width}x${height}.png`) });
      await page.keyboard.press("Escape");
      assert.equal(await page.locator("#floorCatalog").evaluate(el => el.open), false);
    }
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ studioPalette: "pass", persistentTheme: "pass", artworkUnchanged: "pass", catalogSelection: "pass", customFloorImport: "pass", preservedPaintSelection: "pass", focusRestore: "pass", responsive: "pass", previewAspectRatio: "pass", pageErrors: errors }));
  } finally { await browser.close(); }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
