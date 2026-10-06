const DEFAULT_PAINT_HEX = "#19583F";

function updateStudioTheme(name, persist = false) {
  name = window.StudioTheme.apply(name, persist);
  const button = document.getElementById("themeButton");
  const label = `Switch to ${name === "dark" ? "light" : "dark"} theme`;
  button.title = label;
  button.setAttribute("aria-label", label);
  button.innerHTML = window.iconMarkup(name === "dark" ? "sun" : "moon", 18);
  window.courtCreator.setStudioTheme?.(name).catch(error => console.error("Window theme update failed", error));
}

document.getElementById("themeButton").addEventListener("click", () => {
  updateStudioTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark", true);
});
updateStudioTheme(document.documentElement.dataset.theme);

function storedJson(key, fallback) {
  try {
    const value = JSON.parse(localStorage.getItem(key));
    return value ?? fallback;
  } catch {
    return fallback;
  }
}

const state = {
  section: "floors",
  buildMode: "game-uv",
  geometry: null,
  templatePath: "",
  previewPath: "",
  document: null,
  layers: [],
  layersById: new Map(),
  floorImagesById: new Map(),
  visibility: {},
  colorOverrides: {},
  templateColors: {},
  customFloorImages: [],
  teamPalettes: [],
  presets: [],
  floorLibraryName: "NBA 2K27 Courts",
  floorLibraryCount: 0,
  appVersion: "",
  projectPath: null,
  selectedLayerId: null,
  activeHexLayerId: null,
  collapsedLayerGroups: new Set(),
  logos: [],
  selectedLogoId: null,
  floorFilter: localStorage.getItem("courtCreator.floorFilter") || "nba",
  floorSort: localStorage.getItem("courtCreator.floorSort") || "name",
  floorView: localStorage.getItem("courtCreator.floorView") || "grid",
  favoriteFloorIds: new Set(storedJson("courtCreator.favoriteFloors", [])),
  recentFloorIds: storedJson("courtCreator.recentFloors", []),
  paintTab: "layers",
  previewView: "full",
  previewZoom: 100,
  renderToken: 0,
  renderTimer: 0,
};

const importState = {
  source: null,
  target: null,
  bounds: null,
  autoBounds: null,
  previewToken: 0,
  previewTimer: 0,
  previewBusy: false,
  previewPending: false,
  exporting: false,
  preparing: false,
};

const experimentalState = {
  geometry: null, floors: [], image: null, loading: false, loaded: false,
  floorToken: 0, drawFrame: 0,
  settings: storedJson("courtCreator.experimental", { floorId: null, outsideColor: "#19583F", lineSettings: {}, paintSettings: {}, mappingMode: "game-uv" }),
};

const nativePreview = { image: null, imagePath: "", logoImages: new Map(), token: 0 };

const ui = {
  status: document.getElementById("status"),
  sectionTitle: document.getElementById("sectionTitle"),
  sectionSubtitle: document.getElementById("sectionSubtitle"),
  previewImage: document.getElementById("previewImage"),
  previewCard: document.getElementById("previewCard"),
  previewStage: document.getElementById("previewStage"),
  previewShell: document.getElementById("previewShell"),
  previewEmpty: document.getElementById("previewEmpty"),
  selectedText: document.getElementById("selectedText"),
  selectedLabel: document.getElementById("selectedLabel"),
  selectedCategory: document.getElementById("selectedCategory"),
  selectedFloorImage: document.getElementById("selectedFloorImage"),
  selectedFloorFallback: document.getElementById("selectedFloorFallback"),
  currentCourtButton: document.getElementById("currentCourtButton"),
  currentCourtName: document.getElementById("currentCourtName"),
  previewZoomLabel: document.getElementById("previewZoomLabel"),
  colorEditor: document.getElementById("colorEditor"),
  colorEditorHandle: document.getElementById("colorEditorHandle"),
  colorEditorTitle: document.getElementById("colorEditorTitle"),
  colorEditorNative: document.getElementById("colorEditorNative"),
  colorEditorHex: document.getElementById("colorEditorHex"),
  paletteSection: document.getElementById("paletteSection"),
  paletteSearch: document.getElementById("paletteSearch"),
  paletteHost: document.getElementById("paletteHost"),
  layersPanel: document.getElementById("layersPanel"),
  floorBrowser: document.getElementById("floorBrowser"),
  floorCatalog: document.getElementById("floorCatalog"),
  browseFloorButton: document.getElementById("browseFloorButton"),
  paintBrowser: document.getElementById("paintBrowser"),
  logosPanel: document.getElementById("logosPanel"),
  exportPanel: document.getElementById("exportPanel"),
  importPanel: document.getElementById("importPanel"),
  experimentalPanel: document.getElementById("experimentalPanel"),
  experimentalCanvas: document.getElementById("experimentalCanvas"),
  experimentalFloorSearch: document.getElementById("experimentalFloorSearch"),
  experimentalFloor: document.getElementById("experimentalFloor"),
  experimentalMapping: document.getElementById("experimentalMapping"),
  experimentalLines: document.getElementById("experimentalLines"),
  experimentalStatus: document.getElementById("experimentalStatus"),
  experimentalPrepare: document.getElementById("experimentalPrepare"),
  experimentalOutside: document.getElementById("experimentalOutside"),
  experimentalOutsideHex: document.getElementById("experimentalOutsideHex"),
  importPreviewStage: document.getElementById("importPreviewStage"),
  importPreviewImage: document.getElementById("importPreviewImage"),
  importPreviewEmpty: document.getElementById("importPreviewEmpty"),
  importSourceName: document.getElementById("importSourceName"),
  importTexture: document.getElementById("importTexture"),
  importTextureInfo: document.getElementById("importTextureInfo"),
  importTargetName: document.getElementById("importTargetName"),
  importPrepareBase: document.getElementById("importPrepareBase"),
  importStatus: document.getElementById("importStatus"),
  importAutoEdges: document.getElementById("importAutoEdges"),
  importExportPng: document.getElementById("importExportPng"),
  importExportIff: document.getElementById("importExportIff"),
  importEdges: ["Left", "Top", "Right", "Bottom"].map((edge) => document.getElementById(`importEdge${edge}`)),
  layersHost: document.getElementById("layersHost"),
  floorSearch: document.getElementById("floorSearch"),
  layerSearch: document.getElementById("layerSearch"),
  addFloorButton: document.getElementById("addFloorButton"),
  floorFilters: document.getElementById("floorFilters"),
  floorSort: document.getElementById("floorSort"),
  floorGallery: document.getElementById("floorGallery"),
  floorGridButton: document.getElementById("floorGridButton"),
  floorListButton: document.getElementById("floorListButton"),
  paintLayersView: document.getElementById("paintLayersView"),
  paintColorsView: document.getElementById("paintColorsView"),
  paintPaletteSearch: document.getElementById("paintPaletteSearch"),
  paintPaletteHost: document.getElementById("paintPaletteHost"),
  paintPaletteTarget: document.getElementById("paintPaletteTarget"),
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
  projectTitle: document.getElementById("projectTitle"),
  versionLabel: document.getElementById("versionLabel"),
  removeLogoButton: document.getElementById("removeLogoButton"),
  duplicateLogoXButton: document.getElementById("duplicateLogoXButton"),
  duplicateLogoYButton: document.getElementById("duplicateLogoYButton"),
};

function setStatus(message) {
  ui.status.textContent = message;
}

function updateAppChrome() {
  const fileName = String(state.projectPath || "").replace(/\\/g, "/").split("/").pop();
  ui.projectTitle.textContent = fileName
    ? fileName.replace(/\.court\.json$/i, "").replace(/\.json$/i, "")
    : "Untitled court";
  const library = String(state.floorLibraryName || "NBA 2K courts").replace(/\s+Courts?$/i, "");
  ui.versionLabel.textContent = state.appVersion
    ? `Version ${state.appVersion} | ${library}`
    : `Loading ${library}...`;
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

function isGameFloorImage(image) {
  return Boolean(image?.isTemplate) || /^nba2k\d+-/i.test(String(image?.id || ""));
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
    if (name === "3 point lines" || name === "nba three") return -30;
    if (name === "college three") return -20;
    if (name === "high school three") return -10;
  }
  return layer.psd_index ?? 0;
}

const FLOOR_FILTERS = [
  ["all", "All"],
  ["nba", "NBA"],
  ["wnba", "WNBA"],
  ["historic", "Historic"],
  ["events", "Events"],
  ["other", "Other"],
  ["custom", "Custom"],
  ["favorites", "Favorites"],
  ["recent", "Recent"],
];

function selectedFloorLayer() {
  const floorGroup = state.layers.find(isCourtFloorGroup);
  if (!floorGroup) return null;
  return descendants(floorGroup)
    .filter((layer) => !isGroup(layer) && layer.visible)
    .sort((a, b) => courtSortKey(a) - courtSortKey(b) || a.displayName.localeCompare(b.displayName))[0] || null;
}

function floorImageFor(layer) {
  return layer ? state.floorImagesById.get(layer.id) || null : null;
}

function floorCategoryFor(layer) {
  if (!layer) return "NBA 2K27";
  if (layer.isCustomFloor && !layer.isTemplateFloor) return "Custom";
  const image = floorImageFor(layer);
  if (image?.category) return image.category;
  const parent = layerParent(layer);
  return isFloorTemplateCategory(parent) ? parent.displayName : "Court Floor";
}

function floorFilterMatches(layer, filter) {
  const category = normalizeName(floorCategoryFor(layer));
  if (filter === "all") return true;
  if (filter === "custom") return layer.isCustomFloor && !layer.isTemplateFloor;
  if (filter === "favorites") return state.favoriteFloorIds.has(layer.id);
  if (filter === "recent") return state.recentFloorIds.includes(layer.id);
  if (filter === "nba") return ["nba", "city edition", "statement edition"].includes(category);
  if (filter === "wnba") return category === "wnba";
  if (filter === "historic") return category.includes("historic");
  if (filter === "events") return category.includes("event") || category.includes("all star") || category.includes("mode");
  if (filter === "other") {
    return !["nba", "city edition", "statement edition", "wnba"].includes(category)
      && !category.includes("historic")
      && !category.includes("event")
      && !category.includes("all star")
      && !category.includes("mode")
      && !(layer.isCustomFloor && !layer.isTemplateFloor);
  }
  return true;
}

function floorItemsForBrowser() {
  const floorGroup = state.layers.find(isCourtFloorGroup);
  if (!floorGroup) return [];
  const words = ui.floorSearch.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const recentRank = new Map(state.recentFloorIds.map((id, index) => [id, index]));
  const items = descendants(floorGroup)
    .filter((layer) => !isGroup(layer))
    .filter((layer) => floorFilterMatches(layer, state.floorFilter))
    .filter((layer) => {
      const haystack = `${layer.displayName} ${layer.name} ${floorCategoryFor(layer)}`.toLowerCase();
      return words.every((word) => haystack.includes(word));
    });
  items.sort((left, right) => {
    if (state.floorSort === "number") return floorNumber(left.name) - floorNumber(right.name) || left.displayName.localeCompare(right.displayName);
    if (state.floorSort === "category") return floorCategoryFor(left).localeCompare(floorCategoryFor(right)) || left.displayName.localeCompare(right.displayName);
    if (state.floorSort === "recent") return (recentRank.get(left.id) ?? 9999) - (recentRank.get(right.id) ?? 9999) || left.displayName.localeCompare(right.displayName);
    return left.displayName.localeCompare(right.displayName, undefined, { numeric: true });
  });
  return items;
}

function rememberRecentFloor(layer) {
  state.recentFloorIds = [layer.id, ...state.recentFloorIds.filter((id) => id !== layer.id)].slice(0, 24);
  localStorage.setItem("courtCreator.recentFloors", JSON.stringify(state.recentFloorIds));
}

function selectFloor(layer) {
  const previousSelection = state.section === "floors" ? null : state.selectedLayerId;
  rememberRecentFloor(layer);
  setLayerVisibility(layer, true);
  if (previousSelection && !isInsideCourtFloor(state.layersById.get(previousSelection))) {
    state.selectedLayerId = previousSelection;
    if (state.section === "paint") renderLayers();
  }
  if (state.floorFilter === "recent" || state.floorSort === "recent") renderFloorGallery();
  if (ui.floorCatalog.open) ui.floorCatalog.close();
}

function openFloorCatalog() {
  if ((!state.geometry && !state.templatePath) || ui.floorCatalog.open) return;
  closeColorEditor();
  ui.floorSort.value = state.floorSort;
  renderFloorFilters();
  renderFloorGallery();
  ui.floorCatalog.showModal();
  requestAnimationFrame(() => ui.floorSearch.focus());
}

function renderFloorFilters() {
  ui.floorFilters.innerHTML = "";
  for (const [id, label] of FLOOR_FILTERS) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `floor-filter${state.floorFilter === id ? " active" : ""}`;
    button.textContent = label;
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", String(state.floorFilter === id));
    button.addEventListener("click", () => {
      state.floorFilter = id;
      localStorage.setItem("courtCreator.floorFilter", id);
      renderFloorFilters();
      renderFloorGallery(false);
    });
    ui.floorFilters.append(button);
  }
}

function setFloorView(view) {
  state.floorView = view === "list" ? "list" : "grid";
  localStorage.setItem("courtCreator.floorView", state.floorView);
  ui.floorGallery.classList.toggle("list-view", state.floorView === "list");
  ui.floorGridButton.classList.toggle("active", state.floorView === "grid");
  ui.floorListButton.classList.toggle("active", state.floorView === "list");
}

function renderFloorGallery(preserveScroll = true) {
  const previousScroll = preserveScroll ? ui.floorGallery.scrollTop : 0;
  ui.floorGallery.innerHTML = "";
  setFloorView(state.floorView);
  const items = floorItemsForBrowser();
  if (!items.length) {
    const empty = document.createElement("div");
    empty.className = "floor-gallery-empty";
    empty.textContent = state.floorFilter === "favorites" ? "No favorite courts yet." : "No courts match this view.";
    ui.floorGallery.append(empty);
    return;
  }
  for (const layer of items) {
    const image = floorImageFor(layer);
    const card = document.createElement("div");
    card.className = `floor-card${layer.visible ? " selected" : ""}`;
    card.dataset.id = layer.id;
    card.tabIndex = 0;
    card.setAttribute("role", "option");
    card.setAttribute("aria-selected", String(layer.visible));

    const thumbnail = document.createElement("img");
    thumbnail.className = "floor-card-image";
    thumbnail.alt = "";
    thumbnail.loading = "lazy";
    thumbnail.decoding = "async";
    thumbnail.src = fileUrl(image?.previewPath || image?.path || "");

    const body = document.createElement("div");
    body.className = "floor-card-body";
    const name = document.createElement("span");
    name.className = "floor-card-name";
    name.textContent = layer.displayName;
    name.title = layer.displayName;
    const category = document.createElement("span");
    category.className = "floor-card-category";
    category.textContent = floorCategoryFor(layer);
    const favorite = document.createElement("button");
    favorite.type = "button";
    favorite.className = `favorite-button${state.favoriteFloorIds.has(layer.id) ? " active" : ""}`;
    favorite.title = state.favoriteFloorIds.has(layer.id) ? "Remove from favorites" : "Add to favorites";
    favorite.setAttribute("aria-label", favorite.title);
    favorite.innerHTML = window.iconMarkup("star", 17);
    favorite.addEventListener("click", (event) => {
      event.stopPropagation();
      if (state.favoriteFloorIds.has(layer.id)) state.favoriteFloorIds.delete(layer.id);
      else state.favoriteFloorIds.add(layer.id);
      localStorage.setItem("courtCreator.favoriteFloors", JSON.stringify([...state.favoriteFloorIds]));
      if (state.floorFilter === "favorites") renderFloorGallery();
      else {
        favorite.classList.toggle("active", state.favoriteFloorIds.has(layer.id));
        favorite.title = state.favoriteFloorIds.has(layer.id) ? "Remove from favorites" : "Add to favorites";
        favorite.setAttribute("aria-label", favorite.title);
      }
    });
    body.append(name, category, favorite);
    card.append(thumbnail, body);
    card.addEventListener("click", () => selectFloor(layer));
    card.addEventListener("keydown", (event) => {
      if (event.target !== card) return;
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectFloor(layer);
      }
    });
    card.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      const renamed = prompt("Court name", layer.displayName);
      if (!renamed?.trim()) return;
      layer.displayName = renamed.trim();
      persistRecovery();
      renderFloorGallery();
      refreshSelectionText();
    });
    ui.floorGallery.append(card);
  }
  ui.floorGallery.scrollTop = Math.min(previousScroll, ui.floorGallery.scrollHeight);
}

function syncFloorCardStates() {
  const floorId = selectedFloorLayer()?.id;
  for (const card of ui.floorGallery.querySelectorAll(".floor-card")) {
    const selected = card.dataset.id === floorId;
    card.classList.toggle("selected", selected);
    card.setAttribute("aria-selected", String(selected));
  }
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
    activeHex: normalizeHex(layer.color) || "",
    showInlineColorControls: false,
    isCustomFloor: Boolean(layer.isCustomFloor),
    isTemplateFloor: Boolean(layer.isTemplateFloor || String(layer.id || "").startsWith("floor_template_")),
  }));
  state.layersById = new Map(state.layers.map((layer) => [layer.id, layer]));
  state.floorImagesById = new Map((data.customFloorImages || []).map((image) => [image.id, image]));
  for (const floor of data.customFloorImages || []) {
    if (floor.isTemplate && state.layersById.has(floor.id)) {
      state.layersById.get(floor.id).isTemplateFloor = true;
    } else if (state.layersById.has(floor.id)) {
      state.layersById.get(floor.id).isCustomFloor = true;
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
  if (state.section === "floors") {
    syncLayerRowStates();
    syncFloorCardStates();
  }
  else renderLayers();
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
    if (isGroup(layer) && layer.children.length && !state.collapsedLayerGroups.has(layer.id)) {
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

function renderLayers(preserveScroll = true) {
  const previousScroll = preserveScroll ? ui.layersHost.scrollTop : 0;
  refreshInlineColorControls();
  ui.layersHost.innerHTML = "";
  let rows = flattenedRows(sectionRoots());
  const query = ui.layerSearch?.value.trim().toLowerCase() || "";
  if (query) {
    rows = rows.filter(({ layer }) => {
      if (`${layer.displayName} ${layer.name}`.toLowerCase().includes(query)) return true;
      return isGroup(layer) && descendants(layer).some((child) => `${child.displayName} ${child.name}`.toLowerCase().includes(query));
    });
  }
  for (const { layer, depth } of rows) {
    const row = document.createElement("div");
    row.className = `layer-row${isGroup(layer) ? " group" : ""}${state.selectedLayerId === layer.id ? " selected" : ""}`;
    row.dataset.id = layer.id;

    const name = document.createElement("div");
    name.className = "layer-name";
    const collapsed = isGroup(layer) && state.collapsedLayerGroups.has(layer.id);
    const childCount = isGroup(layer) ? descendants(layer).filter((child) => !isGroup(child)).length : 0;
    name.textContent = `${isGroup(layer) ? (collapsed ? "▸ " : "▾ ") : ""}${layer.displayName}`;
    name.title = layer.displayName;
    name.style.paddingLeft = state.section === "paint" ? `${depth * 12}px` : "0";
    if (isGroup(layer)) row.setAttribute("aria-expanded", String(!collapsed));

    const visible = document.createElement("div");
    visible.className = `state ${layer.visible ? "on" : "off"}`;
    if (!isGroup(layer)) {
      const eye = document.createElement("span");
      eye.className = "state-eye";
      eye.innerHTML = window.iconMarkup("eye", 14);
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "visibility-toggle";
      toggle.title = layer.visible ? `Hide ${layer.displayName}` : `Show ${layer.displayName}`;
      toggle.setAttribute("aria-label", toggle.title);
      toggle.setAttribute("aria-pressed", String(layer.visible));
      toggle.addEventListener("click", (event) => {
        event.stopPropagation();
        setLayerVisibility(layer, !layer.visible);
      });
      toggle.addEventListener("dblclick", (event) => event.stopPropagation());
      visible.append(eye, toggle);
    }

    const colorCell = document.createElement("div");
    if (isGroup(layer)) {
      colorCell.className = "group-count";
      colorCell.textContent = `${childCount} layer${childCount === 1 ? "" : "s"}`;
    } else if (layer.showInlineColorControls) {
      colorCell.className = "color-controls";
      colorCell.addEventListener("dblclick", (event) => event.stopPropagation());
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
    row.tabIndex = 0;
    row.setAttribute("role", "treeitem");
    row.setAttribute("aria-selected", String(state.selectedLayerId === layer.id));
    const activateRow = () => {
      state.selectedLayerId = layer.id;
      if (isGroup(layer)) {
        if (state.collapsedLayerGroups.has(layer.id)) state.collapsedLayerGroups.delete(layer.id);
        else state.collapsedLayerGroups.add(layer.id);
        renderLayers();
      } else if (state.section === "floors" && !layer.visible) {
        setLayerVisibility(layer, true);
      } else {
        syncLayerRowStates();
      }
      refreshSelectionText();
    };
    row.addEventListener("click", activateRow);
    row.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        activateRow();
      } else if (event.key === " ") {
        event.preventDefault();
        if (isGroup(layer)) activateRow();
        else setLayerVisibility(layer, state.section === "floors" ? true : !layer.visible);
      }
    });
    row.addEventListener("dblclick", () => {
      if (!isGroup(layer) && state.section !== "floors") setLayerVisibility(layer, !layer.visible);
    });
    row.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      const renamed = prompt("Layer name", layer.displayName);
      if (!renamed?.trim()) return;
      layer.displayName = renamed.trim();
      persistRecovery();
      renderLayers();
      refreshSelectionText();
    });
    ui.layersHost.append(row);
  }
  ui.layersHost.scrollTop = Math.min(previousScroll, ui.layersHost.scrollHeight);
}

function syncLayerRowStates() {
  for (const row of ui.layersHost.querySelectorAll(".layer-row")) {
    const layer = state.layersById.get(row.dataset.id);
    if (!layer) continue;
    const selected = layer.id === state.selectedLayerId;
    row.classList.toggle("selected", selected);
    row.setAttribute("aria-selected", String(selected));
    const stateCell = row.querySelector(".state");
    if (!stateCell) continue;
    stateCell.classList.toggle("on", layer.visible);
    stateCell.classList.toggle("off", !layer.visible);
    const toggle = stateCell.querySelector(".visibility-toggle");
    if (toggle) {
      toggle.title = layer.visible ? `Hide ${layer.displayName}` : `Show ${layer.displayName}`;
      toggle.setAttribute("aria-label", toggle.title);
      toggle.setAttribute("aria-pressed", String(layer.visible));
    }
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
  fitColorEditorToViewport();
}

function fitColorEditorToViewport() {
  if (ui.colorEditor.classList.contains("hidden")) return;
  const margin = 16;
  const bounds = ui.colorEditor.getBoundingClientRect();
  const expanded = !ui.paletteSection.classList.contains("hidden");
  const desiredHeight = expanded ? Math.min(600, window.innerHeight - margin * 2) : Math.min(150, window.innerHeight - margin * 2);
  const currentLeft = Number.parseFloat(ui.colorEditor.style.left) || bounds.left;
  const currentTop = Number.parseFloat(ui.colorEditor.style.top) || bounds.top;
  const left = Math.max(margin, Math.min(window.innerWidth - bounds.width - margin, currentLeft));
  const top = Math.max(margin, Math.min(window.innerHeight - desiredHeight - margin, currentTop));
  ui.colorEditor.style.left = `${left}px`;
  ui.colorEditor.style.top = `${top}px`;
  ui.colorEditor.style.maxHeight = `${Math.max(140, window.innerHeight - top - margin)}px`;
}

function setTeamColorsExpanded(expanded) {
  ui.paletteSection.classList.toggle("hidden", !expanded);
  document.getElementById("teamColorsToggle").textContent = expanded ? "Hide Team Colors" : "Team Colors";
  requestAnimationFrame(fitColorEditorToViewport);
  if (expanded) {
    renderPopupPalette();
    requestAnimationFrame(() => {
      fitColorEditorToViewport();
      ui.paletteSearch.focus();
    });
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
    fitColorEditorToViewport();
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

function activeColorLayer() {
  const active = state.layersById.get(state.activeHexLayerId);
  if (isColorableLayer(active)) return active;
  const selected = state.layersById.get(state.selectedLayerId);
  if (isColorableLayer(selected)) return selected;
  return state.layers.find((layer) => layer.visible && isColorableLayer(layer))
    || state.layers.find(isColorableLayer)
    || null;
}

function renderPaletteInto(host, query, closeAfterSelection) {
  host.innerHTML = "";
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
    host.append(title);
    for (const item of items.sort((a, b) => a.palette.team.localeCompare(b.palette.team))) {
      const details = document.createElement("details");
      details.className = "team-palette";
      details.open = Boolean(query);
      const summary = document.createElement("summary");
      summary.textContent = query ? `${item.palette.team}  ${item.colors.length}/${item.palette.colors.length}` : `${item.palette.team}  ${item.palette.colors.length} colors`;
      const swatches = document.createElement("div");
      swatches.className = "swatches";
      const populateSwatches = () => {
        if (swatches.dataset.populated === "true") return;
        swatches.dataset.populated = "true";
        for (const color of item.colors) {
          const button = document.createElement("button");
          button.className = "swatch-button";
          button.title = `${color.name} ${color.hex}`;
          button.innerHTML = `<span class="swatch" style="background:${color.hex}"></span><span>${color.hex}</span>`;
          button.addEventListener("click", () => {
            const target = activeColorLayer();
            if (target) {
              applyHex(target, color.hex);
              if (closeAfterSelection) closeColorEditor();
            } else {
              setStatus("Select a paint or line layer before applying a team color.");
            }
          });
          swatches.append(button);
        }
      };
      if (query) populateSwatches();
      details.append(summary, swatches);
      details.addEventListener("toggle", () => {
        if (!details.open) return;
        populateSwatches();
        if (query) return;
        for (const other of host.querySelectorAll("details.team-palette[open]")) {
          if (other !== details) other.open = false;
        }
        requestAnimationFrame(() => details.scrollIntoView({ block: "nearest" }));
      });
      host.append(details);
    }
  }
  if (!host.children.length) {
    const empty = document.createElement("div");
    empty.className = "palette-empty";
    empty.textContent = "No team colors found.";
    host.append(empty);
  }
}

function renderPopupPalette() {
  renderPaletteInto(ui.paletteHost, ui.paletteSearch.value.trim(), true);
}

function renderPaintPalette() {
  renderPaletteInto(ui.paintPaletteHost, ui.paintPaletteSearch.value.trim(), false);
  const target = activeColorLayer();
  ui.paintPaletteTarget.textContent = target?.displayName || "Select a visible paint or line layer";
}

function refreshSelectionText() {
  const floor = selectedFloorLayer();
  ui.currentCourtName.textContent = floor?.displayName || "No court selected";
  if (state.section === "experimental") {
    const item = experimentalFloor();
    ui.selectedLabel.textContent = "Experimental Court";
    ui.selectedText.textContent = item ? experimentalFloorName(item) : "No court selected";
    ui.selectedCategory.textContent = item?.category || "Stock hardwood";
    if (item) ui.selectedFloorImage.src = fileUrl(item.previewPath || item.path);
    else ui.selectedFloorImage.removeAttribute("src");
  } else {
    ui.selectedLabel.textContent = "Selected Court";
    ui.selectedText.textContent = floor?.displayName || "No court selected";
    ui.selectedCategory.textContent = floor ? floorCategoryFor(floor) : state.floorLibraryName;
    const image = floorImageFor(floor);
    if (image) ui.selectedFloorImage.src = fileUrl(image.previewPath || image.path);
    else ui.selectedFloorImage.removeAttribute("src");
  }
  ui.selectedFloorFallback.classList.toggle("hidden", Boolean(ui.selectedFloorImage.getAttribute("src")));
  const colorLayer = activeColorLayer();
  ui.paintPaletteTarget.textContent = colorLayer?.displayName || "Select a visible paint or line layer";
}

function setPaintTab(tab) {
  state.paintTab = "layers";
  ui.paintLayersView.classList.toggle("hidden", state.paintTab !== "layers");
  ui.paintColorsView.classList.toggle("hidden", state.paintTab !== "colors");
  document.querySelectorAll(".inspector-tab").forEach((button) => {
    const active = button.dataset.paintTab === state.paintTab;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", String(active));
  });
  if (state.paintTab === "colors") renderPaintPalette();
}

function renderSection() {
  const copy = {
    floors: ["Court Floors", `Choose a court floor from ${state.floorLibraryName} (${state.floorLibraryCount} available), or add your own custom floor.`],
    paint: ["Paint & Lines", "Choose paint and line layers, then apply exact colors or team palette swatches."],
    logos: ["Logos", "Import logo images, then place them on the court preview."],
    import: ["Import", "Align an older court texture with a 2K27 floor and build a converted file."],
    experimental: ["Experimental", ""],
    export: ["Export", "Refresh, save, and export the current court preview."],
  };
  document.body.dataset.section = state.section;
  ui.sectionTitle.textContent = copy[state.section][0];
  ui.sectionSubtitle.textContent = copy[state.section][1];
  if (state.section !== "paint") closeColorEditor();
  ui.layersPanel.classList.toggle("hidden", state.section !== "paint");
  ui.logosPanel.classList.toggle("hidden", state.section !== "logos");
  ui.importPanel.classList.toggle("hidden", state.section !== "import");
  ui.experimentalPanel.classList.toggle("hidden", state.section !== "experimental");
  ui.experimentalCanvas.classList.toggle("hidden", state.buildMode !== "game-uv" && state.section !== "experimental");
  ui.previewImage.classList.toggle("hidden", state.buildMode === "game-uv" || state.section === "experimental");
  ui.exportPanel.classList.toggle("hidden", state.section !== "export");
  ui.previewShell.classList.toggle("hidden", state.section === "import");
  ui.importPreviewStage.classList.toggle("hidden", state.section !== "import");
  ui.paintBrowser.classList.toggle("hidden", state.section !== "paint");
  ui.currentCourtButton.classList.toggle("hidden", ["import", "experimental"].includes(state.section));
  ui.browseFloorButton.classList.toggle("hidden", ["import", "experimental"].includes(state.section));
  document.querySelectorAll(".nav").forEach((button) => button.classList.toggle("active", button.dataset.section === state.section));
  if (state.section === "paint") {
    setPaintTab(state.paintTab);
    renderLayers();
  }
  if (state.section === "experimental") loadExperimental();
  renderLogos();
  refreshSelectionText();
}

function experimentalFloor() {
  return experimentalState.floors.find((floor) => floor.id === experimentalState.settings.floorId) || null;
}

function experimentalFloorName(floor) {
  return state.layersById.get(floor.id)?.displayName || floor.name;
}

function saveExperimentalSettings() {
  localStorage.setItem("courtCreator.experimental", JSON.stringify(experimentalState.settings));
  scheduleExperimentalDraw();
}

function renderExperimentalFloors() {
  const query = ui.experimentalFloorSearch.value.toLowerCase().trim();
  const floors = experimentalState.floors.filter((floor) => `${experimentalFloorName(floor)} ${floor.category}`.toLowerCase().includes(query));
  floors.sort((a, b) => experimentalFloorName(a).localeCompare(experimentalFloorName(b)));
  ui.experimentalFloor.replaceChildren(...floors.map((floor) => new Option(experimentalFloorName(floor), floor.id)));
  ui.experimentalFloor.value = experimentalState.settings.floorId || "";
  ui.experimentalFloor.disabled = !floors.length;
}

function renderExperimentalLines() {
  ui.experimentalLines.replaceChildren();
  const geometry = experimentalState.geometry;
  for (const group of [{ name: "Paint", layers: geometry?.paints || [], settings: "paintSettings" },
    { name: "Lines", layers: geometry?.layers || [], settings: "lineSettings" }]) {
    if (!group.layers.length) continue;
    const heading = document.createElement("strong");
    heading.className = "experimental-group-heading";
    heading.textContent = group.name;
    ui.experimentalLines.append(heading);
    for (const layer of group.layers) {
      const setting = experimentalState.settings[group.settings][layer.id] || { visible: layer.visible, color: layer.color };
      const row = document.createElement("div");
      row.className = "experimental-line-row";
      const name = document.createElement("span");
      name.textContent = layer.name;
      const visible = document.createElement("input");
      visible.type = "checkbox";
      visible.checked = setting.visible;
      visible.setAttribute("aria-label", `Show ${layer.name}`);
      const colors = document.createElement("div");
      colors.className = "experimental-line-colors";
      const swatch = document.createElement("input");
      swatch.type = "color";
      swatch.value = normalizeHex(setting.color) || "#FFFFFF";
      swatch.setAttribute("aria-label", `${layer.name} color`);
      const hex = document.createElement("input");
      hex.type = "text"; hex.maxLength = 7; hex.value = swatch.value.toUpperCase();
      hex.setAttribute("aria-label", `${layer.name} hex color`);
      const updateVisibility = () => {
        experimentalState.settings[group.settings][layer.id] = { visible: visible.checked, color: swatch.value.toUpperCase() };
        colors.classList.toggle("hidden", !visible.checked);
        saveExperimentalSettings();
      };
      visible.addEventListener("change", updateVisibility);
      swatch.addEventListener("input", () => { hex.value = swatch.value.toUpperCase(); updateVisibility(); });
      hex.addEventListener("input", () => {
        const normalized = normalizeHex(hex.value);
        hex.setCustomValidity(normalized ? "" : "Enter a valid hex color.");
        if (normalized) { swatch.value = normalized; updateVisibility(); }
      });
      colors.classList.toggle("hidden", !visible.checked);
      colors.append(swatch, hex); row.append(name, visible, colors); ui.experimentalLines.append(row);
    }
  }
  const ready = Boolean(experimentalState.geometry && experimentalFloor());
  for (const id of ["experimentalPng", "experimentalIff", "experimentalReset"]) document.getElementById(id).disabled = !ready;
}

async function loadExperimental(prepare = false) {
  if (experimentalState.loading) return;
  if (experimentalState.loaded && !prepare) { scheduleExperimentalDraw(); return; }
  experimentalState.loading = true;
  ui.experimentalPrepare.disabled = true;
  ui.experimentalStatus.textContent = prepare ? "Decoding stock marking geometry..." : "Loading stock geometry...";
  try {
    const response = await window.courtCreator.experimental(prepare);
    experimentalState.geometry = response.geometry;
    experimentalState.floors = response.floors || [];
    experimentalState.loaded = true;
    if (!experimentalFloor()) experimentalState.settings.floorId = experimentalState.floors.find((floor) => floor.id === selectedFloorLayer()?.id)?.id || experimentalState.floors[0]?.id || null;
    experimentalState.settings.lineSettings ||= {};
    experimentalState.settings.paintSettings ||= {};
    experimentalState.settings.mappingMode = experimentalState.settings.mappingMode === "template" ? "template" : "game-uv";
    ui.experimentalMapping.value = experimentalState.settings.mappingMode;
    ui.experimentalMapping.disabled = !response.geometry;
    experimentalState.settings.outsideColor = normalizeHex(experimentalState.settings.outsideColor) || "#19583F";
    ui.experimentalOutside.value = experimentalState.settings.outsideColor;
    ui.experimentalOutsideHex.value = experimentalState.settings.outsideColor;
    renderExperimentalFloors(); renderExperimentalLines();
    await loadExperimentalFloor();
    ui.experimentalStatus.textContent = response.geometry ? `${response.geometry.paints.length} paint areas, ${response.geometry.layers.length} marking layers` : "Stock lines not prepared";
  } catch (error) {
    ui.experimentalStatus.textContent = `Geometry failed: ${error.message}`;
  } finally {
    experimentalState.loading = false;
    ui.experimentalPrepare.disabled = false;
  }
}

async function loadExperimentalFloor() {
  const token = ++experimentalState.floorToken;
  experimentalState.image = null;
  scheduleExperimentalDraw();
  refreshSelectionText();
  const floor = experimentalFloor();
  if (!floor) return;
  try {
    const image = new Image(); image.src = fileUrl(floor.path); await image.decode();
    if (token !== experimentalState.floorToken) return;
    experimentalState.image = image;
    saveExperimentalSettings();
  } catch (error) {
    if (token === experimentalState.floorToken) ui.experimentalStatus.textContent = `Hardwood failed: ${error.message}`;
  }
}

function scheduleExperimentalDraw() {
  if (experimentalState.drawFrame) return;
  experimentalState.drawFrame = requestAnimationFrame(() => {
    experimentalState.drawFrame = 0;
    const unified = state.buildMode === "game-uv" && state.section !== "experimental";
    const request = unified ? renderRequest() : experimentalState.settings;
    const geometry = unified ? state.geometry : experimentalState.geometry;
    const canvas = ui.experimentalCanvas;
    const ctx = canvas.getContext("2d");
    const scale = canvas.width / 8192;
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    ctx.clearRect(0, 0, 8192, 4096);
    ctx.fillStyle = request.outsideColor || "#19583F";
    if (request.outsideVisible !== false) ctx.fillRect(0, 0, 8192, 4096);
    const floor = unified ? request.floor : experimentalFloor();
    const image = unified ? nativePreview.image : experimentalState.image;
    const native = request.mappingMode !== "template" && Boolean(geometry?.gameUv);
    if (floor && image) {
      const [left, top, width, height] = native ? geometry.gameUv.hardwoodBounds : floor.bbox;
      const sourceRatio = image.naturalWidth / image.naturalHeight;
      const ratio = width / height;
      const cropWidth = sourceRatio > ratio ? image.naturalHeight * ratio : image.naturalWidth;
      const cropHeight = sourceRatio > ratio ? image.naturalHeight : image.naturalWidth / ratio;
      ctx.save();
      if (native) {
        ctx.beginPath();
        for (const polygon of geometry.gameUv.courtSurfacePolygons) {
          ctx.moveTo(...polygon[0]);
          for (const point of polygon.slice(1)) ctx.lineTo(...point);
          ctx.closePath();
        }
        ctx.clip();
      }
      ctx.drawImage(image, (image.naturalWidth - cropWidth) / 2, (image.naturalHeight - cropHeight) / 2, cropWidth, cropHeight, left, top, width, height);
      ctx.restore();
    }
    const paintIds = new Set((geometry?.paints || []).map(layer => layer.id));
    for (const layer of [...(geometry?.paints || []), ...(geometry?.layers || [])]) {
      const settings = paintIds.has(layer.id)
        ? request.paintSettings : request.lineSettings;
      const setting = settings[layer.id] || layer;
      if (!setting.visible) continue;
      ctx.fillStyle = setting.color;
      ctx.beginPath();
      for (const polygon of native ? layer.gameUvPolygons : layer.polygons) {
        ctx.moveTo(...polygon[0]);
        for (const point of polygon.slice(1)) ctx.lineTo(...point);
        ctx.closePath();
      }
      ctx.fill();
    }
    if (unified) {
      for (const logo of request.logoImages) {
        const logoImage = nativePreview.logoImages.get(logo.path);
        if (!logoImage || logo.visible === false) continue;
        ctx.save();
        ctx.translate(logo.x + logo.width / 2, logo.y + logo.height / 2);
        ctx.rotate((Number(logo.rotation) || 0) * Math.PI / 180);
        ctx.scale(logo.flipX ? -1 : 1, logo.flipY ? -1 : 1);
        ctx.globalAlpha = Math.max(0, Math.min(100, Number(logo.opacity ?? 100))) / 100;
        ctx.drawImage(logoImage, -logo.width / 2, -logo.height / 2, logo.width, logo.height);
        ctx.restore();
      }
    }
  });
}

function experimentalRequest(outputPath) {
  if (!experimentalState.geometry || !experimentalFloor() || !experimentalState.image) throw new Error("Prepare the stock lines and select a loaded hardwood first.");
  return { experimental: true, floor: { ...experimentalFloor() }, outsideColor: experimentalState.settings.outsideColor,
    mappingMode: experimentalState.settings.mappingMode,
    paintSettings: structuredClone(experimentalState.settings.paintSettings),
    lineSettings: structuredClone(experimentalState.settings.lineSettings), outputPath };
}

function importFileName(filePath) {
  return String(filePath || "").split(/[\\/]/).pop();
}

function setImportStatus(message) {
  ui.importStatus.textContent = message;
  setStatus(message);
}

function fillImportSelect(select, textures, selected) {
  select.replaceChildren(...textures.map((texture) => {
    const option = new Option(texture.name, texture.name);
    return option;
  }));
  select.value = selected || "";
  select.disabled = !textures.length;
}

function updateImportControls() {
  const source = importState.source;
  const target = importState.target;
  ui.importSourceName.textContent = source ? importFileName(source.path) : "No court selected";
  ui.importTargetName.textContent = target
    ? `Ready - ${target.textures[0].width} x ${target.textures[0].height} ${target.textures[0].format}`
    : "Not prepared";
  fillImportSelect(ui.importTexture, source?.textures || [], source?.selected);
  const texture = source?.textures.find((item) => item.name === source.selected);
  ui.importTextureInfo.textContent = texture ? `${texture.width} x ${texture.height}  |  ${texture.format}` : "";
  ui.importEdges.forEach((input, index) => {
    input.disabled = !source || importState.exporting;
    input.value = importState.bounds?.[index] ?? "";
    input.max = index % 2 === 0 ? texture?.width || "" : texture?.height || "";
  });
  ui.importTexture.disabled = !source || importState.exporting;
  document.getElementById("importSourceButton").disabled = importState.exporting;
  ui.importAutoEdges.disabled = !source || importState.exporting;
  ui.importExportPng.disabled = !source || importState.exporting || importState.preparing;
  ui.importExportIff.disabled = !source || importState.exporting || importState.preparing;
  ui.importPrepareBase.disabled = Boolean(target) || importState.preparing || importState.exporting;
}

function currentImportBounds() {
  const texture = importState.source?.textures.find((item) => item.name === importState.source.selected);
  if (!texture) throw new Error("Choose an older court texture first.");
  const bounds = ui.importEdges.map((input) => Number(input.value));
  if (bounds.some((value) => !Number.isInteger(value)) || bounds[0] < 0 || bounds[1] < 0
      || bounds[0] >= bounds[2] || bounds[1] >= bounds[3]
      || bounds[2] > texture.width || bounds[3] > texture.height) {
    throw new Error("Court edges must be inside the source texture and form a rectangle.");
  }
  importState.bounds = [...bounds];
  return bounds;
}

function importRequest(outputPath = null) {
  const floor = floorImageFor(selectedFloorLayer());
  return {
    sourcePath: importState.source.path,
    textureName: importState.source.selected,
    bounds: currentImportBounds(),
    backgroundPath: floor?.path || null,
    outputPath,
  };
}

async function refreshImportPreview() {
  if (!importState.source) return;
  if (importState.previewBusy) { importState.previewPending = true; return; }
  const token = ++importState.previewToken;
  let request;
  try { request = importRequest(); } catch (error) { setImportStatus(error.message); return; }
  importState.previewBusy = true;
  ui.importPreviewStage.setAttribute("aria-busy", "true");
  setImportStatus("Aligning court...");
  try {
    const response = await window.courtCreator.previewImport(request);
    if (token !== importState.previewToken) return;
    ui.importPreviewImage.src = `${fileUrl(response.previewPath)}?v=${Date.now()}`;
    await waitForPreviewImage(ui.importPreviewImage);
    if (token !== importState.previewToken) return;
    ui.importPreviewEmpty.classList.add("hidden");
    setImportStatus("Court aligned to 2K27 texture size.");
  } catch (error) {
    if (token === importState.previewToken) setImportStatus(`Import preview failed: ${error.message}`);
  } finally {
    if (token === importState.previewToken) ui.importPreviewStage.setAttribute("aria-busy", "false");
    importState.previewBusy = false;
    if (importState.previewPending) {
      importState.previewPending = false;
      refreshImportPreview();
    }
  }
}

function scheduleImportPreview() {
  ++importState.previewToken;
  clearTimeout(importState.previewTimer);
  importState.previewTimer = setTimeout(refreshImportPreview, 220);
}

async function openImportSource() {
  try {
    const path = await window.courtCreator.chooseImportIff(false);
    if (!path) return;
    const source = await window.courtCreator.inspectImportIff(path);
    importState.source = source;
    importState.bounds = [...source.sourceBounds];
    importState.autoBounds = [...source.sourceBounds];
    updateImportControls();
    scheduleImportPreview();
  } catch (error) { setImportStatus(`Import failed: ${error.message}`); }
}

async function changeImportTexture() {
  if (!importState.source) return;
  try {
    const source = await window.courtCreator.inspectImportIff(importState.source.path, false, ui.importTexture.value);
    importState.source = source;
    importState.bounds = [...source.sourceBounds];
    importState.autoBounds = [...source.sourceBounds];
    updateImportControls();
    scheduleImportPreview();
  } catch (error) { setImportStatus(`Texture failed: ${error.message}`); }
}

async function loadImportBaseStatus() {
  try {
    const result = await window.courtCreator.importBaseStatus();
    importState.target = result.prepared ? result.base : null;
    updateImportControls();
  } catch (error) {
    importState.target = null;
    updateImportControls();
  }
}

async function prepareImportBase() {
  if (importState.preparing) return false;
  try {
    importState.preparing = true;
    updateImportControls();
    setImportStatus("Preparing the stock NBA 2K27 export base...");
    const result = await window.courtCreator.prepareImportBase();
    if (!result) {
      setImportStatus("Export base preparation canceled.");
      return false;
    }
    importState.target = result.base;
    updateImportControls();
    setImportStatus("2K27 export base ready. Court exports use one baked texture.");
    return true;
  } catch (error) {
    setImportStatus(`Export base failed: ${error.message}`);
    return false;
  } finally {
    importState.preparing = false;
    updateImportControls();
  }
}

async function exportImportedCourt(asIff) {
  if (!importState.source || importState.exporting) return;
  if (asIff && !importState.target && !await prepareImportBase()) return;
  try {
    const outputPath = asIff
      ? await window.courtCreator.chooseImportIffOutput()
      : await window.courtCreator.chooseImportPngOutput();
    if (!outputPath) return;
    const request = importRequest(outputPath);
    importState.exporting = true;
    updateImportControls();
    setImportStatus(asIff ? "Building court IFF..." : "Saving full-size texture...");
    const result = asIff
      ? await window.courtCreator.exportImportIff(request)
      : await window.courtCreator.exportImportPng(request);
    if (!result) { setImportStatus("Export canceled."); return; }
    setImportStatus(asIff ? "Converted IFF saved." : "Full-size texture saved.");
    window.courtCreator.showItem(result.outputPath);
  } catch (error) {
    setImportStatus(`Conversion failed: ${error.message}`);
  } finally {
    importState.exporting = false;
    updateImportControls();
  }
}

function updatePreviewTransform() {
  const viewScale = state.previewView === "full" ? 1 : 1.55;
  const scale = viewScale * (state.previewZoom / 100);
  ui.previewImage.style.transform = `scale(${scale})`;
  ui.previewImage.style.transformOrigin = state.previewView === "left"
    ? "left center"
    : state.previewView === "right"
      ? "right center"
      : "center";
  ui.experimentalCanvas.style.transform = ui.previewImage.style.transform;
  ui.experimentalCanvas.style.transformOrigin = ui.previewImage.style.transformOrigin;
  ui.previewZoomLabel.textContent = `${state.previewZoom}%`;
  document.querySelectorAll(".preview-view").forEach((button) => {
    button.classList.toggle("active", button.dataset.previewView === state.previewView);
  });
}

function setPreviewView(view) {
  state.previewView = ["left", "right"].includes(view) ? view : "full";
  updatePreviewTransform();
}

function changePreviewZoom(amount) {
  state.previewZoom = Math.max(70, Math.min(180, state.previewZoom + amount));
  updatePreviewTransform();
}

async function togglePreviewFullscreen() {
  try {
    if (document.fullscreenElement) await document.exitFullscreen();
    else await ui.previewCard.requestFullscreen();
  } catch (error) {
    setStatus(`Fullscreen preview failed: ${error.message}`);
  }
}

function renderRequest(outputPath = null) {
  const request = {
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
  if (state.buildMode === "game-uv" && state.geometry) {
    const setting = item => {
      const layer = state.layersById.get(item.id);
      return { visible: Boolean(layer?.visible && ancestors(layer).every(parent => parent.visible)),
        color: state.colorOverrides[item.id] ? rgbToHex(state.colorOverrides[item.id]) : layer?.activeHex || item.color };
    };
    const outside = state.layersById.get("stock-outside");
    Object.assign(request, { buildMode: "game-uv", mappingMode: "game-uv",
      floor: { ...state.floorImagesById.get(selectedFloorLayer()?.id) },
      outsideColor: outside?.activeHex || DEFAULT_PAINT_HEX, outsideVisible: Boolean(outside?.visible),
      paintSettings: Object.fromEntries(state.geometry.paints.map(item => [item.id, setting(item)])),
      lineSettings: Object.fromEntries(state.geometry.layers.map(item => [item.id, setting(item)])) });
    delete request.templatePath;
  }
  return request;
}

async function refreshNativePreview() {
  const token = ++nativePreview.token;
  const request = renderRequest();
  const unavailableLogos = [];
  ui.previewShell.setAttribute("aria-busy", "true");
  try {
    if (request.floor.path !== nativePreview.imagePath) {
      const image = new Image();
      image.src = fileUrl(request.floor.path);
      await image.decode();
      if (token !== nativePreview.token) return;
      nativePreview.image = image;
      nativePreview.imagePath = request.floor.path;
    }
    const logoPaths = new Set(request.logoImages.map(logo => logo.path));
    for (const cachedPath of nativePreview.logoImages.keys()) {
      if (!logoPaths.has(cachedPath)) nativePreview.logoImages.delete(cachedPath);
    }
    for (const logoPath of logoPaths) {
      if (nativePreview.logoImages.has(logoPath)) continue;
      const image = new Image(); image.src = fileUrl(logoPath);
      try { await image.decode(); }
      catch {
        unavailableLogos.push(request.logoImages.find(logo => logo.path === logoPath)?.name || "Logo");
        continue;
      }
      if (token !== nativePreview.token) return;
      nativePreview.logoImages.set(logoPath, image);
    }
    if (token !== nativePreview.token) return;
    scheduleExperimentalDraw();
    ui.previewEmpty.classList.add("hidden");
    setStatus(unavailableLogos.length ? `Court ready. Missing logo images: ${unavailableLogos.join(", ")}` : "Court ready.");
  } catch (error) {
    if (token === nativePreview.token) setStatus(`Preview failed: ${error.message}`);
  } finally {
    if (token === nativePreview.token) ui.previewShell.setAttribute("aria-busy", "false");
  }
}

let previewBusy = false;
let previewPending = false;

function waitForPreviewImage(image, timeoutMs = 2000) {
  return new Promise((resolve) => {
    let settled = false;
    let timer;
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      image.removeEventListener("load", finish);
      image.removeEventListener("error", finish);
      resolve();
    };
    image.addEventListener("load", finish, { once: true });
    image.addEventListener("error", finish, { once: true });
    timer = setTimeout(finish, timeoutMs);
    image.decode().then(finish, finish);
  });
}

async function refreshPreview(outputPath = null) {
  if (state.buildMode === "game-uv" && state.geometry && !outputPath) return refreshNativePreview();
  if (state.section === "experimental" && !outputPath) { scheduleExperimentalDraw(); return; }
  if (!state.templatePath && !state.geometry) return;
  if (previewBusy) { previewPending = true; return; }
  previewBusy = true;
  ui.previewShell.classList.add("rendering");
  ui.previewShell.setAttribute("aria-busy", "true");
  const token = ++state.renderToken;
  setStatus("Refreshing preview...");
  try {
    const response = await window.courtCreator.render(renderRequest(outputPath));
    if (token !== state.renderToken) return;
    state.previewPath = response.previewPath || state.previewPath;
    ui.previewImage.src = `${fileUrl(state.previewPath)}?v=${Date.now()}`;
    await waitForPreviewImage(ui.previewImage);
    if (token !== state.renderToken) return;
    ui.previewEmpty.classList.add("hidden");
    setStatus(outputPath ? "PNG exported." : "Preview refreshed.");
  } catch (error) {
    setStatus(`Preview failed: ${error.message}`);
  } finally {
    previewBusy = false;
    if (previewPending) {
      previewPending = false;
      refreshPreview();
    } else {
      ui.previewShell.classList.remove("rendering");
      ui.previewShell.setAttribute("aria-busy", "false");
    }
  }
}

function schedulePreview() {
  ++state.renderToken;
  persistRecovery();
  clearTimeout(state.renderTimer);
  state.renderTimer = setTimeout(() => refreshPreview(), 160);
}

function selectedLogo() {
  return state.logos.find((logo) => logo.id === state.selectedLogoId) || null;
}

async function openLogoEditor() {
  const button = document.getElementById("openLogoEditorButton");
  button.disabled = true;
  try {
    const floor = state.floorImagesById.get(selectedFloorLayer()?.id);
    setStatus("Opening logo editor...");
    await window.courtCreator.openLogoEditor({ ...renderRequest(), selectedId: state.selectedLogoId,
      guideBounds: floor?.bbox || null });
    setStatus("Logo editor opened.");
  } catch (error) {
    setStatus(`Logo editor failed: ${error.message}`);
  } finally {
    button.disabled = false;
  }
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
  ui.removeLogoButton.disabled = disabled;
  ui.duplicateLogoXButton.disabled = disabled;
  ui.duplicateLogoYButton.disabled = disabled;
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
    const image = new Image();
    image.src = fileUrl(logoPath);
    try { await image.decode(); } catch { setStatus(`Unable to read logo: ${logoPath}`); continue; }
    const size = Math.min(state.document.width * 0.16 / image.naturalWidth, state.document.height * 0.2 / image.naturalHeight);
    const width = Math.max(1, Math.round(image.naturalWidth * size));
    const height = Math.max(1, Math.round(image.naturalHeight * size));
    const offset = (state.logos.length % 8) * 24;
    const name = logoPath.split(/[\\/]/).pop().replace(/\.[^.]+$/, "");
    state.logos.push({
      id: crypto.randomUUID(),
      name,
      path: logoPath,
      visible: true,
      x: Math.max(0, Math.min(state.document.width - width, (state.document.width - width) / 2 + offset)),
      y: Math.max(0, Math.min(state.document.height - height, (state.document.height - height) / 2 + offset)),
      width,
      height,
      rotation: 0,
      opacity: 100,
      flipX: false,
      flipY: false,
      scaleLocked: true,
    });
  }
  state.selectedLogoId = state.logos.at(-1)?.id || null;
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
    state.floorImagesById.set(layer.id, response.image);
    const parent = layerParent(layer);
    if (parent) {
      parent.children.push(layer);
      parent.children.sort((a, b) => courtSortKey(a) - courtSortKey(b) || String(a.displayName || a.name).localeCompare(String(b.displayName || b.name)));
    }
    refreshFriendlyNames();
    renderSection();
    if (ui.floorCatalog.open) {
      state.floorFilter = "custom";
      localStorage.setItem("courtCreator.floorFilter", "custom");
      ui.floorSearch.value = "";
      renderFloorFilters();
      renderFloorGallery(false);
    }
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
  state.projectPath = null;
  updateAppChrome();
  const preset = state.buildMode === "game-uv" ? null : nbaPreset();
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

function restoreStockLayout(project) {
  const applySetting = (id, setting) => {
    const layer = state.layersById.get(id);
    if (!layer || !setting) return;
    if (typeof setting.visible === "boolean") setLayerVisible(layer, setting.visible);
    const hex = normalizeHex(setting.color);
    if (hex) { layer.activeHex = hex; state.colorOverrides[id] = hexToRgb(hex); }
  };
  // Preserve familiar PSD controls when opening a previous-generation project.
  const legacyNames = {
    "paint": ["paint-left", "paint-right"],
    "secondary paint color": ["secondary-paint-left", "secondary-paint-right"],
    "outside color": ["stock-outside"],
    "nba three": ["NBA_line_three_point_lowShape"], "3 point lines": ["NBA_line_three_point_lowShape"],
    "college three": ["college-three"], "high school three": ["high-school-three"],
    "center line": ["line_midcourt_side_lowShape", "line_midcourt_center_lowShape"],
    "out of bound line": ["line_side_base_lowShape"], "media lines": ["line_camera_lowShape"],
    "hash lines": ["line_tab_low_3Shape", "line_tab_lane_lowShape", "line_tab_lane_inner_lowShape"],
    "charge circle": ["line_charge_circle_lowShape"], "half court circle": ["line_center_circle_outer_lowShape"],
  };
  if (project.version === 1) {
    for (const [oldId, name] of Object.entries(project.layerNames || {})) {
      for (const id of legacyNames[normalizeName(name)] || []) {
        const rgb = project.colorOverrides?.[oldId];
        applySetting(id, { visible: project.visibility?.[oldId], color: rgb ? rgbToHex(rgb) : null });
      }
    }
  }
  for (const [id, setting] of Object.entries({ ...project.paintSettings, ...project.lineSettings })) applySetting(id, setting);
  if (project.outsideColor) applySetting("stock-outside", { color: project.outsideColor, visible: project.outsideVisible });
  const floorId = project.floorId || project.floor?.id
    || project.customFloorImages?.find(image => image.visible && state.floorImagesById.has(image.id))?.id
    || Object.keys(project.visibility || {}).find(id => project.visibility[id] && state.floorImagesById.has(id));
  const floor = state.layersById.get(floorId);
  if (floor && isInsideCourtFloor(floor)) showOnlyCourtFloor(floor);
  else if (!selectedFloorLayer()) {
    const defaultFloor = state.layers.find(layer => state.floorImagesById.has(layer.id) && layer.originalVisible);
    if (defaultFloor) showOnlyCourtFloor(defaultFloor);
  }
}

async function loadWorkspace(templatePath = null, project = null) {
  document.body.classList.add("workspace-loading");
  try {
    setStatus("Loading stock court geometry...");
    const data = await window.courtCreator.load(templatePath);
    state.templatePath = data.templatePath;
    state.buildMode = data.buildMode || "template";
    state.geometry = data.geometry || null;
    state.previewPath = data.previewPath;
    state.document = data.document;
    state.visibility = { ...(data.visibility || {}) };
    state.customFloorImages = data.customFloorImages || [];
    state.teamPalettes = data.teamPalettes || [];
    state.presets = data.presets || [];
    state.floorLibraryName = data.floorLibraryName || "NBA 2K courts";
    state.floorLibraryCount = Number(data.floorLibraryCount) || 0;
    state.projectPath = project?._projectPath || null;
    updateAppChrome();
    state.colorOverrides = {};
    state.templateColors = {};
    state.logos = [];
    state.selectedLogoId = null;
    rebuildLayerIndex(data);
    for (const layer of state.layers) if (layer.activeHex) state.templateColors[layer.id] = layer.activeHex;
    for (const layer of state.layers) layer.visible = Boolean(state.visibility[layer.id] ?? layer.visible);
    const preset = state.buildMode === "game-uv" ? null : nbaPreset();
    if (preset) applyPresetLayout(preset, true);
    applyDefaultPaintColors();
    if (project) {
      state.visibility = { ...state.visibility, ...project.visibility };
      state.colorOverrides = { ...state.colorOverrides, ...project.colorOverrides };
      state.logos = project.logoImages || [];
      for (const layer of state.layers) {
        layer.visible = Boolean(state.visibility[layer.id]);
        if (state.colorOverrides[layer.id]) layer.activeHex = rgbToHex(state.colorOverrides[layer.id]);
        if (project.layerNames?.[layer.id]) layer.displayName = project.layerNames[layer.id];
      }
      if (state.buildMode === "game-uv") restoreStockLayout(project);
    }
    if (state.buildMode === "game-uv" && project?.version !== 2 && !localStorage.getItem("courtCreator.uvPromoted")) restoreStockLayout(experimentalState.settings);
    selectCurrentCourtFloor();
    renderSection();
    await refreshPreview();
    if (state.buildMode === "game-uv") {
      localStorage.setItem("courtCreator.uvPromoted", "1");
      persistRecovery();
    }
    if (data.templateFallback) setStatus("Project restored with the local court template.");
    else if (project) setStatus("Project restored.");
    else setStatus(preset ? "NBA preset loaded." : "Court workspace ready.");
  } catch (error) {
    setStatus(`Startup failed: ${error.message}`);
  } finally {
    document.body.classList.remove("workspace-loading");
  }
}

async function exportPng() {
  const target = await window.courtCreator.chooseExportPng();
  if (!target) return;
  try {
    document.getElementById("exportButton").disabled = true;
    document.getElementById("exportPanelButton").disabled = true;
    setStatus("Exporting full-resolution PNG...");
    await window.courtCreator.render({ ...(state.section === "experimental" ? experimentalRequest(target) : renderRequest(target)), exportFullResolution: true });
    setStatus("Full-resolution PNG exported.");
    await window.courtCreator.showItem(target);
  } catch (error) {
    setStatus(`Export failed: ${error.message}`);
  } finally {
    document.getElementById("exportButton").disabled = false;
    document.getElementById("exportPanelButton").disabled = false;
  }
}

async function exportCurrentIff() {
  const target = await window.courtCreator.chooseCurrentIffOutput();
  if (!target) return;
  const button = document.getElementById("exportIffButton");
  const experimentalButton = document.getElementById("experimentalIff");
  try {
    button.disabled = true;
    experimentalButton.disabled = true;
    setStatus("Baking one full-court texture and building the 2K27 IFF...");
    const result = await window.courtCreator.exportCurrentIff({
      ...(state.section === "experimental" ? experimentalRequest(target) : renderRequest(target)),
      outputPath: target,
      exportFullResolution: true,
    });
    if (!result) { setStatus("Export canceled."); return; }
    setStatus("NBA 2K27 court IFF exported with one baked texture.");
    await window.courtCreator.showItem(result.outputPath);
    await loadImportBaseStatus();
  } catch (error) {
    setStatus(`IFF export failed: ${error.message}`);
  } finally {
    button.disabled = false;
    experimentalButton.disabled = !experimentalState.geometry;
  }
}

function projectSnapshot() {
  const snapshot = renderRequest();
  snapshot.customFloorImages = snapshot.customFloorImages.filter((image) => !isGameFloorImage(image));
  return {
    ...snapshot,
    version: state.buildMode === "game-uv" ? 2 : 1,
    _projectPath: state.projectPath,
    layerNames: Object.fromEntries(state.layers.map(layer => [layer.id, layer.displayName])),
  };
}
function persistRecovery() {
  if (state.geometry || state.templatePath) window.courtCreator.autosave(projectSnapshot());
}
async function saveProject() {
  try {
    const saved = await window.courtCreator.saveProject(projectSnapshot());
    if (saved) {
      state.projectPath = saved;
      updateAppChrome();
      setStatus("Project saved.");
    }
  } catch (error) { setStatus(`Save failed: ${error.message}`); }
}
async function restoreStartup() {
  const [project, info] = await Promise.all([
    window.courtCreator.recovery(),
    window.courtCreator.appInfo(),
  ]);
  state.appVersion = info.version || "";
  updateAppChrome();
  await loadWorkspace(project?.templatePath || null, project);
}

function wireEvents() {
  document.getElementById("openLogoEditorButton").addEventListener("click", openLogoEditor);
  window.courtCreator.onLogoEditorUpdate?.((project) => {
    state.logos = project.items || [];
    state.selectedLogoId = project.selectedId || state.logos[0]?.id || null;
    renderLogos(); refreshSelectionText(); schedulePreview();
  });
  ui.experimentalPrepare.addEventListener("click", () => loadExperimental(true));
  ui.experimentalMapping.addEventListener("change", () => {
    experimentalState.settings.mappingMode = ui.experimentalMapping.value;
    saveExperimentalSettings();
  });
  ui.experimentalFloorSearch.addEventListener("input", renderExperimentalFloors);
  ui.experimentalFloor.addEventListener("change", () => {
    experimentalState.settings.floorId = ui.experimentalFloor.value;
    renderExperimentalLines(); loadExperimentalFloor();
  });
  ui.experimentalOutside.addEventListener("input", () => {
    experimentalState.settings.outsideColor = ui.experimentalOutside.value.toUpperCase();
    ui.experimentalOutsideHex.value = experimentalState.settings.outsideColor;
    saveExperimentalSettings();
  });
  ui.experimentalOutsideHex.addEventListener("input", () => {
    const hex = normalizeHex(ui.experimentalOutsideHex.value);
    ui.experimentalOutsideHex.setCustomValidity(hex ? "" : "Enter a valid hex color.");
    if (hex) { experimentalState.settings.outsideColor = hex; ui.experimentalOutside.value = hex; saveExperimentalSettings(); }
  });
  document.getElementById("experimentalReset").addEventListener("click", () => {
    experimentalState.settings.lineSettings = {}; experimentalState.settings.paintSettings = {};
    renderExperimentalLines(); saveExperimentalSettings();
  });
  document.getElementById("experimentalPng").addEventListener("click", exportPng);
  document.getElementById("experimentalIff").addEventListener("click", exportCurrentIff);
  document.getElementById("saveProjectButton").addEventListener("click", saveProject);
  document.getElementById("openProjectButton").addEventListener("click", async () => {
    try {
      persistRecovery();
      const project = await window.courtCreator.openProject();
      if (project && await window.courtCreator.confirmReplace()) await loadWorkspace(project.templatePath, project);
    } catch (error) { setStatus(`Open failed: ${error.message}`); }
  });
  window.addEventListener("beforeunload", persistRecovery);
  window.addEventListener("keydown", event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") { event.preventDefault(); saveProject(); }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "r") { event.preventDefault(); refreshPreview(); }
    if (event.key === "F5") { event.preventDefault(); refreshPreview(); }
    if (event.key === "Escape") closeColorEditor();
  });
  document.querySelectorAll(".nav").forEach((button) => {
    button.addEventListener("click", () => {
      state.section = button.dataset.section;
      renderSection();
      if (state.section === "import") loadImportBaseStatus();
    });
  });
  ui.floorSearch.addEventListener("input", () => renderFloorGallery(false));
  ui.layerSearch.addEventListener("input", () => renderLayers(false));
  ui.paletteSearch.addEventListener("input", renderPopupPalette);
  ui.paintPaletteSearch.addEventListener("input", renderPaintPalette);
  ui.floorSort.addEventListener("change", () => {
    state.floorSort = ui.floorSort.value;
    localStorage.setItem("courtCreator.floorSort", state.floorSort);
    renderFloorGallery(false);
  });
  ui.floorGridButton.addEventListener("click", () => setFloorView("grid"));
  ui.floorListButton.addEventListener("click", () => setFloorView("list"));
  document.querySelectorAll(".inspector-tab").forEach((button) => {
    button.addEventListener("click", () => setPaintTab(button.dataset.paintTab));
  });
  document.querySelectorAll(".preview-view").forEach((button) => {
    button.addEventListener("click", () => setPreviewView(button.dataset.previewView));
  });
  document.getElementById("zoomOutButton").addEventListener("click", () => changePreviewZoom(-10));
  document.getElementById("zoomInButton").addEventListener("click", () => changePreviewZoom(10));
  document.getElementById("fullscreenPreviewButton").addEventListener("click", togglePreviewFullscreen);
  ui.currentCourtButton.addEventListener("click", openFloorCatalog);
  ui.browseFloorButton.addEventListener("click", openFloorCatalog);
  document.getElementById("closeFloorCatalogButton").addEventListener("click", () => ui.floorCatalog.close());
  ui.floorCatalog.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopPropagation();
    ui.floorCatalog.close();
  }, true);
  ui.floorCatalog.addEventListener("click", event => {
    const bounds = ui.floorCatalog.getBoundingClientRect();
    if (event.target === ui.floorCatalog && (event.clientX < bounds.left || event.clientX > bounds.right
        || event.clientY < bounds.top || event.clientY > bounds.bottom)) ui.floorCatalog.close();
  });
  ui.selectedFloorImage.addEventListener("error", () => {
    ui.selectedFloorImage.removeAttribute("src");
    ui.selectedFloorFallback.classList.remove("hidden");
  });
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
  window.addEventListener("resize", fitColorEditorToViewport);
  makeColorEditorDraggable();
  document.getElementById("newButton").addEventListener("click", async () => {
    persistRecovery();
    if (await window.courtCreator.confirmReplace()) resetToDefault();
  });
  document.getElementById("refreshButton").addEventListener("click", () => refreshPreview());
  document.getElementById("exportButton").addEventListener("click", exportPng);
  document.getElementById("exportPanelButton").addEventListener("click", exportPng);
  document.getElementById("exportIffButton").addEventListener("click", exportCurrentIff);
  document.getElementById("openPsdButton").addEventListener("click", () => window.courtCreator.openPath(state.templatePath));
  document.getElementById("addFloorButton").addEventListener("click", addCustomFloor);
  document.getElementById("importSourceButton").addEventListener("click", openImportSource);
  ui.importPrepareBase.addEventListener("click", prepareImportBase);
  ui.importTexture.addEventListener("change", changeImportTexture);
  ui.importEdges.forEach((input) => input.addEventListener("input", () => {
    try { currentImportBounds(); } catch { return; }
    scheduleImportPreview();
  }));
  ui.importAutoEdges.addEventListener("click", () => {
    if (!importState.autoBounds) return;
    importState.bounds = [...importState.autoBounds];
    updateImportControls();
    scheduleImportPreview();
  });
  ui.importExportPng.addEventListener("click", () => exportImportedCourt(false));
  ui.importExportIff.addEventListener("click", () => exportImportedCourt(true));
  ui.importPreviewImage.addEventListener("error", () => ui.importPreviewEmpty.classList.remove("hidden"));
  document.getElementById("openButton").addEventListener("click", async () => {
    const selected = await window.courtCreator.choosePsd();
    if (selected && await window.courtCreator.confirmReplace()) loadWorkspace(selected);
  });
  document.getElementById("importLogoButton").addEventListener("click", importLogos);
  ui.removeLogoButton.addEventListener("click", () => {
    const logo = selectedLogo();
    if (!logo) return;
    state.logos = state.logos.filter((item) => item.id !== logo.id);
    state.selectedLogoId = state.logos[0]?.id || null;
    renderLogos();
    refreshSelectionText();
    schedulePreview();
  });
  ui.duplicateLogoXButton.addEventListener("click", () => duplicateLogo("x"));
  ui.duplicateLogoYButton.addEventListener("click", () => duplicateLogo("y"));
  for (const input of [ui.logoName, ui.logoX, ui.logoY, ui.logoWidth, ui.logoHeight, ui.logoRotation, ui.logoOpacity, ui.logoVisible, ui.logoScaleLocked]) {
    input.addEventListener("input", updateSelectedLogo);
  }
}

window.applyIcons();
updatePreviewTransform();
wireEvents();
restoreStartup();
