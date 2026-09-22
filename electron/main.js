const { app, BrowserWindow, dialog, ipcMain, shell } = require("electron");
const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const projectRoot = path.resolve(__dirname, "..");
const engineTimeoutMs = 120000;
let mainWindow = null;

function bundledPython() {
  return path.join(
    os.homedir(),
    ".cache",
    "codex-runtimes",
    "codex-primary-runtime",
    "dependencies",
    "python",
    "python.exe"
  );
}

function pythonCommand() {
  const venv = path.join(projectRoot, "runtime", "python", "Scripts", "python.exe");
  if (fs.existsSync(venv)) return { exe: venv, prefix: [] };
  const own = path.join(projectRoot, "runtime", "python", "python.exe");
  if (fs.existsSync(own)) return { exe: own, prefix: [] };
  const bundled = bundledPython();
  if (fs.existsSync(bundled)) return { exe: bundled, prefix: [] };
  return { exe: "py", prefix: ["-3"] };
}

let worker = null;
let sequence = 0;
const requests = new Map();

function rejectRequests(error) {
  for (const pending of requests.values()) {
    clearTimeout(pending.timer);
    pending.reject(error);
  }
  requests.clear();
}

function startWorker() {
  const command = pythonCommand();
  const child = spawn(command.exe, [...command.prefix, "-u", "-m", "court_creator.service"], {
    cwd: projectRoot,
    windowsHide: true,
  });
  worker = child;
  let stdout = "";
  let stderr = "";

  const fail = (error) => {
    if (worker !== child) return;
    worker = null;
    if (!child.killed) child.kill();
    const detail = stderr.trim();
    rejectRequests(new Error(detail ? `${error.message}: ${detail.slice(-2000)}` : error.message));
  };

  child.stdout.on("data", (chunk) => {
    stdout += chunk.toString();
    let end;
    while ((end = stdout.indexOf("\n")) >= 0) {
      const line = stdout.slice(0, end).trim();
      stdout = stdout.slice(end + 1);
      if (!line) continue;
      try {
        const message = JSON.parse(line);
        const pending = requests.get(message.id);
        if (!pending) continue;
        requests.delete(message.id);
        clearTimeout(pending.timer);
        if (message.error) pending.reject(new Error(message.error));
        else pending.resolve(message.result);
      } catch (error) {
        fail(new Error("Rendering engine returned an invalid response"));
      }
    }
  });
  child.stderr.on("data", (chunk) => {
    stderr = `${stderr}${chunk.toString()}`.slice(-8000);
  });
  child.on("error", (error) => fail(new Error(`Unable to start the rendering engine (${error.message})`)));
  child.on("close", (code) => fail(new Error(`Rendering engine stopped${code ? ` with code ${code}` : ""}`)));
  return child;
}

function runPython(args) {
  return new Promise((resolve, reject) => {
    const child = worker || startWorker();
    const id = ++sequence;
    const timer = setTimeout(() => {
      if (!requests.has(id)) return;
      requests.delete(id);
      reject(new Error("Rendering took too long. The engine was restarted; try again."));
      if (worker === child) {
        worker = null;
        child.kill();
        rejectRequests(new Error("The rendering engine was restarted after a timeout."));
      }
    }, engineTimeoutMs);
    requests.set(id, { resolve, reject, timer });
    const handleWriteError = (error) => {
      if (!error || !requests.has(id)) return;
      requests.delete(id);
      clearTimeout(timer);
      reject(new Error(`Could not contact the rendering engine (${error.message})`));
    };
    try {
      child.stdin.write(`${JSON.stringify({ id, args })}\n`, handleWriteError);
    } catch (error) {
      handleWriteError(error);
    }
  });
}

async function renderPreview(request) {
  const requestPath = path.join(os.tmpdir(), `nba2k-court-render-${Date.now()}-${Math.random().toString(16).slice(2)}.json`);
  await fs.promises.writeFile(requestPath, JSON.stringify(request), "utf8");
  try {
    return await runPython(["render", "--request", requestPath]);
  } finally {
    fs.promises.unlink(requestPath).catch(() => {});
  }
}

function createWindow() {
  const window = new BrowserWindow({
    width: 1760,
    height: 920,
    minWidth: 1280,
    minHeight: 760,
    title: "NBA 2K Court Creator",
    titleBarStyle: "hidden",
    titleBarOverlay: {
      color: "#F8FAFB",
      symbolColor: "#102134",
      height: 58,
    },
    autoHideMenuBar: true,
    show: false,
    icon: path.join(projectRoot, "src", "NBA2KCourtCreator", "Assets", "app-icon.ico"),
    backgroundColor: "#F6F7F3",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  mainWindow = window;
  window.once("ready-to-show", () => {
    window.show();
    window.focus();
  });
  window.on("closed", () => {
    if (mainWindow === window) mainWindow = null;
  });
  window.loadFile(path.join(__dirname, "renderer", "index.html"));
}

const hasSingleInstanceLock = app.requestSingleInstanceLock();

if (!hasSingleInstanceLock) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  });

  app.whenReady().then(() => {
    createWindow();
    const python = pythonCommand();
    const updater = spawn(python.exe, [...python.prefix, path.join(projectRoot, "updater.py")], { cwd: projectRoot, windowsHide: true, stdio: "ignore" });
    updater.on("error", error => console.error("Update check failed", error));
    app.on("activate", () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });
}

app.on("window-all-closed", () => {
  if (worker) worker.kill();
  if (process.platform !== "darwin") app.quit();
});

const recoveryPath = () => path.join(app.getPath("userData"), "recovery.json");
let pendingRecovery = null;
let recoveryTimer = null;
function atomicJson(target, data) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const temporary = target + ".tmp";
  fs.writeFileSync(temporary, JSON.stringify(data, null, 2));
  fs.renameSync(temporary, target);
}

function flushRecovery() {
  if (recoveryTimer) clearTimeout(recoveryTimer);
  recoveryTimer = null;
  if (!pendingRecovery) return;
  const data = pendingRecovery;
  pendingRecovery = null;
  try { atomicJson(recoveryPath(), data); } catch (error) { console.error(error); }
}

function pruneRecoveryBackups() {
  const directory = path.dirname(recoveryPath());
  if (!fs.existsSync(directory)) return;
  const backups = fs.readdirSync(directory)
    .filter((name) => /^recovery-\d+\.json$/.test(name))
    .map((name) => ({ name, modified: fs.statSync(path.join(directory, name)).mtimeMs }))
    .sort((left, right) => right.modified - left.modified);
  for (const backup of backups.slice(5)) fs.unlinkSync(path.join(directory, backup.name));
}

ipcMain.on("project:recover-write", (_event, data) => {
  pendingRecovery = data;
  if (recoveryTimer) clearTimeout(recoveryTimer);
  recoveryTimer = setTimeout(flushRecovery, 250);
});
app.on("before-quit", flushRecovery);
ipcMain.handle("project:recovery", () => {
  try { return JSON.parse(fs.readFileSync(recoveryPath(), "utf8")); } catch { return null; }
});
ipcMain.handle("project:confirm-replace", async () => {
  const result = await dialog.showMessageBox(mainWindow, { type: "question", buttons: ["Keep Editing", "Continue"], defaultId: 0, cancelId: 0, message: "Replace the current court?", detail: "Save Project first to keep an editable copy. The current court will also be backed up for recovery." });
  if (result.response !== 1) return false;
  flushRecovery();
  if (fs.existsSync(recoveryPath())) fs.copyFileSync(recoveryPath(), path.join(app.getPath("userData"), `recovery-${Date.now()}.json`));
  pruneRecoveryBackups();
  return true;
});
ipcMain.handle("project:save", async (_event, data) => {
  const result = await dialog.showSaveDialog(mainWindow, { defaultPath: "My Court.court.json", filters: [{ name: "Court project", extensions: ["json"] }] });
  if (result.canceled) return null;
  atomicJson(result.filePath, { ...data, _projectPath: result.filePath });
  return result.filePath;
});
ipcMain.handle("project:open", async () => {
  const result = await dialog.showOpenDialog(mainWindow, { properties: ["openFile"], filters: [{ name: "Court project", extensions: ["json"] }] });
  if (result.canceled) return null;
  const data = JSON.parse(fs.readFileSync(result.filePaths[0], "utf8"));
  if (data.version !== 1 || !data.templatePath) throw new Error("Not a Court Creator project.");
  if (!Array.isArray(data.logoImages)) data.logoImages = [];
  if (!data.visibility || typeof data.visibility !== "object") data.visibility = {};
  if (!data.colorOverrides || typeof data.colorOverrides !== "object") data.colorOverrides = {};
  data._projectPath = result.filePaths[0];
  return data;
});

ipcMain.handle("app:info", () => ({ version: app.getVersion() }));

ipcMain.handle("backend:load", async (_event, templatePath) => {
  const args = templatePath ? ["load", "--template", templatePath] : ["load"];
  return runPython(args);
});

ipcMain.handle("backend:render", async (_event, request) => renderPreview(request));

ipcMain.handle("backend:sample-color", async (_event, layerId) => runPython(["sample-color", "--layer-id", layerId]));

ipcMain.handle("backend:add-floor", async (_event, sourcePath) => runPython(["add-floor", "--source", sourcePath]));

ipcMain.handle("dialog:open-psd", async (event) => {
  const result = await dialog.showOpenDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Load court PSD template",
    filters: [{ name: "Photoshop PSD", extensions: ["psd"] }, { name: "All files", extensions: ["*"] }],
    properties: ["openFile"],
  });
  return result.canceled ? null : result.filePaths[0];
});

ipcMain.handle("dialog:import-logo", async (event) => {
  const result = await dialog.showOpenDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Import logo",
    filters: [{ name: "Images", extensions: ["png", "jpg", "jpeg", "webp"] }, { name: "All files", extensions: ["*"] }],
    properties: ["openFile", "multiSelections"],
  });
  return result.canceled ? [] : result.filePaths;
});

ipcMain.handle("dialog:add-floor", async (event) => {
  const result = await dialog.showOpenDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Add custom court floor",
    filters: [{ name: "Images", extensions: ["png", "jpg", "jpeg"] }, { name: "All files", extensions: ["*"] }],
    properties: ["openFile"],
  });
  return result.canceled ? null : result.filePaths[0];
});

ipcMain.handle("dialog:export-png", async (event) => {
  const result = await dialog.showSaveDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Export court PNG",
    defaultPath: "court-export.png",
    filters: [{ name: "PNG", extensions: ["png"] }],
  });
  return result.canceled ? null : result.filePath;
});

ipcMain.handle("shell:open-path", async (_event, targetPath) => {
  if (!targetPath) return;
  await shell.openPath(targetPath);
});

ipcMain.handle("shell:show-item", async (_event, targetPath) => {
  if (!targetPath) return;
  shell.showItemInFolder(targetPath);
});
