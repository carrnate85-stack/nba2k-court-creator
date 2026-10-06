const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const StudioTheme = require("./studio-theme");

class LogoEditorSession {
  constructor({ BrowserWindow, spawn, pythonCommand, projectRoot, prepare, parent, onUpdate, theme = () => "light" }) {
    Object.assign(this, { BrowserWindow, spawn, pythonCommand, projectRoot, prepare, parent, onUpdate, theme });
    this.session = null;
    this.opening = null;
  }

  async open(request) {
    if (this.opening) return this.opening;
    if (this.session && !this.session.window?.isDestroyed()) {
      this.session.window.show();
      this.session.window.focus();
      return { opened: true };
    }
    this.opening = this.create(request);
    try { return await this.opening; } finally { this.opening = null; }
  }

  async create(request) {
    const prefix = path.join(os.tmpdir(), "nba2k-court-logo-");
    const directory = await fs.promises.mkdtemp(prefix);
    const session = { directory, prefix, statePath: path.join(directory, "state.json"), revision: 0, closing: false };
    this.session = session;
    try {
      const project = await this.prepare({ ...request, backgroundOutput: path.join(directory, "court.png") });
      if (session.closing) throw new Error("Logo editor opening was cancelled.");
      const envelope = { revision: 1, returnRequested: false, project };
      await fs.promises.writeFile(session.statePath, JSON.stringify(envelope), "utf8");
      session.revision = 1;
      const python = this.pythonCommand();
      session.process = this.spawn(python.exe, [...python.prefix, "-B", path.join(this.projectRoot, "tools/court_logo_web.py"), "--state", session.statePath], {
        cwd: this.projectRoot, windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
      });
      const url = await this.serverUrl(session.process);
      if (session.closing) throw new Error("Logo editor opening was cancelled.");
      const parent = this.parent();
      const window = new this.BrowserWindow({ width: 1440, height: 900, minWidth: 800, minHeight: 600,
        parent: parent || undefined, modal: Boolean(parent), show: false, autoHideMenuBar: true,
        title: "Court Logo Editor", backgroundColor: StudioTheme.colors(this.theme()).WindowBrush,
        webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true },
      });
      session.window = window;
      window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
      window.webContents.on("will-navigate", (event, target) => {
        if (new URL(target).origin !== new URL(url).origin) event.preventDefault();
      });
      window.once("ready-to-show", () => { if (!window.isDestroyed()) { window.show(); window.focus(); } });
      window.once("closed", () => { this.close(session).catch(error => console.error(error)); });
      session.process.once("exit", () => { if (!session.closing && !window.isDestroyed()) window.close(); });
      session.timer = setInterval(() => this.poll(session).catch(error => console.error("Logo editor sync failed", error)), 400);
      const themedUrl = new URL(url);
      themedUrl.searchParams.set("theme", this.theme() === "dark" ? "dark" : "light");
      await window.loadURL(themedUrl.href);
      return { opened: true, guidesAvailable: Boolean(project.guides) };
    } catch (error) {
      await this.close(session);
      throw error;
    }
  }

  serverUrl(child) {
    return new Promise((resolve, reject) => {
      let buffer = "", diagnostic = "";
      const timer = setTimeout(() => finish(new Error("Logo editor server did not start.")), 15000);
      const finish = (error, url) => {
        clearTimeout(timer);
        child.stdout.off("data", output);
        child.stderr.off("data", stderr);
        child.off("error", failed);
        child.off("exit", exited);
        if (error) reject(error); else resolve(url);
      };
      const failed = error => finish(error);
      const exited = () => finish(new Error(`Logo editor server stopped. ${diagnostic}`));
      const stderr = chunk => { diagnostic = (diagnostic + chunk).slice(-1200); };
      const output = chunk => {
        buffer += chunk;
        if (buffer.length > 65536) { finish(new Error("Invalid logo editor server response.")); return; }
        const newline = buffer.indexOf("\n");
        if (newline < 0) return;
        try {
          const url = JSON.parse(buffer.slice(0, newline)).url;
          const parsed = new URL(url);
          if (parsed.protocol !== "http:" || parsed.hostname !== "127.0.0.1") throw new Error("Invalid logo editor address.");
          finish(null, url);
        } catch (error) { finish(error); }
      };
      child.stdout.on("data", output); child.stderr.on("data", stderr);
      child.once("error", failed); child.once("exit", exited);
    });
  }

  poll(session, force = false) {
    if (session.polling) return session.polling;
    if (session.closing && !force) return Promise.resolve();
    session.polling = (async () => {
      const state = JSON.parse(await fs.promises.readFile(session.statePath, "utf8"));
      if (state.revision > session.revision) {
        session.revision = state.revision;
        this.onUpdate({ items: state.project.items, selectedId: state.project.selectedId });
      }
      if (state.returnRequested && !session.closing && session.window && !session.window.isDestroyed()) session.window.close();
    })();
    return session.polling.finally(() => { session.polling = null; });
  }

  async close(session = this.session) {
    if (!session || session.closing) return;
    session.closing = true;
    clearInterval(session.timer);
    try {
      if (session.polling) await session.polling;
      if (fs.existsSync(session.statePath)) await this.poll(session, true);
    } finally {
      session.process?.kill();
      if (session.window && !session.window.isDestroyed()) session.window.destroy();
      const directory = path.resolve(session.directory);
      if (directory.startsWith(path.resolve(session.prefix)) && path.dirname(directory) === path.resolve(os.tmpdir())) {
        await fs.promises.rm(directory, { recursive: true, force: true });
      }
      if (this.session === session) this.session = null;
      const parent = this.parent();
      if (parent && !parent.isDestroyed()) { if (parent.isMinimized()) parent.restore(); parent.show(); parent.focus(); }
    }
  }
}

module.exports = { LogoEditorSession };
