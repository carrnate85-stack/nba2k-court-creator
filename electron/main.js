const { app, BrowserWindow, dialog, ipcMain, shell } = require("electron");
const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const projectRoot = path.resolve(__dirname, "..");
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
  const bundled = bundledPython();
  if (fs.existsSync(bundled)) return { exe: bundled, prefix: [] };
  return { exe: "py", prefix: ["-3"] };
}

function runPython(args) {
  return new Promise((resolve, reject) => {
    const command = pythonCommand();
    const child = spawn(command.exe, [...command.prefix, "-m", "court_creator.backend", ...args], {
      cwd: projectRoot,
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("error", reject);
    child.on("close", (code) => {
      let parsed = null;
      try {
        parsed = JSON.parse(stdout);
      } catch {
        parsed = null;
      }
      if (code !== 0) {
        reject(new Error(parsed?.error || stderr.trim() || "The Python engine failed."));
        return;
      }
      if (!parsed) {
        reject(new Error("The Python engine returned an unreadable response."));
        return;
      }
      resolve(parsed);
    });
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
  window.webContents.session.clearCache().finally(() => {
    window.loadFile(path.join(__dirname, "renderer", "index.html"));
  });
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
    app.on("activate", () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });
}

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

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
