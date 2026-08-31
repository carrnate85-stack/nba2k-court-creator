const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("courtCreator", {
  load: (templatePath) => ipcRenderer.invoke("backend:load", templatePath || null),
  render: (request) => ipcRenderer.invoke("backend:render", request),
  sampleColor: (layerId) => ipcRenderer.invoke("backend:sample-color", layerId),
  addFloor: (sourcePath) => ipcRenderer.invoke("backend:add-floor", sourcePath),
  choosePsd: () => ipcRenderer.invoke("dialog:open-psd"),
  chooseLogoImages: () => ipcRenderer.invoke("dialog:import-logo"),
  chooseFloorImage: () => ipcRenderer.invoke("dialog:add-floor"),
  chooseExportPng: () => ipcRenderer.invoke("dialog:export-png"),
  openPath: (targetPath) => ipcRenderer.invoke("shell:open-path", targetPath),
  showItem: (targetPath) => ipcRenderer.invoke("shell:show-item", targetPath),
});
