const DEFAULT_PAINT_HEX = "#19583F";

const state = {
  section: "floors",
  templatePath: "",
  previewPath: "",
  document: null,
  layers: [],
  layersById: new Map(),
  visibility: {},
  colorOverrides: {},
  templateColors: {},
  customFloorImages: [],
  teamPalettes: [],
  presets: [],
  selectedLayerId: null,
  activeHexLayerId: null,
  logos: [],
  selectedLogoId: null,
  renderToken: 0,
  renderTimer: 0,
};

const ui = {
  status: document.getElementById("status"),
  sectionTitle: document.getElementById("sectionTitle"),
  sectionSubtitle: document.getElementById("sectionSubtitle"),
  previewImage: document.getElementById("previewImage"),
  previewEmpty: document.getElementById("previewEmpty"),
  selectedText: document.getElementById("selectedText"),
  colorEditor: document.getElementById("colorEditor"),
  colorEditorHandle: document.getElementById("colorEditorHandle"),
  colorEditorTitle: document.getElementById("colorEditorTitle"),
  colorEditorNative: document.getElementById("colorEditorNative"),
  colorEditorHex: document.getElementById("colorEditorHex"),
  paletteSection: document.getElementById("paletteSection"),
  paletteSearch: document.getElementById("paletteSearch"),
  paletteHost: document.getElementById("paletteHost"),
  layersPanel: document.getElementById("layersPanel"),
  logosPanel: document.getElementById("logosPanel"),
  exportPanel: document.getElementById("exportPanel"),
  layersHost: document.getElementById("layersHost"),
  panelTools: document.querySelector("#layersPanel .panel-tools"),
  floorSearch: document.getElementById("floorSearch"),
  addFloorButton: document.getElementById("addFloorButton"),
  layerNameHeader: document.getElementById("layerNameHeader"),
  layerColorHeader: document.getElementById("layerColorHeader"),
  logoList: document.getElementById("logoList"),
  logoName: document.getElementById("logoName"),
  logoX: document.getElementById("logoX"),
  logoY: document.getElementById("logoY"),
  logoWidth: document.getElementById("logoWidth"),
  logoHeight: document.getElementById("logoHeight"),
  logoRotation: document.getElementById("logoRotation"),
  logoOpacity: document.getElementById("logoOpacity"),
  logoVisible: document.getElementById("logoVisible"),
  logoScaleLocked: document.getElementById("logoScaleLocked"),
};

function setStatus(message) {
  ui.status.textContent = message;
}

function normalizeName(value) {
  return String(value || "").toLowerCase().replace(/[_-]/g, " ").split(/\s+/).filter(Boolean).join(" ");
}

function normalizeHex(value) {
  let hex = String(value || "").trim().replace(/^#/, "");
  if (/^[0-9a-f]{3}$/i.test(hex)) {
    hex = hex.split("").map((ch) => ch + ch).join("");
  }
  if (!/^[0-9a-f]{6}$/i.test(hex)) return null;
  return `#${hex.toUpperCase()}`;
}

function hexToRgb(hex) {
  const clean = normalizeHex(hex).slice(1);
  return [0, 2, 4].map((index) => parseInt(clean.slice(index, index + 2), 16));
}

function rgbToHex(rgb) {
  return `#${rgb.slice(0, 3).map((value) => Math.max(0, Math.min(255, Number(value) || 0)).toString(16).padStart(2, "0")).join("").toUpperCase()}`;
}

function fileUrl(filePath) {
  if (!filePath) return "";
  const normalized = String(filePath).replace(/\\/g, "/");
  const encoded = normalized
    .split("/")
    .map((segment, index) => (index === 0 ? segment : encodeURIComponent(segment)))
    .join("/");
  return `file:///${encoded}`;
}

function layerParent(layer) {
  return layer?.parent_id ? state.layersById.get(layer.parent_id) || null : null;
}

function ancestors(layer) {
  const items = [];
  let parent = layerParent(layer);
  while (parent) {
    items.push(parent);
    parent = layerParent(parent);
  }
  return items;
}

function descendants(layer) {
  const items = [];
  for (const child of layer.children || []) {
    items.push(child, ...descendants(child));
  }
  return items;
}

function isGroup(layer) {
  return normalizeName(layer.kind) === "group";
}

function isCourtFloorGroup(layer) {
  return ["court floors", "court floor", "floor options", "floors"].includes(normalizeName(layer.name));
}

function isFloorTemplateCategory(layer) {
  return String(layer.id || "").toLowerCase().startsWith("floor_template_category_");
}

function courtFloorGroupFor(layer) {
  if (!layer) return null;
  if (isCourtFloorGroup(layer)) return layer;
  return ancestors(layer).find(isCourtFloorGroup) || null;
}

function isInsideCourtFloor(layer) {
  return Boolean(courtFloorGroupFor(layer));
}

function floorRootFor(layer, floorGroup) {
  if (!layer || !floorGroup || layer.id === floorGroup.id) return null;
  if (layer.isCustomFloor || layer.isTemplateFloor || isFloorTemplateCategory(layer)) return layer;
  let root = layer;
  while (root.parent_id && root.parent_id !== floorGroup.id) {
    const parent = layerParent(root);
    if (!parent) return null;
    root = parent;
  }
  return root.parent_id === floorGroup.id ? root : null;
}

function floorNumber(value) {
  const match = String(value || "").match(/#\s*(\d+)|(\d+)/);
  return match ? Number(match[1] || match[2]) : 9999;
}

function floorCategorySortKey(layer) {
  const name = normalizeName(layer.displayName);
  const order = {
    nba: 0,
    wnba: 100,
    "historic nba": 200,
    historic: 200,
    college: 300,
    "high school": 400,
    "all star events": 500,
    event: 500,
    events: 500,
    custom: 900,
    unknown: 999,
  };
  return order[name] ?? 600;
}

function courtSortKey(layer) {
  const parent = layerParent(layer);
  if (parent && isCourtFloorGroup(parent)) {
    if (isGroup(layer) || isFloorTemplateCategory(layer)) return floorCategorySortKey(layer);
    return layer.isCustomFloor ? 10000 : floorNumber(layer.displayName);
  }
  if (parent && normalizeName(parent.name) === "lines") {
    const name = normalizeName(layer.name);
    if (name === "3 point lines") return -30;
    if (name === "college three") return -20;
    if (name === "high school three") return -10;
  }
  return layer.psd_index ?? 0;
}

function friendlyFloorName(name) {
  let clean = String(name || "")
    .replace(/\s*\(\d{3}\)/g, "")
    .replace(/\s+Court\s+Wood\d+\b/gi, "")
    .replace(/\s+Wood\d+\b/gi, "")
    .replace(/\s{2,}/g, " ")
    .trim();
  return clean || name;
}

function friendlyLayerName(layer) {
  const name = normalizeName(layer.name);
  if (name === "3 point lines") return "NBA Three";
  if (name === "college three") return "College Three";
  if (name === "high school three") return "High School Three";
  return isInsideCourtFloor(layer) ? friendlyFloorName(layer.name) : layer.name;
}

function floorWoodVariant(name) {
  return String(name || "").match(/\bWood\s*(\d+)\b/i)?.[1] || null;
}

function refreshFriendlyNames() {
  for (const layer of state.layers) {
    layer.displayName = friendlyLayerName(layer);
  }
  const floorLayers = state.layers.filter((layer) => !isGroup(layer) && isInsideCourtFloor(layer));
  const groups = new Map();
  for (const layer of floorLayers) {
    const key = layer.displayName.toLowerCase();
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(layer);
  }
  for (const group of groups.values()) {
    if (group.length < 2) continue;
    group.sort((a, b) => (a.psd_index ?? 0) - (b.psd_index ?? 0));
    group.forEach((layer, index) => {
      layer.displayName = `${layer.displayName} ${floorWoodVariant(layer.name) || index + 1}`;
    });
  }
}

function rebuildLayerIndex(data) {
  state.layers = [...(data.document?.layers || []), ...(data.customFloorLayers || [])].map((layer) => ({
    ...layer,
    children: [],
    visible: Boolean(data.visibility?.[layer.id] ?? layer.visible),
    originalVisible: Boolean(data.visibility?.[layer.id] ?? layer.visible),
    activeHex: "",
    showInlineColorControls: false,
    isCustomFloor: Boolean(layer.isCustomFloor),
    isTemplateFloor: Boolean(layer.isTemplateFloor || String(layer.id || "").startsWith("floor_template_")),
  }));
  state.layersById = new Map(state.layers.map((layer) => [layer.id, layer]));
  for (const floor of data.customFloorImages || []) {
    if (floor.isTemplate && state.layersById.has(floor.id)) {
      state.layersById.get(floor.id).isTemplateFloor = true;
    }
  }
  for (const layer of state.layers) {
    const parent = layerParent(layer);
    if (parent) parent.children.push(layer);
  }
  for (const layer of state.layers) {
    layer.children.sort((a, b) => courtSortKey(a) - courtSortKey(b) || String(a.displayName || a.name).localeCompare(String(b.displayName || b.name)));
  }
  refreshFriendlyNames();
}

function isColorableLayer(layer) {
  if (!layer || isGroup(layer)) return false;
  if (normalizeName(layer.name) === "outside color") return true;
  return ancestors(layer).some((parent) => ["paint colors", "lines"].includes(normalizeName(parent.name)));
}

function isDefaultPaintColorLayer(layer) {
  return ["outside color", "paint", "secondary paint color"].includes(normalizeName(layer.name));
}

function applyDefaultPaintColors() {
  const rgb = hexToRgb(DEFAULT_PAINT_HEX);
  for (const layer of state.layers.filter(isDefaultPaintColorLayer)) {
    state.colorOverrides[layer.id] = [...rgb];
    layer.activeHex = DEFAULT_PAINT_HEX;
  }
}

function setLayerVisible(layer, visible, includeChildren = false) {
  state.visibility[layer.id] = visible;
  layer.visible = visible;
  if (!includeChildren) return;
  for (const child of descendants(layer)) {
    state.visibility[child.id] = visible;
    child.visible = visible;
  }
}

function showAncestors(layer) {
  for (const parent of ancestors(layer)) {
    state.visibility[parent.id] = true;
    parent.visible = true;
  }
}

function showOnlyCourtFloor(layer) {
  const group = courtFloorGroupFor(layer);
  const selectedRoot = floorRootFor(layer, group);
  if (!group || !selectedRoot) {
    setLayerVisible(layer, true, isGroup(layer));
    return;
  }
  if (isFloorTemplateCategory(selectedRoot)) {
    const selectedTemplate = layer.id === selectedRoot.id ? selectedRoot.children.find((child) => child.visible) || selectedRoot.children[0] : layer;
    setLayerVisible(group, true);
    showAncestors(group);
    for (const option of group.children) setLayerVisible(option, false, true);
    setLayerVisible(selectedRoot, true);
    if (selectedTemplate && selectedTemplate.id !== selectedRoot.id) {
      setLayerVisible(selectedTemplate, true, isGroup(selectedTemplate));
      showAncestors(selectedTemplate);
    } else {
      showAncestors(selectedRoot);
    }
    return;
  }
  setLayerVisible(group, true);
  showAncestors(group);
  for (const option of group.children) setLayerVisible(option, false, true);
  setLayerVisible(selectedRoot, true, isGroup(selectedRoot));
  showAncestors(selectedRoot);
}

function setLayerVisibility(layer, visible) {
  if (isInsideCourtFloor(layer) && visible) {
    showOnlyCourtFloor(layer);
  } else {
    setLayerVisible(layer, visible, isGroup(layer));
    if (visible) showAncestors(layer);
  }
  state.selectedLayerId = layer.id;
  refreshInlineColorControls();
  renderLayers();
  refreshSelectionText();
  schedulePreview();
}

function sectionRoots() {
  if (state.section === "floors") {
    const floorGroup = state.layers.find(isCourtFloorGroup);
    if (!floorGroup) return [];
    const query = ui.floorSearch.value.trim().toLowerCase();
    if (!query) return [...floorGroup.children].sort((a, b) => courtSortKey(a) - courtSortKey(b) || a.displayName.localeCompare(b.displayName));
    const words = query.split(/\s+/).filter(Boolean);
    return descendants(floorGroup)
      .filter((layer) => !isGroup(layer))
      .filter((layer) => {
        const parent = layerParent(layer);
        const haystack = `${layer.displayName} ${layer.name} ${parent?.displayName || ""} ${parent?.name || ""}`.toLowerCase();
        return words.every((word) => haystack.includes(word));
      })
      .sort((a, b) => courtSortKey(a) - courtSortKey(b) || a.displayName.localeCompare(b.displayName));
  }
  if (state.section === "paint") {
    const roots = [];
    for (const groupName of ["paint colors", "lines"]) {
      const group = state.layers.find((layer) => isGroup(layer) && normalizeName(layer.name) === groupName);
      if (group) roots.push(group);
    }
    roots.push(...state.layers.filter((layer) => !isGroup(layer) && normalizeName(layer.name) === "outside color").sort((a, b) => (a.psd_index ?? 0) - (b.psd_index ?? 0)));
    return roots;
  }
  return [];
}

function flattenedRows(roots, depth = 0) {
  const rows = [];
  for (const layer of roots) {
    rows.push({ layer, depth });
    if (isGroup(layer) && layer.children.length) {
      rows.push(...flattenedRows([...layer.children].sort((a, b) => courtSortKey(a) - courtSortKey(b) || a.displayName.localeCompare(b.displayName)), depth + 1));
    }
  }
  return rows;
}

function refreshInlineColorControls() {
  for (const layer of state.layers) {
    layer.showInlineColorControls = state.section === "paint" && layer.visible && isColorableLayer(layer);
  }
}

function renderLayers() {
  refreshInlineColorControls();
  ui.layersHost.innerHTML = "";
  const rows = flattenedRows(sectionRoots());
  for (const { layer, depth } of rows) {
    const row = document.createElement("div");
    row.className = `layer-row${isGroup(layer) ? " group" : ""}${state.selectedLayerId === layer.id ? " selected" : ""}`;
    row.dataset.id = layer.id;

    const name = document.createElement("div");
    name.className = "layer-name";
    name.textContent = `${isGroup(layer) ? "▸ " : ""}${layer.displayName}`;
    name.title = layer.displayName;
    name.style.paddingLeft = state.section === "paint" ? `${depth * 12}px` : "0";

    const visible = document.createElement("div");
    visible.className = "state";
    visible.textContent = layer.visible ? "On" : "Off";

    const colorCell = document.createElement("div");
    if (layer.showInlineColorControls) {
      colorCell.className = "color-controls";
      const activeHex = normalizeHex(layer.activeHex) || DEFAULT_PAINT_HEX;
      const colorBox = document.createElement("button");
      colorBox.type = "button";
      colorBox.className = "color-box";
      colorBox.title = `Edit ${layer.displayName} color`;
      colorBox.innerHTML = `<span class="color-box-swatch" style="background:${activeHex}"></span><span>${activeHex}</span>`;
      colorBox.addEventListener("click", (event) => {
        event.stopPropagation();
        openColorEditor(layer, false);
      });
      const teamColors = document.createElement("button");
      teamColors.type = "button";
      teamColors.className = "team-color-link";
      teamColors.textContent = "Team Colors";
      teamColors.addEventListener("click", (event) => {
        event.stopPropagation();
        openColorEditor(layer, true);
      });
      colorCell.append(colorBox, teamColors);
    }

    row.append(name, visible, colorCell);
    row.addEventListener("click", () => {
      state.selectedLayerId = layer.id;
      refreshSelectionText();
      renderLayers();
    });
    row.addEventListener("dblclick", () => setLayerVisibility(layer, !layer.visible));
    row.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      const renamed = prompt("Layer name", layer.displayName);
      if (!renamed?.trim()) return;
      layer.displayName = renamed.trim();
      renderLayers();
      refreshSelectionText();
    });
    ui.layersHost.append(row);
  }
}

async function applyHex(layer, value) {
  const normalized = normalizeHex(value);
  if (!normalized) {
    setStatus("Enter a 3 or 6 digit hex color.");
    return;
  }
  if (!isColorableLayer(layer)) return;
  state.selectedLayerId = layer.id;
  state.activeHexLayerId = layer.id;
  state.colorOverrides[layer.id] = hexToRgb(normalized);
  layer.activeHex = normalized;
  renderLayers();
  if (state.activeHexLayerId === layer.id) syncColorEditor(layer);
  refreshSelectionText();
  schedulePreview();
}

function syncColorEditor(layer) {
  if (!layer) return;
  const activeHex = normalizeHex(layer.activeHex) || DEFAULT_PAINT_HEX;
  ui.colorEditorTitle.textContent = layer.displayName;
  ui.colorEditorNative.value = activeHex;
  ui.colorEditorHex.value = activeHex;
}

function positionColorEditor() {
  if (ui.colorEditor.dataset.positioned === "true") return;
  const width = 430;
  ui.colorEditor.style.left = `${Math.min(254, Math.max(0, window.innerWidth - width))}px`;
  ui.colorEditor.style.top = `${Math.min(340, Math.max(24, window.innerHeight - 230))}px`;
  ui.colorEditor.dataset.positioned = "true";
}

function setTeamColorsExpanded(expanded) {
  ui.paletteSection.classList.toggle("hidden", !expanded);
  document.getElementById("teamColorsToggle").textContent = expanded ? "Hide Team Colors" : "Team Colors";
  if (expanded) {
    renderPalette();
    requestAnimationFrame(() => ui.paletteSearch.focus());
  }
}

function openColorEditor(layer, showTeamColors) {
  state.activeHexLayerId = layer.id;
  state.selectedLayerId = layer.id;
  syncColorEditor(layer);
  ui.colorEditor.classList.remove("hidden");
  positionColorEditor();
  setTeamColorsExpanded(showTeamColors);
  refreshSelectionText();
  renderLayers();
  if (!showTeamColors) requestAnimationFrame(() => ui.colorEditorHex.select());
}

function closeColorEditor() {
  ui.colorEditor.classList.add("hidden");
  setTeamColorsExpanded(false);
}

function applyColorEditorHex() {
  const layer = state.layersById.get(state.activeHexLayerId);
  if (layer) applyHex(layer, ui.colorEditorHex.value);
}

function makeColorEditorDraggable() {
  let drag = null;
  ui.colorEditorHandle.addEventListener("pointerdown", (event) => {
    if (event.target.closest("button")) return;
    const bounds = ui.colorEditor.getBoundingClientRect();
    drag = { x: event.clientX - bounds.left, y: event.clientY - bounds.top };
    ui.colorEditorHandle.setPointerCapture(event.pointerId);
  });
  ui.colorEditorHandle.addEventListener("pointermove", (event) => {
    if (!drag) return;
    const bounds = ui.colorEditor.getBoundingClientRect();
    const left = Math.max(0, Math.min(window.innerWidth - bounds.width, event.clientX - drag.x));
    const top = Math.max(0, Math.min(window.innerHeight - 48, event.clientY - drag.y));
    ui.colorEditor.style.left = `${left}px`;
    ui.colorEditor.style.top = `${top}px`;
  });
  const stopDragging = () => { drag = null; };
  ui.colorEditorHandle.addEventListener("pointerup", stopDragging);
  ui.colorEditorHandle.addEventListener("pointercancel", stopDragging);
}

function paletteLeagueLabel(league) {
  const normalized = normalizeName(league);
  if (["ncaa d1", "ncaa", "college"].includes(normalized)) return "College";
  if (normalized === "nba") return "NBA";
  return String(league || "").trim() || "Other";
}

function paletteLeagueSortKey(league) {
  const normalized = normalizeName(league);
  if (normalized === "nba") return 0;
  if (normalized === "college") return 1;
  return 99;
}

function paletteSearchTokens(query) {
  return String(query || "")
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .map((word) => word.replace(/^#/, ""))
    .filter(Boolean);
}

function paletteSearchText(palette, color) {
  const text = [
    palette.team,
    palette.league,
    paletteLeagueLabel(palette.league),
    color.name,
    color.hex,
    String(color.hex || "").replace(/^#/, ""),
  ]
    .join(" ")
    .toLowerCase();
  return `${text} ${text.replace(/[^a-z0-9]/g, "")}`;
}

function paletteMatches(palette, color, query) {
  if (!query) return true;
  const haystack = paletteSearchText(palette, color);
  return paletteSearchTokens(query).every((word) => haystack.includes(word) || haystack.includes(word.replace(/[^a-z0-9]/g, "")));
}

function renderPalette() {
  ui.paletteHost.innerHTML = "";
  const query = ui.paletteSearch.value.trim();
  const grouped = new Map();
  for (const palette of state.teamPalettes) {
    const colors = (palette.colors || []).filter((color) => paletteMatches(palette, color, query));
    if (!colors.length) continue;
    const league = paletteLeagueLabel(palette.league);
    if (!grouped.has(league)) grouped.set(league, []);
    grouped.get(league).push({ palette, colors });
  }
  for (const [league, items] of [...grouped.entries()].sort((a, b) => paletteLeagueSortKey(a[0]) - paletteLeagueSortKey(b[0]) || a[0].localeCompare(b[0]))) {
    const title = document.createElement("div");
    title.className = "league-title";
    title.textContent = league;
    ui.paletteHost.append(title);
    for (const item of items.sort((a, b) => a.palette.team.localeCompare(b.palette.team))) {
      const details = document.createElement("details");
      details.className = "team-palette";
      details.open = Boolean(query);
      const summary = document.createElement("summary");
      summary.textContent = query ? `${item.palette.team}  ${item.colors.length}/${item.palette.colors.length}` : `${item.palette.team}  ${item.palette.colors.length} colors`;
      const swatches = document.createElement("div");
      swatches.className = "swatches";
      for (const color of item.colors) {
        const button = document.createElement("button");
        button.className = "swatch-button";
        button.title = `${color.name} ${color.hex}`;
        button.innerHTML = `<span class="swatch" style="background:${color.hex}"></span><span>${color.hex}</span>`;
        button.addEventListener("click", () => {
          const target = state.layersById.get(state.activeHexLayerId) || state.layersById.get(state.selectedLayerId);
          if (target) {
            applyHex(target, color.hex);
            closeColorEditor();
          }
        });
        swatches.append(button);
      }
      details.append(summary, swatches);
      ui.paletteHost.append(details);
    }
  }
  if (!ui.paletteHost.children.length) {
    const empty = document.createElement("div");
    empty.className = "palette-empty";
    empty.textContent = "No team colors found.";
    ui.paletteHost.append(empty);
  }
}

function refreshSelectionText() {
  if (state.section === "logos") {
    const logo = selectedLogo();
    ui.selectedText.textContent = logo ? `Logo: ${logo.name}` : "Logo: No logo selected.";
    return;
  }
  const layer = state.layersById.get(state.selectedLayerId);
  ui.selectedText.textContent = layer ? `Court: ${layer.displayName}` : "Court: No court selected.";
}

function renderSection() {
  const copy = {
    floors: ["Court Floors", "Choose one court floor at a time, including NBA 2K26 templates."],
    paint: ["Paint & Lines", "Choose paint and line layers, then apply exact colors or team palette swatches."],
    logos: ["Logos", "Import logo images, then place them on the court preview."],
    export: ["Export", "Refresh, save, and export the current court preview."],
  };
  document.body.dataset.section = state.section;
  ui.sectionTitle.textContent = copy[state.section][0];
  ui.sectionSubtitle.textContent = copy[state.section][1];
  if (state.section !== "paint") closeColorEditor();
  ui.layersPanel.classList.toggle("hidden", !["floors", "paint"].includes(state.section));
  ui.logosPanel.classList.toggle("hidden", state.section !== "logos");
  ui.exportPanel.classList.toggle("hidden", state.section !== "export");
  ui.panelTools.classList.toggle("hidden", state.section !== "floors");
  ui.floorSearch.classList.toggle("hidden", state.section !== "floors");
  ui.addFloorButton.classList.toggle("hidden", state.section !== "floors");
  ui.layerNameHeader.textContent = state.section === "paint" ? "Layer / section" : "Court";
  ui.layerColorHeader.classList.toggle("hidden", state.section !== "paint");
  document.querySelectorAll(".nav").forEach((button) => button.classList.toggle("active", button.dataset.section === state.section));
  renderLayers();
  renderPalette();
  renderLogos();
  refreshSelectionText();
}

function renderRequest(outputPath = null) {
  return {
    templatePath: state.templatePath,
    visibility: state.visibility,
    colorOverrides: state.colorOverrides,
    outputPath,
    customFloorImages: state.customFloorImages.map((image) => ({
      id: image.id,
      name: image.name,
      path: image.path,
      bbox: image.bbox,
      visible: Boolean(state.visibility[image.id]),
    })),
    logoImages: state.logos.map((logo) => ({ ...logo })),
  };
}

async function refreshPreview(outputPath = null) {
  if (!state.templatePath) return;
  const token = ++state.renderToken;
  setStatus("Refreshing preview...");
  try {
    const response = await window.courtCreator.render(renderRequest(outputPath));
    if (token !== state.renderToken) return;
    state.previewPath = response.previewPath || state.previewPath;
    ui.previewImage.src = `${fileUrl(state.previewPath)}?v=${Date.now()}`;
    ui.previewEmpty.classList.add("hidden");
    setStatus(outputPath ? "PNG exported." : "Preview refreshed.");
  } catch (error) {
    setStatus(`Preview failed: ${error.message}`);
  }
}

function schedulePreview() {
  clearTimeout(state.renderTimer);
  state.renderTimer = setTimeout(() => refreshPreview(), 160);
}

function selectedLogo() {
  return state.logos.find((logo) => logo.id === state.selectedLogoId) || null;
}

function logoPreviewSrc(logo) {
  return fileUrl(logo.path);
}

function renderLogos() {
  ui.logoList.innerHTML = "";
  if (!state.logos.length) {
    ui.logoList.innerHTML = `<div class="logo-row"><span></span><span>No logos imported.</span></div>`;
  }
  for (const logo of state.logos) {
    const row = document.createElement("div");
    row.className = `logo-row${logo.id === state.selectedLogoId ? " selected" : ""}`;
    row.innerHTML = `<img src="${logoPreviewSrc(logo)}" alt=""><span>${logo.name}</span>`;
    row.addEventListener("click", () => {
      state.selectedLogoId = logo.id;
      renderLogos();
      refreshSelectionText();
    });
    row.addEventListener("dblclick", () => {
      const renamed = prompt("Logo name", logo.name);
      if (!renamed?.trim()) return;
      logo.name = renamed.trim();
      renderLogos();
      refreshSelectionText();
      schedulePreview();
    });
    ui.logoList.append(row);
  }
  const logo = selectedLogo();
  const disabled = !logo;
  for (const input of [ui.logoName, ui.logoX, ui.logoY, ui.logoWidth, ui.logoHeight, ui.logoRotation, ui.logoOpacity, ui.logoVisible, ui.logoScaleLocked]) {
    input.disabled = disabled;
  }
  if (!logo) {
    ui.logoName.value = "";
    return;
  }
  ui.logoName.value = logo.name;
  ui.logoX.value = Math.round(logo.x);
  ui.logoY.value = Math.round(logo.y);
  ui.logoWidth.value = Math.round(logo.width);
  ui.logoHeight.value = Math.round(logo.height);
  ui.logoRotation.value = Math.round(logo.rotation);
  ui.logoOpacity.value = Math.round(logo.opacity);
  ui.logoVisible.checked = logo.visible;
  ui.logoScaleLocked.checked = logo.scaleLocked;
}

function updateSelectedLogo() {
  const logo = selectedLogo();
  if (!logo) return;
  const oldWidth = logo.width;
  const oldHeight = logo.height;
  logo.name = ui.logoName.value.trim() || "Logo";
  logo.x = Number(ui.logoX.value) || 0;
  logo.y = Number(ui.logoY.value) || 0;
  logo.width = Math.max(1, Number(ui.logoWidth.value) || 1);
  logo.height = Math.max(1, Number(ui.logoHeight.value) || 1);
  if (logo.scaleLocked) {
    if (document.activeElement === ui.logoWidth && oldWidth > 0) logo.height = Math.max(1, Math.round((logo.width / oldWidth) * oldHeight));
    if (document.activeElement === ui.logoHeight && oldHeight > 0) logo.width = Math.max(1, Math.round((logo.height / oldHeight) * oldWidth));
  }
  logo.rotation = Number(ui.logoRotation.value) || 0;
  logo.opacity = Math.max(0, Math.min(100, Number(ui.logoOpacity.value) || 0));
  logo.visible = ui.logoVisible.checked;
  logo.scaleLocked = ui.logoScaleLocked.checked;
  renderLogos();
  refreshSelectionText();
  schedulePreview();
}

function duplicateLogo(axis) {
  const logo = selectedLogo();
  if (!logo) return;
  const copy = { ...logo, id: crypto.randomUUID(), name: `${logo.name} Copy` };
  if (axis === "x") copy.y = state.document.height - logo.y - logo.height;
  if (axis === "y") copy.x = state.document.width - logo.x - logo.width;
  state.logos.push(copy);
  state.selectedLogoId = copy.id;
  renderLogos();
  refreshSelectionText();
  schedulePreview();
}

async function importLogos() {
  const paths = await window.courtCreator.chooseLogoImages();
  if (!paths.length) return;
  for (const logoPath of paths) {
    const name = logoPath.split(/[\\/]/).pop().replace(/\.[^.]+$/, "");
    state.logos.push({
      id: crypto.randomUUID(),
      name,
      path: logoPath,
      visible: true,
      x: 840,
      y: 430,
      width: 320,
      height: 160,
      rotation: 0,
      opacity: 100,
      flipX: false,
      flipY: false,
      scaleLocked: true,
    });
  }
  state.selectedLogoId = state.logos[state.logos.length - 1].id;
  renderLogos();
  refreshSelectionText();
  schedulePreview();
}

async function addCustomFloor() {
  const source = await window.courtCreator.chooseFloorImage();
  if (!source) return;
  try {
    setStatus("Adding custom floor...");
    const response = await window.courtCreator.addFloor(source);
    const layer = {
      ...response.layer,
      children: [],
      visible: false,
      originalVisible: false,
      activeHex: "",
      showInlineColorControls: false,
      isCustomFloor: true,
      isTemplateFloor: false,
    };
    state.layers.push(layer);
    state.layersById.set(layer.id, layer);
    state.visibility[layer.id] = false;
    state.customFloorImages.push(response.image);
    const parent = layerParent(layer);
    if (parent) {
      parent.children.push(layer);
      parent.children.sort((a, b) => courtSortKey(a) - courtSortKey(b) || String(a.displayName || a.name).localeCompare(String(b.displayName || b.name)));
    }
    refreshFriendlyNames();
    renderSection();
    setStatus("Custom floor added.");
  } catch (error) {
    setStatus(`Custom floor failed: ${error.message}`);
  }
}

function applyPresetLayout(preset, includeLogos) {
  state.visibility = {};
  for (const layer of state.layers) {
    const visible = Boolean(preset?.visibility?.[layer.id] ?? layer.visible);
    layer.visible = visible;
    state.visibility[layer.id] = visible;
  }
  state.colorOverrides = {};
  for (const [id, rgb] of Object.entries(preset?.color_overrides || preset?.colorOverrides || {})) {
    state.colorOverrides[id] = rgb;
    const layer = state.layersById.get(id);
    if (layer) layer.activeHex = rgbToHex(rgb);
  }
  for (const layer of state.layers) {
    if (!state.colorOverrides[layer.id]) layer.activeHex = state.templateColors[layer.id] || "";
  }
  if (includeLogos && preset?.logos?.length) {
    state.logos = preset.logos.map((logo) => ({ ...logo, id: logo.id || crypto.randomUUID() }));
    state.selectedLogoId = state.logos[0]?.id || null;
  }
  state.selectedLayerId = preset?.selected_layer_id || preset?.selectedLayerId || state.selectedLayerId;
}

function nbaPreset() {
  return state.presets.find((preset) => preset?.name?.toLowerCase() === "nba") || null;
}

function resetToDefault() {
  const preset = nbaPreset();
  if (preset) {
    applyPresetLayout(preset, false);
  } else {
    state.colorOverrides = {};
    for (const layer of state.layers) {
      layer.activeHex = state.templateColors[layer.id] || "";
      layer.visible = layer.isCustomFloor ? false : Boolean(layer.originalVisible);
      state.visibility[layer.id] = layer.visible;
    }
  }
  applyDefaultPaintColors();
  state.logos = [];
  state.selectedLogoId = null;
  selectCurrentCourtFloor();
  renderSection();
  schedulePreview();
  setStatus("New NBA court started.");
}

function selectCurrentCourtFloor() {
  const floorGroup = state.layers.find(isCourtFloorGroup);
  if (!floorGroup) {
    state.selectedLayerId = null;
    return;
  }
  const selected = descendants(floorGroup)
    .filter((layer) => !isGroup(layer) && layer.visible)
    .sort((a, b) => courtSortKey(a) - courtSortKey(b) || a.displayName.localeCompare(b.displayName))[0];
  state.selectedLayerId = selected?.id || null;
}

async function loadWorkspace(templatePath = null) {
  try {
    setStatus("Loading court template...");
    const data = await window.courtCreator.load(templatePath);
    state.templatePath = data.templatePath;
    state.previewPath = data.previewPath;
    state.document = data.document;
    state.visibility = { ...(data.visibility || {}) };
    state.customFloorImages = data.customFloorImages || [];
    state.teamPalettes = data.teamPalettes || [];
    state.presets = data.presets || [];
    state.colorOverrides = {};
    state.templateColors = {};
    state.logos = [];
    state.selectedLogoId = null;
    rebuildLayerIndex(data);
    for (const layer of state.layers) layer.visible = Boolean(state.visibility[layer.id] ?? layer.visible);
    const preset = nbaPreset();
    if (preset) applyPresetLayout(preset, true);
    applyDefaultPaintColors();
    selectCurrentCourtFloor();
    renderSection();
    await refreshPreview();
    setStatus(preset ? "NBA preset loaded." : "Court workspace ready.");
  } catch (error) {
    setStatus(`Startup failed: ${error.message}`);
  }
}

async function exportPng() {
  const target = await window.courtCreator.chooseExportPng();
  if (!target) return;
  await refreshPreview(target);
  await window.courtCreator.showItem(target);
}

function wireEvents() {
  document.querySelectorAll(".nav").forEach((button) => {
    button.addEventListener("click", () => {
      state.section = button.dataset.section;
      renderSection();
    });
  });
  ui.floorSearch.addEventListener("input", renderLayers);
  ui.paletteSearch.addEventListener("input", renderPalette);
  document.getElementById("colorEditorClose").addEventListener("click", closeColorEditor);
  document.getElementById("colorEditorApply").addEventListener("click", applyColorEditorHex);
  document.getElementById("teamColorsToggle").addEventListener("click", () => {
    setTeamColorsExpanded(ui.paletteSection.classList.contains("hidden"));
  });
  ui.colorEditorNative.addEventListener("input", () => {
    ui.colorEditorHex.value = normalizeHex(ui.colorEditorNative.value) || DEFAULT_PAINT_HEX;
  });
  ui.colorEditorNative.addEventListener("change", applyColorEditorHex);
  ui.colorEditorHex.addEventListener("keydown", (event) => {
    if (event.key === "Enter") applyColorEditorHex();
    if (event.key === "Escape") closeColorEditor();
  });
  ui.colorEditorHex.addEventListener("change", applyColorEditorHex);
  ui.colorEditorHex.addEventListener("paste", () => {
    requestAnimationFrame(() => {
      if (normalizeHex(ui.colorEditorHex.value)) applyColorEditorHex();
    });
  });
  makeColorEditorDraggable();
  document.getElementById("newButton").addEventListener("click", resetToDefault);
  document.getElementById("refreshButton").addEventListener("click", () => refreshPreview());
  document.getElementById("exportButton").addEventListener("click", exportPng);
  document.getElementById("exportPanelButton").addEventListener("click", exportPng);
  document.getElementById("openPsdButton").addEventListener("click", () => window.courtCreator.openPath(state.templatePath));
  document.getElementById("addFloorButton").addEventListener("click", addCustomFloor);
  document.getElementById("openButton").addEventListener("click", async () => {
    const selected = await window.courtCreator.choosePsd();
    if (selected) loadWorkspace(selected);
  });
  document.getElementById("importLogoButton").addEventListener("click", importLogos);
  document.getElementById("removeLogoButton").addEventListener("click", () => {
    const logo = selectedLogo();
    if (!logo) return;
    state.logos = state.logos.filter((item) => item.id !== logo.id);
    state.selectedLogoId = state.logos[0]?.id || null;
    renderLogos();
    refreshSelectionText();
    schedulePreview();
  });
  document.getElementById("duplicateLogoXButton").addEventListener("click", () => duplicateLogo("x"));
  document.getElementById("duplicateLogoYButton").addEventListener("click", () => duplicateLogo("y"));
  for (const input of [ui.logoName, ui.logoX, ui.logoY, ui.logoWidth, ui.logoHeight, ui.logoRotation, ui.logoOpacity, ui.logoVisible, ui.logoScaleLocked]) {
    input.addEventListener("input", updateSelectedLogo);
    input.addEventListener("change", updateSelectedLogo);
  }
}

wireEvents();
loadWorkspace();
