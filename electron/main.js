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
function runPython(args) {
  return new Promise((resolve, reject) => {
    if (!worker) {
      const command = pythonCommand();
      const child = spawn(command.exe, [...command.prefix, "-u", "-m", "court_creator.service"], {
      cwd: projectRoot,
      windowsHide: true,
    });
      worker = child;
      let stdout = "";
      const fail = (error) => {
        if (worker === child) worker = null;
        for (const pending of requests.values()) pending.reject(error);
        requests.clear();
      };
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
      let end;
      while ((end = stdout.indexOf("\n")) >= 0) {
        const line = stdout.slice(0, end);
        stdout = stdout.slice(end + 1);
        try {
          const message = JSON.parse(line);
          const pending = requests.get(message.id);
          requests.delete(message.id);
          if (pending) message.error ? pending.reject(new Error(message.error)) : pending.resolve(message.result);
        } catch (error) { fail(error); }
      }
    });
      child.stderr.on("data", () => {});
      child.on("error", fail);
      child.on("close", () => fail(new Error("Rendering engine stopped; try again.")));
    }
    const id = ++sequence;
    requests.set(id, { resolve, reject });
    worker.stdin.write(JSON.stringify({ id, args }) + "\n");
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
function atomicJson(target, data) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const temporary = target + ".tmp";
  fs.writeFileSync(temporary, JSON.stringify(data, null, 2));
  fs.renameSync(temporary, target);
}
  ipcMain.on("project:recover-write", (_event, data) => {
  try { atomicJson(recoveryPath(), data); } catch (error) { console.error(error); }
});
ipcMain.handle("project:recovery", () => {
  try { return JSON.parse(fs.readFileSync(recoveryPath(), "utf8")); } catch { return null; }
});
ipcMain.handle("project:confirm-replace", async () => {
  const result = await dialog.showMessageBox(mainWindow, { type: "question", buttons: ["Keep Editing", "Continue"], defaultId: 0, cancelId: 0, message: "Replace the current court?", detail: "Save Project first to keep an editable copy. The current court will also be backed up for recovery." });
  if (result.response !== 1) return false;
  if (fs.existsSync(recoveryPath())) fs.copyFileSync(recoveryPath(), path.join(app.getPath("userData"), `recovery-${Date.now()}.json`));
  return true;
});
ipcMain.handle("project:save", async (_event, data) => {
  const result = await dialog.showSaveDialog(mainWindow, { defaultPath: "My Court.court.json", filters: [{ name: "Court project", extensions: ["json"] }] });
  if (result.canceled) return null;
  atomicJson(result.filePath, data);
  return result.filePath;
});
ipcMain.handle("project:open", async () => {
  const result = await dialog.showOpenDialog(mainWindow, { properties: ["openFile"], filters: [{ name: "Court project", extensions: ["json"] }] });
  if (result.canceled) return null;
  const data = JSON.parse(fs.readFileSync(result.filePaths[0], "utf8"));
  if (data.version !== 1 || !data.templatePath || !Array.isArray(data.logoImages)) throw new Error("Not a Court Creator project.");
  return data;
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
