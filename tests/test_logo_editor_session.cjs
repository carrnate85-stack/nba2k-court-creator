const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const fs = require("node:fs");
const { LogoEditorSession } = require("../electron/logo-editor-session");

class FakeWindow extends EventEmitter {
  constructor(options) {
    super(); this.destroyed = false;
    this.options = options;
    this.webContents = new EventEmitter();
    this.webContents.setWindowOpenHandler = () => {};
  }
  isDestroyed() { return this.destroyed; }
  isMinimized() { return false; }
  show() { this.shown = true; }
  focus() { this.focused = true; }
  async loadURL(url) { this.url = url; this.emit("ready-to-show"); }
  close() { this.destroy(); }
  destroy() { if (!this.destroyed) { this.destroyed = true; this.emit("closed"); } }
}

function setup() {
  const parent = new FakeWindow(), updates = [], children = [];
  let prepared = 0;
  const editor = new LogoEditorSession({ BrowserWindow: FakeWindow, projectRoot: ".", parent: () => parent,
    pythonCommand: () => ({ exe: "python", prefix: [] }), onUpdate: update => updates.push(update),
    prepare: async () => { prepared++; await new Promise(resolve => setTimeout(resolve, 10)); return { items: [], selectedId: null, guides: {} }; },
    spawn: () => {
      const child = new EventEmitter(); child.stdout = new EventEmitter(); child.stderr = new EventEmitter();
      child.kill = () => { child.killed = true; child.emit("exit"); };
      children.push(child);
      setImmediate(() => child.stdout.emit("data", '{"url":"http://127.0.0.1:3456/"}\n'));
      return child;
    },
  });
  return { editor, parent, updates, children, prepared: () => prepared };
}

test("concurrent opens reuse one editor and a later open focuses it", async () => {
  const fixture = setup();
  try {
    await Promise.all([fixture.editor.open({}), fixture.editor.open({})]);
    await fixture.editor.open({});
    assert.equal(fixture.prepared(), 1);
    assert.equal(fixture.children.length, 1);
    assert.equal(fixture.editor.session.window.focused, true);
  } finally { await fixture.editor.close(); }
  assert.equal(fixture.children[0].killed, true);
  assert.equal(fixture.parent.focused, true);
});

test("revision sync returns items, closes on return, and removes only its temporary folder", async () => {
  const fixture = setup();
  try {
    await fixture.editor.open({});
    const session = fixture.editor.session;
    const state = JSON.parse(fs.readFileSync(session.statePath, "utf8"));
    state.revision++;
    state.returnRequested = true;
    state.project.items = [{ id: "logo", x: 123 }];
    state.project.selectedId = "logo";
    fs.writeFileSync(session.statePath, JSON.stringify(state));
    await fixture.editor.poll(session);
    for (let i = 0; i < 50 && fixture.editor.session; i++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.deepEqual(fixture.updates, [{ items: [{ id: "logo", x: 123 }], selectedId: "logo" }]);
    assert.equal(fixture.editor.session, null);
    assert.equal(fs.existsSync(session.directory), false);
    assert.equal(fixture.children[0].killed, true);
  } finally { await fixture.editor.close(); }
});

test("logo editor inherits the studio theme without changing project artwork", async () => {
  const fixture = setup();
  fixture.editor.theme = () => "dark";
  try {
    await fixture.editor.open({});
    const session = fixture.editor.session;
    assert.equal(session.window.options.backgroundColor, "#0B1119");
    assert.equal(new URL(session.window.url).searchParams.get("theme"), "dark");
    const state = JSON.parse(fs.readFileSync(session.statePath, "utf8"));
    assert.equal(state.project.theme, undefined);
  } finally { await fixture.editor.close(); }
});
