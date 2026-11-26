"use strict";

const $ = id => document.getElementById(id);
let cameras = [];
let players = [];
let currentView = "home";
let lastStatus = null;
let recordingsLoadVersion = 0;
let recordingsBySlot = [[], [], [], []];
let polling = null;
let trafficPolling = null;
let trafficPoints = [];
let scannedDevices = [];
let selectedCameraIds = new Set();
let mosaicInitialized = false;
let editingCameraId = null;
function mosaicLayout() {
  const value = $("gridLayout").value;
  const presets = {"1":[1,1],"2":[1,2],"4":[2,2],"9":[3,3],"3x4":[3,4],"4x3":[4,3],"16":[4,4]};
  const [rows,cols] = value === "custom" ? [Number($("gridRows").value),Number($("gridColumns").value)] : (presets[value] || presets["4"]);
  const valid = Number.isInteger(rows) && Number.isInteger(cols) && rows >= 1 && cols >= 1 && rows*cols <= 16;
  return {rows,cols,slots:valid?rows*cols:0,valid};
}
function saveLiveLayout() {
  try { localStorage.setItem("nvr-live-layout",JSON.stringify({layout:$("gridLayout").value,rows:$("gridRows").value,cols:$("gridColumns").value,ids:[...selectedCameraIds]})); } catch (_) {}
}
function restoreLiveLayout() {
  try {
    const saved = JSON.parse(localStorage.getItem("nvr-live-layout") || "null");
    if (!saved || ![...$("gridLayout").options].some(o=>o.value===saved.layout)) return;
    $("gridLayout").value=saved.layout;
    $("gridRows").value=saved.rows || 3;
    $("gridColumns").value=saved.cols || 4;
    if (!mosaicLayout().valid) $("gridLayout").value="4";
    $("gridCustomDimensions").classList.toggle("hidden",$("gridLayout").value!=="custom");
    if (Array.isArray(saved.ids) && saved.ids.length) {
      selectedCameraIds=new Set(saved.ids.slice(0,mosaicLayout().slots));
      mosaicInitialized=true;
    }
  } catch (_) {}
}
let sessionState = {setupRequired: true, passwordEnabled: false, authenticated: false};

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers: {"Content-Type": "application/json", "X-NVR-Request": "1", ...(options.headers || {})}
  });
  if (response.status === 401) {
    showLogin();
    throw new Error("Sessão expirada ou senha incorreta.");
  }
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.error || body.detail || "Erro HTTP " + response.status);
  }
  return response.status === 204 ? null : response.json();
}

function notify(message) {
  $("messages").textContent = message;
  $("messages").classList.remove("hidden");
  clearTimeout(notify.timer);
  notify.timer = setTimeout(() => $("messages").classList.add("hidden"), 7000);
}

function showLogin() {
  $("setup").classList.add("hidden");
  $("login").classList.remove("hidden");
  $("dashboard").classList.add("hidden");
  if (polling) clearInterval(polling);
  polling = null;
  if (trafficPolling) clearInterval(trafficPolling);
  trafficPolling = null;
  players.forEach(p => p.destroy?.());
  players = [];
  for (let i = 0; i < 4; i++) {
    const recorded = $("recordedVideo" + i);
    recorded.pause();
    recorded.removeAttribute("src");
    recorded.load();
  }
}

function showDashboard() {
  $("setup").classList.add("hidden");
  $("login").classList.add("hidden");
  $("logout").classList.toggle("hidden", !sessionState.passwordEnabled);
  $("dashboard").classList.remove("hidden");
  section("home");
  refresh();
  loadSuggestedNetworks();
  if (polling) clearInterval(polling);
  polling = setInterval(refresh, 8000);
  if (trafficPolling) clearInterval(trafficPolling);
  trafficPolling = setInterval(loadTraffic, 2000);
  loadTraffic();
}

function section(view) {
  const previous = currentView;
  currentView = view;
  ["home", "camera", "recordings", "discovery", "system"].forEach(name => {
    $(name + "View").classList.toggle("hidden", name !== view);
  });
  $("pageTitle").textContent = {
    home: "Início",
    camera: "Visualização",
    recordings: "Gravações",
    discovery: "Cadastro de câmeras",
    system: "Sistema"
  }[view];
  $("navHome").classList.toggle("selected", view === "home");
  $("navCameras").classList.toggle("selected", view === "camera");
  $("navDiscovery").classList.toggle("selected", view === "discovery");
  $("navRecordings").classList.toggle("selected", view === "recordings");
  $("navSystem").classList.toggle("selected", view === "system");
  $("addCamera").classList.toggle("hidden", view !== "discovery");
  if (previous === "camera" && view !== "camera") {
    players.forEach(p => p.destroy?.());
    players = [];
    $("cameraGrid").replaceChildren();
  }
  if (previous === "recordings" && view !== "recordings") {
    for (let i = 0; i < 4; i++) $("recordedVideo" + i).pause();
  }
  if (view === "camera" && previous !== "camera") renderCameras();
  if (view === "system") {
    updateSecurityForm();
    loadStorageSettings();
    checkNvrUpdate();
    loadNetworkAddresses();
    loadDesktopOptions();
  }
  if (view === "home") requestAnimationFrame(drawNetworkChart);
  if (view === "recordings") loadSelectedRecordings();
}

function renderHomeSummary(status) {
  if (!status) return;
  const root = $("homeCameraSummary");
  root.replaceChildren();
  const recording = new Set(status.recordingCameraIds || []);
  const previewing = new Set(status.previewCameraIds || []);
  if (!cameras.length) {
    root.append(element("p", "soft-text", "Nenhuma câmera cadastrada."));
  }
  for (const camera of cameras) {
    const item = element("div", "home-camera-line");
    item.append(element("b", "", camera.name));
    const statuses = element("div", "home-camera-states");
    statuses.append(element("span",
      recording.has(camera.id) ? "home-state active" : "home-state",
      recording.has(camera.id) ? "Gravando" :
      !status.storageConfigured ? "Disco pendente" :
      !camera.recordEnabled ? "Gravação desligada" :
      !camera.scheduledNow ? "Fora da agenda" : "Gravação indisponível"));
    statuses.append(element("span",
      previewing.has(camera.id) ? "home-state active" : "home-state",
      previewing.has(camera.id) ? "Ao vivo" :
      camera.previewEnabled ? "Visualização indisponível" : "Visualização desligada"));
    item.append(statuses);
    root.append(item);
  }
  $("homeStorageSummary").textContent = status.storageConfigured
    ? "Disco: " + status.recordingsPath + " · Limite: " + status.maxGb +
      " GB · Livre: " + status.freeGb + " GB."
    : "Nenhum disco de gravação selecionado. A visualização funciona, mas a gravação aguarda configuração em Sistema.";
}

async function refresh() {
  try {
    const [status, list] = await Promise.all([
      api("/api/status"), api("/api/cameras")
    ]);
    $("cameraCount").textContent = status.cameraCount;
    $("activeRecordings").textContent = status.recordingProcesses;
    $("activePreviews").textContent = status.previewProcesses;
    lastStatus = status;
    $("freeSpace").textContent = status.storageConfigured
      ? status.freeGb.toLocaleString("pt-BR") + " GB" : "Não definido";
    $("diskLimit").textContent = status.storageConfigured
      ? status.maxGb.toLocaleString("pt-BR") + " GB" : "Não definido";
    $("storagePath").textContent = status.recordingsPath || "Não configurada";
    $("storageSetupNotice").classList.toggle("hidden", !!status.storageConfigured);
    const rawVersion = String(status.version || "desconhecida");
    const [release, revision] = rawVersion.split("+");
    $("nvrVersion").textContent = "Versão " + release + (revision ? " · " + revision.slice(0, 8) : "");
    $("nvrVersion").title = "Build em execução: " + rawVersion;
    const oldSignature = JSON.stringify(cameras);
    const oldCameraNames = cameras.map(c => c.id + ":" + c.name).join("|");
    const newSignature = JSON.stringify(list);
    cameras = list;
    const knownIds = new Set(cameras.map(c => c.id));
    selectedCameraIds = new Set([...selectedCameraIds].filter(id => knownIds.has(id)));
    if (!mosaicInitialized && cameras.length) {
      const slots = mosaicLayout().slots;
      for (const c of cameras.slice(0, slots)) selectedCameraIds.add(c.id);
      mosaicInitialized = true;
    }
    renderHomeSummary(status);
    if ($("applyLinuxMonitor").classList.contains("hidden")) fetch("/api/system/linux-monitor",{credentials:"same-origin"}).then(r=>{if(r.ok) $("applyLinuxMonitor").classList.remove("hidden");}).catch(()=>{});
    renderCameraSelection();
    if (oldCameraNames !== cameras.map(c => c.id + ":" + c.name).join("|")) {
      renderRecordingsCameraOptions();
      renderMonitorCameraOptions();
      if (currentView === "recordings") loadSelectedRecordings();
    }
    renderRegisteredCameras();
    if (scannedDevices.length) renderFoundCameras();
    if (currentView === "camera" &&
        (oldSignature !== newSignature || !$("cameraGrid").children.length))
      renderCameras();
  } catch (err) {
    if (!$("dashboard").classList.contains("hidden")) notify(err.message);
  }
}

function element(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

function button(text, handler, className = "") {
  const b = element("button", className, text);
  b.type = "button";
  b.addEventListener("click", handler);
  return b;
}

function renderCameras() {
  players.forEach(p => p.destroy?.());
  players = [];
  const grid = $("cameraGrid");
  grid.replaceChildren();
  const slots = mosaicLayout().slots;
  const layout = mosaicLayout();
  if (!layout.valid) return;
  grid.dataset.cols = String(layout.cols);
  grid.style.gridTemplateColumns = "repeat(" + layout.cols + ",minmax(0,1fr))";
  grid.style.setProperty("--mosaic-rows",layout.rows);
  saveLiveLayout();
  const visible = cameras.filter(c => selectedCameraIds.has(c.id)).slice(0, slots);
  if (!visible.length) {
    grid.append(element("p", "empty", cameras.length
      ? "Selecione uma ou mais câmeras acima."
      : "Nenhuma câmera cadastrada. Cadastre uma câmera para começar."));
    return;
  }
  for (const c of visible) {
    const card = element("article", "camera live-tile");
    const wrap = element("div", "video-wrap");
    const video = element("video");
    video.autoplay = true;
    video.muted = true;
    video.playsInline = true;
    video.controls = false;
    video.addEventListener("dblclick", () => video.requestFullscreen?.());
    wrap.append(video);
    const fallback = element("div", "video-fallback", "Aguardando vídeo…");
    wrap.append(fallback);
    card.append(wrap);
    const name = element("div", "live-camera-label", c.name);
    card.append(name);

    if (c.previewEnabled && c.previewAudio) {
      const sound = button("Ouvir", () => {
        const activate = video.muted;
        document.querySelectorAll("#cameraGrid video").forEach(other => {
          if (other !== video) other.muted = true;
        });
        document.querySelectorAll(".live-sound").forEach(other => {
          if (other !== sound) other.textContent = "Ouvir";
        });
        video.muted = !activate;
        sound.textContent = activate ? "Silenciar" : "Ouvir";
        if (activate) video.play().catch(() => {});
      }, "live-sound");
      card.append(sound);
    }

    const url = "/hls/" + c.id + "/index.m3u8";
    if (!c.previewEnabled) {
      fallback.textContent = "Visualização desativada nas configurações desta câmera.";
    } else if (window.Hls && Hls.isSupported()) {
      const hls = new Hls({maxBufferLength: 12, lowLatencyMode: false});
      hls.loadSource(url);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        fallback.classList.add("hidden");
        video.play().catch(() => {});
      });
      hls.on(Hls.Events.ERROR, (_e, data) => {
        if (data.fatal) {
          fallback.textContent = "Aguardando transmissão RTSP…";
          fallback.classList.remove("hidden");
          setTimeout(() => {
            if (!hls.media) return;
            if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
            else hls.startLoad();
          }, 3500);
        }
      });
      players.push(hls);
    } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = url;
      video.addEventListener("playing", () => fallback.classList.add("hidden"));
    } else {
      fallback.textContent = "Navegador sem suporte a HLS. Verifique o hls.js.";
    }
    grid.append(card);
  }
  for(let i=visible.length;i<slots;i++) grid.append(element("article","camera mosaic-empty-slot","Sem câmera"));
}

function renderCameraSelection() {
  const root = $("cameraSelection");
  root.replaceChildren();
  if (!cameras.length) {
    root.append(element("span", "soft-text", "Cadastre câmeras para montar o mosaico."));
    return;
  }
  for (const camera of cameras) {
    const label = element("label", "camera-choice");
    const checkbox = element("input");
    checkbox.type = "checkbox";
    checkbox.checked = selectedCameraIds.has(camera.id);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        const slots = mosaicLayout().slots;
        if (selectedCameraIds.size >= slots) {
          checkbox.checked = false;
          notify("Este layout permite no máximo " + slots + " câmera(s). Desmarque outra ou escolha uma grade maior.");
          return;
        }
        selectedCameraIds.add(camera.id);
      } else {
        selectedCameraIds.delete(camera.id);
      }
      renderCameras();
    });
    label.append(checkbox, document.createTextNode(camera.name));
    root.append(label);
  }
}

function localDateKey(date = new Date()) {
  const pad = v => String(v).padStart(2, "0");
  return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate());
}

function clockSeconds(value) {
  const parts = String(value || "").split(":").map(Number);
  if (parts.length < 2 || parts.length > 3 || parts.some(x => !Number.isFinite(x)))
    return NaN;
  return parts[0] * 3600 + parts[1] * 60 + (parts[2] || 0);
}

function renderRecordingsCameraOptions() {
  for (let slot = 0; slot < 4; slot++) {
    const select = $("recordingCamera" + slot);
    const old = select.value;
    select.replaceChildren();
    const none = element("option", "", "— Sem câmera —");
    none.value = "";
    select.append(none);
    for (const camera of cameras) {
      const option = element("option", "", camera.name);
      option.value = camera.id;
      select.append(option);
    }
    select.value = old && cameras.some(c => c.id === old)
      ? old : (cameras[slot]?.id || "");
  }
}

function nearestRecording(files, reference, requireNearby = false) {
  const target = clockSeconds(reference);
  if (!files.length) return null;
  if (!Number.isFinite(target)) return requireNearby ? null : files[0];
  const timed = files.filter(f => Number.isFinite(clockSeconds(f.time)));
  if (!timed.length) return requireNearby ? null : files[0];
  // Prefer the segment that started before the chosen time; a later segment
  // might look numerically closer while actually skipping the desired moment.
  const before = timed.filter(f => clockSeconds(f.time) <= target);
  const closest = before.length ? before[before.length - 1] : timed[0];
  const distance = Math.abs(target - clockSeconds(closest.time));
  // Recording segments are nominally one minute; avoid silently showing
  // footage from unrelated times when four-camera sync is requested.
  return requireNearby && distance > 90 ? null : closest;
}

function recordingClipChanged(slot) {
  const clips = recordingsBySlot[slot];
  const file = clips.find(f => f.file === $("recordingClip" + slot).value);
  const link = $("recordingDownload" + slot);
  link.classList.toggle("hidden", !file);
  if (file) {
    link.href = file.url;
    link.download = file.file;
    $("recordingStatus" + slot).textContent = "Selecionado: " +
      (file.time || file.file) + " · " + (file.bytes / 1048576).toFixed(1) + " MB";
  } else {
    link.removeAttribute("href");
    $("recordingStatus" + slot).textContent =
      clips.length ? "Selecione um arquivo." : "Sem gravações para esta data.";
  }
}

async function loadSelectedRecordings() {
  const generation = ++recordingsLoadVersion;
  const date = $("recordingsDate").value;
  const ids = Array.from({length: 4}, (_, i) => $("recordingCamera" + i).value);
  for (let i = 0; i < 4; i++) {
    const video = $("recordedVideo" + i);
    video.pause();
    video.onloadedmetadata = null;
    video.removeAttribute("src");
    video.load();
    recordingsBySlot[i] = [];
    $("recordingClip" + i).replaceChildren();
    $("recordingDownload" + i).classList.add("hidden");
    $("recordingStatus" + i).textContent = ids[i] ? "Carregando arquivos…" : "Sem câmera selecionada";
  }
  if (!date) {
    $("recordingsFeedback").textContent = "Escolha uma data no calendário.";
    return;
  }
  if (!ids.some(Boolean)) {
    $("recordingsFeedback").textContent = "Selecione pelo menos uma câmera.";
    return;
  }

  $("recordingsFeedback").textContent = "Pesquisando gravações em " + date + "…";
  const uniqueIds = [...new Set(ids.filter(Boolean))];
  const results = new Map();
  await Promise.all(uniqueIds.map(async id => {
    try {
      const files = await api("/api/cameras/" + id + "/recordings?date=" +
        encodeURIComponent(date));
      results.set(id, {files: files.slice().reverse()});
    } catch (error) {
      results.set(id, {files: [], error: error.message});
    }
  }));
  if (generation !== recordingsLoadVersion || $("recordingsDate").value !== date) return;

  let available = 0;
  let errors = 0;
  for (let i = 0; i < 4; i++) {
    const response = results.get(ids[i]);
    const clips = response?.files || [];
    recordingsBySlot[i] = clips;
    const select = $("recordingClip" + i);
    select.replaceChildren();
    const blank = element("option", "", clips.length ? "Escolha o horário…" : "Nenhuma gravação");
    blank.value = "";
    select.append(blank);
    for (const clip of clips) {
      const option = element("option", "",
        (clip.time || clip.file) + " · " + (clip.bytes / 1048576).toFixed(1) + " MB");
      option.value = clip.file;
      select.append(option);
    }
    const suggestion = nearestRecording(clips, $("recordingsTime").value);
    select.value = suggestion?.file || "";
    recordingClipChanged(i);
    if (response?.error) {
      errors++;
      $("recordingStatus" + i).textContent = response.error;
    }
    if (clips.length) available++;
  }
  $("recordingsFeedback").textContent = available + " câmera(s) com gravações em " +
    date + ". Escolha arquivos individuais ou use Reproduzir até 4 no horário desejado." +
    (errors ? " Algumas câmeras não puderam ser consultadas." : "");
}

function playRecordingSlot(slot, file, targetSeconds = null) {
  if (!file) {
    $("recordingStatus" + slot).textContent = "Nenhum arquivo disponível para reproduzir.";
    return;
  }
  const video = $("recordedVideo" + slot);
  const select = $("recordingClip" + slot);
  select.value = file.file;
  recordingClipChanged(slot);
  video.pause();
  video.onloadedmetadata = () => {
    let offset = Number.isFinite(targetSeconds)
      ? targetSeconds - clockSeconds(file.time) : 0;
    if (!Number.isFinite(offset) || offset < 0) offset = 0;
    if (Number.isFinite(video.duration))
      offset = Math.min(offset, Math.max(0, video.duration - 0.2));
    if (offset > 0.5) {
      try { video.currentTime = offset; } catch (_) {}
    }
    video.play().catch(() => {
      $("recordingStatus" + slot).textContent =
        "Clique em ▶ no vídeo para reproduzir. Confira se o navegador suporta o codec.";
    });
  };
  video.src = file.url;
  video.load();
}

function playFourRecordings() {
  const clock = $("recordingsTime").value;
  const target = clockSeconds(clock);
  if (!Number.isFinite(target)) {
    $("recordingsFeedback").textContent = "Informe o horário desejado.";
    return;
  }
  let started = 0;
  for (let slot = 0; slot < 4; slot++) {
    const file = nearestRecording(recordingsBySlot[slot], clock, true);
    const video = $("recordedVideo" + slot);
    video.muted = true; // Avoid four simultaneous speakers.
    if (!file) {
      video.pause();
      video.removeAttribute("src");
      video.load();
      $("recordingStatus" + slot).textContent =
        $("recordingCamera" + slot).value
          ? "Sem gravação próxima de " + clock + " neste dia."
          : "Sem câmera selecionada.";
      continue;
    }
    playRecordingSlot(slot, file, target);
    started++;
  }
  $("recordingsFeedback").textContent = started
    ? "Iniciando " + started + " gravação(ões) próximas de " + clock +
      ". Sincronização aproximada; arquivos são segmentos independentes. " +
      "Use os controles de cada vídeo para ativar o som."
    : "Nenhuma das câmeras tem arquivos próximos de " + clock +
      ". Experimente outro horário ou selecione um arquivo individual.";
}

function td(row, value, className = "") {
  const cell = element("td", className, String(value ?? "—"));
  row.append(cell);
  return cell;
}

async function loadSuggestedNetworks() {
  try {
    const subnets = await api("/api/network/subnets");
    if (!$("scanSubnet").value && subnets.length)
      $("scanSubnet").value = subnets[0].cidr;
  } catch (_) { /* Manual subnet entry remains available. */ }
}

function scopeName(scopes, ip) {
  for (const entry of (scopes || "").split(/\s+/)) {
    const match = entry.match(/onvif:\/\/www\.onvif\.org\/(?:name|model)\/(.+)$/i);
    if (match) {
      try { return decodeURIComponent(match[1].replace(/_/g, " ")); }
      catch (_) { return match[1]; }
    }
  }
  return "Câmera IP " + ip;
}

function renderFoundCameras() {
  const root = $("discoveredRows");
  root.replaceChildren();
  $("scanResultCount").textContent = scannedDevices.length + " dispositivo(s)";
  if (!scannedDevices.length) {
    const tr = element("tr");
    td(tr, "Nenhum dispositivo encontrado.", "empty-table").colSpan = 7;
    root.append(tr);
    return;
  }
  scannedDevices.forEach((device, index) => {
    const tr = element("tr");
    const registered = cameras.some(c => c.ip === device.ip);
    td(tr, index + 1);
    td(tr, device.ip, "mono");
    td(tr, device.port);
    const name = td(tr, device.name);
    if (device.xAddr) name.title = device.xAddr;
    td(tr, device.onvif ? device.rtsp ? "ONVIF + RTSP" : "ONVIF" :
      device.onvifCandidate ? "Possível ONVIF" : "RTSP");
    const state = td(tr, registered ? "Cadastrada" : "Encontrada");
    state.classList.add(registered ? "green-text" : "soft-text");
    const actions = element("td");
    // A single DVR IP may expose several remote channels; never block adding another.
    const add = button(registered ? "+ Outro canal" : "Adicionar", () => openCameraForm(device), "small-button");
    actions.append(add);
    tr.append(actions);
    root.append(tr);
  });
}

function renderRegisteredCameras() {
  const root = $("registeredRows");
  root.replaceChildren();
  $("registeredCount").textContent = cameras.length + " câmera(s)";
  if (!cameras.length) {
    const row = element("tr");
    td(row, "Nenhuma câmera cadastrada.", "empty-table").colSpan = 9;
    root.append(row);
    return;
  }
  cameras.forEach((c, index) => {
    const tr = element("tr");
    td(tr, index + 1);
    td(tr, c.name);
    td(tr, c.ip, "mono");
    td(tr, c.port);
    td(tr, c.protocol);
    td(tr, c.recordEnabled ? "Ligada · " + (c.recordingStream === "sub" ? "Extra" : "Principal") : "Desligada");
    td(tr, c.previewEnabled ? "Ligada · " + (c.previewStream === "main" ? "Principal" : "Extra") : "Desligada");
    td(tr, c.scheduleMode === "window" ? c.scheduleStart + "–" + c.scheduleEnd : "24 horas");
    const actions = element("td", "row-actions");
    actions.append(button("Ver", () => {
      selectedCameraIds = new Set([c.id]);
      $("gridLayout").value = "1";
      renderCameraSelection();
      section("camera");
      renderCameras();
    }, "small-button"));
    actions.append(button("Editar", () => editCamera(c.id), "small-button"));
    if (c.ptzConfigured)
      actions.append(button("PTZ", () => editCamera(c.id, true), "small-button"));
    actions.append(button("Excluir", async () => {
      if (!confirm("Remover " + c.name + " do cadastro? Os arquivos já gravados serão preservados.")) return;
      try { await api("/api/cameras/" + c.id, {method: "DELETE"}); await refresh(); }
      catch (e) { notify(e.message); }
    }, "small-button danger-button"));
    tr.append(actions);
    root.append(tr);
  });
}

function cameraField(name) {
  return $("cameraForm").elements.namedItem(name);
}

function validIp(value) {
  const parts = value.trim().split(".");
  return parts.length === 4 && parts.every(p => /^\d{1,3}$/.test(p) && +p <= 255);
}

function deviceSettings() {
  const get = name => cameraField(name).value.trim();
  const ip = get("ip");
  const rtspPort = Number(get("rtspPort"));
  const httpPort = Number(get("httpPort"));
  const remoteChannel = Number(get("remoteChannel"));
  if (!validIp(ip)) throw new Error("Informe um endereço IPv4 válido.");
  if (![rtspPort, httpPort].every(p => Number.isInteger(p) && p >= 1 && p <= 65535))
    throw new Error("Informe portas de 1 a 65535.");
  if (!Number.isInteger(remoteChannel) || remoteChannel < 1 || remoteChannel > 256)
    throw new Error("Informe um canal remoto entre 1 e 256.");
  return {ip, rtspPort, httpPort, remoteChannel,
    username: get("username"), password: cameraField("password").value};
}

function defaultOnvifUrl(settings) {
  return "http://" + settings.ip + ":" + settings.httpPort + "/onvif/device_service";
}

function applyRtspCredentials(address, user, password) {
  if (!address) return address;
  try {
    const url = new URL(address);
    // Respect edited credentials, including intentionally removing a username.
    url.username = user || "";
    url.password = user ? password : "";
    return url.href;
  } catch (_) { return address; }
}

function intelbrasUrls(settings) {
  const auth = settings.username
    ? encodeURIComponent(settings.username) + ":" + encodeURIComponent(settings.password) + "@"
    : "";
  const base = "rtsp://" + auth + settings.ip + ":" + settings.rtspPort +
    "/cam/realmonitor?channel=" + settings.remoteChannel + "&subtype=";
  return {main: base + "0", sub: base + "1"};
}

function updateCameraProtocol() {
  const protocol = cameraField("protocol").value;
  $("protocolTip").textContent = {
    onvif: "ONVIF: preencha o IP e as credenciais; o botão Conectar consulta os perfis e preenche RTSP principal e extra.",
    intelbras: "Intelbras / Dahua: escolha o canal remoto. O botão monta os endereços RTSP padrão (não testa se a câmera transmite).",
    rtsp: "RTSP personalizado: informe os endereços principal e extra em Configurações avançadas."
  }[protocol];
  $("detectStreams").textContent = {
    onvif: "Conectar / detectar ONVIF",
    intelbras: "Montar RTSP Intelbras",
    rtsp: "Conferir RTSP"
  }[protocol];
  if (protocol === "rtsp") $("advancedStreams").open = true;
}

function openCameraForm(device = null) {
  editingCameraId = null;
  $("cameraDialogTitle").textContent = "Adição manual";
  $("saveCamera").textContent = "Salvar câmera";
  const form = $("cameraForm");
  form.reset();
  updateRecordingCodecWarning();
  $("scheduleMode").value = "always";
  updateScheduleMode();
  $("advancedStreams").open = false;
  $("streamFeedback").textContent = "";
  $("ptzCameraControls").classList.add("hidden");
  $("selectedDeviceHint").classList.toggle("hidden", !device);
  $("selectedDeviceHint").textContent = device
    ? "Dispositivo encontrado: " + device.ip + ". Informe usuário, senha e o canal remoto da transmissão."
    : "";
  cameraField("localChannel").value = cameras.length + 1;
  if (device) {
    cameraField("ip").value = device.ip;
    cameraField("name").value = device.name;
    if (device.xAddr) {
      cameraField("onvifUrl").value = device.xAddr;
      try {
        const url = new URL(device.xAddr);
        cameraField("httpPort").value = url.port || (url.protocol === "https:" ? 443 : 80);
      } catch (_) {}
    }
    if (device.rtsp) cameraField("rtspPort").value = device.port;
    if (!device.onvif && !device.onvifCandidate) cameraField("protocol").value = "rtsp";
  }
  updateCameraProtocol();
  $("cameraDialog").showModal();
}


function updateRecordingCodecWarning() {
  const mode = $("recordingCodec").value;
  $("recordingCodecWarning").textContent = mode === "copy"
    ? "Original: copia o codec da câmera sem recodificar; recomendado para gravação 24h."
    : mode === "h264"
      ? "H.264: o servidor converte o vídeo com FFmpeg/libx264. Usa mais CPU."
      : "H.265: converte com FFmpeg/libx265, exigindo muito mais CPU. Alguns navegadores não reproduzem HEVC.";
}

function updateScheduleMode() {
  const windowMode = $("scheduleMode").value === "window";
  $("scheduleWindow").classList.toggle("hidden", !windowMode);
  $("scheduleStart").required = windowMode;
  $("scheduleEnd").required = windowMode;
}

async function editCamera(id, focusPtz = false) {
  try {
    const camera = await api("/api/cameras/" + id);
    openCameraForm();
    editingCameraId = id;
    $("cameraDialogTitle").textContent = "Editar câmera";
    $("saveCamera").textContent = "Salvar alterações";
    $("ptzCameraControls").classList.toggle("hidden", !camera.onvifUrl);
    cameraField("name").value = camera.name;
    cameraField("mainRtsp").value = camera.mainRtsp;
    cameraField("subRtsp").value = camera.subRtsp || "";
    cameraField("onvifUrl").value = camera.onvifUrl || "";
    const main = new URL(camera.mainRtsp);
    cameraField("ip").value = main.hostname;
    cameraField("rtspPort").value = main.port || 554;
    cameraField("username").value = camera.onvifUser || decodeURIComponent(main.username);
    cameraField("password").value = camera.onvifPassword || decodeURIComponent(main.password);
    const selectedUrl = camera.onvifUrl ? new URL(camera.onvifUrl) : null;
    cameraField("httpPort").value = selectedUrl?.port || (selectedUrl?.protocol === "https:" ? 443 : 80);
    cameraField("remoteChannel").value = main.searchParams.get("channel") || "1";
    cameraField("protocol").value = main.pathname.toLowerCase() === "/cam/realmonitor"
      ? "intelbras" : camera.onvifUrl ? "onvif" : "rtsp";
    $("recordEnabled").checked = camera.recordEnabled;
    $("previewEnabled").checked = camera.previewEnabled;
    $("recordingStream").value = camera.recordingStream || "main";
    $("recordingCodec").value = camera.recordingCodec || "copy";
    updateRecordingCodecWarning();
    $("recordAudio").checked = !!camera.recordAudio;
    $("previewAudio").checked = !!camera.previewAudio;
    $("previewStream").value = camera.previewStream || "sub";
    $("scheduleMode").value = camera.scheduleMode || "always";
    $("scheduleStart").value = camera.scheduleStart || "00:00";
    $("scheduleEnd").value = camera.scheduleEnd || "00:00";
    const activeDays = new Set(camera.scheduleDays || [0,1,2,3,4,5,6]);
    document.querySelectorAll('input[name="scheduleDay"]').forEach(
      input => input.checked = activeDays.has(Number(input.value)));
    updateScheduleMode();
    updateCameraProtocol();
    $("advancedStreams").open = true;
    if (focusPtz && camera.onvifUrl) {
      $("ptzCameraControls").scrollIntoView({behavior: "smooth", block: "center"});
    }
  } catch (error) {
    notify("Não foi possível editar a câmera: " + error.message);
  }
}

async function detectOnvifStreams() {
  const protocol = cameraField("protocol").value;
  const b = $("detectStreams");
  b.disabled = true;
  $("streamFeedback").textContent = "";
  try {
    const settings = deviceSettings();
    if (protocol === "intelbras") {
      const urls = intelbrasUrls(settings);
      cameraField("mainRtsp").value = urls.main;
      cameraField("subRtsp").value = urls.sub;
      $("advancedStreams").open = true;
      $("streamFeedback").textContent =
        "Endereços padrão do canal " + settings.remoteChannel +
        " preparados. A conexão de vídeo ainda não foi testada; confira se o seu modelo utiliza esse padrão.";
      return;
    }
    if (protocol === "rtsp") {
      $("advancedStreams").open = true;
      const main = cameraField("mainRtsp").value.trim();
      if (!/^rtsps?:\/\//i.test(main))
        throw new Error("Informe a URL RTSP principal completa em Configurações avançadas.");
      $("streamFeedback").textContent =
        "URL RTSP informada. A conectividade será verificada ao iniciar a câmera, após salvar.";
      return;
    }

    const endpoint = cameraField("onvifUrl").value.trim() || defaultOnvifUrl(settings);
    cameraField("onvifUrl").value = endpoint;
    b.textContent = "Consultando perfis ONVIF…";
    $("streamFeedback").textContent = "Consultando a câmera pelo ONVIF…";
    const data = await api("/api/onvif/streams", {
      method: "POST",
      body: JSON.stringify({
        onvifUrl: endpoint,
        username: settings.username,
        password: settings.password
      })
    });
    cameraField("mainRtsp").value = data.mainRtsp;
    cameraField("subRtsp").value = data.subRtsp || "";
    $("advancedStreams").open = true;
    $("streamFeedback").textContent = data.profiles.length > 1
      ? data.profiles.length + " perfis encontrados. RTSP principal e extra preenchidos; confira os campos."
      : "Um perfil encontrado. Se necessário, informe o RTSP extra manualmente.";
  } catch (e) {
    $("advancedStreams").open = true;
    $("streamFeedback").textContent = e.message;
  } finally {
    b.disabled = false;
    updateCameraProtocol();
  }
}

function cameraPayload() {
  const settings = deviceSettings();
  const protocol = cameraField("protocol").value;
  let mainRtsp = cameraField("mainRtsp").value.trim();
  let subRtsp = cameraField("subRtsp").value.trim();
  if (protocol === "intelbras" && !mainRtsp) {
    const urls = intelbrasUrls(settings);
    mainRtsp = urls.main;
    if (!subRtsp) subRtsp = urls.sub;
  }
  if (!/^rtsps?:\/\//i.test(mainRtsp)) {
    $("advancedStreams").open = true;
    throw new Error(protocol === "onvif"
      ? "Clique em Conectar / detectar ONVIF antes de salvar, ou informe manualmente o RTSP principal."
      : "Informe o endereço RTSP principal válido em Configurações avançadas.");
  }
  if (subRtsp && !/^rtsps?:\/\//i.test(subRtsp))
    throw new Error("O endereço RTSP extra precisa começar com rtsp:// ou rtsps://.");
  mainRtsp = applyRtspCredentials(mainRtsp, settings.username, settings.password);
  subRtsp = applyRtspCredentials(subRtsp, settings.username, settings.password);
  const onvifUrl = cameraField("onvifUrl").value.trim() ||
    (protocol === "onvif" ? defaultOnvifUrl(settings) : "");
  return {
    name: cameraField("name").value.trim(),
    mainRtsp,
    subRtsp: subRtsp || null,
    onvifUrl: onvifUrl || null,
    onvifUser: onvifUrl ? settings.username : null,
    onvifPassword: onvifUrl ? settings.password : null,
    recordEnabled: $("recordEnabled").checked,
    previewEnabled: $("previewEnabled").checked,
    recordingStream: $("recordingStream").value,
    recordingCodec: $("recordingCodec").value,
    recordAudio: $("recordAudio").checked,
    previewAudio: $("previewAudio").checked,
    previewStream: $("previewStream").value,
    scheduleMode: $("scheduleMode").value,
    scheduleDays: [...document.querySelectorAll('input[name="scheduleDay"]:checked')]
      .map(input => Number(input.value)),
    scheduleStart: $("scheduleStart").value,
    scheduleEnd: $("scheduleEnd").value
  };
}

async function discover() {
  const b = $("discover");
  const mode = $("scanMode").value;
  const subnet = $("scanSubnet").value.trim();
  if (!subnet) {
    $("scanFeedback").textContent = "Informe a faixa da rede (por exemplo, 192.168.10.0/24).";
    return;
  }
  b.disabled = true;
  b.textContent = "Buscando…";
  $("scanFeedback").textContent = "Procurando dispositivos na rede local. Isso pode levar alguns segundos…";
  scannedDevices = [];
  renderFoundCameras();
  try {
    const requests = [];
    if (mode !== "rtsp")
      requests.push(api("/api/discover/onvif", {method: "POST"}).then(devices => ({type: "onvif", devices})));
    requests.push(api("/api/discover/rtsp", {
      method: "POST", body: JSON.stringify({cidr: subnet, mode})
    }).then(devices => ({type: "ports", devices})));
    const settled = await Promise.allSettled(requests);
    const merged = new Map();
    const errors = [];
    for (const outcome of settled) {
      if (outcome.status === "rejected") {
        errors.push(outcome.reason?.message || "Erro desconhecido");
        continue;
      }
      for (const d of outcome.value.devices) {
        const existing = merged.get(d.ip) || {
          ip: d.ip, port: "—", name: "Câmera IP " + d.ip,
          onvif: false, onvifCandidate: false, rtsp: false, xAddr: ""
        };
        if (outcome.value.type === "onvif") {
          existing.onvif = true;
          existing.xAddr = d.xAddr;
          existing.name = scopeName(d.scopes, d.ip);
          try { existing.port = new URL(d.xAddr).port || (d.xAddr.startsWith("https:") ? 443 : 80); }
          catch (_) {}
        } else if (d.protocol === "ONVIF_CANDIDATE") {
          existing.onvifCandidate = true;
          if (!existing.xAddr) {
            existing.xAddr = "http://" + d.ip + ":" + d.port + "/onvif/device_service";
            if (!existing.rtsp) existing.port = d.port;
          }
        } else {
          existing.rtsp = true;
          existing.port = d.port;
        }
        merged.set(d.ip, existing);
      }
    }
    scannedDevices = [...merged.values()].sort((a, b) =>
      a.ip.localeCompare(b.ip, undefined, {numeric: true}));
    renderFoundCameras();
    $("scanFeedback").textContent = scannedDevices.length
      ? scannedDevices.length + " dispositivo(s) encontrado(s). Clique em Adicionar para configurar."
      : "Nenhum dispositivo encontrado. Verifique ONVIF, permissões de rede, IP e portas RTSP.";
    if (errors.length) $("scanFeedback").textContent += " Aviso: " + errors.join(" | ");
  } catch (e) {
    $("scanFeedback").textContent = e.message;
  } finally {
    b.disabled = false;
    b.textContent = "⌕ Buscar dispositivos";
  }
}



async function loadNetworkAddresses() {
  const root = $("systemNetworkAddresses");
  root.replaceChildren();
  try {
    const data = await api("/api/system/network");
    const addresses = (data.urls || []);
    if (!addresses.length) {
      root.append(element("p", "soft-text", "Nenhuma interface com IPv4 privado ativo foi encontrada."));
    }
    for (const entry of addresses) {
      const row = element("div", "network-address-row");
      const link = element("a", "network-address", entry.url);
      link.href = entry.url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      row.append(link, element("span", "soft-text", entry.interfaceName));
      row.append(button("Copiar", () => {
        navigator.clipboard?.writeText(entry.url).then(() => notify("Endereço copiado."))?.catch(
          () => notify("Selecione e copie o endereço acima."));
      }, "small-button"));
      root.append(row);
    }
    $("systemNetworkStatus").textContent = data.installed
      ? (data.lanListening
        ? "Instalado: o programa aceita conexões locais. O firewall é configurado pelo instalador para redes privadas."
        : "Instalado, mas a escuta de rede foi alterada manualmente. Verifique as configurações.")
      : "Execução manual: para habilitar acesso e inicialização automática, utilize o instalador Setup.exe.";
  } catch (error) {
    $("systemNetworkStatus").textContent = error.message;
  }
}

function renderMonitorCameraOptions() {
  const select = $("monitorCamera");
  const previous = select.value;
  select.replaceChildren();
  const first = element("option", "", "Selecione uma câmera…");
  first.value = "";
  select.append(first);
  for (const camera of cameras.filter(c => c.previewEnabled)) {
    const option = element("option", "", camera.name);
    option.value = camera.id;
    select.append(option);
  }
  if (cameras.some(c => c.id === previous && c.previewEnabled)) select.value = previous;
}

function updateMonitorFields() {
  const enabled = $("startWithWindows").checked;
  if (!enabled) $("monitorEnabled").checked = false;
  $("monitorEnabled").disabled = !enabled;
  $("monitorCamera").disabled = !$("monitorEnabled").checked;
}

async function loadDesktopOptions() {
  try {
    const config = await api("/api/system/windows");
    renderMonitorCameraOptions();
    $("startWithWindows").checked = !!config.startWithWindows;
    $("monitorEnabled").checked = !!config.monitorEnabled;
    $("monitorCamera").value = config.monitorCameraId || "";
    updateMonitorFields();
    $("startWithWindows").disabled = !config.installed;
    $("monitorEnabled").disabled = !config.installed || !config.startWithWindows;
    $("saveDesktopOptions").disabled = !config.installed;
    $("lockLiveCamera").disabled = !config.installed;
    $("desktopOptionsFeedback").textContent = config.note || "";
  } catch (error) {
    $("desktopOptionsFeedback").textContent = error.message;
  }
}

async function saveDesktopOptions(startWithWindows, monitorEnabled, monitorCameraId) {
  const result = await api("/api/system/windows", {
    method: "POST",
    body: JSON.stringify({startWithWindows, monitorEnabled,
      monitorCameraId: monitorCameraId || null})
  });
  return result;
}

function drawNetworkChart() {
  if (currentView !== "home") return;
  const canvas = $("networkChart");
  const bounds = canvas.getBoundingClientRect();
  const width = Math.max(300, Math.round(bounds.width));
  const height = 180;
  const density = Math.min(2, window.devicePixelRatio || 1);
  canvas.width = Math.round(width * density);
  canvas.height = Math.round(height * density);
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  ctx.setTransform(density, 0, 0, density, 0, 0);
  ctx.clearRect(0, 0, width, height);
  const left = 47, top = 12, right = 12, bottom = 27;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;
  const values = trafficPoints.slice(-90);
  const rawPeak = Math.max(0.2, ...values.flatMap(p => [
    Number(p.downloadMbps) || 0, Number(p.uploadMbps) || 0]));
  const peak = Math.ceil(rawPeak * 2) / 2;

  ctx.strokeStyle = "#304457";
  ctx.fillStyle = "#96aabd";
  ctx.lineWidth = 1;
  ctx.font = "11px Segoe UI, sans-serif";
  ctx.textAlign = "right";
  for (let i = 0; i <= 4; i++) {
    const y = top + plotHeight * i / 4;
    ctx.beginPath();
    ctx.moveTo(left, y);
    ctx.lineTo(width - right, y);
    ctx.stroke();
    ctx.fillText((peak * (1 - i / 4)).toFixed(1), left - 8, y + 4);
  }
  ctx.textAlign = "center";
  if (values.length) {
    const xLabels = [0, Math.floor((values.length - 1) / 2), values.length - 1];
    for (const index of xLabels) {
      const x = left + plotWidth * index / Math.max(1, values.length - 1);
      ctx.fillText(values[index].time, x, height - 7);
    }
  }

  function pathFor(key, color) {
    if (!values.length) return;
    ctx.strokeStyle = color;
    ctx.lineWidth = 2.1;
    ctx.lineJoin = "round";
    ctx.beginPath();
    values.forEach((point, i) => {
      const x = left + plotWidth * i / Math.max(1, values.length - 1);
      const y = top + plotHeight * (1 - (Number(point[key]) || 0) / peak);
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    });
    ctx.stroke();
  }
  pathFor("downloadMbps", "#6bc5ed");
  pathFor("uploadMbps", "#61d0ac");
}

async function loadTraffic() {
  if (currentView !== "home" || $("dashboard").classList.contains("hidden")) return;
  try {
    const traffic = await api("/api/system/traffic");
    trafficPoints = traffic.points || [];
    $("netDownload").textContent = Number(traffic.downloadMbps || 0).toLocaleString("pt-BR",
      {maximumFractionDigits: 2});
    $("netUpload").textContent = Number(traffic.uploadMbps || 0).toLocaleString("pt-BR",
      {maximumFractionDigits: 2});
    $("networkChartStatus").textContent = trafficPoints.length
      ? "Últimos " + Math.round(trafficPoints.length * traffic.sampleIntervalSeconds) +
        " segundos · Valores em megabits por segundo (Mbps)."
      : "Coletando as primeiras amostras de rede…";
    drawNetworkChart();
  } catch (error) {
    $("networkChartStatus").textContent = "Tráfego indisponível: " + error.message;
  }
}

async function loadStorageSettings() {
  try {
    const [settings, drives] = await Promise.all([
      api("/api/storage/settings"), api("/api/storage/drives")
    ]);
    const select = $("recordingDrive");
    select.replaceChildren();
    const selected = settings.drives || (settings.drive ? [settings.drive] : []);
    for (const drive of drives) {
      const label = document.createElement("label");
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = drive.letter;
      input.checked = selected.includes(drive.letter);
      label.style.display = "block";
      label.append(input, document.createTextNode(" " + (drive.display || drive.letter + ":") +
        " — " + (drive.label || "Disco local") + " · Livre: " + drive.freeGb + " GB / " + drive.sizeGb + " GB"));
      select.append(label);
    }
    $("storageSegmentSeconds").value = String(settings.segmentSeconds || 60);
    if (settings.configured) {
      $("storageMaxGb").value = settings.maxGb;
      $("storageFreeGb").value = settings.minFreeGb;
    }
    $("storageFeedback").textContent = settings.configured
      ? "Diretórios atuais: " + (settings.paths || [settings.path]).join(", ") + ". Alterações ficam salvas no NVR."
      : "Nenhuma unidade definida ainda. Escolha um ou mais discos e salve para começar a gravar.";
  } catch (error) {
    $("storageFeedback").textContent = error.message;
  }
}

function showSetup() {
  $("login").classList.add("hidden");
  $("dashboard").classList.add("hidden");
  $("setup").classList.remove("hidden");
  $("setupError").textContent = "";
  $("setupForm").reset();
  updateSetupMode();
}

function updateSetupMode() {
  const usePassword = document.querySelector('input[name="setupMode"]:checked')?.value === "password";
  $("setupPasswordFields").classList.toggle("hidden", !usePassword);
  $("setupPassword").required = usePassword;
  $("setupPasswordConfirm").required = usePassword;
}

function updateSecurityForm() {
  const form = $("securityForm");
  const mode = sessionState.passwordEnabled ? "password" : "none";
  form.querySelector('input[name="securityMode"][value="' + mode + '"]').checked = true;
  $("securityCurrentStatus").textContent = sessionState.passwordEnabled
    ? "Situação atual: acesso protegido por senha."
    : "Situação atual: acesso local sem senha.";
  $("securityCurrentPasswordRow").classList.toggle("hidden", !sessionState.passwordEnabled);
  $("currentSecurityPassword").value = "";
  $("newSecurityPassword").value = "";
  $("newSecurityPasswordConfirm").value = "";
  $("securityError").textContent = "";
  updateSecurityMode();
}

function updateSecurityMode() {
  const usePassword = document.querySelector('input[name="securityMode"]:checked')?.value === "password";
  $("securityNewPasswordFields").classList.toggle("hidden", !usePassword);
  $("newSecurityPassword").required = usePassword;
  $("newSecurityPasswordConfirm").required = usePassword;
}

function validPasswordPair(password, confirmation) {
  if (password.length < 4 || password.length > 8)
    throw new Error("A senha precisa ter entre 4 e 8 caracteres.");
  if (password !== confirmation) throw new Error("As duas senhas não coincidem.");
}

$("loginForm").addEventListener("submit", async e => {
  e.preventDefault();
  try {
    await api("/api/login", {
      method: "POST", body: JSON.stringify({password: $("password").value})
    });
    $("loginError").textContent = "";
    $("password").value = "";
    showDashboard();
  } catch (err) { $("loginError").textContent = err.message; }
});
$("logout").addEventListener("click", async () => {
  try { await api("/api/logout", {method: "POST"}); } catch (_) {}
  showLogin();
});

function changeLiveLayout() {
  $("gridCustomDimensions").classList.toggle("hidden",$("gridLayout").value!=="custom");
  const slots = mosaicLayout().slots;
  if (!slots) { notify("Escolha linhas e colunas com até 16 posições."); return; }
  const selected = [...selectedCameraIds].slice(0, slots);
  selectedCameraIds = new Set(selected);
  for (const c of cameras) {
    if (selectedCameraIds.size >= slots) break;
    selectedCameraIds.add(c.id);
  }
  renderCameraSelection();
  renderCameras();
}
$("gridLayout").addEventListener("change",changeLiveLayout);
$("gridRows").addEventListener("change",changeLiveLayout);
$("gridColumns").addEventListener("change",changeLiveLayout);
$("fullscreenMosaic").addEventListener("click",()=> {
  const grid=$("cameraGrid");
  if(document.fullscreenElement) document.exitFullscreen?.();
  else grid.requestFullscreen?.().catch(()=>notify("Não foi possível abrir tela cheia."));
});
restoreLiveLayout();
$("applyLinuxMonitor").addEventListener("click", async ()=>{
  const layout=mosaicLayout();
  if(!layout.valid) return notify("Mosaico invalido.");
  const ids=cameras.filter(c=>selectedCameraIds.has(c.id)&&c.previewEnabled).map(c=>c.id).slice(0,layout.slots);
  try {
    await api("/api/system/linux-monitor",{method:"POST",body:JSON.stringify({rows:layout.rows,columns:layout.cols,cameraIds:ids})});
    notify("Mosaico salvo no servidor.");
  } catch(e){notify(e.message);}
});
$("storageForm").addEventListener("submit", async event => {
  event.preventDefault();
  const driveLetters = [...$("recordingDrive").querySelectorAll("input:checked")].map(input => input.value);
  if (!driveLetters.length) {
    $("storageFeedback").textContent = "Nenhum disco local disponível.";
    return;
  }
  try {
    const answer = await api("/api/storage/settings", {
      method: "POST",
      body: JSON.stringify({
        driveLetter: driveLetters[0],
        driveLetters,
        maxGb: Number($("storageMaxGb").value),
        segmentSeconds: Number($("storageSegmentSeconds").value),
        minFreeGb: Number($("storageFreeGb").value)
      })
    });
    $("storageFeedback").textContent =
      "Configuração salva em " + answer.path +
      ". O gravador começará a usar este local; os arquivos antigos não são movidos.";
    await refresh();
  } catch (error) {
    $("storageFeedback").textContent = error.message;
  }
});

async function checkNvrUpdate() {
  const button = $("checkNvrUpdate");
  button.disabled = true;
  $("maintenanceFeedback").textContent = "Verificando o executável publicado…";
  try {
    const status = await api("/api/system/update/status");
    $("applyNvrUpdate").disabled = !status.available;
    $("maintenanceFeedback").textContent = status.message +
      (status.updateSource ? " Origem: " + status.updateSource : "");
  } catch (error) {
    $("maintenanceFeedback").textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function followNvrRestart(message) {
  $("maintenanceFeedback").textContent = message +
    " Reconectando ao NVR após a reinicialização…";
  $("restartNvr").disabled = true;
  $("applyNvrUpdate").disabled = true;
  let attempts = 0;
  // Give the original process time to exit before accepting a response.
  setTimeout(() => {
    const checker = setInterval(async () => {
      attempts++;
      try {
        const response = await fetch("/api/session", {cache: "no-store"});
        if (response.ok) {
          clearInterval(checker);
          window.location.reload();
        }
      } catch (_) { /* NVR is between the two processes. */ }
      if (attempts >= 45) {
        clearInterval(checker);
        $("maintenanceFeedback").textContent =
          "O processo ainda não respondeu. Verifique o PowerShell do servidor.";
      }
    }, 2000);
  }, 3000);
}

$("refreshNvr").addEventListener("click", () => window.location.reload());
$("checkNvrUpdate").addEventListener("click", checkNvrUpdate);
$("restartNvr").addEventListener("click", async () => {
  if (!confirm("Reiniciar o processo NVR agora? A visualização e a gravação serão interrompidas brevemente. O Windows NÃO será reiniciado.")) return;
  try {
    const result = await api("/api/system/restart", {method: "POST"});
    followNvrRestart(result.message || "Reinício solicitado.");
  } catch (error) {
    $("maintenanceFeedback").textContent = error.message;
  }
});
$("applyNvrUpdate").addEventListener("click", async () => {
  if (!confirm("Aplicar a versão publicada e reiniciar o NVR? As gravações serão interrompidas durante a troca.")) return;
  try {
    const result = await api("/api/system/update/apply", {method: "POST"});
    followNvrRestart(result.message || "Atualização em andamento.");
  } catch (error) {
    $("maintenanceFeedback").textContent = error.message;
  }
});

$("startWithWindows").addEventListener("change", updateMonitorFields);
$("monitorEnabled").addEventListener("change", updateMonitorFields);
$("desktopOptionsForm").addEventListener("submit", async event => {
  event.preventDefault();
  const start = $("startWithWindows").checked;
  const monitor = $("monitorEnabled").checked;
  const cameraId = $("monitorCamera").value;
  if (monitor && !cameraId) {
    $("desktopOptionsFeedback").textContent = "Selecione qual câmera será exibida no monitor.";
    return;
  }
  $("saveDesktopOptions").disabled = true;
  $("desktopOptionsFeedback").textContent = "Aplicando as opções no Windows…";
  try {
    await saveDesktopOptions(start, monitor, cameraId);
    $("desktopOptionsFeedback").textContent =
      monitor
        ? "Salvo. O gravador iniciará no Windows e a câmera escolhida abrirá em tela cheia no próximo login."
        : start
          ? "Salvo. O gravador iniciará com o Windows sem abrir a visualização automaticamente."
          : "Salvo. O gravador não iniciará após o próximo reinício. A execução atual continua ativa.";
  } catch (error) {
    $("desktopOptionsFeedback").textContent = error.message;
  } finally {
    $("saveDesktopOptions").disabled = false;
  }
});
$("lockLiveCamera").addEventListener("click", async () => {
  const camera = cameras.find(c => selectedCameraIds.has(c.id) && c.previewEnabled);
  if (!camera) {
    notify("Selecione uma câmera com visualização habilitada.");
    return;
  }
  try {
    await saveDesktopOptions(true, true, camera.id);
    $("gridLayout").value = "1";
    selectedCameraIds = new Set([camera.id]);
    renderCameraSelection();
    renderCameras();
    notify(camera.name + " fixada no monitor. Ao entrar novamente no Windows, ela abrirá automaticamente em tela cheia. Para visualizar agora, abra Sistema > Monitor.");
  } catch (error) { notify(error.message); }
});
window.addEventListener("resize", drawNetworkChart);

$("navHome").addEventListener("click", () => section("home"));
$("navCameras").addEventListener("click", () => section("camera"));
$("navDiscovery").addEventListener("click", () => section("discovery"));
$("navRecordings").addEventListener("click", () => section("recordings"));
$("navSystem").addEventListener("click", () => section("system"));
$("addCamera").addEventListener("click", () => section("discovery"));
$("manualAdd").addEventListener("click", () => openCameraForm());
$("registeredRefresh").addEventListener("click", refresh);
$("detectStreams").addEventListener("click", detectOnvifStreams);
$("cameraProtocol").addEventListener("change", updateCameraProtocol);
$("scheduleMode").addEventListener("change", updateScheduleMode);
$("recordingCodec").addEventListener("change", updateRecordingCodecWarning);
$("closeDialog").addEventListener("click", () => $("cameraDialog").close());
$("cancelAdd").addEventListener("click", () => $("cameraDialog").close());
$("recordingsDate").value = localDateKey();
$("recordingsDate").addEventListener("change", loadSelectedRecordings);
$("refreshRecordings").addEventListener("click", loadSelectedRecordings);
$("playAllRecordings").addEventListener("click", playFourRecordings);
$("pauseAllRecordings").addEventListener("click", () => {
  for (let slot = 0; slot < 4; slot++) $("recordedVideo" + slot).pause();
  $("recordingsFeedback").textContent = "Reprodução pausada nas quatro câmeras.";
});
for (let slot = 0; slot < 4; slot++) {
  $("recordingCamera" + slot).addEventListener("change", loadSelectedRecordings);
  $("recordingClip" + slot).addEventListener("change", () => recordingClipChanged(slot));
  $("recordingPlay" + slot).addEventListener("click", () => {
    const file = recordingsBySlot[slot].find(x => x.file === $("recordingClip" + slot).value);
    playRecordingSlot(slot, file);
  });
  $("recordedVideo" + slot).addEventListener("ended", () => {
    const clips = recordingsBySlot[slot];
    const selected = $("recordingClip" + slot).value;
    const currentIndex = clips.findIndex(x => x.file === selected);
    if (currentIndex < 0 || currentIndex + 1 >= clips.length) return;
    const current = clips[currentIndex], next = clips[currentIndex + 1];
    const distance = clockSeconds(next.time) - clockSeconds(current.time);
    // Continue only to adjacent one-minute segments. Never jump across a gap.
    if (distance > 0 && distance <= 130) playRecordingSlot(slot, next);
  });
  $("recordedVideo" + slot).addEventListener("error", () => {
    if ($("recordedVideo" + slot).getAttribute("src")) {
      $("recordingStatus" + slot).textContent =
        "Erro ao abrir o arquivo. Verifique se o navegador aceita H.264/H.265 ou tente baixar.";
    }
  });
}
$("homeViewLive").addEventListener("click", () => section("camera"));
$("homeViewRecordings").addEventListener("click", () => section("recordings"));
$("homeOpenSystem").addEventListener("click", () => section("system"));
document.querySelectorAll("#ptzCameraControls [data-ptz]").forEach(control =>
  control.addEventListener("click", async () => {
    if (!editingCameraId) return;
    control.disabled = true;
    try {
      await api("/api/cameras/" + editingCameraId + "/ptz", {
        method: "POST", body: JSON.stringify({action: control.dataset.ptz})
      });
      $("streamFeedback").textContent = "Comando PTZ: " + control.textContent;
    } catch (error) {
      $("streamFeedback").textContent = "PTZ: " + error.message;
    } finally { control.disabled = false; }
  }));
$("diagnosePtz").addEventListener("click", async () => {
  if (!editingCameraId) return;
  try {
    const data = await api("/api/cameras/" + editingCameraId + "/ptz/check");
    $("streamFeedback").textContent = data.message;
  } catch (error) {
    $("streamFeedback").textContent = "PTZ: " + error.message;
  }
});
$("discover").addEventListener("click", discover);
$("cameraForm").addEventListener("submit", async e => {
  e.preventDefault();
  try {
    const data = cameraPayload();
    await api(editingCameraId ? "/api/cameras/" + editingCameraId : "/api/cameras",
      {method: editingCameraId ? "PUT" : "POST", body: JSON.stringify(data)});
    $("cameraDialog").close();
    await refresh();
    section("discovery");
    $("scanFeedback").textContent = editingCameraId
      ? "Câmera atualizada. O gravador aplicará as alterações automaticamente."
      : "Câmera cadastrada. Visualização e gravação seguem as opções definidas.";
    editingCameraId = null;
  } catch (err) { notify(err.message); }
});

document.querySelectorAll('input[name="setupMode"]').forEach(r => r.addEventListener("change", updateSetupMode));
document.querySelectorAll('input[name="securityMode"]').forEach(r => r.addEventListener("change", updateSecurityMode));

$("setupForm").addEventListener("submit", async e => {
  e.preventDefault();
  const usePassword = document.querySelector('input[name="setupMode"]:checked').value === "password";
  const password = $("setupPassword").value;
  try {
    if (usePassword) validPasswordPair(password, $("setupPasswordConfirm").value);
    const result = await api("/api/setup", {
      method: "POST",
      body: JSON.stringify({usePassword, password: usePassword ? password : null})
    });
    sessionState = {setupRequired: false, authenticated: true, passwordEnabled: result.passwordEnabled};
    $("setupPassword").value = "";
    $("setupPasswordConfirm").value = "";
    showDashboard();
  } catch (error) {
    $("setupError").textContent = error.message;
  }
});

$("securityForm").addEventListener("submit", async e => {
  e.preventDefault();
  const usePassword = document.querySelector('input[name="securityMode"]:checked').value === "password";
  const password = $("newSecurityPassword").value;
  try {
    if (usePassword) validPasswordPair(password, $("newSecurityPasswordConfirm").value);
    const result = await api("/api/security/change", {
      method: "POST",
      body: JSON.stringify({
        usePassword,
        password: usePassword ? password : null,
        currentPassword: $("currentSecurityPassword").value
      })
    });
    sessionState.passwordEnabled = result.passwordEnabled;
    $("logout").classList.toggle("hidden", !result.passwordEnabled);
    updateSecurityForm();
    notify(result.passwordEnabled
      ? "Senha ativada. Ela será solicitada nos próximos acessos."
      : "Senha desativada. O acesso permanece limitado ao próprio servidor.");
  } catch (error) {
    $("securityError").textContent = error.message;
  }
});

setInterval(() => {
  $("clock").textContent = new Date().toLocaleString("pt-BR");
}, 1000);
fetch("/api/session", {credentials: "same-origin"}).then(r => r.json())
  .then(s => {
    sessionState = s;
    if (s.setupRequired) showSetup();
    else if (s.authenticated) showDashboard();
    else showLogin();
  })
  .catch(() => showLogin());
