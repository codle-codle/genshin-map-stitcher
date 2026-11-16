"use strict";
// 11/12 새 화면(설계/화면재설계_1112.md): 상태마다 화면 하나. 실시간 붙이기는 B안(맞춰진 사진만 첫 사진부터 퍼짐).

const $ = (id) => document.getElementById(id);
const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : v; } catch (e) { return d; } },
  set(k, v) { try { localStorage.setItem(k, String(v)); } catch (e) {} },
};
const S = {
  job: null,
  jobs: [],
  mode: null,            // "manual" | "intro"
  introSeen: store.get("introSeen", "") === "1",
  upload: null,
  viewKind: "trimmed",
  capSeen: null,         // 찍기 시작을 본 때(경과 시간용)
  pollTimer: null,
  opts: { line: "cyan", islands: "default" },
  keys: { start: { vk: 0x24, name: "Home" }, stop: { vk: 0x23, name: "End" } },
};
const escHtml = (t) => String(t).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const GOOD = ["done", "partial", "limit", "stuck", "lost"];
const PROBLEM_CODES = ["void_start", "black", "calibration_failed", "not_genshin"];

// ---------- 서버 ----------
async function api(path, options = {}) {
  const res = await fetch(path, options);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `요청 실패 (${res.status})`);
  return data;
}
const post = (path, body) =>
  api(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });

// ---------- 형식 ----------
const fmtSec = (sec) => { const s = Math.max(0, Math.round(sec)); return s < 60 ? `${s}초` : `${Math.floor(s / 60)}분 ${s % 60}초`; };
const fmtLeft = (sec) => (sec >= 90 ? `약 ${Math.round(sec / 60)}분 남음` : sec >= 10 ? `약 ${Math.ceil(sec / 10) * 10}초 남음` : "곧 끝나요");
function fmtId(id) {
  const m = id.match(/^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})/);
  return m ? `${+m[2]}월 ${+m[3]}일 ${m[4]}:${m[5]}` : id;
}
const splitMsg = (text) => {
  const t = String(text || "").trim();
  const i = t.indexOf(". ");
  return i < 0 ? [t.replace(/\.$/, ""), ""] : [t.slice(0, i), t.slice(i + 2)];
};
let toastTimer;
function toast(text) {
  const el = $("toast");
  el.textContent = text;
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 3200);
}

// ---------- 작업 ----------
async function refreshJobs() {
  S.jobs = (await api("/api/jobs")).jobs;
  renderHistory();
}
async function loadJob(id) {
  S.job = await api(`/api/jobs/${id}`);
  S.mode = null;
  render();
  schedulePoll();
}
const captureActive = (job) => !!(job && job.capture && ["launching", "waiting", "capturing"].includes(job.capture.state));
const stitchDone = (job) => !!(job && job.stitch && job.stitch.status === "done" && !job.stitch.stale);
const trimDone = (job) => !!(stitchDone(job) && job.trim && job.trim.status === "done" && !job.trim.stale);

function needsPoll(job) {
  if (!job) return false;
  const cap = job.capture;
  return !!(job.live || captureActive(job)
    || (stitchDone(job) && !job.trim)                               // 합친 뒤 자동 다듬기가 곧 시작
    || (cap && cap.state === "done" && GOOD.includes(cap.capture_result) && !job.stitch
        && !(cap.auto_stitch && cap.auto_stitch.status === "skipped")));
}
function schedulePoll() {
  clearTimeout(S.pollTimer);
  if (needsPoll(S.job)) S.pollTimer = setTimeout(poll, 700);
}
async function poll() {
  if (!S.job) return;
  const id = S.job.id;
  try {
    const job = await api(`/api/jobs/${id}`);
    if (!S.job || S.job.id !== id) return;
    const wasActive = captureActive(S.job);
    S.job = job;
    if (wasActive && !captureActive(job)) refreshJobs();
    if (!job.live && !needsPoll(job)) refreshJobs();
    render();
  } catch (err) {
    toast(err.message);
  }
  schedulePoll();
}

// ---------- 어느 화면? ----------
function pickScreen() {
  if (!S.introSeen || S.mode === "intro") return "Intro";
  if (S.mode === "manual") return "Manual";
  const job = S.job;
  if (!job) return "Ready";
  const cap = job.capture;
  if (cap && (cap.state === "launching" || cap.state === "waiting")) return "Waiting";
  if (cap && cap.state === "capturing") return "Capturing";
  if (job.live) return "Merge";
  if (job.stitch && job.stitch.status === "failed" && !job.stitch.stale) return "Fail";
  if (stitchDone(job)) return "Merge";
  if (cap && cap.state === "aborted" && cap.code === "aborted" && !cap.saved) return "Ready"; // 시작 전에 그만둠
  if (cap && cap.state === "done" && GOOD.includes(cap.capture_result)) {
    return cap.auto_stitch && cap.auto_stitch.status === "skipped" ? "Fail" : "Merge";
  }
  if (cap && ["aborted", "error", "lost", "done"].includes(cap.state)) return "Fail";
  if (job.images.length) return "Manual";
  return "Ready";
}

function render() {
  const name = pickScreen();
  for (const s of ["Intro", "Ready", "Waiting", "Capturing", "Merge", "Fail", "Manual"]) $(`scr${s}`).hidden = s !== name;
  ({ Intro: renderIntro, Ready: renderReady, Waiting: renderWaiting, Capturing: renderCapturing,
     Merge: renderMerge, Fail: renderFail, Manual: renderManual })[name]();
  if (name !== "Capturing") S.capSeen = null;
  MV.active = name === "Merge" && S.tab !== "shots";   // 사진 탭을 보는 동안 지도 그리기는 쉰다
  if (name !== "Merge") closeViewer();
  renderHistory();
}

// ---------- 처음 안내 · 준비 ----------
function renderIntro() {}
function renderReady() {
  const busy = S.job && (S.job.live || captureActive(S.job));
  $("startCapture").disabled = !!busy;
}
function readMax() { return Math.max(10, Math.min(600, Math.round(Number($("autoMax").value) || 300))); }

async function startCapture() {
  $("readyMsg").classList.add("hidden");
  try {
    S.mode = null;
    S.job = await post("/api/jobs");
    S.job.capture = { state: "launching", message: "" };
    render();
    S.job = await post(`/api/jobs/${S.job.id}/autocapture`, { max_cells: readMax() });
    render();
    schedulePoll();
    refreshJobs();
  } catch (err) {
    S.job = null;
    render();
    $("readyMsg").textContent = /권한/.test(err.message)
      ? "관리자 권한을 받지 못했어요. 원신 지도를 끌려면 권한 창에서 '예'를 눌러야 해요."
      : err.message;
    $("readyMsg").classList.remove("hidden");
  }
}

// ---------- (나) Home 기다림 ----------
function renderWaiting() {
  const cap = S.job.capture;
  const problem = PROBLEM_CODES.includes(cap.code);
  $("waitProblem").classList.toggle("hidden", !problem);
  if (problem) {
    const [t, s] = splitMsg(cap.message);
    $("waitProblemTitle").textContent = t;
    $("waitProblemSub").textContent = s;
  }
  if (cap.state === "launching") {
    $("waitTitle").innerHTML = "관리자 권한 창이 뜨면<br>'예'를 눌러 주세요";
    $("waitSub").textContent = "원신이 관리자 권한으로 돌아서, 같은 권한이 있어야 지도를 끌 수 있어요.";
  } else if (cap.code === "countdown") {
    $("waitTitle").textContent = "곧 찍기 시작해요";
    $("waitSub").textContent = "마우스와 키보드에서 손을 떼 주세요.";
  } else {
    const key = `<kbd class="keycap inline">${escHtml(S.keys.start.name)}</kbd>`;
    $("waitTitle").innerHTML = problem ? `고친 뒤 원신 지도 화면에서<br>${key} 키를 다시 누르세요`
      : `이제 원신으로 가서<br>지도 화면에서 ${key} 키를 누르세요`;
    $("waitSub").textContent = "지도 열기 → 끝까지 축소 → 땅이 화면 가운데";
  }
}

// ---------- (다) 찍는 중 ----------
const TYPICAL_CAPTURE = 450; // 보통 6~8분 → 막대는 7.5분 기준(99%에서 멈춤)
function renderCapturing() {
  const cap = S.job.capture;
  if (!S.capSeen) S.capSeen = Date.now();
  const sec = (Date.now() - S.capSeen) / 1000;
  $("capBar").style.width = `${Math.min(97, (sec / TYPICAL_CAPTURE) * 100)}%`;
  $("capLeft").textContent = `${fmtSec(sec)} / 보통 6~8분`;
  $("capCount").textContent = `사진 ${cap.saved || 0}장`;
}
setInterval(() => { if (!$("scrCapturing").hidden) renderCapturing(); }, 1000);

// ---------- (바) 멈춤 · 실패 ----------
function renderFail() {
  const job = S.job;
  const cap = job.capture || {};
  const st = job.stitch;
  let title, sub, stitchLabel = null;
  if (st && st.status === "failed" && !st.stale) {
    title = "사진을 한 장으로 합치지 못했어요";
    const bad = st.unconnected || [];
    sub = bad.length ? `${bad.length}장이 나머지와 겹치는 곳을 못 찾았어요. 사진은 폴더에 그대로 있어요.` : "사진은 폴더에 그대로 있어요.";
    stitchLabel = "다시 합치기";
  } else if (cap.state === "done") {
    title = "다 찍었는데 합치기를 시작하지 못했어요";
    sub = "사진은 폴더에 그대로 있어요. 아래 버튼으로 합칠 수 있어요.";
    stitchLabel = "합치기";
  } else if (cap.state === "lost") {
    title = "찍기가 응답하지 않아요";
    sub = "앱을 껐다 켠 뒤 다시 찍어 주세요. 찍은 사진은 폴더에 그대로 있어요.";
  } else {
    [title, sub] = splitMsg(cap.message || "멈췄어요");
  }
  if (!stitchLabel && (cap.saved || 0) >= 2) stitchLabel = "찍은 데까지 합치기";
  $("failTitle").textContent = title;
  $("failSub").textContent = sub;
  $("failStitch").classList.toggle("hidden", !stitchLabel);
  if (stitchLabel) $("failStitch").textContent = stitchLabel;
  $("failRetry").classList.toggle("primary", !stitchLabel);
}

// ---------- (라)(마) 합치는 중 · 완성 ----------
function stitchPhase(live, placed, n) {
  const p = live.phase || "";
  if (live.kind === "trim") return "가장자리를 다듬고 있어요";
  if (p.includes("특징")) return `사진을 읽고 있어요 (${live.done}/${live.total})`;
  if (p.includes("겹치는") || p.includes("먼 사진")) return placed ? `겹치는 곳을 찾아 한 장씩 맞춰 붙이고 있어요 (${placed}/${n})` : "겹치는 곳을 찾고 있어요";
  if (p.includes("한 장으로")) return "이음매를 다듬고 있어요 · 그동안 지도를 둘러봐도 돼요";
  if (p.includes("저장") || p.includes("미리보기")) return "큰 그림을 저장하고 있어요";
  return "합칠 준비를 하고 있어요";
}
function livePercent(live) {
  if (live.percent === undefined || live.percent === null) return live.total > 0 ? (100 * live.done) / live.total : null;
  let v = live.percent;
  if (live.percent_seconds > 0 && live.percent_to > v) v += (live.percent_to - v) * Math.min(1, (Date.now() / 1000 - live.percent_at) / live.percent_seconds);
  return Math.min(99, v);
}
const TRIM_SECONDS = 25;

function renderMerge() {
  const job = S.job;
  const live = job.live;
  const cap = job.capture;
  const fromCapture = !!(cap && GOOD.includes(cap.capture_result));
  const m = job.mosaic;
  const placed = m ? m.order.length : 0;
  const n = m ? m.total : job.images.length;
  const running = !!live || (stitchDone(job) && !job.trim) || !job.stitch;
  const complete = !running;

  $("mergeTitle").textContent = complete ? "지도가 완성됐어요" : fromCapture ? "다 찍었어요. 이제 마우스를 써도 돼요." : "사진을 합치고 있어요";
  $("mergePhase").textContent = live ? stitchPhase(live, placed, n) : complete ? "" : "합칠 준비를 하고 있어요";
  $("mergeBarRow").classList.toggle("hidden", complete);
  if (!complete) {
    let pct = 0, left = null;
    if (live && live.kind === "stitch") {
      const p = livePercent(live);
      pct = p === null ? 2 : p * 0.88;
      const el = Date.now() / 1000 - live.started;
      if (p !== null && p > 3) left = (el * (100 - p)) / p + TRIM_SECONDS;
    } else if (live && live.kind === "trim") {
      pct = 88 + 12 * (live.total ? live.done / live.total : 0);
      left = Math.max(5, TRIM_SECONDS - (Date.now() / 1000 - live.started));
    } else if (stitchDone(job)) pct = 88;
    $("mergeBar").style.width = `${Math.min(99, pct)}%`;
    $("mergeLeft").textContent = left === null ? "" : fmtLeft(left);
  }
  // 경고: 다듬기 실패 · 지도가 같이 잘림
  const trim = job.trim;
  let warn = "";
  if (stitchDone(job) && trim && trim.status === "failed") warn = `<b>가장자리 다듬기를 못 했어요.</b> 합친 그대로의 지도는 있어요. '가장자리 다시 다듬기'에서 설정을 바꿔 보세요.`;
  // 11/12: 기준 2% → 0.5%(정상 0.1% 안팎, 작업 035245 나타 북쪽이 0.98% 잘렸는데 경고가 안 떴음)
  else if (trimDone(job) && trim.mapCut >= 0.005) warn = `<b>지도 ${Math.round(trim.mapCut * 100)}%가 같이 잘렸어요.</b> 경계선 근처에 안 찍힌 틈이 있으면 생겨요. '합친 그대로'로 보거나 다시 찍어 주세요.`;
  $("mergeWarn").innerHTML = warn;
  $("mergeWarn").classList.toggle("hidden", !warn);

  $("mergeStop").classList.toggle("hidden", !live);
  $("openFolder").classList.toggle("hidden", !complete);
  $("openBig").classList.toggle("hidden", !complete);
  $("doneLinks").classList.toggle("hidden", !complete);
  const both = !!(job.previews && job.previews.trimmed && job.previews.stitched);
  $("viewToggle").classList.toggle("hidden", !(complete && both));
  if (!both && job.previews && job.previews.stitched && !job.previews.trimmed) S.viewKind = "trimmed";
  for (const b of $("viewToggle").querySelectorAll("button")) b.classList.toggle("on", b.dataset.v === S.viewKind);
  renderTabs(job);
  mvSync(job, complete);
}

// ---------- 탭: 지도 · 찍은 사진 ----------
// 11/14 사용자: 합치는 동안 "사진 덜 찍었는지 걱정될 수 있으니까" 찍은 사진을 직접 보게. 기본 화면은 지도, 궁금한 사람만 탭으로 간다.
function renderTabs(job) {
  if (S.tabJob !== job.id) { S.tabJob = job.id; S.tab = "map"; closeViewer(); }
  $("tabShotsCount").textContent = job.images.length;
  for (const b of $("mergeTabs").querySelectorAll(".tab")) {
    const on = b.dataset.tab === S.tab;
    b.setAttribute("aria-selected", on ? "true" : "false");
    b.tabIndex = on ? 0 : -1;
  }
  $("stage").hidden = S.tab !== "map";
  $("shotsPane").hidden = S.tab !== "shots";
  if (S.tab === "shots") renderShotGrid(job);
}
function setTab(tab) {
  if (S.tab === tab) return;
  S.tab = tab;
  render();
  if (tab === "map" && MV.auto && MV.target) requestAnimationFrame(() => { MV.cam = fitAspect(MV.target); });
}
$("mergeTabs").addEventListener("click", (e) => { const b = e.target.closest(".tab"); if (b) setTab(b.dataset.tab); });
$("mergeTabs").addEventListener("keydown", (e) => {
  if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
  const tab = S.tab === "map" ? "shots" : "map";
  setTab(tab);
  $("mergeTabs").querySelector(`[data-tab="${tab}"]`).focus();
});
function renderShotGrid(job) {
  const grid = $("shotGrid");
  const key = job.id + "|" + job.images.join("|");
  if (grid.dataset.key === key) return;
  grid.dataset.key = key;
  const frag = document.createDocumentFragment();
  job.images.forEach((name, i) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "pshot";
    b.dataset.i = i;
    b.title = name;
    b.innerHTML = `<img loading="lazy" alt=""><span>${i + 1}</span>`;
    const img = b.querySelector("img"), src = `/api/jobs/${job.id}/shot/${encodeURIComponent(name)}`;
    img.onerror = () => { if (!img.dataset.retry) { img.dataset.retry = "1"; setTimeout(() => (img.src = src + "?r=1"), 1500); } };
    img.src = src;
    frag.appendChild(b);
  });
  grid.replaceChildren(frag);
}
$("shotGrid").addEventListener("click", (e) => { const b = e.target.closest(".pshot"); if (b) openViewer(+b.dataset.i); });

// 크게 보기: 작은 사진을 먼저 보이고, 큰 사진이 오면 바꾼다
const VW = { i: -1, open: false };
function openViewer(i) { VW.open = true; VW.i = i; $("viewer").classList.remove("hidden"); showViewer(); $("viewerClose").focus(); }
function closeViewer() {
  if (!VW.open) return;
  VW.open = false;
  $("viewer").classList.add("hidden");
  $("viewerImg").removeAttribute("src");
  const b = $("shotGrid").querySelector(`[data-i="${VW.i}"]`);
  if (b && !$("shotsPane").hidden) b.focus();
}
function showViewer() {
  const job = S.job;
  if (!job || !job.images.length) return closeViewer();
  const n = job.images.length;
  VW.i = Math.max(0, Math.min(n - 1, VW.i));
  const i = VW.i, name = encodeURIComponent(job.images[i]), img = $("viewerImg");
  img.src = `/api/jobs/${job.id}/shot/${name}`;
  const big = new Image();
  big.onload = () => { if (VW.open && VW.i === i) img.src = big.src; };
  big.src = `/api/jobs/${job.id}/photo/${name}`;
  if (i + 1 < n) new Image().src = `/api/jobs/${job.id}/photo/${encodeURIComponent(job.images[i + 1])}`; // 다음 장 미리 받기
  $("viewerCount").textContent = `${i + 1} / ${n}`;
  $("viewerPrev").disabled = i === 0;
  $("viewerNext").disabled = i === n - 1;
}
const stepViewer = (d) => { VW.i += d; showViewer(); };
$("viewerPrev").onclick = () => stepViewer(-1);
$("viewerNext").onclick = () => stepViewer(1);
$("viewerClose").onclick = closeViewer;
$("viewer").addEventListener("click", (e) => { if (e.target === $("viewer")) closeViewer(); });
window.addEventListener("keydown", (e) => {
  if (!VW.open) return;
  if (e.key === "Escape") { e.preventDefault(); closeViewer(); }
  else if (e.key === "ArrowLeft") { e.preventDefault(); if (VW.i > 0) stepViewer(-1); }
  else if (e.key === "ArrowRight") { e.preventDefault(); if (S.job && VW.i < S.job.images.length - 1) stepViewer(1); }
});

// ---------- 지도 그리기(모자이크 + 완성 그림) ----------
const cv = $("cv");
const ctx = cv.getContext("2d");
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
const ease = (x) => 1 - Math.pow(1 - Math.min(1, Math.max(0, x)), 3);
const MV = {
  active: false, jobId: null, tiles: new Map(), queue: [], nextRelease: 0, crop: null, off: [0, 0],
  finalApplied: false, canvasRect: null, recent: new Map(), lastSeq: 0,
  big: null, bigKey: "", bigRect: null, bigA: 0, bigT0: 0, bigWorldReset: false,
  cam: { x0: -1000, y0: -1000, x1: 1000, y1: 1000 }, target: null, auto: true, t: 0,
};
function mvReset(jobId) {
  Object.assign(MV, { jobId, tiles: new Map(), queue: [], nextRelease: 0, crop: null, off: [0, 0], finalApplied: false,
    canvasRect: null, recent: new Map(), lastSeq: 0, big: null, bigKey: "", bigRect: null, bigA: 0, target: null, auto: true });
  $("fitBtn").classList.add("hidden");
}
function loadThumb(job, name, tile) {
  const img = new Image();
  img.onload = () => { tile.img = img; };
  img.onerror = () => setTimeout(() => loadThumb(job, name, tile), 1500); // 작은 사진을 아직 만드는 중
  img.src = `/api/jobs/${job.id}/mosaic/${encodeURIComponent(name)}?t=${tile.tries++}`;
}
function mvSync(job, complete) {
  if (MV.jobId !== job.id) mvReset(job.id);
  const m = job.mosaic;
  if (m && !stitchDone(job) || (m && MV.tiles.size)) {
    MV.crop = m.crop;
    if (!MV.tiles.size && m.pos[m.first]) {
      const [x, y] = m.pos[m.first];
      const first = { name: m.first, x, y, tx: x, ty: y, a: 0, img: null, tries: 0, born: MV.t, glow: -9, tw: null };
      MV.tiles.set(m.first, first);
      loadThumb(job, m.first, first);
      first.aTw = { f: 0, to: 1, t0: MV.t, dur: 0.4 };
      const cw = m.crop[2] / 0.45, ch = m.crop[3] / 0.45, cx = x + m.crop[0] + m.crop[2] / 2, cy = y + m.crop[1] + m.crop[3] / 2;
      MV.target = { x0: cx - cw / 2, y0: cy - ch / 2, x1: cx + cw / 2, y1: cy + ch / 2 };
      MV.cam = fitAspect(MV.target);
    }
    for (const name of m.order) {
      if (!MV.tiles.has(name) && !MV.queue.includes(name)) MV.queue.push(name);
    }
    MV.pos = m.pos;
    for (const [a, b, ok, seq] of m.recent) if (seq > MV.lastSeq) { MV.recent.set(seq, { a, b, ok, t0: MV.t }); MV.lastSeq = seq; }
    if (m.final && !MV.finalApplied && m.final[m.first] && m.pos[m.first]) {
      MV.finalApplied = true;
      MV.off = [m.pos[m.first][0] - m.final[m.first][0], m.pos[m.first][1] - m.final[m.first][1]];
      MV.finalPos = m.final;
      for (const name of m.order) if (!MV.tiles.has(name)) releaseTile(job, name, true);
      MV.queue = [];
      for (const [name, f] of Object.entries(m.final)) {   // 쌍 비교가 아닌 방법(촘촘 맞추기 등)으로 붙은 사진은 이때 제자리에 나타난다
        if (!MV.tiles.has(name)) releaseTile(job, name, false, [f[0] + MV.off[0], f[1] + MV.off[1]]);
      }
      for (const [name, tile] of MV.tiles) {
        const f = m.final[name];
        if (f) tile.tw = { fx: tile.x, fy: tile.y, tx: f[0] + MV.off[0], ty: f[1] + MV.off[1], t0: MV.t, dur: reduceMotion ? 0 : 0.8 };
      }
      if (m.canvas) MV.canvasRect = { x0: m.canvas.origin[0] + MV.off[0], y0: m.canvas.origin[1] + MV.off[1],
        x1: m.canvas.origin[0] + MV.off[0] + m.canvas.size[0], y1: m.canvas.origin[1] + MV.off[1] + m.canvas.size[1] };
      if (MV.auto) setTimeout(() => { if (MV.auto) MV.target = bboxTiles(0.25); }, 900);
    }
  }
  // 완성 그림: 합친 그림은 모자이크와 같은 자리에서 바뀌고, 다듬은 그림은 화면에 다시 맞춘다
  const p = job.previews || {};
  let kind = null;
  if (complete && p.trimmed && S.viewKind === "trimmed") kind = "trimmed";
  else if (p.stitched && (MV.canvasRect || complete || !m)) kind = "stitched";
  if (kind) {
    const key = `${job.id}/${kind}/${p[kind]}`;
    if (MV.bigKey !== key) {
      MV.bigKey = key;
      const img = new Image();
      img.onload = () => {
        if (MV.bigKey !== key) return;
        const inPlace = kind === "stitched" && MV.canvasRect && MV.tiles.size;
        MV.big = img;
        MV.bigRect = inPlace ? MV.canvasRect : { x0: 0, y0: 0, x1: img.naturalWidth, y1: img.naturalHeight };
        MV.bigA = 0; MV.bigT0 = MV.t; MV.bigInPlace = !!inPlace;
        $("stageNote").classList.add("hidden");  // 11/14: 다 된 지도를 열면 그림이 온 뒤에도 '합치고 있어요'가 남던 것
        if (!inPlace) { MV.tiles.clear(); MV.queue = []; MV.target = { ...MV.bigRect }; MV.cam = fitAspect(MV.target); MV.auto = true; $("fitBtn").classList.add("hidden"); }
      };
      img.src = `/api/jobs/${job.id}/preview/${kind}.jpg?v=${p[kind]}`;
    }
  }
  const empty = !MV.tiles.size && !MV.big;
  $("stageNote").classList.toggle("hidden", !empty);
  if (empty) $("stageNote").textContent = m ? "첫 사진을 불러오고 있어요" : "사진을 합치고 있어요. 다 되면 여기에 보여 드려요.";
}
function releaseTile(job, name, instant, at = null) {
  const p = at || (MV.pos && MV.pos[name]);
  if (!p || MV.tiles.has(name)) return;
  const tile = { name, x: p[0], y: p[1], a: instant ? 1 : 0, img: null, tries: 0, born: MV.t, glow: instant ? -9 : MV.t, tw: null,
    aTw: instant ? null : { f: 0, to: 1, t0: MV.t, dur: reduceMotion ? 0 : 0.35 } };
  MV.tiles.set(name, tile);
  loadThumb(job, name, tile);
}
function tileRect(t) { const c = MV.crop; return { x0: t.x + c[0], y0: t.y + c[1], x1: t.x + c[0] + c[2], y1: t.y + c[1] + c[3] }; }
function bboxTiles(margin) {
  let r = null;
  for (const t of MV.tiles.values()) {
    const q = t.tw ? tileRect({ x: t.tw.tx, y: t.tw.ty }) : tileRect(t);
    r = r ? { x0: Math.min(r.x0, q.x0), y0: Math.min(r.y0, q.y0), x1: Math.max(r.x1, q.x1), y1: Math.max(r.y1, q.y1) } : q;
  }
  if (!r) return MV.bigRect || MV.target;
  const mg = MV.crop[2] * margin;
  return { x0: r.x0 - mg, y0: r.y0 - mg, x1: r.x1 + mg, y1: r.y1 + mg };
}
function fitAspect(r) {
  const W = cv.clientWidth || 1, H = cv.clientHeight || 1, ar = W / H;
  let w = r.x1 - r.x0, h = r.y1 - r.y0;
  const cx = (r.x0 + r.x1) / 2, cy = (r.y0 + r.y1) / 2;
  if (w / h > ar) h = w / ar; else w = h * ar;
  w *= 1.04; h *= 1.04;
  return { x0: cx - w / 2, y0: cy - h / 2, x1: cx + w / 2, y1: cy + h / 2 };
}
function growTarget() {  // 화면은 넓어지기만 한다(사진이 안쪽 테두리를 넘을 때만, 넘은 쪽으로 넉넉히)
  if (!MV.auto || !MV.tiles.size || !MV.target || MV.bigInPlace === false && MV.big) return;
  const b = bboxTiles(0), T = MV.target, w = T.x1 - T.x0, h = T.y1 - T.y0, ix = w * 0.06, iy = h * 0.06;
  const out = { l: b.x0 < T.x0 + ix, r: b.x1 > T.x1 - ix, t: b.y0 < T.y0 + iy, b: b.y1 > T.y1 - iy };
  if (!(out.l || out.r || out.t || out.b)) return;
  const half = MV.crop[2] * 0.25, extra = MV.crop[2] * 0.6;
  MV.target = { x0: Math.min(T.x0, b.x0 - half - (out.l ? extra : 0)), y0: Math.min(T.y0, b.y0 - half - (out.t ? extra / 2 : 0)),
    x1: Math.max(T.x1, b.x1 + half + (out.r ? extra : 0)), y1: Math.max(T.y1, b.y1 + half + (out.b ? extra / 2 : 0)) };
}
function step(dt) {
  MV.t += dt;
  const job = S.job;
  if (job && MV.queue.length) {
    if (MV.queue.length > 40) { for (const n of MV.queue.splice(0)) releaseTile(job, n, true); } // 늦게 열었으면 한꺼번에
    else if (MV.t >= MV.nextRelease) {
      releaseTile(job, MV.queue.shift(), false);
      MV.nextRelease = MV.t + (MV.queue.length > 6 ? 0.04 : 0.11);   // 실제보다 2초 넘게 늦지 않게
    }
  }
  for (const t of MV.tiles.values()) {
    if (t.tw) { const u = t.tw.dur ? ease((MV.t - t.tw.t0) / t.tw.dur) : 1; t.x = t.tw.fx + (t.tw.tx - t.tw.fx) * u; t.y = t.tw.fy + (t.tw.ty - t.tw.fy) * u; if (u >= 1) t.tw = null; }
    if (t.aTw) { const u = t.aTw.dur ? ease((MV.t - t.aTw.t0) / t.aTw.dur) : 1; t.a = t.aTw.f + (t.aTw.to - t.aTw.f) * u; if (u >= 1) t.aTw = null; }
  }
  if (MV.big) MV.bigA = reduceMotion ? 1 : ease((MV.t - MV.bigT0) / 0.8);
  for (const [k, l] of MV.recent) if (MV.t - l.t0 > 0.45) MV.recent.delete(k);
  growTarget();
  if (MV.auto && MV.target) {
    const k = reduceMotion ? 1 : 1 - Math.exp(-dt / 0.18), T = fitAspect(MV.target);
    for (const e of ["x0", "y0", "x1", "y1"]) MV.cam[e] += (T[e] - MV.cam[e]) * k;
  }
}
function draw() {
  const dpr = devicePixelRatio || 1, W = cv.clientWidth, H = cv.clientHeight;
  if (!W || !H) return;
  if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
  const s = W / (MV.cam.x1 - MV.cam.x0), X = (x) => (x - MV.cam.x0) * s, Y = (y) => (y - MV.cam.y0) * s;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, W, H);
  ctx.imageSmoothingQuality = "high";
  const fade = MV.big && MV.bigInPlace ? 1 - MV.bigA : MV.big ? 0 : 1;
  if (fade > 0.01 && MV.crop) {
    for (const t of MV.tiles.values()) {
      if (!t.img) continue;
      let sc = 1;
      if (!reduceMotion && MV.t - t.born < 0.35) sc = 1.06 - 0.06 * ease((MV.t - t.born) / 0.35);
      const r = tileRect(t), w = (r.x1 - r.x0) * s * sc, h = (r.y1 - r.y0) * s * sc;
      ctx.globalAlpha = t.a * fade;
      ctx.drawImage(t.img, X(r.x0) - (w - (r.x1 - r.x0) * s) / 2, Y(r.y0) - (h - (r.y1 - r.y0) * s) / 2, w, h);
    }
    ctx.globalAlpha = 1;
    for (const t of MV.tiles.values()) {          // 방금 붙은 사진 테두리 빛
      const g = MV.t - t.glow;
      if (g < 0 || g > 0.7) continue;
      const r = tileRect(t);
      ctx.strokeStyle = `rgba(120,200,255,${(1 - g / 0.7) * 0.9 * fade})`;
      ctx.lineWidth = 3;
      ctx.strokeRect(X(r.x0), Y(r.y0), (r.x1 - r.x0) * s, (r.y1 - r.y0) * s);
    }
    for (const l of MV.recent.values()) {           // 지금 비교 중인 두 장(둘 다 보일 때만)
      const A = MV.tiles.get(l.a), B = MV.tiles.get(l.b);
      if (!A || !B) continue;
      const u = (MV.t - l.t0) / 0.45, ra = tileRect(A), rb = tileRect(B);
      ctx.strokeStyle = l.ok ? `rgba(63,191,134,${0.9 * (1 - u) * fade})` : `rgba(160,168,184,${0.5 * (1 - u) * fade})`;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(X((ra.x0 + ra.x1) / 2), Y((ra.y0 + ra.y1) / 2));
      ctx.lineTo(X((rb.x0 + rb.x1) / 2), Y((rb.y0 + rb.y1) / 2));
      ctx.stroke();
    }
  }
  if (MV.big) {
    ctx.globalAlpha = MV.bigA;
    const r = MV.bigRect;
    ctx.drawImage(MV.big, X(r.x0), Y(r.y0), (r.x1 - r.x0) * s, (r.y1 - r.y0) * s);
    ctx.globalAlpha = 1;
  }
}
let lastFrame = 0;
function frame(now) {
  const dt = Math.min(0.05, (now - (lastFrame || now)) / 1000);
  lastFrame = now;
  if (MV.active) { step(dt); draw(); }
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);

// 사용자가 직접 움직이면 자동 맞추기를 멈추고 '전체 보기'를 띄운다
function manual() { if (MV.auto) { MV.auto = false; $("fitBtn").classList.remove("hidden"); } }
cv.addEventListener("wheel", (e) => {
  e.preventDefault();
  manual();
  const r = cv.getBoundingClientRect(), mx = (e.clientX - r.left) / r.width, my = (e.clientY - r.top) / r.height;
  const f = Math.exp(e.deltaY * 0.0015), c = MV.cam, wx = c.x0 + (c.x1 - c.x0) * mx, wy = c.y0 + (c.y1 - c.y0) * my;
  MV.cam = { x0: wx - (wx - c.x0) * f, x1: wx + (c.x1 - wx) * f, y0: wy - (wy - c.y0) * f, y1: wy + (c.y1 - wy) * f };
}, { passive: false });
let dragAt = null;
cv.addEventListener("pointerdown", (e) => { if (e.button === 0) { dragAt = { x: e.clientX, y: e.clientY }; cv.setPointerCapture(e.pointerId); cv.classList.add("drag"); } });
cv.addEventListener("pointermove", (e) => {
  if (!dragAt) return;
  const dx = e.clientX - dragAt.x, dy = e.clientY - dragAt.y;
  if (!dx && !dy) return;
  manual();
  const k = (MV.cam.x1 - MV.cam.x0) / cv.clientWidth;
  MV.cam.x0 -= dx * k; MV.cam.x1 -= dx * k; MV.cam.y0 -= dy * k; MV.cam.y1 -= dy * k;
  dragAt = { x: e.clientX, y: e.clientY };
});
const endDrag = () => { dragAt = null; cv.classList.remove("drag"); };
cv.addEventListener("pointerup", endDrag);
cv.addEventListener("pointercancel", endDrag);
function fitAll() {
  MV.auto = true;
  $("fitBtn").classList.add("hidden");
  MV.target = MV.big && !MV.bigInPlace ? { ...MV.bigRect } : MV.big ? { ...MV.bigRect } : bboxTiles(0.25);
}
$("fitBtn").onclick = fitAll;
cv.addEventListener("dblclick", fitAll);
window.addEventListener("keydown", (e) => { if ((e.key === "f" || e.key === "F") && MV.active && !e.target.closest("input")) fitAll(); });
window.addEventListener("resize", () => { if (MV.auto && MV.target) MV.cam = fitAspect(MV.target); });

// ---------- 손으로 찍은 사진 ----------
function renderManual() {
  const job = S.job;
  const images = job ? job.images : [];
  $("dropEmpty").classList.toggle("hidden", images.length > 0 || !!S.upload);
  $("manualStitch").disabled = images.length < 2 || !!S.upload || !!(job && job.live);
  $("pickFiles").disabled = !!S.upload;
  renderUpload();
  renderShots();
}
function renderUpload() {
  const box = $("uploadProgress");
  box.classList.toggle("hidden", !S.upload);
  if (!S.upload) return;
  box.querySelector("i").style.width = `${Math.round((S.upload.done / S.upload.total) * 100)}%`;
  box.querySelector(".left").textContent = `넣는 중 ${S.upload.done}/${S.upload.total}`;
}
function renderShots() {
  const view = $("shotsView");
  const job = S.job;
  if (!job) { view.innerHTML = ""; view.dataset.key = ""; return; }
  const key = job.id + "|" + job.images.join("|");
  if (view.dataset.key === key) return;
  view.dataset.key = key;
  view.innerHTML = "";
  job.images.forEach((name, i) => {
    const fig = document.createElement("figure");
    fig.className = "shot";
    fig.innerHTML = `<img loading="lazy" alt=""><figcaption></figcaption><button class="rm" type="button" title="빼기">✕</button>`;
    fig.querySelector("img").src = `/api/jobs/${job.id}/shot/${encodeURIComponent(name)}`;
    fig.querySelector("figcaption").textContent = `${i + 1}. ${name}`;
    fig.querySelector(".rm").onclick = async () => {
      try { S.job = await post(`/api/jobs/${job.id}/remove`, { name }); render(); } catch (err) { toast(err.message); }
    };
    view.appendChild(fig);
  });
}
async function addFiles(fileList) {
  const files = [...fileList].filter((f) => /\.(png|jpe?g|webp|bmp)$/i.test(f.name));
  if (!files.length) return toast("이미지 파일이 없어요");
  if (S.upload) return;
  if (!S.job || stitchDone(S.job) || (S.job.capture && !S.job.images.length)) S.job = await post("/api/jobs");
  S.mode = "manual";
  files.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  S.upload = { done: 0, total: files.length, failed: [] };
  render();
  const id = S.job.id;
  let next = 0;
  async function worker() {
    while (next < files.length) {
      const file = files[next++];
      try {
        await api(`/api/jobs/${id}/files?name=${encodeURIComponent(file.name)}`, { method: "PUT", body: file });
      } catch (err) {
        S.upload.failed.push(`${file.name}: ${err.message}`);
      }
      S.upload.done += 1;
      renderUpload();
    }
  }
  await Promise.all([worker(), worker(), worker()]);
  const failed = S.upload.failed;
  S.upload = null;
  S.job = await api(`/api/jobs/${id}`);
  render();
  $("manualMsg").textContent = failed.length ? `${failed.length}장은 못 넣었어요 — ${failed[0]}` : "";
  $("manualMsg").classList.toggle("hidden", !failed.length);
  refreshJobs();
}

// ---------- 옆 판 ----------
function openSheet(id) {
  for (const s of ["sheetHistory", "sheetSettings", "sheetTrim"]) $(s).classList.toggle("hidden", s !== id);
  $("veil").classList.toggle("hidden", !id);
}
function renderHistory() {
  const list = $("historyList");
  list.innerHTML = "";
  if (!S.jobs.length) { list.innerHTML = `<li class="empty">아직 만든 지도가 없어요</li>`; return; }
  for (const j of S.jobs) {
    const li = document.createElement("li");
    li.classList.toggle("current", !!(S.job && S.job.id === j.id));
    li.innerHTML = `<span class="thumb"></span><span><div class="when"></div><div class="what"></div></span><button class="del" type="button" title="임시휴지통으로">✕</button>`;
    if (j.thumb) li.querySelector(".thumb").style.backgroundImage = `url(/api/jobs/${j.id}/preview/${j.thumb}_small.jpg)`;
    li.querySelector(".when").textContent = fmtId(j.id);
    li.querySelector(".what").textContent = `사진 ${j.count}장 · ${j.trimmed ? "완성" : j.stitched ? "합침" : "합치기 전"}`;
    li.onclick = () => { openSheet(null); loadJob(j.id); };
    li.querySelector(".del").onclick = async (e) => {
      e.stopPropagation();
      if (!confirm(`${fmtId(j.id)} 지도를 임시휴지통으로 옮길까요?`)) return;
      try {
        await post(`/api/jobs/${j.id}/trash`);
        if (S.job && S.job.id === j.id) { S.job = null; render(); }
        refreshJobs();
      } catch (err) { toast(err.message); }
    };
    list.appendChild(li);
  }
}
function syncTrimOptions(o) {
  if (!o) return;
  $("margin").value = o.margin;
  $("marginValue").textContent = o.margin;
  S.opts.line = o.line;
  S.opts.islands = o.islands;
  setSeg("lineSeg", o.line);
  setSeg("islandSeg", o.islands);
}
function setSeg(id, value) {
  for (const b of $(id).querySelectorAll("button")) { b.classList.toggle("on", b.dataset.v === value); b.setAttribute("aria-checked", b.dataset.v === value); }
}

// ---------- 버튼 ----------
const openPath = (what) => S.job && post(`/api/jobs/${S.job.id}/open`, { what }).catch((e) => toast(e.message));
$("introOk").onclick = () => { S.introSeen = true; store.set("introSeen", "1"); if (S.mode === "intro") S.mode = null; render(); };
$("navAbout").onclick = () => { S.mode = "intro"; render(); };
$("navHistory").onclick = () => { refreshJobs(); openSheet("sheetHistory"); };
$("navSettings").onclick = () => openSheet("sheetSettings");
$("veil").onclick = () => openSheet(null);
for (const b of document.querySelectorAll("[data-close]")) b.onclick = () => openSheet(null);
$("startCapture").onclick = startCapture;
$("goManual").onclick = () => { S.mode = "manual"; if (S.job && (stitchDone(S.job) || S.job.capture)) S.job = null; render(); };
$("manualBack").onclick = () => { S.mode = null; if (S.job && !S.job.live && !stitchDone(S.job)) S.job = null; render(); };
const stopCapture = () => S.job && post(`/api/jobs/${S.job.id}/capture-stop`).catch((e) => toast(e.message));
$("waitCancel").onclick = stopCapture;
$("capStop").onclick = stopCapture;
$("mergeStop").onclick = () => post(`/api/jobs/${S.job.id}/cancel`).catch((e) => toast(e.message));
async function runAction(action, body) {
  try {
    S.mode = null;
    S.job = await post(`/api/jobs/${S.job.id}/${action}`, body);
    render();
    schedulePoll();
  } catch (err) { toast(err.message); }
}
$("manualStitch").onclick = () => runAction("stitch");
$("failStitch").onclick = () => runAction("stitch");
$("failRetry").onclick = () => { S.job = null; S.mode = null; render(); };
$("failFolder").onclick = () => openPath("folder");
$("newCapture").onclick = () => { S.job = null; S.mode = null; render(); };
$("openFolder").onclick = () => openPath("folder");
$("openBig").onclick = () => openPath(S.viewKind === "trimmed" && S.job.previews.trimmed ? "trimmed" : "stitched");
$("openTrim").onclick = () => { syncTrimOptions(S.job.trim && S.job.trim.options); openSheet("sheetTrim"); };
$("trimBtn").onclick = () => { openSheet(null); runAction("trim", { margin: Number($("margin").value), line: S.opts.line, islands: S.opts.islands }); };
$("margin").oninput = () => ($("marginValue").textContent = $("margin").value);
for (const [id, key] of [["lineSeg", "line"], ["islandSeg", "islands"]]) {
  $(id).addEventListener("click", (e) => { const b = e.target.closest("button"); if (!b) return; S.opts[key] = b.dataset.v; setSeg(id, b.dataset.v); });
}
$("viewToggle").addEventListener("click", (e) => { const b = e.target.closest("button"); if (!b) return; S.viewKind = b.dataset.v; render(); });
const pick = () => $("fileInput").click();
$("pickFiles").onclick = pick;
$("pickFilesEmpty").onclick = pick;
$("fileInput").onchange = (e) => { addFiles(e.target.files); e.target.value = ""; };
$("autoMax").value = store.get("autoMax", "300");
$("autoMax").oninput = () => store.set("autoMax", readMax());

// ---------- 키 설정(11/12 사용자: Home 키가 없는 키보드도 있다) ----------
// 브라우저의 키 이름(e.code) → Windows 가상 키 번호와 화면에 보일 이름. Esc는 늘 '멈추기'라 고를 수 없다.
function keyOf(code) {
  let m;
  if ((m = code.match(/^Key([A-Z])$/))) return { vk: m[1].charCodeAt(0), name: m[1], game: true };
  if ((m = code.match(/^Digit(\d)$/))) return { vk: 0x30 + +m[1], name: m[1], game: true };
  if ((m = code.match(/^F(\d{1,2})$/)) && +m[1] >= 1 && +m[1] <= 12) return { vk: 0x6f + +m[1], name: `F${m[1]}` };
  if ((m = code.match(/^Numpad(\d)$/))) return { vk: 0x60 + +m[1], name: `숫자패드 ${m[1]}` };
  const named = {
    Home: [0x24, "Home"], End: [0x23, "End"], Insert: [0x2d, "Insert"], Delete: [0x2e, "Delete"],
    PageUp: [0x21, "Page Up"], PageDown: [0x22, "Page Down"], Pause: [0x13, "Pause"], ScrollLock: [0x91, "Scroll Lock"],
    ArrowLeft: [0x25, "←"], ArrowUp: [0x26, "↑"], ArrowRight: [0x27, "→"], ArrowDown: [0x28, "↓"],
    Backquote: [0xc0, "`"], Minus: [0xbd, "-"], Equal: [0xbb, "="], BracketLeft: [0xdb, "["], BracketRight: [0xdd, "]"],
    Backslash: [0xdc, "\\"], Semicolon: [0xba, ";"], Quote: [0xde, "'"], Comma: [0xbc, ","], Period: [0xbe, "."], Slash: [0xbf, "/"],
    NumpadAdd: [0x6b, "숫자패드 +"], NumpadSubtract: [0x6d, "숫자패드 -"], NumpadMultiply: [0x6a, "숫자패드 *"], NumpadDivide: [0x6f, "숫자패드 /"],
  };
  return named[code] ? { vk: named[code][0], name: named[code][1] } : null;
}
function applyKeyNames() {
  for (const el of document.querySelectorAll("[data-key]")) el.textContent = S.keys[el.dataset.key].name;
  $("keyStart").textContent = S.keys.start.name;
  $("keyStop").textContent = S.keys.stop.name;
}
async function loadKeys() {
  try { S.keys = (await api("/api/settings")).keys; } catch (e) {}
  applyKeyNames();
}
let listening = null;
function keyMsg(text) { $("keyMsg").textContent = text; $("keyMsg").classList.toggle("hidden", !text); }
for (const id of ["keyStart", "keyStop"]) {
  $(id).onclick = () => {
    if (listening) $(listening).classList.remove("listen");
    listening = id;
    $(id).classList.add("listen");
    $(id).textContent = "바꿀 키를 누르세요…";
    keyMsg("");
  };
}
async function saveKeys(keys) {
  try {
    S.keys = (await post("/api/settings", { keys })).keys;
  } catch (err) {
    keyMsg(err.message);
  }
  applyKeyNames();
}
window.addEventListener("keydown", (e) => {
  if (!listening) return;
  e.preventDefault();
  e.stopPropagation();
  const id = listening;
  listening = null;
  $(id).classList.remove("listen");
  if (e.code === "Escape") { keyMsg("Esc는 늘 '멈추기'라서 고를 수 없어요."); applyKeyNames(); return; }
  const k = keyOf(e.code);
  if (!k) { keyMsg("그 키는 쓸 수 없어요. F1~F12, Home, End, Insert, Page Up 같은 키를 골라 주세요."); applyKeyNames(); return; }
  const which = $(id).dataset.which, other = which === "start" ? "stop" : "start";
  if (S.keys[other].vk === k.vk) { keyMsg("시작 키와 멈추기 키는 달라야 해요."); applyKeyNames(); return; }
  keyMsg(k.game ? `${k.name} 키는 원신에서도 쓰는 키일 수 있어요(예: M은 지도 열기). 겹치면 F1~F12 같은 키를 추천해요.` : "");
  saveKeys({ ...S.keys, [which]: { vk: k.vk, name: k.name } });
}, true);
$("keyReset").onclick = () => { keyMsg(""); saveKeys({ start: { vk: 0x24, name: "Home" }, stop: { vk: 0x23, name: "End" } }); };
$("keyHint").onclick = () => openSheet("sheetSettings");

// 끌어다 놓기(손으로 찍은 사진 화면에서만)
let dragDepth = 0;
const hasFiles = (e) => [...((e.dataTransfer && e.dataTransfer.types) || [])].includes("Files");
window.addEventListener("dragenter", (e) => { if (!hasFiles(e)) return; e.preventDefault(); dragDepth += 1; document.body.classList.add("dragging"); });
window.addEventListener("dragover", (e) => hasFiles(e) && e.preventDefault());
window.addEventListener("dragleave", () => { dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth) document.body.classList.remove("dragging"); });
window.addEventListener("drop", (e) => {
  e.preventDefault();
  dragDepth = 0;
  document.body.classList.remove("dragging");
  const busy = S.job && (S.job.live || captureActive(S.job));
  if (e.dataTransfer && e.dataTransfer.files.length && !busy) addFiles(e.dataTransfer.files);
});

// 창이 열려 있다고 서버에 알리기
const ping = () => fetch("/api/ping", { method: "POST" }).catch(() => toast("앱 서버가 꺼져 있어요. 바로가기로 다시 켜 주세요."));
setInterval(ping, 20000);
document.addEventListener("visibilitychange", () => document.visibilityState === "visible" && ping());
window.addEventListener("pagehide", () => navigator.sendBeacon("/api/bye"));

// ---------- 시작 ----------
(async function init() {
  ping();
  loadKeys();
  try {
    await refreshJobs();
    const latest = S.jobs[0];
    if (latest) {  // 돌고 있는 작업이 있으면 그 화면으로, 아니면 준비 화면
      const job = await api(`/api/jobs/${latest.id}`);
      if (job.live || captureActive(job)) { S.job = job; schedulePoll(); }
    }
  } catch (err) {
    toast(err.message);
  }
  render();
})();
