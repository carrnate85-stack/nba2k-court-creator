const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { PythonWorker } = require("../electron/python-worker");

function fakeProcess() {
  const child = new EventEmitter();
  child.stdout = new EventEmitter();
  child.stderr = new EventEmitter();
  child.jobs = [];
  child.stdin = { write: (line, callback) => { child.jobs.push(JSON.parse(line)); callback(null); } };
  child.kill = () => { child.killed = true; };
  child.reply = (result) => child.stdout.emit("data", JSON.stringify({ id: child.jobs.at(-1).id, result }) + "\n");
  return child;
}

const command = () => ({ exe: "python", prefix: [] });

test("queued preview receives its timeout when it starts", async () => {
  const child = fakeProcess();
  const worker = new PythonWorker(command, ".", () => child);
  const first = worker.run(["first"], 1000);
  const second = worker.run(["second"], 15);
  const result = Promise.all([first, second]);
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(child.jobs.length, 1);
  child.reply("first finished");
  assert.equal(child.jobs.length, 2);
  child.reply("second finished");
  assert.deepEqual(await result, ["first finished", "second finished"]);
  worker.stop();
});

test("preview failure leaves a running export intact", async () => {
  const previewProcess = fakeProcess();
  const exportProcess = fakeProcess();
  const previews = new PythonWorker(command, ".", () => previewProcess);
  const exports = new PythonWorker(command, ".", () => exportProcess);
  const exportResult = exports.run(["export-current-iff"], 1000);
  const previewFailure = assert.rejects(previews.run(["preview"], 10), /took too long/);
  await previewFailure;
  assert.equal(Boolean(exportProcess.killed), false);
  exportProcess.reply("export finished");
  assert.equal(await exportResult, "export finished");
  previews.stop();
  exports.stop();
});

test("manual court bounds survive a control refresh after export", () => {
  const source = fs.readFileSync(path.join(__dirname, "../electron/renderer/renderer.js"), "utf8");
  const functions = source.slice(source.indexOf("function updateImportControls()"), source.indexOf("function importRequest("));
  const input = () => ({ value: "", disabled: false });
  const ui = Object.fromEntries([
    "importSourceName", "importTargetName", "importTexture", "importTextureInfo",
    "importAutoEdges", "importExportPng", "importExportIff", "importPrepareBase",
  ].map(name => [name, input()]));
  ui.importEdges = [10, 20, 190, 90].map(value => ({ value: String(value) }));
  const importState = { source: { path: "court.iff", selected: "bigfloor.dds", textures: [
    { name: "bigfloor.dds", width: 200, height: 100, format: "BC7_UNORM" },
  ] }, bounds: [0, 0, 200, 100], target: null, exporting: false, preparing: false };
  const context = { ui, importState, importFileName: value => value, fillImportSelect: () => {},
    document: { getElementById: () => input() } };
  vm.createContext(context);
  vm.runInContext(functions + "\ncurrentImportBounds(); updateImportControls();", context);
  assert.deepEqual(ui.importEdges.map(item => Number(item.value)), [10, 20, 190, 90]);
  assert.deepEqual(Array.from(importState.bounds), [10, 20, 190, 90]);
});
