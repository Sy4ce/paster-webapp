'use strict';

/* Paster — one page, one job: drop a file, get a link that dies in five minutes.
   The countdown is driven by the server's expiry, not the visitor's clock,
   because client clocks drift and the server is the only authority here. */

const els = {
  vitals: document.getElementById('vitals'),
  vitalsText: document.getElementById('vitalsText'),
  ticket: document.getElementById('ticket'),
  drop: document.getElementById('drop'),
  file: document.getElementById('file'),
  dropMain: document.getElementById('dropMain'),
  dropMeta: document.getElementById('dropMeta'),
  progress: document.getElementById('progress'),
  progressBar: document.getElementById('progressBar'),
  notice: document.getElementById('notice'),
  collect: document.getElementById('collect'),
  pickupLink: document.getElementById('pickupLink'),
  copy: document.getElementById('copy'),
  clock: document.querySelector('.clock'),
  flap: document.getElementById('flap'),
  pickupMeta: document.getElementById('pickupMeta'),
  stamp: document.getElementById('stamp'),
};

const DEFAULT_DROP_MAIN = els.dropMain.textContent;

let limits = null;
let busy = false;
let digits = [];
let ticker = null;
let remaining = 0;
let current = null;          // { token, expiresAtUtc, name, size }

/* ------------------------------------------------------------------ format */

function fmtBytes(bytes) {
  if (bytes >= 1024 * 1024) return (bytes / 1048576).toFixed(bytes % 1048576 === 0 ? 0 : 1) + ' MiB';
  if (bytes >= 1024) return (bytes / 1024).toFixed(bytes % 1024 === 0 ? 0 : 1) + ' KiB';
  return bytes + ' B';
}

function fmtClock(seconds) {
  const whole = Math.max(0, Math.ceil(seconds));
  const mm = String(Math.floor(whole / 60)).padStart(2, '0');
  const ss = String(whole % 60).padStart(2, '0');
  return mm + ss;
}

function fmtSpoken(seconds) {
  const whole = Math.max(0, Math.ceil(seconds));
  const mm = Math.floor(whole / 60);
  const ss = whole % 60;
  return mm > 0 ? `剩余 ${mm} 分 ${ss} 秒` : `剩余 ${ss} 秒`;
}

/* ------------------------------------------------------------------ status */

function setVitals(state, text) {
  els.vitals.dataset.state = state;
  els.vitalsText.textContent = text;
}

function showNotice(message) {
  els.notice.textContent = message;
  els.notice.hidden = false;
}

function clearNotice() {
  els.notice.hidden = true;
  els.notice.textContent = '';
}

function describeLimits() {
  if (!limits) return '单文件 ≤ 1 MiB';
  return `单文件 ≤ ${fmtBytes(limits.maxFileBytes)} · 剩余 ${fmtBytes(limits.freeBytes)} · 存活 ${Math.round(limits.ttlSeconds / 60)} 分钟`;
}

async function fetchJson(url, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { signal: controller.signal, headers: { accept: 'application/json' } });
    const body = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, body };
  } finally {
    clearTimeout(timer);
  }
}

/* The free tier unloads idle apps, so the very first request of the day can sit
   there for tens of seconds. Say so instead of looking broken. */
async function loadHealth() {
  try {
    const { ok, body } = await fetchJson('/api/health', 60000);
    if (!ok || !body || body.status !== 'ok') throw new Error('health unavailable');
    limits = body;
    setVitals('live', `剩余容量 ${fmtBytes(body.freeBytes)}`);
    if (!busy) els.dropMeta.textContent = describeLimits();
  } catch {
    setVitals('waking', '正在唤醒服务器…');
    els.dropMeta.textContent = '免费层闲置会休眠，首次访问需要十几秒';
    setTimeout(loadHealth, 5000);
  }
}

/* ------------------------------------------------------------------ upload */

function setBusy(value) {
  busy = value;
  els.drop.classList.toggle('is-busy', value);
}

function upload(file) {
  if (!file || busy) return;

  clearNotice();
  els.drop.classList.remove('is-over');
  els.file.value = '';

  if (limits && file.size > limits.maxFileBytes) {
    showNotice(`这个文件 ${fmtBytes(file.size)}，超过 ${fmtBytes(limits.maxFileBytes)} 上限。压缩后再试。`);
    return;
  }

  const form = new FormData();
  form.append('file', file, file.name);

  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/upload');
  xhr.responseType = 'json';

  setBusy(true);
  els.dropMain.textContent = `正在上传 ${file.name}`;
  els.dropMeta.textContent = fmtBytes(file.size) + ' · 0%';
  els.progress.hidden = false;
  els.progressBar.style.width = '0%';

  xhr.upload.addEventListener('progress', (event) => {
    if (!event.lengthComputable) return;
    const percent = Math.round((event.loaded / event.total) * 100);
    els.progressBar.style.width = percent + '%';
    els.dropMeta.textContent = fmtBytes(file.size) + ' · ' + percent + '%';
  });

  xhr.addEventListener('load', () => {
    els.progress.hidden = true;
    els.progressBar.style.width = '0%';
    setBusy(false);

    const body = xhr.response;
    if (xhr.status === 201 && body && body.url) {
      els.dropMain.textContent = '已寄存 · 可以再放一个';
      els.dropMeta.textContent = describeLimits();
      setTimeout(() => { els.dropMain.textContent = DEFAULT_DROP_MAIN; }, 2500);
      showTicket(body);
      return;
    }

    showNotice(messageFor(xhr.status, body));
  });

  xhr.addEventListener('error', () => {
    els.progress.hidden = true;
    setBusy(false);
    els.dropMain.textContent = DEFAULT_DROP_MAIN;
    els.dropMeta.textContent = describeLimits();
    showNotice('连不上服务器。检查网络后再试一次。');
  });

  xhr.send(form);
}

function messageFor(status, body) {
  if (body && typeof body.error === 'string') return body.error;
  if (status === 413) return '文件超过上限。压缩后再试。';
  if (status === 507) return '暂存空间已满，等已有链接过期后再试。';
  if (status === 400) return '没有收到文件，请重新选择一个非空文件。';
  return `上传失败（HTTP ${status || '无响应'}）。稍后再试。`;
}

/* --------------------------------------------------------------- the ticket */

function showTicket(data) {
  current = {
    token: data.token,
    expiresAtUtc: data.expiresAtUtc,
    name: data.name,
    size: data.size,
  };

  els.pickupLink.value = data.url;
  els.copy.disabled = false;
  els.copy.textContent = '复制链接';
  els.stamp.hidden = true;
  els.ticket.classList.remove('is-dead');
  els.collect.hidden = false;
  els.collect.classList.remove('is-in');
  void els.collect.offsetWidth;
  els.collect.classList.add('is-in');

  if (limits) {
    limits.freeBytes = data.freeBytes;
    setVitals('live', `剩余容量 ${fmtBytes(data.freeBytes)}`);
  }

  startCountdown(data.expiresInSeconds, data.token);
}

function startCountdown(seconds, token) {
  const total = Math.max(0, seconds);
  const startedAt = performance.now();
  const totalMinutes = Math.max(1, Math.round(total / 60));
  let lastSync = startedAt;

  els.clock.classList.remove('is-dead', 'is-critical');
  renderPickupMeta(totalMinutes);

  if (ticker) clearInterval(ticker);

  const paint = () => {
    remaining = total - (performance.now() - startedAt) / 1000;
    if (remaining <= 0) {
      expire();
      return;
    }

    els.clock.classList.toggle('is-critical', remaining <= 60);
    drawFlap(fmtClock(remaining));
    els.flap.setAttribute('aria-label', fmtSpoken(remaining));

    if (performance.now() - lastSync > 15000) {
      lastSync = performance.now();
      syncStatus(token);
    }
  };

  paint();
  ticker = setInterval(paint, 250);
}

function renderPickupMeta(totalMinutes) {
  const parts = [];
  if (current) parts.push(current.name, fmtBytes(current.size));
  parts.push(`${totalMinutes} 分钟后自动删除`);
  els.pickupMeta.textContent = parts.join(' · ');
}

function expire(reason) {
  if (ticker) {
    clearInterval(ticker);
    ticker = null;
  }

  els.clock.classList.remove('is-critical');
  els.clock.classList.add('is-dead');
  els.ticket.classList.add('is-dead');
  els.copy.disabled = true;
  els.stamp.hidden = false;
  drawFlap('0000');
  els.flap.setAttribute('aria-label', '已到期，文件已删除');
  els.pickupMeta.textContent = reason || '取件链接已作废，文件已从服务器删除。重新寄存可以再拿一条。';
  current = null;
  loadHealth();
}

async function syncStatus(token) {
  try {
    const { body } = await fetchJson(`/api/status/${encodeURIComponent(token)}`, 20000);
    if (!body) return;
    if (!body.exists) {
      if (!els.ticket.classList.contains('is-dead')) expire();
      return;
    }
    // Trust the server's remaining time over our own arithmetic.
    if (typeof body.expiresInSeconds === 'number' && Math.abs(body.expiresInSeconds - remaining) > 2) {
      startCountdown(body.expiresInSeconds, token);
    }
  } catch {
    // A missed poll is not worth telling the visitor about; the timer keeps running.
  }
}

/* ---------------------------------------------------- split-flap rendering */

function buildFlap() {
  els.flap.textContent = '';
  digits = [];

  for (let index = 0; index < 4; index++) {
    const cell = document.createElement('span');
    cell.className = 'flap-cell';
    const digit = document.createElement('span');
    digit.className = 'flap-digit';
    digit.textContent = '0';
    cell.append(digit);
    els.flap.append(cell);
    digits.push(digit);

    if (index === 1) {
      const colon = document.createElement('span');
      colon.className = 'flap-colon';
      colon.setAttribute('aria-hidden', 'true');
      colon.append(document.createElement('i'), document.createElement('i'));
      els.flap.append(colon);
    }
  }
}

function drawFlap(text) {
  for (let index = 0; index < digits.length; index++) {
    const digit = digits[index];
    const next = text[index];
    if (digit.textContent === next) continue;
    digit.textContent = next;
    digit.classList.remove('is-flipping');
    void digit.offsetWidth;
    digit.classList.add('is-flipping');
  }
}

/* --------------------------------------------------------------- clipboard */

async function copyLink() {
  const value = els.pickupLink.value;
  if (!value) return;

  let copied = false;
  try {
    await navigator.clipboard.writeText(value);
    copied = true;
  } catch {
    els.pickupLink.select();
    copied = document.execCommand('copy');
    els.pickupLink.setSelectionRange(0, 0);
  }

  if (copied) {
    els.copy.textContent = '已复制';
    setTimeout(() => { els.copy.textContent = '复制链接'; }, 1600);
  } else {
    els.pickupLink.select();
    showNotice('浏览器不允许自动复制，链接已选中，按 Ctrl+C 复制。');
  }
}

/* ------------------------------------------------------------------ wiring */

buildFlap();

els.file.addEventListener('change', () => upload(els.file.files && els.file.files[0]));

['dragenter', 'dragover'].forEach((type) => {
  els.drop.addEventListener(type, (event) => {
    event.preventDefault();
    if (!busy) els.drop.classList.add('is-over');
  });
});

['dragleave', 'dragend'].forEach((type) => {
  els.drop.addEventListener(type, () => els.drop.classList.remove('is-over'));
});

els.drop.addEventListener('drop', (event) => {
  event.preventDefault();
  els.drop.classList.remove('is-over');
  const file = event.dataTransfer && event.dataTransfer.files && event.dataTransfer.files[0];
  upload(file);
});

els.copy.addEventListener('click', copyLink);
els.pickupLink.addEventListener('focus', () => els.pickupLink.select());

document.addEventListener('visibilitychange', () => {
  if (!document.hidden && current) syncStatus(current.token);
});

loadHealth();
