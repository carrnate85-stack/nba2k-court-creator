const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("courtCreator", {
  appInfo: () => ipcRenderer.invoke("app:info"),
  setStudioTheme: (name) => ipcRenderer.invoke("app:studio-theme", name),
  saveProject: (data) => ipcRenderer.invoke("project:save", data),
  openProject: () => ipcRenderer.invoke("project:open"),
  recovery: () => ipcRenderer.invoke("project:recovery"),
  confirmReplace: () => ipcRenderer.invoke("project:confirm-replace"),
  autosave: (data) => ipcRenderer.send("project:recover-write", data),
  load: (templatePath) => ipcRenderer.invoke("backend:load", templatePath || null),
  render: (request) => ipcRenderer.invoke("backend:render", request),
  experimental: (prepare = false) => ipcRenderer.invoke("backend:experimental", prepare),
  openLogoEditor: (request) => ipcRenderer.invoke("logo-editor:open", request),
  onLogoEditorUpdate: (callback) => {
    const listener = (_event, data) => callback(data);
    ipcRenderer.on("logo-editor:update", listener);
    return () => ipcRenderer.removeListener("logo-editor:update", listener);
  },
  sampleColor: (layerId) => ipcRenderer.invoke("backend:sample-color", layerId),
  addFloor: (sourcePath) => ipcRenderer.invoke("backend:add-floor", sourcePath),
  inspectImportIff: (sourcePath, target = false, selected = null) => ipcRenderer.invoke("backend:inspect-import", sourcePath, target, selected),
  importBaseStatus: () => ipcRenderer.invoke("backend:import-base-status"),
  prepareImportBase: () => ipcRenderer.invoke("backend:prepare-import-base"),
  previewImport: (request) => ipcRenderer.invoke("backend:preview-import", request),
  exportImportPng: (request) => ipcRenderer.invoke("backend:export-import-png", request),
  exportImportIff: (request) => ipcRenderer.invoke("backend:export-import-iff", request),
  exportCurrentIff: (request) => ipcRenderer.invoke("backend:export-current-iff", request),
  chooseImportIff: (target = false) => ipcRenderer.invoke("dialog:import-iff", target),
  chooseImportPngOutput: () => ipcRenderer.invoke("dialog:export-import-png"),
  chooseImportIffOutput: () => ipcRenderer.invoke("dialog:export-import-iff"),
  chooseCurrentIffOutput: () => ipcRenderer.invoke("dialog:export-current-iff"),
  choosePsd: () => ipcRenderer.invoke("dialog:open-psd"),
  chooseLogoImages: () => ipcRenderer.invoke("dialog:import-logo"),
  chooseFloorImage: () => ipcRenderer.invoke("dialog:add-floor"),
  chooseExportPng: () => ipcRenderer.invoke("dialog:export-png"),
  openPath: (targetPath) => ipcRenderer.invoke("shell:open-path", targetPath),
  showItem: (targetPath) => ipcRenderer.invoke("shell:show-item", targetPath),
});
