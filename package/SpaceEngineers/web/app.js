'use strict';
// Веб-интерфейс симулятора: опрашивает SeHelper и рисует схему блоков, экраны и инспектор.

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

let S = null;            // последнее состояние
let selected = null;     // id выбранного блока
let catalog = { blocks: [], items: [] };
let lastVersion = -1;
let inspectorFor = null; // для какого блока нарисован инспектор

async function api(path, data) {
  const opt = data === undefined ? {} : { method: 'POST', body: JSON.stringify(data), headers: { 'Content-Type': 'application/json' } };
  const r = await fetch('/api/' + path, opt);
  return r.json();
}
const control = (action, extra) => api('control', Object.assign({ action }, extra || {})).then(poll);
const updateBlock = (id, patch) => api('block', Object.assign({ id }, patch)).then(poll);

// ---------- опрос ----------

async function poll() {
  try {
    const st = await api('state');
    $('offline').classList.add('hidden');
    S = st;
    if (st.version !== lastVersion) { lastVersion = st.version; render(); }
  } catch (e) {
    $('offline').classList.remove('hidden');
  }
}
setInterval(poll, 200);

// ---------- отрисовка ----------

function render() {
  renderHeader();
  renderLeft();
  renderScreens();
  renderBlocks();
  const insp = $('inspector');
  if (!insp.contains(document.activeElement) || inspectorFor !== selected) renderInspector();
}

function renderHeader() {
  const sc = S.script, st = $('scriptStatus');
  if (!sc.loaded && !sc.errors && !S.error) { st.textContent = 'скрипт не загружен — сохрани .cs в Sublime'; st.className = 'badge'; }
  else if (sc.errors) { st.textContent = `✗ ${sc.name || 'скрипт'}: ошибок ${sc.errors}`; st.className = 'badge bad'; }
  else if (S.crashed) { st.textContent = `⚠ ${sc.name || 'скрипт'}: остановлен`; st.className = 'badge warn'; }
  else { st.textContent = `✓ ${sc.name || 'скрипт'}` + (sc.warnings ? ` (предупр. ${sc.warnings})` : ''); st.className = 'badge ok'; }
  const run = $('btnRun');
  run.textContent = S.running ? '⏸ Пауза' : '▶ Пуск';
  run.classList.toggle('on', S.running);
  const secs = S.tick / 60;
  $('clock').textContent = `тик ${S.tick} · ${secs.toFixed(1)} с`;
  const sp = $('speed');
  if (document.activeElement !== sp) sp.value = String(S.speed);
  const g = S.grid;
  $('gridName').textContent = g.name + (g.file ? '' : ' (не сохранена)');
  $('gridName').title = g.file || 'Сетка ещё не сохранена в файл рядом со скриптом';
}

function renderLeft() {
  const pb = S.blocks.find((b) => b.isPb);
  $('pbName').textContent = pb ? '— ' + pb.name : '';
  const err = $('error');
  if (S.error) {
    err.classList.remove('hidden');
    err.textContent = S.error + (S.errorLine >= 0 ? `\n→ строка ${S.errorLine + 1}` : '');
  } else err.classList.add('hidden');
  $('freq').textContent = S.frequency;
  $('lastRun').textContent = S.runs ? `${S.lastRunMs.toFixed(3)} мс · запусков ${S.runs}` : '—';
  $('echo').textContent = S.echo || '';
  const d = S.script.diagnostics || [];
  $('diags').innerHTML = d.length
    ? '<div class="sub">Диагностика</div>' + d.map((x) => `<div class="diag ${x.severity}">${x.line + 1}:${x.col + 1} ${esc(x.id)} ${esc(x.message)}</div>`).join('')
    : '';
  const log = $('log');
  const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 4;
  log.textContent = S.log.join('\n');
  if (atBottom) log.scrollTop = log.scrollHeight;
}

function colorToCss(hex) { return /^#[0-9a-f]{6}/i.test(hex || '') ? hex.slice(0, 7) : '#ffffff'; }

// ---------- спрайты ----------

const SCREEN_SCALE = 0.4;
const texCache = new Map();    // имя -> Image | null (нет такой текстуры)
const tintCache = new Map();   // имя|цвет -> canvas

function texture(name) {
  if (texCache.has(name)) return texCache.get(name);
  const img = new Image();
  texCache.set(name, img);
  img.onload = () => { lastVersion = -1; };              // перерисуем на следующем опросе
  img.onerror = () => { texCache.set(name, null); lastVersion = -1; };
  img.src = '/api/texture?name=' + encodeURIComponent(name);
  return img;
}

// Тонировка как в игре: RGB текстуры умножается на цвет спрайта, альфа берётся из текстуры.
function tinted(name, color) {
  const key = name + '|' + color;
  if (tintCache.has(key)) return tintCache.get(key);
  const img = texture(name);
  if (!img || !img.complete || !img.naturalWidth) return null;
  const c = document.createElement('canvas');
  c.width = img.naturalWidth; c.height = img.naturalHeight;
  const g = c.getContext('2d');
  g.fillStyle = opaque(color);
  g.fillRect(0, 0, c.width, c.height);
  g.globalCompositeOperation = 'multiply';
  g.drawImage(img, 0, 0);
  g.globalCompositeOperation = 'destination-in';
  g.drawImage(img, 0, 0);
  tintCache.set(key, c);
  return c;
}

function parseRgba(c) {
  const m = /rgba?\((\d+),(\d+),(\d+)(?:,([\d.]+))?\)/.exec(c || '');
  return m ? { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] } : { r: 255, g: 255, b: 255, a: 1 };
}
function opaque(c) { const x = parseRgba(c); return `rgb(${x.r},${x.g},${x.b})`; }

function drawSprites(canvas, s) {
  const g = canvas.getContext('2d');
  const W = s.width, H = s.height;
  g.setTransform(SCREEN_SCALE, 0, 0, SCREEN_SCALE, 0, 0);
  g.fillStyle = colorToCss(s.scriptBg);
  g.fillRect(0, 0, W, H);
  g.save();
  for (const sp of s.sprites || []) {
    if (sp.type === 'CLIP_RECT') {
      g.restore(); g.save();
      if (sp.size) { g.beginPath(); g.rect(sp.pos ? sp.pos[0] : 0, sp.pos ? sp.pos[1] : 0, sp.size[0], sp.size[1]); g.clip(); }
      continue;
    }
    const color = sp.color || 'rgba(255,255,255,1)';
    const alpha = parseRgba(color).a;
    if (sp.type === 'TEXT') {
      const scale = sp.rot || 1;
      const px = 28.8 * scale;
      const mono = (sp.font || '').toLowerCase() === 'monospace';
      g.font = `${mono ? '' : '600 '}${px}px ${mono ? 'Consolas, monospace' : '"Segoe UI", Arial, sans-serif'}`;
      g.textAlign = { LEFT: 'left', RIGHT: 'right' }[sp.align] || 'center';
      g.textBaseline = 'top';
      g.fillStyle = opaque(color);
      g.globalAlpha = alpha;
      const x = sp.pos ? sp.pos[0] : W / 2, y = sp.pos ? sp.pos[1] : 0;
      String(sp.data || '').split('\n').forEach((line, i) => g.fillText(line, x, y + i * 37 * scale));
      g.globalAlpha = 1;
      continue;
    }
    // TEXTURE: позиция — центр (или край при LEFT/RIGHT), размер по умолчанию — весь экран
    const sw = sp.size ? sp.size[0] : W, sh = sp.size ? sp.size[1] : H;
    let cx = sp.pos ? sp.pos[0] : W / 2;
    const cy = sp.pos ? sp.pos[1] : H / 2;
    if (sp.align === 'LEFT') cx += sw / 2; else if (sp.align === 'RIGHT') cx -= sw / 2;
    g.save();
    g.translate(cx, cy);
    if (sp.rot) g.rotate(sp.rot);
    g.globalAlpha = alpha;
    const name = sp.data || 'SquareSimple';
    const tex = tinted(name, color);
    if (tex) g.drawImage(tex, -sw / 2, -sh / 2, sw, sh);
    else if (name === 'SquareSimple' || texCache.get(name) === null) {
      // текстуры нет (или ещё грузится): квадрат — заливкой, остальное — пунктирной рамкой с именем
      if (name === 'SquareSimple') { g.fillStyle = opaque(color); g.fillRect(-sw / 2, -sh / 2, sw, sh); }
      else {
        g.strokeStyle = opaque(color); g.setLineDash([8, 6]); g.lineWidth = 3;
        g.strokeRect(-sw / 2, -sh / 2, sw, sh);
        g.fillStyle = opaque(color); g.font = '20px sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
        g.fillText(name, 0, 0, sw);
      }
    }
    g.restore();
  }
  g.restore();
}

function drawAllSprites() {
  document.querySelectorAll('canvas[data-sprites]').forEach((cv) => {
    const [id, idx] = cv.dataset.sprites.split(':');
    const b = S.blocks.find((x) => x.id === id);
    if (b && b.surfaces[+idx]) drawSprites(cv, b.surfaces[+idx]);
  });
}

function screenHtml(b, s) {
  const scale = SCREEN_SCALE;
  const w = s.width * scale, h = s.height * scale;
  if (s.contentType === 'SCRIPT') {
    const hint = s.sprites && s.sprites.length ? '' : '<div class="screen-hint">ContentType = SCRIPT, кадров DrawFrame ещё не было</div>';
    return `<div class="screen" style="width:${w}px;height:${h}px;padding:0">
      <canvas width="${w}" height="${h}" data-sprites="${b.id}:${s.index}"></canvas>${hint}</div>`;
  }
  const fpx = 28 * s.fontSize * scale;
  const pad = (s.padding / 100) * Math.min(w, h);
  const align = { LEFT: 'left', CENTER: 'center', RIGHT: 'right' }[s.alignment] || 'left';
  const hidden = s.sprites && s.sprites.length
    ? '<div class="screen-hint">спрайты есть, но ContentType ≠ SCRIPT — в игре их не видно</div>' : '';
  return `<div class="screen" style="width:${w}px;height:${h}px;background:${colorToCss(s.backgroundColor)};padding:${pad}px">
    <pre style="font-size:${fpx}px;color:${colorToCss(s.fontColor)};text-align:${align}">${esc(s.text)}</pre>${hidden}</div>`;
}

function renderScreens() {
  const parts = [];
  for (const b of S.blocks) {
    b.surfaces.forEach((s, i) => {
      const show = b.typeId === 'TextPanel' || (s.text && s.text.length) || s.contentType === 'SCRIPT' || (s.sprites && s.sprites.length);
      if (!show) return;
      const title = b.surfaces.length > 1 ? `${b.name} · ${s.name}` : b.name;
      parts.push(`<div class="screen-wrap ${b.id === selected ? 'sel' : ''}" data-id="${b.id}">
        <div class="screen-title" title="${esc(title)}">${esc(title)}</div>${screenHtml(b, s)}</div>`);
    });
  }
  $('screens').innerHTML = parts.join('');
  drawAllSprites();
  $('screensTitle').textContent = `Экраны (${parts.length})`;
  $('screensBox').classList.toggle('hidden', parts.length === 0);
}

const GOOD_HIGH = ['Заряд', 'Заполнение', 'Топливо'];
function barColor(bar) {
  const v = bar.value;
  if (GOOD_HIGH.includes(bar.label)) return `hsl(${Math.round(v * 120)},65%,45%)`;
  if (/ л$/.test(bar.text)) return v > 0.95 ? '#ef5f5f' : v > 0.8 ? '#f2a64e' : '#3f8fd8';
  return '#4fc3f7';
}

function barHtml(bar) {
  const pct = (bar.value * 100).toFixed(bar.value < 0.1 && bar.value > 0 ? 1 : 0);
  return `<div class="bar"><div class="bl"><span>${esc(bar.label)}</span><b>${pct}% · ${esc(bar.text)}</b></div>
    <div class="track"><div class="fill" style="width:${(bar.value * 100).toFixed(2)}%;background:${barColor(bar)}"></div></div></div>`;
}

const CHIP_PROPS = ['Status', 'ChargeMode', 'Velocity', 'TargetVelocityRPM', 'IsLocked', 'IsConnected', 'IsCountingDown', 'IsActive', 'Stockpile', 'GyroOverride'];
function chipsHtml(b) {
  const chips = [];
  for (const k of CHIP_PROPS) {
    if (!(k in b.props)) continue;
    let v = b.props[k];
    if (typeof v === 'number') v = Math.round(v * 100) / 100;
    if (typeof v === 'boolean') { if (!v) continue; v = '✓'; }
    chips.push(`<span class="chip">${esc(k)}: ${esc(v)}</span>`);
  }
  for (const g of b.groups) chips.push(`<span class="chip" title="группа">⧉ ${esc(g)}</span>`);
  return chips.length ? `<div class="chips">${chips.join('')}</div>` : '';
}

function hasEnabled(b) {
  return b.iface !== 'IMyTerminalBlock' && b.typeId !== 'CargoContainer';
}

function cardHtml(b) {
  const cls = ['card'];
  if (b.id === selected) cls.push('sel');
  if (b.isPb) cls.push('pb');
  if (!b.enabled && hasEnabled(b)) cls.push('off');
  const tgl = hasEnabled(b) ? `<div class="toggle ${b.enabled ? 'on' : ''}" data-toggle="${b.id}" title="Вкл/выкл"></div>` : '';
  return `<div class="${cls.join(' ')}" data-id="${b.id}">${tgl}
    <div class="name" title="${esc(b.name)}">${b.isPb ? '⌨ ' : ''}${esc(b.name)}</div>
    <div class="type">${esc(b.display)} · ${esc(b.iface)}</div>
    ${b.bars.map(barHtml).join('')}${chipsHtml(b)}</div>`;
}

function renderBlocks() {
  const f = $('filter').value.trim().toLowerCase();
  const list = S.blocks.filter((b) => !f || (b.name + ' ' + b.display + ' ' + b.typeId + ' ' + b.iface).toLowerCase().includes(f));
  const by = $('groupBy').value;
  const groups = new Map();
  const put = (k, b) => { if (!groups.has(k)) groups.set(k, []); groups.get(k).push(b); };
  for (const b of list) {
    if (by === 'type') put(b.display, b);
    else if (by === 'grid') put(S.grid.gridNames[b.grid] || 'Сетка ' + b.grid, b);
    else if (by === 'group') { if (b.groups.length) b.groups.forEach((g) => put('⧉ ' + g, b)); else put('Без группы', b); }
    else put('Все блоки', b);
  }
  let keys = [...groups.keys()];
  if (by === 'type') keys.sort((a, b) => a.localeCompare(b, 'ru'));
  $('blocks').innerHTML = keys.map((k) => `<div class="group"><h4>${esc(k)} · ${groups.get(k).length}</h4>
    <div class="cards">${groups.get(k).map(cardHtml).join('')}</div></div>`).join('') || '<div class="dim">Нет блоков</div>';
}

// ---------- инспектор ----------

function fieldHtml(name, kind, value, opts) {
  opts = opts || {};
  const id = 'f_' + name;
  const lc = opts.sim ? 'sim' : (opts.ro ? 'ro' : '');
  const label = `<label class="${lc}" title="${esc(name)}">${esc(opts.label || name)}</label>`;
  let input;
  if (kind === 'bool') input = `<input type="checkbox" data-prop="${esc(name)}" data-kind="bool" ${value ? 'checked' : ''}>`;
  else if (kind === 'enum') input = `<select data-prop="${esc(name)}" data-kind="enum">${(opts.options || []).map((o) => `<option ${o === value ? 'selected' : ''}>${esc(o)}</option>`).join('')}</select>`;
  else if (kind === 'color') input = `<input type="color" data-prop="${esc(name)}" data-kind="color" value="${colorToCss(value)}">`;
  else if (kind === 'number') input = `<input type="number" step="any" data-prop="${esc(name)}" data-kind="number" value="${value === undefined || value === null ? '' : Math.round(Number(value) * 1e6) / 1e6}">`;
  else input = `<input data-prop="${esc(name)}" data-kind="string" value="${esc(value ?? '')}">`;
  return `<div class="field">${label}${input}</div>`;
}

function itemOptions() {
  let html = '', last = '';
  for (const it of catalog.items) {
    const t = it.typeId.replace('MyObjectBuilder_', '');
    if (t !== last) { if (last) html += '</optgroup>'; html += `<optgroup label="${esc(t)}">`; last = t; }
    html += `<option value="${esc(it.typeId + '|' + it.subtype)}">${esc(it.name)}</option>`;
  }
  return html + (last ? '</optgroup>' : '');
}

function renderInspector() {
  const el = $('inspector');
  inspectorFor = selected;
  const b = S && S.blocks.find((x) => x.id === selected);
  if (!b) { el.innerHTML = '<div class="dim">Кликни по блоку, чтобы увидеть и поправить его состояние.</div>'; return; }
  const schema = S.schemas[b.iface] || [];
  const known = new Map(schema.map((s) => [s.name, s]));
  const stateFields = [], moreFields = [];
  for (const [k, v] of Object.entries(b.props)) {
    const s = known.get(k);
    if (s) stateFields.push(fieldHtml(k, s.kind, v, { options: s.options, ro: !s.scriptCanSet && !s.sim, sim: s.sim, label: s.label }));
    else stateFields.push(fieldHtml(k, typeof v === 'boolean' ? 'bool' : typeof v === 'number' ? 'number' : 'string', v, { sim: k.startsWith('_') }));
  }
  for (const s of schema) if (!(s.name in b.props) && !s.sim) moreFields.push(fieldHtml(s.name, s.kind, s.kind === 'bool' ? false : s.kind === 'enum' ? (s.options || [])[0] : '', { options: s.options, ro: !s.scriptCanSet }));
  const allGroups = Object.keys(S.grid.groups);

  const inv = b.inventories.map((iv, i) => `<div class="section"><div class="title">Инвентарь ${b.inventories.length > 1 ? (i === 0 ? '(вход)' : '(выход)') : ''}
      <span class="dim">${(iv.curVolume * 1000).toFixed(0)} / <input type="number" step="any" data-inv-max="${i}" value="${(iv.maxVolume * 1000).toFixed(0)}" style="width:80px"> л</span></div>
      ${barHtml({ label: 'Заполнено', value: iv.maxVolume ? iv.curVolume / iv.maxVolume : 0, text: '' })}
      <table class="items">${iv.items.map((it, j) => `<tr><td title="${esc(it.typeId + '/' + it.subtype)}">${esc(it.name)}</td>
        <td><input type="number" step="any" data-inv="${i}" data-item="${j}" value="${Math.round(it.amount * 1000) / 1000}"></td>
        <td><button class="mini danger" data-inv-del="${i}" data-item="${j}">×</button></td></tr>`).join('')}</table>
      <div class="add-item"><select data-inv-new="${i}">${itemOptions()}</select><input type="number" value="100" data-inv-amt="${i}"><button data-inv-add="${i}">+</button></div></div>`).join('');

  const surf = b.surfaces.map((s, i) => `<div class="section"><div class="title">${esc(s.name)} <span class="dim">${s.width}×${s.height}</span></div>
      <textarea class="wide" data-surf="${i}" data-sprop="text">${esc(s.text)}</textarea>
      <div class="field"><label>FontSize</label><input type="number" step="0.1" data-surf="${i}" data-sprop="fontSize" value="${s.fontSize}"></div>
      <div class="field"><label>FontColor</label><input type="color" data-surf="${i}" data-sprop="fontColor" value="${colorToCss(s.fontColor)}"></div>
      <div class="field"><label>BackgroundColor</label><input type="color" data-surf="${i}" data-sprop="backgroundColor" value="${colorToCss(s.backgroundColor)}"></div>
      <div class="field"><label>Alignment</label><select data-surf="${i}" data-sprop="alignment">${['LEFT', 'CENTER', 'RIGHT'].map((a) => `<option ${a === s.alignment ? 'selected' : ''}>${a}</option>`).join('')}</select></div></div>`).join('');

  el.innerHTML = `
    <h3>${esc(b.display)}</h3><div class="dim mono" style="font-size:11px">${esc(b.typeId)}/${esc(b.subtype)} · ${esc(b.iface)} · id ${b.id}</div>
    <div class="section">
      <div class="field"><label>Имя (CustomName)</label><input data-meta="name" value="${esc(b.name)}"></div>
      ${hasEnabled(b) ? `<div class="field"><label>Enabled</label><input type="checkbox" data-meta="enabled" ${b.enabled ? 'checked' : ''}></div>` : ''}
      <div class="sub">Группы</div>
      <div class="group-chips">${b.groups.map((g) => `<span class="chip" data-group-del="${esc(g)}" title="Убрать из группы">⧉ ${esc(g)} ×</span>`).join('') || '<span class="dim">нет</span>'}</div>
      <div class="add-item" style="grid-template-columns:1fr auto"><input list="groupList" placeholder="добавить в группу…" data-group-new>
        <button data-group-add>+</button></div>
      <datalist id="groupList">${allGroups.map((g) => `<option value="${esc(g)}">`).join('')}</datalist>
      <div class="sub">CustomData</div>
      <textarea class="wide" data-meta="customData">${esc(b.customData)}</textarea>
    </div>
    ${b.bars.length ? `<div class="section"><div class="title">Шкалы</div>${b.bars.map(barHtml).join('')}</div>` : ''}
    <div class="section"><div class="title">Состояние</div>${stateFields.join('') || '<div class="dim">нет особых состояний</div>'}
      ${moreFields.length ? `<details><summary>Ещё свойства интерфейса (${moreFields.length})</summary>${moreFields.join('')}</details>` : ''}
    </div>
    ${surf}${inv}
    <div class="section btns">
      ${b.typeId === 'MyProgrammableBlock' && !b.isPb ? `<button data-make-pb>Запускать скрипт на этом PB</button>` : ''}
      <button class="danger" data-remove>Удалить блок</button>
    </div>`;
}

// ---------- события ----------

function parseVal(kind, el) {
  if (kind === 'bool') return el.checked;
  if (kind === 'number') { const v = parseFloat(el.value); return isNaN(v) ? 0 : v; }
  return el.value;
}

document.addEventListener('click', async (e) => {
  const t = e.target;
  const tg = t.closest('[data-toggle]');
  if (tg) { e.stopPropagation(); const b = S.blocks.find((x) => x.id === tg.dataset.toggle); updateBlock(b.id, { enabled: !b.enabled }); return; }
  const card = t.closest('.card, .screen-wrap');
  if (card && card.dataset.id) { selected = card.dataset.id; inspectorFor = null; render(); return; }
  const b = S && S.blocks.find((x) => x.id === selected);
  if (!b) return;
  if (t.matches('[data-inv-del]')) {
    const iv = b.inventories[+t.dataset.invDel];
    const items = iv.items.filter((_, j) => j !== +t.dataset.item);
    updateBlock(b.id, { inventory: { index: +t.dataset.invDel, items } });
  } else if (t.matches('[data-inv-add]')) {
    const i = +t.dataset.invAdd;
    const [typeId, subtype] = document.querySelector(`[data-inv-new="${i}"]`).value.split('|');
    const amount = parseFloat(document.querySelector(`[data-inv-amt="${i}"]`).value) || 1;
    const items = b.inventories[i].items.slice();
    const same = items.find((x) => x.typeId === typeId && x.subtype === subtype);
    if (same) same.amount += amount; else items.push({ typeId, subtype, amount });
    updateBlock(b.id, { inventory: { index: i, items } });
  } else if (t.matches('[data-group-del]')) {
    updateBlock(b.id, { groups: b.groups.filter((g) => g !== t.dataset.groupDel) });
  } else if (t.matches('[data-group-add]')) {
    const v = document.querySelector('[data-group-new]').value.trim();
    if (v) updateBlock(b.id, { groups: [...new Set([...b.groups, v])] });
  } else if (t.matches('[data-make-pb]')) {
    await api('pb', { id: b.id }); poll();
  } else if (t.matches('[data-remove]')) {
    if (confirm(`Удалить «${b.name}» из симуляции?`)) { await api('block/remove', { id: b.id }); selected = null; poll(); }
  }
});

document.addEventListener('change', (e) => {
  const t = e.target;
  const b = S && S.blocks.find((x) => x.id === selected);
  if (!b || !$('inspector').contains(t)) return;
  if (t.dataset.prop) updateBlock(b.id, { props: { [t.dataset.prop]: parseVal(t.dataset.kind, t) } });
  else if (t.dataset.meta) updateBlock(b.id, { [t.dataset.meta]: t.type === 'checkbox' ? t.checked : t.value });
  else if (t.dataset.surf !== undefined) {
    const k = t.dataset.sprop;
    updateBlock(b.id, { surface: { index: +t.dataset.surf, [k]: k === 'fontSize' ? parseFloat(t.value) : t.value } });
  } else if (t.dataset.invMax !== undefined) {
    updateBlock(b.id, { inventory: { index: +t.dataset.invMax, maxVolume: (parseFloat(t.value) || 0) / 1000 } });
  } else if (t.dataset.inv !== undefined) {
    const i = +t.dataset.inv;
    const items = b.inventories[i].items.map((x, j) => (j === +t.dataset.item ? Object.assign({}, x, { amount: parseFloat(t.value) || 0 }) : x));
    updateBlock(b.id, { inventory: { index: i, items } });
  }
});

$('btnRun').onclick = () => control(S && S.running ? 'pause' : 'start');
$('btnStep').onclick = () => control('step', { ticks: 1 });
$('btnStep10').onclick = () => control('step', { ticks: 10 });
$('btnStep100').onclick = () => control('step', { ticks: 100 });
$('speed').onchange = (e) => control('speed', { speed: parseFloat(e.target.value) });
$('btnTerm').onclick = () => control('run', { argument: $('arg').value });
$('arg').onkeydown = (e) => { if (e.key === 'Enter') control('run', { argument: $('arg').value }); };
$('btnRecompile').onclick = () => control('recompile');
$('btnReset').onclick = () => control('reset');
$('btnClearLog').onclick = () => control('clearlog');
$('filter').oninput = () => renderBlocks();
$('groupBy').onchange = () => { localStorage.setItem('groupBy', $('groupBy').value); renderBlocks(); };
$('btnImport').onclick = async () => {
  const name = $('bpSelect').value;
  if (!name) return;
  $('btnImport').disabled = true;
  try { await api('grid/import', { name }); selected = null; } finally { $('btnImport').disabled = false; poll(); }
};
$('btnNewGrid').onclick = async () => { if (confirm('Заменить текущую сетку тестовой?')) { await api('grid/new', {}); selected = null; poll(); } };
$('btnSaveGrid').onclick = async () => { await api('grid/save', {}); poll(); };
$('btnAdd').onclick = async () => {
  const [typeId, subtype, label] = $('addSelect').value.split('|');
  const r = await api('block/add', { typeId, subtype, name: label });
  if (r.id) selected = r.id;
  poll();
};
document.addEventListener('keydown', (e) => {
  if (e.code === 'Space' && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName)) { e.preventDefault(); $('btnRun').click(); }
});

async function init() {
  $('groupBy').value = localStorage.getItem('groupBy') || 'type';
  try {
    const bp = await api('blueprints');
    $('bpSelect').innerHTML = bp.blueprints.map((n) => `<option>${esc(n)}</option>`).join('') || '<option value="">(блупринтов нет)</option>';
    catalog = await api('catalog');
    $('addSelect').innerHTML = catalog.blocks.map((c) => `<option value="${esc(c.typeId + '|' + c.subtype + '|' + c.label)}">${esc(c.label)}</option>`).join('');
  } catch (e) { /* хелпер ещё стартует */ setTimeout(init, 1000); return; }
  poll();
}
init();
