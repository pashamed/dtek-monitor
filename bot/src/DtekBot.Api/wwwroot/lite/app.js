// Lite settings editor for the DTEK alert bot (vanilla JS, no deps).
const tg = window.Telegram && window.Telegram.WebApp;
const app = document.getElementById('app');
const initData = tg ? tg.initData : '';

const KINDS = [['Emergency', 'Екстрене'], ['Accident', 'Аварія'], ['Unknown', 'Невідомо'], ['Ended', 'Завершення']];
const TABS = [['main', 'Основне'], ['addr', 'Адреса і черга'], ['sched', 'Графік'], ['msg', 'Повідомлення']];
const state = { boot: null, chats: [], editing: null };
const groupsCache = new Map(); // regionId -> Promise of schedule groups
const PRIVATE_LABEL = 'Цей чат із ботом';
const CREDIT = 'Дані графіків: outage-data-ua (ДТЕК).';

const HELP = {
  region: ['Регіон', 'Регіон, за яким бот стежить за відключеннями. Від нього залежать доступні адреси, черги та графіки.'],
  part: ['Частина регіону', 'Необовʼязково. Якщо обрати частину регіону, бот надсилатиме повідомлення лише про неї; «Весь регіон» — без обмежень.'],
  active: ['Підписка активна', 'Поки підписка активна, бот надсилає сюди сповіщення. Якщо її вимкнути, сповіщення припиняються, а збережена адреса видаляється.'],
  pause: ['Пауза', 'Тимчасово зупиняє сповіщення, не скасовуючи підписку й не видаляючи адресу. Зніміть паузу, щоб відновити їх.'],
  filter: ['Які сповіщення', 'Усі — також нерозпізнані повідомлення з сайту ДТЕК. Лише екстрені та аварії — тільки екстрені відключення й аварії.'],
  notifyOnEnd: ['Писати про завершення', 'Коли екстрені відключення скасовано, бот відповідає на початкове повідомлення про відключення.'],
  editInPlace: ['Оновлення', 'Редагувати те саме — одне повідомлення оновлюється на місці. Щоразу нове — кожна зміна приходить окремим повідомленням зі сповіщенням.'],
  address: ['Адреса', 'Потрібна, щоб визначити вашу чергу й перевіряти саме вашу адресу на сайті ДТЕК. Зберігається, поки діє підписка, і видаляється при відписці.'],
  group: ['Черга', 'Номер черги в графіку відключень, наприклад 45.1. Визначається за адресою, але її можна змінити вручну. Від черги залежать повідомлення за графіком і сповіщення про екстрені відключення черги.'],
  outage: ['Реагувати на «Світло зникло»', 'Якщо застосунок моніторингу світла пише в канал «Світло зникло», бот знайде причину: спершу перевіряє графік черги, потім загальне повідомлення ДТЕК про екстрені відключення чи аварію, і лише тоді адресу на сайті ДТЕК.'],
  schedule: ['Сповіщення за графіком', 'Бот публікує одне повідомлення на кожне планове відключення з очікуваним часом повернення, редагує його при зміні графіка й коли світло повертається. Використовує дані outage-data-ua.'],
  image: ['Картинка', 'Таблиця на 2 дні — графік на сьогодні й завтра. Коротка картинка — картка з годинами відключень. Без картинки — лише текст.'],
  template: ['Шаблон повідомлення', 'Ваш текст екстрених сповіщень. Параметри у {фігурних дужках} замінюються даними, теги Telegram дозволені.'],
  kind: ['Тип попереднього перегляду', 'Обирає, для якого типу події показати приклад повідомлення за вашим шаблоном.'],
  connect: ['Підключити канал', 'Додайте бота адміністратором у канал із правом публікувати повідомлення — канал зʼявиться тут автоматично (за потреби натисніть «Оновити список»). @імʼя потрібне лише як запасний варіант для публічного каналу.'],
  remove: ['Видалити зі списку', 'Знімає канал зі списку. Якщо ви єдиний адміністратор у боті, сповіщення вимикаються, а адреса видаляється. Сам бот лишається в каналі.'],
};

// h('div', {class:'x', onclick:fn}, 'text', childNode) — safe DOM builder (text only via textContent).
function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat(Infinity)) if (c != null && c !== false) el.append(c);
  return el;
}

const ICONS = { back: 'M15 18l-6-6 6-6', refresh: 'M20 12a8 8 0 1 1-2.5-5.8M20 4v5h-5', chev: 'M9 6l6 6-6 6' };
function icon(name) {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  for (const [k, v] of Object.entries({ viewBox: '0 0 24 24', width: 20, height: 20, fill: 'none', stroke: 'currentColor', 'stroke-width': 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(k, v);
  const p = document.createElementNS(NS, 'path');
  p.setAttribute('d', ICONS[name]);
  svg.append(p);
  return svg;
}

function show(...nodes) {
  closeSheet(true);
  app.removeAttribute('aria-busy');
  app.replaceChildren(...nodes.flat(Infinity).filter(n => n != null));
  window.scrollTo(0, 0);
}

async function api(method, path, body) {
  let res;
  try {
    res = await fetch(path, {
      method,
      headers: { 'Authorization': 'tma ' + initData, ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw Object.assign(new Error('Немає зʼєднання з сервером.'), { network: true });
  }
  let data = null;
  try { data = await res.json(); } catch { /* empty body */ }
  if (!res.ok) {
    const msg = res.status === 401 ? 'Відкрийте цей застосунок із Telegram (кнопка в меню бота).'
      : res.status === 403 ? 'Недостатньо прав для цієї дії.'
      : (data && data.error) || `Помилка сервера (${res.status}).`;
    throw Object.assign(new Error(msg), { status: res.status, details: (data && data.details) || [] });
  }
  return data;
}

function errorText(e) {
  const d = Array.isArray(e.details) ? e.details : e.details ? [String(e.details)] : [];
  return [e.message, ...d];
}

function haptic(kind) { try { tg.HapticFeedback && tg.HapticFeedback.notificationOccurred(kind); } catch { /* optional */ } }

function loadGroups(regionId) {
  if (!groupsCache.has(regionId)) {
    const p = api('GET', '/api/schedules/groups?regionId=' + encodeURIComponent(regionId));
    p.catch(() => groupsCache.delete(regionId));
    groupsCache.set(regionId, p);
  }
  return groupsCache.get(regionId);
}

// ---------- help popover (one bottom sheet at a time) ----------
let sheet = null;
function closeSheet(silent) {
  if (!sheet) return;
  const { back, btn } = sheet;
  sheet = null;
  back.remove();
  btn.setAttribute('aria-expanded', 'false');
  if (!silent) btn.focus();
}

function help(key) {
  const [title, text] = HELP[key];
  const btn = h('button', { type: 'button', class: 'qm', 'aria-label': 'Довідка: ' + title, 'aria-haspopup': 'dialog', 'aria-expanded': 'false' },
    h('span', { class: 'qi', 'aria-hidden': 'true', text: '?' }));
  btn.addEventListener('click', e => {
    e.preventDefault(); e.stopPropagation();
    closeSheet(true);
    const ok = h('button', { type: 'button', class: 'btn', text: 'Зрозуміло', onclick: () => closeSheet() });
    const card = h('div', { class: 'sheet glass', id: 'help-sheet', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'help-title' },
      h('div', { class: 'sh-t', id: 'help-title', text: title }), h('p', { text }), ok);
    const back = h('div', { class: 'backdrop', onclick: ev => { if (ev.target === back) closeSheet(); } }, card);
    document.body.append(back);
    btn.setAttribute('aria-expanded', 'true');
    btn.setAttribute('aria-controls', 'help-sheet');
    sheet = { back, btn };
    ok.focus();
  });
  return btn;
}
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeSheet(); });

const card = (...kids) => h('section', { class: 'card glass' }, ...kids);
const fieldLabel = (title, key, forId) => h('div', { class: 'fl' }, h('label', { for: forId, text: title }), key && help(key));

// ---------- helpers ----------
function regionOf(id) { return state.boot.regions.find(x => x.id === id) || null; }

function statusOf(o) {
  if (!o.isActive || !o.regionId) return ['off', 'Не підписано'];
  return o.isPaused ? ['warn', 'Пауза'] : ['ok', 'Активний'];
}
const pill = o => { const [c, t] = statusOf(o); return h('span', { class: 'pill ' + c, text: t }); };
const chatTitle = c => c.isPrivate ? PRIVATE_LABEL : (c.title || 'Канал');

function regionLine(s) {
  const r = regionOf(s.regionId);
  if (!s.regionId) return 'Регіон не обрано';
  if (!r) return 'Невідомий регіон';
  const p = s.partId && r.parts.find(x => x.id === s.partId);
  return r.name + (p ? ' · ' + p.name : '');
}

function showFatal(e, retry) {
  show(h('h1', { class: 'ttl', text: 'Налаштування DTEK' }),
    card(h('p', { class: 'msg err', text: e.message }),
      e.status === 401 ? null : h('button', { class: 'btn', text: 'Повторити', onclick: retry })));
}

// ---------- home: channel dashboard ----------
let linkDraft = '';
let lastRefresh = 0;

async function refreshChats(manual) {
  const now = Date.now();
  if (!manual && now - lastRefresh < 1500) return;
  lastRefresh = now;
  try {
    const list = await api('GET', '/api/chats');
    const changed = JSON.stringify(list) !== JSON.stringify(state.chats);
    state.chats = list;
    if (!state.editing && (manual || changed)) renderList(manual ? 'Список оновлено.' : null);
  } catch (e) {
    if (manual && !state.editing) renderList(null, e.message);
  }
}

function chatCard(c) {
  const s = c.settings;
  const [cls, label] = statusOf(s);
  const chips = [
    s.groupId ? h('span', { class: 'chip purple', text: 'Черга ' + s.groupId }) : null,
    s.scheduleNotify ? h('span', { class: 'chip purple', text: 'Графік' }) : null,
    s.outageTrigger ? h('span', { class: 'chip blue', text: 'Реакція на «Світло зникло»' }) : null,
  ].filter(Boolean);
  return h('button', { type: 'button', class: 'chat glass', onclick: () => renderEditor(c) },
    h('div', { class: 'chat-h' }, h('b', { text: chatTitle(c) }), h('span', { class: 'pill ' + cls, text: label })),
    h('div', { class: 'sub', text: regionLine(s) }),
    chips.length ? h('div', { class: 'chips' }, chips) : null,
    h('span', { class: 'go', 'aria-hidden': 'true' }, icon('chev')));
}

function renderList(note, errNote) {
  try { tg.BackButton.hide(); } catch { /* optional */ }
  state.editing = null;
  const input = h('input', { type: 'text', value: linkDraft, placeholder: '@імʼя_каналу', autocapitalize: 'none', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Імʼя публічного каналу' });
  input.addEventListener('input', () => { linkDraft = input.value; });
  const msg = h('div', { class: 'msg', role: 'status' });
  const btn = h('button', { type: 'button', class: 'btn sm', text: 'Підключити' });
  btn.addEventListener('click', async () => {
    const chat = input.value.trim();
    if (!chat) return;
    btn.disabled = true;
    msg.className = 'msg'; msg.textContent = '';
    try {
      const c = await api('POST', '/api/chats/link', { chat });
      const i = state.chats.findIndex(x => x.chatId === c.chatId);
      if (i >= 0) state.chats[i] = c; else state.chats.push(c);
      linkDraft = '';
      renderList('Канал підключено: ' + c.title);
    } catch (e) {
      msg.className = 'msg err';
      msg.replaceChildren(...errorText(e).map(t => h('div', { text: t })));
      btn.disabled = false;
    }
  });
  const refresh = h('button', { type: 'button', class: 'btn ghost sm', onclick: () => { refresh.disabled = true; refreshChats(true); } }, icon('refresh'), 'Оновити список');

  show(
    h('header', { class: 'hero' },
      h('h1', { class: 'ttl', text: 'DTEK Сповіщення' }),
      h('p', { class: 'lead', text: 'Керуйте каналами зі сповіщеннями про відключення світла.' })),
    note ? h('div', { class: 'toast ok', role: 'status', text: note }) : null,
    errNote ? h('div', { class: 'toast err', role: 'alert', text: errNote }) : null,
    h('div', { class: 'sechead' }, h('h2', { text: 'Ваші канали' }), refresh),
    state.chats.length ? state.chats.map(chatCard) : card(h('p', { class: 'hint', text: 'Каналів поки немає. Підключіть перший за інструкцією нижче.' })),
    card(
      h('div', { class: 'fl big' }, h('h2', { class: 'ct', text: 'Підключити канал' }), help('connect')),
      h('ol', { class: 'steps' },
        h('li', { text: 'Додайте бота адміністратором у ваш канал з правом публікувати повідомлення.' }),
        h('li', { text: 'Канал зʼявиться тут автоматично.' }),
        h('li', { text: 'Оберіть регіон і адресу.' })),
      h('div', { class: 'or', text: 'Або введіть @імʼя публічного каналу' }),
      h('div', { class: 'linkrow' }, input, btn), msg));
}

// ---------- chat editor ----------
function renderEditor(chat) {
  const { boot } = state;
  const isPriv = !!chat.isPrivate;
  const s = { ...chat.settings, scheduleImage: chat.settings.scheduleImage || 'Grid' };
  state.editing = chat;
  try { tg.BackButton.show(); } catch { /* optional */ }
  let kind = 'Emergency', seq = 0, timer = 0, errors = [], busy = false, tab = 'main', hintFor = null, uid = 0;

  const normT = v => (!v || !v.trim() || v === boot.defaultTemplate) ? null : v;
  const groupOf = () => (chat.settings.regionId === s.regionId && chat.settings.groupId) || null;
  const addrOf = () => (groupOf() && chat.settings.addressLabel) || null;
  // Form fields only; scheduleNotify counts only when a group is saved for the chosen region.
  const form = (src, template, saved) => {
    const r = regionOf(src.regionId);
    return {
      regionId: src.regionId || null, partId: src.partId || null, isActive: !!src.isActive, isPaused: !!src.isPaused,
      filter: src.filter || 'All', notifyOnEnd: !!src.notifyOnEnd, editInPlace: !!src.editInPlace,
      outageTrigger: !!src.outageTrigger, scheduleImage: src.scheduleImage || 'Grid', template,
      scheduleNotify: !!(src.scheduleNotify && r && r.hasSchedules && saved.regionId === src.regionId && saved.groupId),
    };
  };

  const tpl = h('textarea', { maxlength: boot.maxTemplateLength, rows: 8, spellcheck: 'false', 'aria-label': 'Шаблон повідомлення', id: 'tpl' });
  tpl.value = s.template || boot.defaultTemplate;
  const cur = () => form(s, normT(tpl.value), chat.settings);
  let base = JSON.stringify(form(chat.settings, normT(chat.settings.template), chat.settings));
  const isDirty = () => JSON.stringify(cur()) !== base;

  // ----- chrome: top bar, tabs, save bar -----
  const topPill = h('span', { class: 'pill' });
  const back = h('button', { type: 'button', class: 'iconbtn', 'aria-label': 'Назад', onclick: () => goBack() }, icon('back'));
  const rd = h('i', { class: 'rd', hidden: true, 'aria-label': 'Є помилки' });
  const tabBtns = TABS.map(([id, label], i) => h('button', {
    type: 'button', role: 'tab', id: 'tab-' + id, 'aria-controls': 'panel-' + id, class: 'tab', text: label,
    onclick: () => setTab(id),
    onkeydown: e => {
      const d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
      if (d) { const n = TABS[(i + d + TABS.length) % TABS.length][0]; setTab(n); document.getElementById('tab-' + n).focus(); }
    },
  }, id === 'msg' ? rd : null));
  const panels = Object.fromEntries(TABS.map(([id]) => [id, h('div', { class: 'panel', role: 'tabpanel', id: 'panel-' + id, 'aria-labelledby': 'tab-' + id })]));

  const errLine = h('div', { class: 'errline', role: 'alert' });
  const stDot = h('i', { class: 'dot' });
  const stTxt = h('span');
  const saveBtn = h('button', { type: 'button', class: 'btn primary', text: 'Зберегти', onclick: () => doSave() });
  const bar = h('div', { class: 'savebar glass' }, h('div', { class: 'savein' }, errLine,
    h('div', { class: 'saverow' }, h('div', { class: 'stat', role: 'status' }, stDot, stTxt), saveBtn)));
  const setErr = lines => errLine.replaceChildren(...lines.map(t => h('div', { text: t })));

  function setTab(id) {
    tab = id;
    TABS.forEach(([t], i) => {
      panels[t].hidden = t !== id;
      tabBtns[i].setAttribute('aria-selected', t === id);
      tabBtns[i].tabIndex = t === id ? 0 : -1;
    });
    window.scrollTo(0, 0);
    try { tabBtns[TABS.findIndex(x => x[0] === id)].scrollIntoView({ inline: 'center', block: 'nearest' }); } catch { /* optional */ }
    if (id === 'sched') loadPv();
  }

  // ----- widgets bound to form state -----
  function toggle(title, key, helpKey, opts = {}) {
    const id = 'sw' + (++uid);
    const cb = h('input', { type: 'checkbox', role: 'switch', id, disabled: !!opts.disabled });
    cb.checked = !!s[key] && !opts.disabled;
    cb.addEventListener('change', () => { s[key] = cb.checked; touch(); });
    const row = h('div', { class: 'row' }, fieldLabel(title, helpKey, id), h('label', { class: 'sw' }, cb, h('i', { class: 'trk' })));
    row.cb = cb;
    return row;
  }

  // Segmented control; options = [[value, label], ...].
  function seg(options, get, set, label) {
    const box = h('div', { class: 'seg', role: 'group', 'aria-label': label });
    const paint = () => [...box.children].forEach((b, i) => { const on = options[i][0] === get(); b.classList.toggle('on', on); b.setAttribute('aria-pressed', on); });
    options.forEach(([v, l]) => box.append(h('button', { type: 'button', text: l, onclick: () => { set(v); paint(); } })));
    paint();
    return box;
  }

  function touch() { setErr([]); updateUi(); }

  // ----- tab: Основне -----
  const regionSel = h('select', { 'aria-label': 'Регіон', id: 'regionSel' });
  const partSel = h('select', { 'aria-label': 'Частина регіону', id: 'partSel' });
  regionSel.replaceChildren(h('option', { value: '', text: '— Оберіть регіон —' }), ...boot.regions.map(r => h('option', { value: r.id, text: r.name })));
  regionSel.value = s.regionId || '';
  const partField = h('div', { class: 'fld' }, fieldLabel('Частина регіону', 'part', 'partSel'), partSel);
  const hintBox = h('div');
  const activeRow = toggle('Підписка активна', 'isActive', 'active');

  function fillParts() {
    const r = regionOf(s.regionId);
    partField.hidden = !r || !r.parts.length;
    partSel.replaceChildren(h('option', { value: '', text: 'Весь регіон' }), ...(r ? r.parts : []).map(p => h('option', { value: p.id, text: p.name })));
    partSel.value = s.partId || '';
  }

  function renderHint() {
    const r = regionOf(s.regionId);
    if (!r || hintFor !== s.regionId || groupOf() || !(r.hasGroups || r.hasSchedules)) { hintBox.replaceChildren(); return; }
    hintBox.replaceChildren(h('div', { class: 'nudge glass' },
      h('div', {}, h('b', { text: r.hasGroups ? 'Тепер оберіть адресу' : 'Тепер оберіть чергу' }),
        h('p', { class: 'hint', text: 'Це потрібно, щоб отримувати сповіщення саме для вашої черги.' })),
      h('button', { type: 'button', class: 'btn sm', text: 'Адреса і черга', onclick: () => setTab('addr') })));
  }

  regionSel.addEventListener('change', () => {
    s.regionId = regionSel.value || null; s.partId = null;
    if (s.regionId && !s.isActive) { s.isActive = true; activeRow.cb.checked = true; }
    const r = regionOf(s.regionId);
    hintFor = r && (r.hasGroups || r.hasSchedules) ? s.regionId : null;
    setErr([]); refreshAll(); schedulePreview();
  });
  partSel.addEventListener('change', () => { s.partId = partSel.value || null; touch(); schedulePreview(); });

  panels.main.append(
    card(h('div', { class: 'fld' }, fieldLabel('Регіон', 'region', 'regionSel'), regionSel), partField),
    hintBox,
    card(activeRow, toggle('Пауза', 'isPaused', 'pause')),
    card(h('div', { class: 'fld' }, fieldLabel('Які сповіщення', 'filter'),
        seg([['All', 'Усі'], ['EmergencyOnly', 'Лише екстрені та аварії']], () => s.filter, v => { s.filter = v; touch(); }, 'Які сповіщення')),
      toggle('Писати про завершення', 'notifyOnEnd', 'notifyOnEnd'),
      h('div', { class: 'fld' }, fieldLabel('Оновлення', 'editInPlace'),
        seg([[true, 'Редагувати те саме повідомлення'], [false, 'Щоразу нове повідомлення']], () => !!s.editInPlace, v => { s.editInPlace = v; touch(); }, 'Оновлення'))));

  // ----- save logic -----
  function applyChat(c) {
    chat = c; state.editing = c;
    const i = state.chats.findIndex(x => x.chatId === c.chatId);
    if (i >= 0) state.chats[i] = c;
    if (!c.settings.groupId) s.scheduleNotify = false;
    const r = regionOf(c.settings.regionId);
    const b = JSON.parse(base);
    b.scheduleNotify = !!(c.settings.scheduleNotify && c.settings.groupId && r && r.hasSchedules);
    base = JSON.stringify(b);
  }

  // Shared by the save bar and ensureSaved(). Resolves to null on success or the Error.
  async function doSave(silent) {
    if (!silent) setErr([]);
    if (s.isActive && !s.regionId) {
      const e = new Error('Оберіть регіон або вимкніть підписку.');
      if (!silent) setErr([e.message]);
      return e;
    }
    busy = true; updateUi();
    try {
      applyChat(await api('PUT', '/api/chats/' + encodeURIComponent(chat.chatId), { ...chat.settings, ...cur() }));
      base = JSON.stringify(cur());
      if (!silent) { haptic('success'); busy = false; refreshAll(); }
      return null;
    } catch (e) {
      if (!silent) setErr(errorText(e));
      return e;
    } finally {
      busy = false; updateUi();
    }
  }

  // The address/group endpoints need the region (and subscription) to be saved first.
  function ensureSaved() {
    const sv = chat.settings;
    if (sv.regionId === s.regionId && !!sv.isActive === !!s.isActive) return Promise.resolve(null);
    return doSave(true);
  }

  // ----- tab: Адреса і черга -----
  const empty = (text, to) => card(h('p', { class: 'empty', text }),
    to ? h('button', { type: 'button', class: 'btn ghost sm', text: 'Перейти до «Основне»', onclick: () => setTab(to) }) : null);

  function renderAddr() {
    const r = regionOf(s.regionId);
    const nodes = [];
    if (!r) nodes.push(empty('Спершу оберіть регіон на вкладці «Основне».', 'main'));
    else if (!r.hasGroups && !r.hasSchedules) nodes.push(empty('Для цього регіону вибір адреси й черги недоступний.'));
    else {
      const regionId = s.regionId, grp = groupOf(), addr = addrOf();
      const msg = h('div', { class: 'msg', role: 'status' });
      const fail = e => { msg.className = 'msg err'; msg.textContent = errorText(e).join(' '); };

      async function commit(method, path, body, needSave) {
        msg.className = 'msg'; msg.textContent = 'Зберігаю…';
        if (needSave) {
          const err = await ensureSaved();
          if (err) { fail(err); return false; }
        }
        try {
          applyChat(await api(method, '/api/chats/' + encodeURIComponent(chat.chatId) + path, body));
          haptic('success'); refreshAll();
          return true;
        } catch (e) { fail(e); return false; }
      }

      if (r.hasGroups) {
        if (addr) {
          nodes.push(card(fieldLabel('Адреса', 'address'),
            h('div', { class: 'result' }, h('div', { class: 'rl', text: addr }), h('span', { class: 'chip purple', text: 'Черга ' + grp })),
            h('button', { type: 'button', class: 'btn ghost sm', text: 'Прибрати адресу', onclick: () => commit('DELETE', '/address') })));
        } else {
          let street = null, sseq = 0, stimer = 0;
          const list = h('div', { class: 'picks' });
          const input = h('input', { type: 'text', id: 'addrIn', placeholder: 'Почніть вводити вулицю', autocomplete: 'off', 'aria-label': 'Вулиця' });
          async function search() {
            const my = ++sseq, q = input.value.trim();
            msg.textContent = '';
            if (q.length < (street ? 1 : 2)) { list.replaceChildren(); return; }
            try {
              const url = street
                ? `/api/address/houses?regionId=${encodeURIComponent(regionId)}&streetId=${street.id}&query=${encodeURIComponent(q)}`
                : `/api/address/streets?regionId=${encodeURIComponent(regionId)}&query=${encodeURIComponent(q)}`;
              const items = await api('GET', url);
              if (my !== sseq) return;
              if (!items.length) { list.replaceChildren(h('p', { class: 'hint', text: 'Нічого не знайдено.' })); return; }
              list.replaceChildren(...items.map(i => h('button', { type: 'button', class: 'pick', text: i.name, onclick: () => (street ? pickHouse : pickStreet)(i) })));
            } catch (e) { if (my === sseq) fail(e); }
          }
          const pickStreet = i => {
            street = i; input.value = ''; input.placeholder = `Номер будинку (${i.name})`; input.setAttribute('aria-label', 'Будинок');
            list.replaceChildren(); input.focus();
          };
          const pickHouse = i => {
            list.replaceChildren();
            commit('PUT', '/address', { regionId, streetId: street.id, houseId: i.id, label: `${street.name}, ${i.name}`, street: street.name, house: i.name }, true);
          };
          input.addEventListener('input', () => { clearTimeout(stimer); stimer = setTimeout(search, 300); });
          nodes.push(card(h('div', { class: 'fld' }, fieldLabel('Адреса', 'address', 'addrIn'), input, list,
            h('p', { class: 'hint', text: 'Знайдіть вулицю, потім номер будинку — черга визначиться автоматично.' }))));
        }
      }

      if (r.hasSchedules) {
        const sel = h('select', { id: 'groupSel', 'aria-label': 'Черга' }, h('option', { value: '', text: '— Оберіть чергу —' }));
        loadGroups(regionId).then(gs => {
          const ids = new Set(gs.map(g => g.id));
          sel.append(...gs.map(g => h('option', { value: g.id, text: g.name })));
          if (grp && !ids.has(grp)) sel.append(h('option', { value: grp, text: 'Черга ' + grp }));
          sel.value = grp || '';
        }, fail);
        sel.addEventListener('change', async () => {
          if (sel.value && sel.value !== grp) { if (!await commit('PUT', '/group', { regionId, groupId: sel.value }, true)) sel.value = grp || ''; }
          else sel.value = grp || '';
        });
        nodes.push(card(h('div', { class: 'fld' }, fieldLabel('Черга', 'group', 'groupSel'), sel,
          h('p', { class: 'hint', text: 'Якщо чергу визначено неправильно, змініть її вручну. Адреса залишається для перевірки на сайті ДТЕК.' })),
          grp && !addr ? h('button', { type: 'button', class: 'btn ghost sm', text: 'Скинути чергу', onclick: () => commit('DELETE', '/address') }) : null));
      }
      nodes.push(msg);
    }
    if (!isPriv) nodes.push(card(toggle('Реагувати на «Світло зникло»', 'outageTrigger', 'outage')));
    panels.addr.replaceChildren(...nodes);
  }

  // ----- tab: Графік -----
  const pv = { key: '', state: 'idle', data: null, msg: '' };
  let pvSeq = 0;
  const pvImg = h('div', { class: 'pimg' });
  const pvBubble = h('div', { class: 'tgb' });
  const pvNote = h('p', { class: 'hint' });
  const pvCard = card(h('h3', { class: 'ct', text: 'Попередній перегляд' }), pvImg, pvBubble, pvNote);

  function paintPv() {
    pvImg.replaceChildren(); pvBubble.replaceChildren(); pvNote.textContent = ''; pvNote.className = 'hint';
    pvBubble.hidden = pv.state !== 'ok' && pv.state !== 'loading';
    if (pv.state === 'loading') {
      pvImg.append(h('div', { class: 'sk sk-img' }));
      pvBubble.append(h('div', { class: 'sk sk-line' }), h('div', { class: 'sk sk-line s' }));
    } else if (pv.state === 'ok') {
      const d = pv.data || {};
      if (d.imageUrl && /^(https?:\/\/|\/)/.test(d.imageUrl)) {
        const img = h('img', { src: d.imageUrl, loading: 'lazy', alt: 'Графік відключень для черги ' + (d.groupId || '') });
        img.addEventListener('error', () => img.replaceWith(h('p', { class: 'hint err', text: 'Не вдалося завантажити картинку' })));
        pvImg.append(img);
      }
      pvBubble.textContent = d.text || '';
    } else if (pv.state === 'err') { pvNote.className = 'hint err'; pvNote.textContent = pv.msg; }
    else pvNote.textContent = 'Оберіть чергу, щоб побачити приклад повідомлення.';
  }

  function loadPv() {
    const r = regionOf(s.regionId), g = groupOf();
    if (!r || !r.hasSchedules || !g) { pv.key = ''; pv.state = 'idle'; paintPv(); return; }
    const k = `${s.regionId}|${g}|${s.scheduleImage}`;
    if (k === pv.key && pv.state !== 'err') return;
    pv.key = k; pv.state = 'loading'; paintPv();
    const my = ++pvSeq;
    api('GET', `/api/schedules/preview?regionId=${encodeURIComponent(s.regionId)}&groupId=${encodeURIComponent(g)}&kind=${s.scheduleImage.toLowerCase()}`).then(d => {
      if (my !== pvSeq) return;
      pv.state = 'ok'; pv.data = d; paintPv();
    }, e => {
      if (my !== pvSeq) return;
      pv.state = 'err'; pv.msg = e.message; paintPv();
    });
  }

  function renderSched() {
    const r = regionOf(s.regionId);
    if (!r || !r.hasSchedules) {
      pvSeq++; pv.key = '';
      panels.sched.replaceChildren(empty(r ? 'Графіки відключень для цього регіону поки недоступні.' : 'Спершу оберіть регіон на вкладці «Основне».', r ? null : 'main'));
      return;
    }
    const has = !!groupOf();
    panels.sched.replaceChildren(
      card(toggle('Сповіщення за графіком', 'scheduleNotify', 'schedule', { disabled: !has }),
        has ? null : h('div', { class: 'lockhint' }, h('span', { class: 'hint', text: 'Спершу оберіть адресу або чергу.' }),
          h('button', { type: 'button', class: 'btn ghost sm', text: 'Адреса і черга', onclick: () => setTab('addr') }))),
      card(h('div', { class: 'fld' }, fieldLabel('Картинка', 'image'),
        seg([['Grid', 'Таблиця на 2 дні'], ['Card', 'Коротка картинка'], ['None', 'Без картинки']], () => s.scheduleImage, v => { s.scheduleImage = v; touch(); loadPv(); }, 'Картинка'))),
      pvCard,
      h('p', { class: 'hint credit', text: CREDIT }));
    if (tab === 'sched') loadPv();
  }

  // ----- tab: Повідомлення -----
  const counter = h('div', { class: 'counter' });
  const errBox = h('div', { class: 'errs' });
  const bubble = h('div', { class: 'bubble' });

  function schedulePreview() {
    clearTimeout(timer);
    updateUi();
    timer = setTimeout(runPreview, 300);
  }

  async function runPreview() {
    const my = ++seq;
    try {
      const r = await api('POST', '/api/preview', {
        template: tpl.value.trim() ? tpl.value : boot.defaultTemplate,
        kind, regionId: s.regionId, partId: s.partId,
      });
      if (my !== seq) return; // stale response
      errors = r.errors || [];
      bubble.innerHTML = errors.length ? '' : r.html; // server-validated, whitelisted tags only
    } catch (e) {
      if (my !== seq) return;
      errors = [];
      bubble.textContent = 'Не вдалося отримати попередній перегляд: ' + e.message;
    }
    updateUi();
  }

  tpl.addEventListener('input', schedulePreview);

  const chips = h('div', { class: 'chips' }, boot.placeholders.map(p => h('button', {
    type: 'button', class: 'chip mono', text: '{' + p.name + '}',
    onclick: () => {
      const a = tpl.selectionStart ?? tpl.value.length, b = tpl.selectionEnd ?? a, t = '{' + p.name + '}';
      if (tpl.value.length - (b - a) + t.length > boot.maxTemplateLength) return;
      tpl.setRangeText(t, a, b, 'end');
      tpl.focus();
      schedulePreview();
    },
  })));

  const helpBox = h('details', { class: 'fold' }, h('summary', { text: 'Довідка щодо плейсхолдерів' }),
    boot.placeholders.map(p => h('div', { class: 'ph' },
      h('code', { text: '{' + p.name + '}' }), ' — ' + p.description,
      p.example ? h('small', { text: 'Приклад: ' + p.example }) : null)),
    h('div', { class: 'ph', text: 'Дозволені теги: <b>, <i>, <u>, <s>, <code>, <pre>, <a href>, <tg-spoiler>, <blockquote>. Щоб вивести фігурні дужки, пишіть {{ і }}.' }));

  // Linked channels can be taken off the list; the private chat can only be unsubscribed.
  const removeBtn = isPriv ? null : h('button', { type: 'button', class: 'btn danger', text: 'Видалити зі списку', onclick: async () => {
    const ask = 'Видалити цей канал зі списку? Якщо ви єдиний, хто ним керує, сповіщення буде вимкнено, а збережену адресу видалено.';
    const ok = await new Promise(resolve => tg.showConfirm ? tg.showConfirm(ask, resolve) : resolve(confirm(ask)));
    if (!ok) return;
    removeBtn.disabled = true;
    try {
      await api('DELETE', '/api/chats/' + encodeURIComponent(chat.chatId));
      state.chats = state.chats.filter(x => x.chatId !== chat.chatId);
      renderList('Канал видалено зі списку.');
    } catch (e) {
      removeBtn.disabled = false;
      setErr(errorText(e));
    }
  } });

  panels.msg.append(
    card(fieldLabel('Шаблон повідомлення', 'template', 'tpl'), tpl, counter, errBox, chips,
      h('button', { type: 'button', class: 'btn ghost sm', text: 'Скинути до стандартного', onclick: () => { tpl.value = boot.defaultTemplate; schedulePreview(); } }),
      helpBox),
    card(fieldLabel('Попередній перегляд', 'kind'),
      seg(KINDS, () => kind, v => { kind = v; schedulePreview(); }, 'Тип попереднього перегляду'), bubble),
    removeBtn ? card(h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Керування' }), help('remove')), removeBtn) : null);

  // ----- wiring -----
  function updateUi() {
    counter.textContent = `${tpl.value.length} / ${boot.maxTemplateLength}`;
    errBox.replaceChildren(...errors.map(t => h('div', { text: t })));
    const d = isDirty();
    saveBtn.disabled = busy || errors.length > 0 || !d;
    const mode = errors.length ? 'err' : d ? 'dirty' : 'ok';
    bar.dataset.mode = mode;
    stTxt.textContent = mode === 'err' ? 'Виправте помилки шаблону' : mode === 'dirty' ? 'Є незбережені зміни' : 'Збережено ✓';
    rd.hidden = !errors.length;
    const [c, t] = statusOf(s);
    topPill.className = 'pill ' + c; topPill.textContent = t;
  }

  function refreshAll() { fillParts(); renderHint(); renderAddr(); renderSched(); updateUi(); }

  async function goBack() {
    if (isDirty()) {
      const ask = 'Є незбережені зміни. Вийти без збереження?';
      const ok = await new Promise(resolve => tg.showConfirm ? tg.showConfirm(ask, resolve) : resolve(confirm(ask)));
      if (!ok) return;
    }
    renderList();
  }
  state.goBack = goBack;

  show(h('div', { class: 'ed' },
    h('header', { class: 'topbar glass' }, back, h('h1', { class: 'tt', text: chatTitle(chat) }), topPill),
    h('nav', { class: 'tabs glass', role: 'tablist', 'aria-label': 'Розділи налаштувань' }, tabBtns),
    TABS.map(([id]) => panels[id]), bar));
  refreshAll();
  setTab('main');
  window.scrollTo(0, 0);
  runPreview();
}

// ---------- startup ----------
function skeleton() {
  return [h('div', { class: 'sk sk-title' }), h('div', { class: 'sk sk-card' }), h('div', { class: 'sk sk-card' })];
}

async function load() {
  show(skeleton());
  app.setAttribute('aria-busy', 'true');
  try {
    const [boot, chats] = await Promise.all([state.boot || api('GET', '/api/bootstrap'), api('GET', '/api/chats')]);
    state.boot = boot; state.chats = chats;
    renderList();
  } catch (e) { showFatal(e, load); }
}

if (!tg || !initData) {
  show(h('h1', { class: 'ttl', text: 'Налаштування DTEK' }),
    card(h('p', { class: 'msg', text: 'Відкрийте цей застосунок із Telegram (кнопка в меню бота).' })));
} else {
  for (const [m, c] of [['setHeaderColor', '#070b16'], ['setBackgroundColor', '#070b16'], ['setBottomBarColor', '#0d1226']]) {
    try { if (tg[m]) tg[m](c); } catch { /* unsupported in this client version */ }
  }
  tg.ready();
  tg.expand();
  tg.BackButton.onClick(() => { if (state.editing && state.goBack) state.goBack(); else renderList(); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden && state.boot) refreshChats(false); });
  try { tg.onEvent && tg.onEvent('activated', () => { if (state.boot) refreshChats(false); }); } catch { /* optional */ }
  load();
}
