const test = require("node:test");
const assert = require("node:assert/strict");
const { colors } = require("../electron/studio-theme");

test("studio palette matches companion semantic colors in both modes", () => {
  const light = colors("light"), dark = colors("dark");
  assert.equal(light.WindowBrush, "#ECEDEF");
  assert.equal(light.PanelBrush, "#F7F7F9");
  assert.equal(light.WorkspaceBrush, "#E1E3E7");
  assert.equal(light.ActionBrush, "#30313A");
  assert.equal(light.AccentDarkBrush, "#DDE5F1");
  assert.equal(dark.WindowBrush, "#0B1119");
  assert.equal(dark.PanelRaisedBrush, "#172230");
  assert.equal(dark.AccentBrightBrush, "#40B5FF");
  assert.equal(dark.ActionBrush, "#075A67");
  assert.deepEqual(Object.keys(light), Object.keys(dark));
  assert.ok(Object.values(light).every(value => /^#[0-9A-F]{6}$/.test(value)));
  assert.deepEqual(colors("invalid"), light);
  light.WindowBrush = "changed";
  assert.equal(colors("light").WindowBrush, "#ECEDEF");
});
