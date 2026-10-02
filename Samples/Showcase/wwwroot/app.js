"use strict";

// ProtoStream Live — the browser side. Everything here talks to one Axiom.ProtoStream server:
// fetch() for the HTTP endpoints, one WebSocket on the same origin for everything live.

const $ = id => document.getElementById(id);
const format = new Intl.NumberFormat("en");

// ---- connection -----------------------------------------------------------------------------

const RECONNECT_DELAY_MS = 1500;
const handlers = new Map();
let socket = null;

function connect() {
  setStatus("connecting", "Connecting…");
  socket = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`);
  socket.binaryType = "arraybuffer";
  socket.onopen = () => setStatus("live", "Live");
  socket.onclose = () => {
    setStatus("offline", "Offline · retrying");
    setTimeout(connect, RECONNECT_DELAY_MS);
  };
  socket.onmessage = event => {
    if (event.data instanceof ArrayBuffer) {
      handlers.get("stroke")?.(event.data);
      return;
    }
    const message = JSON.parse(event.data);
    handlers.get(message.type)?.(message);
  };
}

function send(data) {
  if (socket?.readyState === WebSocket.OPEN)
    socket.send(data);
}

function setStatus(state, text) {
  $("status").dataset.state = state;
  $("status-text").textContent = text;
}

handlers.set("presence", m => $("online").textContent = m.online);

// ---- shared canvas: 10-byte binary frames ---------------------------------------------------
// [x0 u16][y0 u16][x1 u16][y1 u16][colour u8][width u8], big-endian, coordinates scaled to 0..65535.

const COLOURS = ["#7c5cff", "#21d4fd", "#ff5c8a", "#3ddc97", "#ffc857", "#f2f4ff"];
const COORDINATE_SCALE = 65535;
const STROKE_BYTES = 10;
const canvas = $("canvas");
const context = canvas.getContext("2d");
let colour = 0;
let last = null;
let strokes = 0;

COLOURS.forEach((value, index) => {
  const swatch = document.createElement("button");
  swatch.className = "swatch";
  swatch.style.background = value;
  swatch.setAttribute("role", "radio");
  swatch.setAttribute("aria-label", value);
  swatch.setAttribute("aria-checked", index === colour);
  swatch.onclick = () => {
    colour = index;
    document.querySelectorAll(".swatch").forEach((s, i) => s.setAttribute("aria-checked", i === index));
  };
  $("swatches").append(swatch);
});

function resizeCanvas() {
  const ratio = window.devicePixelRatio || 1;
  const { width, height } = canvas.getBoundingClientRect();
  const snapshot = canvas.width ? context.getImageData(0, 0, canvas.width, canvas.height) : null;
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  context.lineCap = "round";
  if (snapshot)
    context.putImageData(snapshot, 0, 0);
}

function drawSegment(x0, y0, x1, y1, colourIndex, width) {
  const w = canvas.width, h = canvas.height, ratio = window.devicePixelRatio || 1;
  context.strokeStyle = COLOURS[colourIndex % COLOURS.length];
  context.shadowColor = context.strokeStyle;
  context.shadowBlur = 10 * ratio;
  context.lineWidth = width * ratio;
  context.beginPath();
  context.moveTo(x0 / COORDINATE_SCALE * w, y0 / COORDINATE_SCALE * h);
  context.lineTo(x1 / COORDINATE_SCALE * w, y1 / COORDINATE_SCALE * h);
  context.stroke();
  strokes++;
  $("stroke-count").textContent = `${format.format(strokes)} strokes`;
  $("canvas-hint").style.opacity = 0;
}

function point(event) {
  const rect = canvas.getBoundingClientRect();
  const clamp = v => Math.max(0, Math.min(COORDINATE_SCALE, Math.round(v * COORDINATE_SCALE)));
  return [clamp((event.clientX - rect.left) / rect.width), clamp((event.clientY - rect.top) / rect.height)];
}

canvas.addEventListener("pointerdown", event => {
  canvas.setPointerCapture(event.pointerId);
  last = point(event);
});

canvas.addEventListener("pointermove", event => {
  if (!last)
    return;
  const next = point(event);
  const width = Number($("brush").value);
  drawSegment(last[0], last[1], next[0], next[1], colour, width);

  const frame = new DataView(new ArrayBuffer(STROKE_BYTES));
  frame.setUint16(0, last[0]);
  frame.setUint16(2, last[1]);
  frame.setUint16(4, next[0]);
  frame.setUint16(6, next[1]);
  frame.setUint8(8, colour);
  frame.setUint8(9, width);
  send(frame.buffer);
  last = next;
});

["pointerup", "pointercancel", "pointerleave"].forEach(type => canvas.addEventListener(type, () => last = null));

handlers.set("stroke", buffer => {
  const frame = new DataView(buffer);
  if (frame.byteLength === STROKE_BYTES)
    drawSegment(frame.getUint16(0), frame.getUint16(2), frame.getUint16(4), frame.getUint16(6), frame.getUint8(8), frame.getUint8(9));
});

$("clear").onclick = () => send(JSON.stringify({ type: "clear" }));
handlers.set("clear", () => {
  context.clearRect(0, 0, canvas.width, canvas.height);
  strokes = 0;
  $("stroke-count").textContent = "0 strokes";
});

new ResizeObserver(resizeCanvas).observe(canvas);

// ---- server pulse ---------------------------------------------------------------------------

const SPARK_POINTS = 60;
const rates = [];
let previousMessages = null;

function setTile(id, value) {
  const tile = $(id);
  if (tile.textContent === value)
    return;
  tile.textContent = value;
  tile.classList.remove("bump");
  void tile.offsetWidth; // restart the animation
  tile.classList.add("bump");
}

function bytes(value) {
  const units = ["B", "KiB", "MiB", "GiB"];
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}

function duration(seconds) {
  const s = Math.floor(seconds);
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${s % 60} s`;
  return `${Math.floor(s / 3600)} h ${Math.floor(s / 60) % 60} min`;
}

handlers.set("pulse", p => {
  setTile("t-online", format.format(p.online));
  setTile("t-http", format.format(p.httpRequests));
  setTile("t-in", format.format(p.messagesReceived));
  setTile("t-out", format.format(p.messagesSent));
  setTile("t-violations", format.format(p.violations));
  setTile("t-memory", bytes(p.managedMemory));
  $("online").textContent = p.online;
  $("uptime").textContent = duration(p.uptimeSeconds);

  const total = p.messagesReceived + p.messagesSent;
  if (previousMessages !== null) {
    rates.push(total - previousMessages);
    if (rates.length > SPARK_POINTS)
      rates.shift();
    drawSpark();
  }
  previousMessages = total;
});

function drawSpark() {
  const spark = $("spark");
  const ratio = window.devicePixelRatio || 1;
  const { width, height } = spark.getBoundingClientRect();
  spark.width = width * ratio;
  spark.height = height * ratio;
  const g = spark.getContext("2d");
  const max = Math.max(4, ...rates);
  const step = spark.width / (SPARK_POINTS - 1);
  const y = v => spark.height - (v / max) * (spark.height * 0.7) - 6 * ratio;
  const offset = SPARK_POINTS - rates.length;

  const fill = g.createLinearGradient(0, 0, 0, spark.height);
  fill.addColorStop(0, "rgba(33, 212, 253, 0.35)");
  fill.addColorStop(1, "rgba(124, 92, 255, 0)");
  g.beginPath();
  g.moveTo(offset * step, spark.height);
  rates.forEach((v, i) => g.lineTo((offset + i) * step, y(v)));
  g.lineTo(spark.width, spark.height);
  g.fillStyle = fill;
  g.fill();

  g.beginPath();
  rates.forEach((v, i) => (i ? g.lineTo : g.moveTo).call(g, (offset + i) * step, y(v)));
  g.strokeStyle = "#21d4fd";
  g.lineWidth = 2 * ratio;
  g.shadowColor = "#21d4fd";
  g.shadowBlur = 8 * ratio;
  g.stroke();
}

// ---- chat -----------------------------------------------------------------------------------

let via = "ws";
let lastSentVia = null;

document.querySelectorAll(".segmented button").forEach(button => button.onclick = () => {
  via = button.dataset.via;
  document.querySelectorAll(".segmented button").forEach(b => {
    b.classList.toggle("active", b === button);
    b.setAttribute("aria-checked", b === button);
  });
});

function showMessage(m, how) {
  const list = $("messages");
  list.querySelector(".empty")?.remove();
  const item = document.createElement("li");
  const meta = document.createElement("div");
  meta.className = "meta";
  const who = Object.assign(document.createElement("span"), { className: "who", textContent: m.name });
  const time = Object.assign(document.createElement("span"), { textContent: new Date(m.at).toLocaleTimeString() });
  meta.append(who, time);
  if (how)
    meta.append(Object.assign(document.createElement("span"), { className: "via", textContent: how }));
  const body = Object.assign(document.createElement("div"), { className: "body", textContent: m.text });
  item.append(meta, body);
  list.append(item);
  list.scrollTop = list.scrollHeight;
}

handlers.set("chat", m => {
  const mine = m.name === ($("name").value.trim() || "anonymous") && lastSentVia;
  showMessage(m, mine ? lastSentVia : null);
  if (mine)
    lastSentVia = null;
});

$("composer").onsubmit = async event => {
  event.preventDefault();
  const name = $("name").value.trim();
  const text = $("text").value.trim();
  if (!text)
    return;
  localStorage.setItem("protostream-name", name);
  $("text").value = "";
  lastSentVia = via === "ws" ? "WS" : "POST";

  if (via === "ws") {
    send(JSON.stringify({ type: "chat", name, text }));
    return;
  }

  const response = await fetch("/api/messages", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, text }),
  });
  if (!response.ok)
    showMessage({ name: "server", text: (await response.json()).error, at: Date.now() }, `${response.status}`);
};

try { $("name").value = localStorage.getItem("protostream-name") ?? ""; } catch { /* private mode */ }

fetch("/api/messages").then(r => r.json()).then(history => {
  if (history.length === 0)
    $("messages").innerHTML = '<li class="empty">No messages yet. Say hello.</li>';
  history.forEach(m => showMessage(m, null));
});

// ---- round trip -----------------------------------------------------------------------------

const PROBES = 200;
const BUCKETS = 24;
const pending = new Map();
let probeId = 0;

handlers.set("ping", m => pending.get(m.id)?.(performance.now() - m.sentAt));

function probe() {
  return new Promise(resolve => {
    const id = ++probeId;
    pending.set(id, latency => {
      pending.delete(id);
      resolve(latency);
    });
    send(JSON.stringify({ type: "ping", id, sentAt: performance.now() }));
  });
}

$("probe").onclick = async () => {
  const button = $("probe");
  button.disabled = true;
  const samples = [];
  for (let i = 0; i < PROBES; i++) {
    samples.push(await probe());
    button.textContent = `Probing… ${i + 1}/${PROBES}`;
  }
  button.disabled = false;
  button.textContent = `Send ${PROBES} probes`;

  samples.sort((a, b) => a - b);
  const ms = v => `${v.toFixed(2)} ms`;
  $("l-min").textContent = ms(samples[0]);
  $("l-avg").textContent = ms(samples.reduce((a, b) => a + b, 0) / samples.length);
  $("l-p95").textContent = ms(samples[Math.floor(samples.length * 0.95)]);
  $("l-max").textContent = ms(samples[samples.length - 1]);

  const low = samples[0], high = samples[Math.floor(samples.length * 0.99)];
  const counts = new Array(BUCKETS).fill(0);
  samples.forEach(v => counts[Math.min(BUCKETS - 1, Math.floor((v - low) / Math.max(high - low, 0.001) * BUCKETS))]++);
  const peak = Math.max(...counts);
  $("histogram").replaceChildren(...counts.map((c, i) => {
    const column = document.createElement("div");
    column.className = "col";
    column.style.height = `${Math.max(2, c / peak * 100)}%`;
    column.style.animationDelay = `${i * 15}ms`;
    column.title = `${c} probes`;
    return column;
  }));
};

// ---- request inspector ----------------------------------------------------------------------

function row(label, value) {
  const dt = Object.assign(document.createElement("dt"), { textContent: label });
  const dd = document.createElement("dd");
  if (value instanceof Node) dd.append(value); else dd.textContent = value ?? "—";
  return [dt, dd];
}

$("inspect-form").onsubmit = async event => {
  event.preventDefault();
  const target = $("inspect-target").value.trim() || "/api/inspect";
  const response = await fetch(target.startsWith("/api/inspect") ? target : `/api/inspect${target.startsWith("?") ? target : ""}`);
  const r = await response.json();
  const result = $("inspect-result");
  if (!response.ok) {
    result.replaceChildren(Object.assign(document.createElement("p"), { className: "empty", textContent: `${response.status}: ${r.error}` }));
    return;
  }

  const kv = document.createElement("dl");
  kv.className = "kv";
  const form = Object.assign(document.createElement("span"), { className: "chip", textContent: r.targetForm });
  kv.append(
    ...row("request line", `${r.method} ${r.target} ${r.version}`),
    ...row("target form", form),
    ...row("path", r.path),
    ...row("query", r.query),
    ...row("decoded query", r.query ? [...new URLSearchParams(r.query)].map(([k, v]) => `${k} = ${v}`).join("  ·  ") : null),
    ...row("authority", r.authority),
    ...row("keep-alive", String(r.keepAlive)));

  const table = document.createElement("table");
  table.className = "fields";
  r.headers.forEach(h => {
    const tr = table.insertRow();
    tr.insertCell().textContent = h.name;
    tr.insertCell().textContent = h.value;
  });
  result.replaceChildren(kv, table);
};

// ---- streamed upload ------------------------------------------------------------------------

const dropzone = $("dropzone");
["dragenter", "dragover"].forEach(type => dropzone.addEventListener(type, e => { e.preventDefault(); dropzone.classList.add("over"); }));
["dragleave", "drop"].forEach(type => dropzone.addEventListener(type, () => dropzone.classList.remove("over")));
dropzone.addEventListener("drop", e => { e.preventDefault(); if (e.dataTransfer.files[0]) upload(e.dataTransfer.files[0]); });
$("file").addEventListener("change", e => { if (e.target.files[0]) upload(e.target.files[0]); });

function upload(file) {
  const request = new XMLHttpRequest(); // fetch() cannot report upload progress
  const progress = $("progress");
  progress.classList.add("active");
  $("bar").style.width = "0";
  $("drop-text").textContent = `Uploading ${file.name}…`;
  request.upload.onprogress = e => { if (e.lengthComputable) $("bar").style.width = `${e.loaded / e.total * 100}%`; };
  request.onload = () => {
    progress.classList.remove("active");
    $("drop-text").textContent = "Drop another file or click to choose.";
    const r = JSON.parse(request.responseText);
    const list = $("upload-result");
    if (request.status !== 200) {
      list.replaceChildren(...row("error", `${request.status}: ${r.error}`));
      return;
    }
    list.replaceChildren(
      ...row("file", file.name),
      ...row("bytes", format.format(r.bytes)),
      ...row("sha-256", r.sha256),
      ...row("server time", `${r.milliseconds} ms`),
      ...row("throughput", `${r.megabytesPerSecond} MiB/s`));
  };
  request.onerror = () => {
    progress.classList.remove("active");
    $("drop-text").textContent = "The upload failed. Try again.";
  };
  request.open("POST", "/api/upload");
  request.send(file);
}

connect();
