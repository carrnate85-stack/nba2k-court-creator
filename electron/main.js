const { app, BrowserWindow, dialog, ipcMain, shell } = require("electron");
const { spawn } = require("child_process");
const { PythonWorker } = require("./python-worker");
const { LogoEditorSession } = require("./logo-editor-session");
const StudioTheme = require("./studio-theme");
const fs = require("fs");
const os = require("os");
const path = require("path");

const projectRoot = path.resolve(__dirname, "..");
const assetRoot = path.join(projectRoot, "assets");
const legacyProjectRoot = path.join(os.homedir(), "NBA 2K Court Creator");
const legacyAssetRoot = path.join(os.homedir(), "OneDrive", "Documents", "2kcourtmodder");
const legacyOneDriveProjectRoot = path.join(os.homedir(), "OneDrive", "Documents", "NBA 2K Court Creator");
const engineTimeoutMs = 120000;
let mainWindow = null;
let studioTheme = "light";

function remapStoredPath(value) {
  if (typeof value !== "string") return value;
  for (const [oldRoot, newRoot] of [
    [legacyAssetRoot, fs.existsSync(assetRoot) ? assetRoot : legacyAssetRoot],
    [legacyProjectRoot, projectRoot],
    [legacyOneDriveProjectRoot, projectRoot],
  ]) {
    if (value.toLowerCase() === oldRoot.toLowerCase()) return newRoot;
    if (value.toLowerCase().startsWith((oldRoot + path.sep).toLowerCase())) {
      return path.join(newRoot, value.slice(oldRoot.length + 1));
    }
  }
  return value;
}

function remapProjectPaths(project) {
  if (!project || typeof project !== "object") return project;
  return {
    ...project,
    templatePath: remapStoredPath(project.templatePath),
    _projectPath: remapStoredPath(project._projectPath),
    logoImages: Array.isArray(project.logoImages)
      ? project.logoImages.map(item => ({ ...item, path: remapStoredPath(item.path) }))
      : project.logoImages,
    customFloorImages: Array.isArray(project.customFloorImages)
      ? project.customFloorImages.map(item => ({ ...item, path: remapStoredPath(item.path) }))
      : project.customFloorImages,
  };
}

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

const previewWorker = new PythonWorker(pythonCommand, projectRoot);
const exportWorker = new PythonWorker(pythonCommand, projectRoot);
const logoEditor = new LogoEditorSession({ BrowserWindow, spawn, pythonCommand, projectRoot,
  theme: () => studioTheme,
  prepare: request => runImportRequest("prepare-logo-editor", request, 240000),
  parent: () => mainWindow,
  onUpdate: data => { if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send("logo-editor:update", data); },
});

function runPython(args, timeoutMs = engineTimeoutMs, lane = "preview") {
  return (lane === "export" ? exportWorker : previewWorker).run(args, timeoutMs);
}

async function renderPreview(request) {
  const requestPath = path.join(os.tmpdir(), `nba2k-court-render-${Date.now()}-${Math.random().toString(16).slice(2)}.json`);
  await fs.promises.writeFile(requestPath, JSON.stringify(request), "utf8");
  try {
    return await runPython(["render", "--request", requestPath], request.exportFullResolution ? 900000 : engineTimeoutMs,
      request.exportFullResolution ? "export" : "preview");
  } finally {
    fs.promises.unlink(requestPath).catch(() => {});
  }
}

async function runImportRequest(command, request, timeoutMs = engineTimeoutMs) {
  const requestPath = path.join(os.tmpdir(), `nba2k-court-import-${Date.now()}-${Math.random().toString(16).slice(2)}.json`);
  await fs.promises.writeFile(requestPath, JSON.stringify(request), "utf8");
  try {
    return await runPython([command, requestPath], timeoutMs, command.startsWith("export-") ? "export" : "preview");
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
      color: StudioTheme.colors(studioTheme).PanelBrush,
      symbolColor: StudioTheme.colors(studioTheme).TextBrush,
      height: 46,
    },
    autoHideMenuBar: true,
    show: false,
    icon: path.join(projectRoot, "src", "NBA2KCourtCreator", "Assets", "app-icon.ico"),
    backgroundColor: StudioTheme.colors(studioTheme).WindowBrush,
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
  logoEditor.close().catch(error => console.error(error));
  previewWorker.stop();
  exportWorker.stop();
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
app.on("before-quit", () => { flushRecovery(); logoEditor.session?.process?.kill(); });
ipcMain.handle("project:recovery", () => {
  try { return remapProjectPaths(JSON.parse(fs.readFileSync(recoveryPath(), "utf8"))); } catch { return null; }
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
  const data = remapProjectPaths(JSON.parse(fs.readFileSync(result.filePaths[0], "utf8")));
  if (!((data.version === 1 && data.templatePath) || (data.version === 2 && data.buildMode === "game-uv"))) throw new Error("Not a Court Creator project.");
  if (!Array.isArray(data.logoImages)) data.logoImages = [];
  if (!data.visibility || typeof data.visibility !== "object") data.visibility = {};
  if (!data.colorOverrides || typeof data.colorOverrides !== "object") data.colorOverrides = {};
  data._projectPath = result.filePaths[0];
  return data;
});

ipcMain.handle("app:info", () => ({ version: app.getVersion() }));
ipcMain.handle("app:studio-theme", (event, name) => {
  if (event.sender !== mainWindow?.webContents) throw new Error("Theme changes require the main workspace.");
  studioTheme = name === "dark" ? "dark" : "light";
  const palette = StudioTheme.colors(studioTheme);
  mainWindow.setTitleBarOverlay({ color: palette.PanelBrush, symbolColor: palette.TextBrush, height: 46 });
  mainWindow.setBackgroundColor(palette.WindowBrush);
  return studioTheme;
});

ipcMain.handle("backend:load", async (_event, templatePath) => {
  return runPython(["load-stock"], 240000);
});

ipcMain.handle("backend:render", async (_event, request) => renderPreview(request));
ipcMain.handle("backend:experimental", async (_event, prepare) => runPython(["experimental-lines", Boolean(prepare)], 240000));
ipcMain.handle("logo-editor:open", async (_event, request) => logoEditor.open(request));

ipcMain.handle("backend:sample-color", async (_event, layerId) => runPython(["sample-color", "--layer-id", layerId]));

ipcMain.handle("backend:add-floor", async (_event, sourcePath) => runPython(["add-stock-floor", "--source", sourcePath]));

ipcMain.handle("backend:inspect-import", async (_event, sourcePath, target, selected) => runPython(["inspect-import", sourcePath, Boolean(target), selected]));
ipcMain.handle("backend:import-base-status", async () => runPython(["import-base-status"]));
async function ensureExportBase(event) {
  try {
    return await runPython(["prepare-import-base", ""], 600000, "export");
  } catch (error) {
    if (!String(error.message || error).includes("NBA 2K27 was not found")) throw error;
    const result = await dialog.showOpenDialog(BrowserWindow.fromWebContents(event.sender), {
      title: "Choose the NBA 2K27 game folder",
      properties: ["openDirectory"],
    });
    if (result.canceled) return null;
    return runPython(["prepare-import-base", result.filePaths[0]], 600000, "export");
  }
}
ipcMain.handle("backend:prepare-import-base", ensureExportBase);
ipcMain.handle("backend:preview-import", async (_event, request) => runImportRequest("preview-import", request));
ipcMain.handle("backend:export-import-png", async (_event, request) => runImportRequest("export-import-png", request, 600000));
ipcMain.handle("backend:export-import-iff", async (event, request) => {
  if (!await ensureExportBase(event)) return null;
  return runImportRequest("export-import-iff", request, 900000);
});
ipcMain.handle("backend:export-current-iff", async (event, request) => {
  if (!await ensureExportBase(event)) return null;
  return runImportRequest("export-current-iff", request, 900000);
});

ipcMain.handle("dialog:import-iff", async (event, target) => {
  const result = await dialog.showOpenDialog(BrowserWindow.fromWebContents(event.sender), {
    title: target ? "Choose a working NBA 2K27 floor IFF" : "Import an older court IFF",
    filters: [{ name: "NBA 2K IFF", extensions: ["iff"] }],
    properties: ["openFile"],
  });
  return result.canceled ? null : result.filePaths[0];
});

ipcMain.handle("dialog:export-import-png", async (event) => {
  const result = await dialog.showSaveDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Save aligned court texture",
    defaultPath: "converted-court-2k27.png",
    filters: [{ name: "PNG", extensions: ["png"] }],
  });
  return result.canceled ? null : result.filePath;
});

ipcMain.handle("dialog:export-import-iff", async (event) => {
  const result = await dialog.showSaveDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Save converted court IFF",
    defaultPath: "converted-court-2k27.iff",
    filters: [{ name: "NBA 2K IFF", extensions: ["iff"] }],
  });
  return result.canceled ? null : result.filePath;
});

ipcMain.handle("dialog:export-current-iff", async (event) => {
  const result = await dialog.showSaveDialog(BrowserWindow.fromWebContents(event.sender), {
    title: "Export NBA 2K27 court IFF",
    defaultPath: "court-export-2k27.iff",
    filters: [{ name: "NBA 2K IFF", extensions: ["iff"] }],
  });
  return result.canceled ? null : result.filePath;
});

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
