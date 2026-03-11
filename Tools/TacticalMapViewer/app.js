const state = {
  fileIndex: new Map(),
  objectUrls: new Map(),
  manifest: null,
  map: null,
  grid: null,
  selectedMapId: null,
  selectedGridId: null,
  zoom: 1,
  hover: null,
  pinned: null,
  enteredCoordinates: null,
  coordinateOffset: null,
  image: null,
  isPanning: false,
  panStartX: 0,
  panStartY: 0,
  panOffsetStartX: 0,
  panOffsetStartY: 0,
  suppressClick: false,
  lastPointerX: null,
  lastPointerY: null,
  viewOffsetX: 0,
  viewOffsetY: 0,
};

const els = {
  folderInput: document.getElementById("folderInput"),
  loadStatus: document.getElementById("loadStatus"),
  mapSelect: document.getElementById("mapSelect"),
  gridSelect: document.getElementById("gridSelect"),
  showLabels: document.getElementById("showLabels"),
  showGrid: document.getElementById("showGrid"),
  meta: document.getElementById("meta"),
  cellInfo: document.getElementById("cellInfo"),
  areaInfo: document.getElementById("areaInfo"),
  entityInfo: document.getElementById("entityInfo"),
  canvas: document.getElementById("mapCanvas"),
  wrap: document.getElementById("canvasWrap"),
  zoomIn: document.getElementById("zoomIn"),
  zoomOut: document.getElementById("zoomOut"),
  resetView: document.getElementById("resetView"),
  coordRefX: document.getElementById("coordRefX"),
  coordRefY: document.getElementById("coordRefY"),
  applyCoords: document.getElementById("applyCoords"),
  clearCoords: document.getElementById("clearCoords"),
  coordStatus: document.getElementById("coordStatus"),
};

const ctx = els.canvas.getContext("2d", { alpha: false });

wireEvents();

function wireEvents() {
  els.folderInput.addEventListener("change", async ev => {
    const files = Array.from(ev.target.files ?? []);
    await loadExportFolder(files);
  });

  els.mapSelect.addEventListener("change", async () => {
    await loadMap(els.mapSelect.value);
  });

  els.gridSelect.addEventListener("change", async () => {
    state.selectedGridId = els.gridSelect.value;
    state.grid = getSelectedGrid();
    state.image = await loadGridImage(state.grid);
    resetHover();
    clearCoordinateReference(false);
    updateMeta();
    fitCanvas();
    render();
  });

  els.showLabels.addEventListener("change", render);
  els.showGrid.addEventListener("change", render);
  els.zoomIn.addEventListener("click", () => zoomBy(1.25));
  els.zoomOut.addEventListener("click", () => zoomBy(0.8));
  els.resetView.addEventListener("click", () => {
    fitCanvas();
    render();
  });
  els.applyCoords.addEventListener("click", applyCoordinateReference);
  els.clearCoords.addEventListener("click", clearCoordinateReference);

  els.canvas.addEventListener("mousemove", ev => {
    if (!state.grid) {
      return;
    }

    state.lastPointerX = ev.clientX;
    state.lastPointerY = ev.clientY;

    if (state.isPanning) {
      return;
    }

    state.hover = eventToIndices(ev);
    updateInfoPanels();
    render();
  });

  els.canvas.addEventListener("mouseleave", () => {
    state.hover = null;
    updateInfoPanels();
    render();
  });

  els.canvas.addEventListener("click", ev => {
    if (!state.grid) {
      return;
    }

    if (state.suppressClick) {
      state.suppressClick = false;
      return;
    }

    state.pinned = eventToIndices(ev);
    updateInfoPanels();
    render();
  });

  window.addEventListener("resize", () => {
    if (!state.grid) {
      return;
    }

    applyCanvasSize();
    clampViewOffset();
    render();
  });

  els.wrap.addEventListener("wheel", ev => {
    if (!state.grid) {
      return;
    }

    ev.preventDefault();
    zoomBy(ev.deltaY < 0 ? 1.15 : 1 / 1.15, ev.clientX, ev.clientY);
  }, { passive: false });

  els.wrap.addEventListener("mousedown", ev => {
    if (ev.button !== 0 || !state.grid) {
      return;
    }

    ev.preventDefault();
    state.isPanning = true;
    state.panStartX = ev.clientX;
    state.panStartY = ev.clientY;
    state.panOffsetStartX = state.viewOffsetX;
    state.panOffsetStartY = state.viewOffsetY;
    els.wrap.classList.add("panning");
  });

  window.addEventListener("mousemove", ev => {
    if (!state.isPanning) {
      return;
    }

    const dx = ev.clientX - state.panStartX;
    const dy = ev.clientY - state.panStartY;

    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) {
      state.suppressClick = true;
    }

    state.viewOffsetX = state.panOffsetStartX + dx;
    state.viewOffsetY = state.panOffsetStartY + dy;
    clampViewOffset();
    render();
  });

  window.addEventListener("mouseup", () => {
    if (!state.isPanning) {
      return;
    }

    state.isPanning = false;
    els.wrap.classList.remove("panning");
  });
}

async function loadExportFolder(files) {
  clearObjectUrls();
  state.fileIndex = buildFileIndex(files);

  try {
    state.manifest = await readJsonFile("manifest.json");
    state.manifest.maps.sort((a, b) => a.name.localeCompare(b.name));
    setLoadStatus(`Loaded export with ${state.manifest.maps.length} maps.`);
    els.mapSelect.disabled = false;
    els.gridSelect.disabled = false;
    populateMapSelect();

    if (state.manifest.maps.length > 0) {
      await loadMap(state.manifest.maps[0].id);
    }
  } catch (error) {
    console.error(error);
    setLoadStatus(error.message, true);
    clearLoadedState();
  }
}

function buildFileIndex(files) {
  const index = new Map();

  for (const file of files) {
    const relative = normalizeRelativePath(file.webkitRelativePath || file.name);
    index.set(relative, file);
  }

  return index;
}

function normalizeRelativePath(path) {
  const normalized = path.replaceAll("\\", "/");
  const slash = normalized.indexOf("/");
  return slash >= 0 ? normalized.slice(slash + 1) : normalized;
}

function setLoadStatus(message, isError = false) {
  els.loadStatus.className = isError ? "info" : "info";
  if (!isError) {
    els.loadStatus.classList.remove("empty");
  }
  els.loadStatus.textContent = message;
}

function clearLoadedState() {
  state.manifest = null;
  state.map = null;
  state.grid = null;
  state.image = null;
  els.mapSelect.innerHTML = "";
  els.gridSelect.innerHTML = "";
  els.mapSelect.disabled = true;
  els.gridSelect.disabled = true;
  resetHover();
  updateMeta();
  render();
}

async function readJsonFile(path) {
  const file = state.fileIndex.get(path);
  if (!file) {
    throw new Error(`Missing required file: ${path}`);
  }

  const text = await file.text();
  return JSON.parse(text);
}

function populateMapSelect() {
  els.mapSelect.innerHTML = "";

  for (const map of state.manifest.maps) {
    const opt = document.createElement("option");
    opt.value = map.id;
    opt.textContent = map.name;
    els.mapSelect.appendChild(opt);
  }
}

async function loadMap(mapId) {
  if (!state.manifest) {
    return;
  }

  const mapEntry = state.manifest.maps.find(m => m.id === mapId);
  if (!mapEntry) {
    return;
  }

  state.map = await readJsonFile(mapEntry.file);
  state.selectedMapId = mapId;
  state.selectedGridId = state.map.grids[0]?.gridId ?? null;
  state.grid = getSelectedGrid();
  state.image = await loadGridImage(state.grid);
  els.mapSelect.value = mapId;

  populateGridSelect();
  resetHover();
  clearCoordinateReference(false);
  updateMeta();
  fitCanvas();
  render();
}

function populateGridSelect() {
  els.gridSelect.innerHTML = "";

  for (const grid of state.map.grids) {
    const opt = document.createElement("option");
    opt.value = grid.gridId;
    opt.textContent = grid.gridId;
    els.gridSelect.appendChild(opt);
  }

  if (state.selectedGridId) {
    els.gridSelect.value = state.selectedGridId;
  }
}

function getSelectedGrid() {
  if (!state.map) {
    return null;
  }

  return state.map.grids.find(g => g.gridId === state.selectedGridId) ?? state.map.grids[0] ?? null;
}

function resetHover() {
  state.hover = null;
  state.pinned = null;
  updateInfoPanels();
}

function updateMeta() {
  if (!state.map || !state.grid) {
    els.meta.textContent = "";
    return;
  }

  const width = getGridWidth(state.grid);
  const height = getGridHeight(state.grid);
  els.meta.innerHTML = [
    row("Map", state.map.name),
    row("Map Key", state.map.id),
    row("Grid", state.grid.gridId),
    row("Bounds", `${state.grid.bounds.minX}, ${state.grid.bounds.minY} -> ${state.grid.bounds.maxX}, ${state.grid.bounds.maxY}`),
    row("Render Bounds", `${getGridBounds(state.grid).minX}, ${getGridBounds(state.grid).minY} -> ${getGridBounds(state.grid).maxX}, ${getGridBounds(state.grid).maxY}`),
    row("Extent", `${width} x ${height}`),
    row("Image", state.grid.image ? `${state.grid.image.width} x ${state.grid.image.height}` : "None"),
    row("Entity Tiles", (state.grid.entities?.length ?? 0).toString()),
  ].join("");
}

function updateInfoPanels() {
  const indices = state.hover ?? state.pinned;
  if (!state.grid || !indices) {
    els.cellInfo.className = "info empty";
    els.cellInfo.textContent = "Move the cursor over the map.";
    els.areaInfo.className = "info empty";
    els.areaInfo.textContent = "No area selected.";
    els.entityInfo.className = "info empty";
    els.entityInfo.textContent = "No entities on this tile.";
    updateCoordinateStatus();
    return;
  }

  const areaCell = findAreaCell(state.grid, indices.x, indices.y);
  const areaLabel = findLabel(state.grid.labels, indices.x, indices.y);
  const entityCell = findEntityCell(state.grid, indices.x, indices.y);
  const calculatedCoordinates = getCalculatedCoordinates(indices);

  els.cellInfo.className = "info";
  els.cellInfo.innerHTML = [
    row("Indices", `${indices.x}, ${indices.y}`),
    row("Coordinates", calculatedCoordinates ? formatCoordinates(calculatedCoordinates) : "Not set"),
    row("Area Label", areaLabel?.t ?? "None"),
    row("Pinned", state.pinned ? "Yes" : "No"),
  ].join("");

  if (!areaCell) {
    els.areaInfo.className = "info empty";
    els.areaInfo.textContent = "No area at this cell.";
    updateEntityInfo(entityCell);
    updateCoordinateStatus();
    return;
  }

  const areaId = state.grid.areaIds[areaCell.a] ?? null;
  const info = state.grid.areaInfo.find(a => a.id === areaId) ?? null;
  if (!info) {
    els.areaInfo.className = "info";
    els.areaInfo.innerHTML = row("Area Id", areaId ?? "Unknown");
    updateEntityInfo(entityCell);
    updateCoordinateStatus();
    return;
  }

  const flags = areaFlags(info);
  els.areaInfo.className = "info";
  els.areaInfo.innerHTML = [
    row("Name", info.name || info.id),
    row("Id", info.id),
    row("Linked LZ", info.linkedLz || "None"),
    flags.length > 0 ? `<div class="infoRow"><span class="infoKey">Flags:</span><div>${flags.map(flag => `<span class="tag">${escapeHtml(flag)}</span>`).join("")}</div></div>` : row("Flags", "None"),
  ].join("");

  updateEntityInfo(entityCell);
  updateCoordinateStatus();
}

function fitCanvas() {
  if (!state.grid) {
    return;
  }

  resizeCanvasViewport();

  const width = getCanvasBaseWidth(state.grid);
  const height = getCanvasBaseHeight(state.grid);
  const availableWidth = Math.max(1, els.canvas.width);
  const availableHeight = Math.max(1, els.canvas.height);
  const fitZoom = Math.min(availableWidth / width, availableHeight / height);

  state.zoom = Math.max(0.05, fitZoom || 1);
  const contentWidth = width * state.zoom;
  const contentHeight = height * state.zoom;
  state.viewOffsetX = Math.round((availableWidth - contentWidth) / 2);
  state.viewOffsetY = Math.round((availableHeight - contentHeight) / 2);
  clampViewOffset();
  applyCanvasSize();
}

function zoomBy(factor, clientX = null, clientY = null) {
  const previousZoom = state.zoom;
  const nextZoom = Math.max(0.05, Math.min(8, state.zoom * factor));
  if (nextZoom === previousZoom) {
    return;
  }

  const wrapRect = els.wrap.getBoundingClientRect();
  const anchorClientX = clientX ?? state.lastPointerX ?? (wrapRect.left + wrapRect.width / 2);
  const anchorClientY = clientY ?? state.lastPointerY ?? (wrapRect.top + wrapRect.height / 2);
  const anchorOffsetX = anchorClientX - wrapRect.left;
  const anchorOffsetY = anchorClientY - wrapRect.top;
  const worldX = (anchorOffsetX - state.viewOffsetX) / previousZoom;
  const worldY = (anchorOffsetY - state.viewOffsetY) / previousZoom;

  state.zoom = nextZoom;
  applyCanvasSize();
  state.viewOffsetX = anchorOffsetX - worldX * nextZoom;
  state.viewOffsetY = anchorOffsetY - worldY * nextZoom;
  clampViewOffset();
  render();
}

function applyCanvasSize() {
  if (!state.grid) {
    return;
  }

  resizeCanvasViewport();
}

function render() {
  if (!state.grid) {
    ctx.clearRect(0, 0, els.canvas.width, els.canvas.height);
    return;
  }

  const bounds = getGridBounds(state.grid);
  const width = getGridWidth(state.grid);
  const height = getGridHeight(state.grid);
  const tileScale = getTilePixelScale();
  const drawOffsetX = state.viewOffsetX;
  const drawOffsetY = state.viewOffsetY;

  ctx.fillStyle = "#0b0f14";
  ctx.fillRect(0, 0, els.canvas.width, els.canvas.height);

  if (state.grid.image && state.image) {
    ctx.drawImage(
      state.image,
      drawOffsetX,
      drawOffsetY,
      state.grid.image.width * state.zoom,
      state.grid.image.height * state.zoom);
  }

  if (els.showGrid.checked && tileScale >= 6) {
    ctx.strokeStyle = "rgba(255,255,255,0.08)";
    ctx.lineWidth = 1;

    for (let x = 0; x <= width; x++) {
      const px = Math.round(drawOffsetX + x * tileScale) + 0.5;
      ctx.beginPath();
      ctx.moveTo(px, drawOffsetY);
      ctx.lineTo(px, drawOffsetY + height * tileScale);
      ctx.stroke();
    }

    for (let y = 0; y <= height; y++) {
      const py = Math.round(drawOffsetY + y * tileScale) + 0.5;
      ctx.beginPath();
      ctx.moveTo(drawOffsetX, py);
      ctx.lineTo(drawOffsetX + width * tileScale, py);
      ctx.stroke();
    }
  }

  if (els.showLabels.checked && tileScale >= 8 && state.grid.labels) {
    ctx.font = `${Math.max(10, Math.floor(tileScale * 0.45))}px Consolas, monospace`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#ffffff";
    ctx.strokeStyle = "rgba(0, 0, 0, 0.75)";
    ctx.lineWidth = Math.max(2, tileScale * 0.10);

    for (const label of state.grid.labels) {
      const p = toDrawPosition(bounds, label.x, label.y);
      const cx = drawOffsetX + (p.x + 0.5) * tileScale;
      const cy = drawOffsetY + (p.y + 0.5) * tileScale;
      ctx.strokeText(label.t, cx, cy);
      ctx.fillText(label.t, cx, cy);
    }
  }

  const active = state.hover ?? state.pinned;
  if (active && isWithinBounds(bounds, active.x, active.y)) {
    const p = toDrawPosition(bounds, active.x, active.y);
    ctx.strokeStyle = state.hover ? "#4fb3ff" : "#7ee787";
    ctx.lineWidth = Math.max(2, tileScale * 0.08);
    ctx.strokeRect(drawOffsetX + p.x * tileScale, drawOffsetY + p.y * tileScale, tileScale, tileScale);
  }
}

function eventToIndices(ev) {
  const rect = els.canvas.getBoundingClientRect();
  const bounds = getGridBounds(state.grid);
  const worldX = (ev.clientX - rect.left - state.viewOffsetX) / state.zoom;
  const worldY = (ev.clientY - rect.top - state.viewOffsetY) / state.zoom;
  const pixelsPerTile = getPixelsPerTile();
  return {
    x: bounds.minX + Math.floor(worldX / pixelsPerTile),
    y: bounds.maxY - Math.floor(worldY / pixelsPerTile),
  };
}

function getGridWidth(grid) {
  const bounds = getGridBounds(grid);
  return Math.max(1, bounds.maxX - bounds.minX + 1);
}

function getGridHeight(grid) {
  const bounds = getGridBounds(grid);
  return Math.max(1, bounds.maxY - bounds.minY + 1);
}

function getGridBounds(grid) {
  return grid.image ? grid.renderBounds : grid.bounds;
}

function getPixelsPerTile() {
  return state.grid?.image?.pixelsPerTile ?? 1;
}

function getTilePixelScale() {
  return getPixelsPerTile() * state.zoom;
}

function getCanvasBaseWidth(grid) {
  return grid.image ? grid.image.width : getGridWidth(grid);
}

function getCanvasBaseHeight(grid) {
  return grid.image ? grid.image.height : getGridHeight(grid);
}

function toDrawPosition(bounds, x, y) {
  return {
    x: x - bounds.minX,
    y: bounds.maxY - y,
  };
}

function isWithinBounds(bounds, x, y) {
  return x >= bounds.minX && x <= bounds.maxX && y >= bounds.minY && y <= bounds.maxY;
}

function findAreaCell(grid, x, y) {
  return grid.areas?.find(cell => cell.x === x && cell.y === y) ?? null;
}

function findLabel(labels, x, y) {
  return labels?.find(label => label.x === x && label.y === y) ?? null;
}

function findEntityCell(grid, x, y) {
  return grid.entities?.find(cell => cell.x === x && cell.y === y) ?? null;
}

function updateEntityInfo(entityCell) {
  if (!entityCell || !entityCell.entities || entityCell.entities.length === 0) {
    els.entityInfo.className = "info empty";
    els.entityInfo.textContent = "No entities on this tile.";
    return;
  }

  els.entityInfo.className = "info";
  els.entityInfo.innerHTML = entityCell.entities.map(entity =>
    row(entity.name, entity.prototypeId || "No prototype")).join("");
}

function applyCoordinateReference() {
  const indices = state.pinned ?? state.hover;
  if (!state.grid || !indices) {
    updateCoordinateStatus("Select or pin a tile before applying a coordinate reference.");
    return;
  }

  const x = Number.parseInt(els.coordRefX.value, 10);
  const y = Number.parseInt(els.coordRefY.value, 10);
  if (!Number.isInteger(x) || !Number.isInteger(y)) {
    updateCoordinateStatus("Enter valid integer X and Y coordinates.");
    return;
  }

  state.enteredCoordinates = { x, y };
  state.coordinateOffset = {
    x: x - indices.x,
    y: y - indices.y,
  };

  updateCoordinateStatus();
  updateInfoPanels();
  render();
}

function clearCoordinateReference(clearInputs = true) {
  state.enteredCoordinates = null;
  state.coordinateOffset = null;

  if (clearInputs) {
    els.coordRefX.value = "";
    els.coordRefY.value = "";
  }

  updateCoordinateStatus();
}

function getCalculatedCoordinates(indices) {
  if (!state.coordinateOffset) {
    return null;
  }

  return {
    x: indices.x + state.coordinateOffset.x,
    y: indices.y + state.coordinateOffset.y,
  };
}

function updateCoordinateStatus(message) {
  if (message) {
    els.coordStatus.className = "info";
    els.coordStatus.textContent = message;
    return;
  }

  if (!state.coordinateOffset || !state.enteredCoordinates) {
    els.coordStatus.className = "info empty";
    els.coordStatus.textContent = "Select or pin a tile, enter its coordinates, then apply the reference.";
    return;
  }

  const indices = state.pinned ?? state.hover;
  const calculated = indices ? getCalculatedCoordinates(indices) : null;
  const rows = [row("Reference", formatCoordinates(state.enteredCoordinates))];

  if (indices) {
    rows.push(row("Current Tile", `${indices.x}, ${indices.y}`));
  }

  if (calculated) {
    rows.push(row("Calculated", formatCoordinates(calculated)));
  }

  els.coordStatus.className = "info";
  els.coordStatus.innerHTML = rows.join("");
}

function formatCoordinates(coordinates) {
  return `${coordinates.x}, ${coordinates.y}`;
}

async function loadGridImage(grid) {
  if (!grid?.image?.file) {
    return null;
  }

  return await loadImageFile(grid.image.file);
}

async function loadImageFile(path) {
  const file = state.fileIndex.get(path);
  if (!file) {
    throw new Error(`Missing image file: ${path}`);
  }

  let url = state.objectUrls.get(path);
  if (!url) {
    url = URL.createObjectURL(file);
    state.objectUrls.set(path, url);
  }

  return await new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error(`Failed to load image: ${path}`));
    image.src = url;
  });
}

function clearObjectUrls() {
  for (const url of state.objectUrls.values()) {
    URL.revokeObjectURL(url);
  }

  state.objectUrls.clear();
}

function resizeCanvasViewport() {
  const width = Math.max(320, els.wrap.clientWidth);
  const height = Math.max(240, els.wrap.clientHeight);

  if (els.canvas.width !== width) {
    els.canvas.width = width;
  }

  if (els.canvas.height !== height) {
    els.canvas.height = height;
  }
}

function clampViewOffset() {
  if (!state.grid) {
    return;
  }

  const viewportWidth = els.canvas.width;
  const viewportHeight = els.canvas.height;
  const contentWidth = getCanvasBaseWidth(state.grid) * state.zoom;
  const contentHeight = getCanvasBaseHeight(state.grid) * state.zoom;

  state.viewOffsetX = clampAxis(state.viewOffsetX, viewportWidth, contentWidth);
  state.viewOffsetY = clampAxis(state.viewOffsetY, viewportHeight, contentHeight);
}

function clampAxis(offset, viewportSize, contentSize) {
  const padding = Math.max(240, Math.round(viewportSize * 0.4));
  const minOffset = viewportSize - contentSize - padding;
  const maxOffset = padding;
  return Math.max(minOffset, Math.min(maxOffset, offset));
}

function areaFlags(info) {
  const flags = [];
  if (info.cas) flags.push("CAS");
  if (info.mortarFire) flags.push("Mortar Fire");
  if (info.mortarPlacement) flags.push("Mortar Placement");
  if (info.lasing) flags.push("Lasing");
  if (info.medevac) flags.push("Medevac");
  if (info.paradropping) flags.push("Paradropping");
  if (info.orbitalBombard) flags.push("OB");
  if (info.supplyDrop) flags.push("Supply Drop");
  if (info.fulton) flags.push("Fulton");
  if (info.landingZone) flags.push("Landing Zone");
  return flags;
}

function row(key, value) {
  return `<div class="infoRow"><span class="infoKey">${escapeHtml(key)}:</span> ${escapeHtml(value)}</div>`;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}
