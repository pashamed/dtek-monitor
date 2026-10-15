// Lite settings editor for the DTEK alert bot (vanilla JS, no deps).
const tg = window.Telegram && window.Telegram.WebApp;
const app = document.getElementById('app');
const initData = tg ? tg.initData : '';

const KINDS = [['Emergency', 'Екстрене'], ['Accident', 'Аварія'], ['Unknown', 'Невідомо'], ['Ended', 'Завершення']];
const state = { boot: null, chats: [] };

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
  for (const c of kids.flat()) if (c != null) el.append(c);
  return el;
}

function show(...nodes) { app.replaceChildren(...nodes.flat(Infinity).filter(n => n != null)); window.scrollTo(0, 0); }

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

function showFatal(e, retry) {
  show(h('h1', { text: 'Налаштування DTEK' }),
    h('p', { class: 'msg err', text: e.message }),
    e.status === 401 ? null : h('button', { class: 'btn', text: 'Повторити', onclick: retry }));
}

// ---------- chat list ----------
function regionName(id) {
  const r = state.boot.regions.find(x => x.id === id);
  return r ? r : null;
}

function summary(s) {
  if (!s.isActive || !s.regionId) return 'Не підписано';
  const r = regionName(s.regionId);
  if (!r) return 'Невідомий регіон';
  const p = s.partId && r.parts.find(x => x.id === s.partId);
  return r.name + (p ? ' · ' + p.name : '');
}

function renderList(note) {
  tg.BackButton.hide();
  state.editing = null;
  const input = h('input', { type: 'text', placeholder: '@username або числовий id', autocapitalize: 'none', autocomplete: 'off' });
  const msg = h('div', { class: 'msg' });
  const btn = h('button', { class: 'btn', text: 'Підключити' });
  btn.addEventListener('click', async () => {
    const chat = input.value.trim();
    if (!chat) return;
    btn.disabled = true;
    msg.className = 'msg'; msg.textContent = '';
    try {
      const c = await api('POST', '/api/chats/link', { chat });
      const i = state.chats.findIndex(x => x.chatId === c.chatId);
      if (i >= 0) state.chats[i] = c; else state.chats.push(c);
      renderList('Підключено: ' + c.title);
    } catch (e) {
      msg.className = 'msg err';
      msg.replaceChildren(...errorText(e).map(t => h('div', { text: t })));
      btn.disabled = false;
    }
  });

  show(
    h('h1', { text: 'Ваші чати' }),
    note ? h('div', { class: 'msg ok', text: note }) : null,
    state.chats.map(c => h('button', { class: 'card', onclick: () => renderEditor(c) },
      h('b', { text: c.isPrivate ? (c.title || 'Особистий чат') : c.title },
        c.settings.isPaused ? h('span', { class: 'badge', text: '⏸ Пауза' }) : null),
      h('span', { text: summary(c.settings) }))),
    h('h2', { text: 'Підключити канал або групу' }),
    h('div', { class: 'linkrow' }, input, btn),
    h('p', { class: 'hint', text: 'Додайте бота до чату й переконайтеся, що ви там адміністратор.' }),
    msg);
}

// ---------- chat editor ----------
function renderEditor(chat) {
  const { boot } = state;
  const s = { ...chat.settings };
  state.editing = chat;
  tg.BackButton.show();
  let kind = 'Emergency';
  let seq = 0, timer = 0, errors = [];

  const tpl = h('textarea', { maxlength: boot.maxTemplateLength, rows: 8, spellcheck: 'false', 'aria-label': 'Шаблон повідомлення' });
  tpl.value = s.template || boot.defaultTemplate;
  const counter = h('div', { class: 'counter' });
  const errBox = h('div', { class: 'errs' });
  const bubble = h('div', { class: 'bubble' });
  const status = h('div', { class: 'msg' });
  const save = h('button', { class: 'btn save', text: 'Зберегти' });

  const regionSel = h('select', { 'aria-label': 'Регіон' });
  const partSel = h('select', { 'aria-label': 'Частина регіону' });

  function fillRegions() {
    regionSel.replaceChildren(h('option', { value: '', text: '— Оберіть регіон —' }),
      ...boot.regions.map(r => h('option', { value: r.id, text: r.name })));
    regionSel.value = s.regionId || '';
  }
  function fillParts() {
    const r = regionName(s.regionId);
    partSel.replaceChildren(h('option', { value: '', text: 'Весь регіон' }),
      ...(r ? r.parts : []).map(p => h('option', { value: p.id, text: p.name })));
    partSel.value = s.partId || '';
    partSel.disabled = !r;
  }
  fillRegions(); fillParts();
  regionSel.addEventListener('change', () => { s.regionId = regionSel.value || null; s.partId = null; fillParts(); schedulePreview(); });
  partSel.addEventListener('change', () => { s.partId = partSel.value || null; schedulePreview(); });

  function toggle(label, key) {
    const cb = h('input', { type: 'checkbox' });
    cb.checked = !!s[key];
    cb.addEventListener('change', () => { s[key] = cb.checked; });
    return h('label', { class: 'row' }, h('span', { text: label }), cb);
  }

  // Segmented control; options = [[value, label], ...], get/set map to settings.
  function seg(options, get, set, onChange) {
    const box = h('div', { class: 'seg', role: 'group' });
    const paint = () => [...box.children].forEach((b, i) => b.classList.toggle('on', options[i][0] === get()));
    options.forEach(([v, label]) => box.append(h('button', { type: 'button', text: label, onclick: () => { set(v); paint(); onChange && onChange(); } })));
    paint();
    return box;
  }

  function updateUi() {
    counter.textContent = `${tpl.value.length} / ${boot.maxTemplateLength}`;
    errBox.replaceChildren(...errors.map(t => h('div', { text: t })));
    save.disabled = errors.length > 0;
  }

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
    type: 'button', class: 'chip', text: '{' + p.name + '}',
    onclick: () => {
      const a = tpl.selectionStart ?? tpl.value.length, b = tpl.selectionEnd ?? a, t = '{' + p.name + '}';
      if (tpl.value.length - (b - a) + t.length > boot.maxTemplateLength) return;
      tpl.setRangeText(t, a, b, 'end');
      tpl.focus();
      schedulePreview();
    },
  })));

  const reset = h('button', { type: 'button', class: 'btn sec', text: 'Скинути до стандартного', onclick: () => {
    tpl.value = boot.defaultTemplate; schedulePreview();
  } });

  const help = h('details', {}, h('summary', { text: 'Довідка щодо плейсхолдерів' }),
    boot.placeholders.map(p => h('div', { class: 'ph' },
      h('code', { text: '{' + p.name + '}' }), ' — ' + p.description,
      p.example ? h('small', { text: 'Приклад: ' + p.example }) : null)),
    h('div', { class: 'ph', text: 'Дозволені теги: <b>, <i>, <u>, <s>, <code>, <pre>, <a href>, <tg-spoiler>, <blockquote>. Щоб вивести фігурні дужки, пишіть {{ і }}.' }));

  save.addEventListener('click', async () => {
    status.className = 'msg'; status.textContent = '';
    if (s.isActive && !s.regionId) {
      status.className = 'msg err'; status.textContent = 'Оберіть регіон або вимкніть підписку.';
      return;
    }
    const t = tpl.value.trim();
    const body = { ...s, template: !t || tpl.value === boot.defaultTemplate ? null : tpl.value };
    save.disabled = true;
    try {
      const c = await api('PUT', '/api/chats/' + encodeURIComponent(chat.chatId), body);
      const i = state.chats.findIndex(x => x.chatId === c.chatId);
      if (i >= 0) state.chats[i] = c;
      chat = c; state.editing = c;
      status.className = 'msg ok'; status.textContent = 'Збережено ✓';
      tg.HapticFeedback && tg.HapticFeedback.notificationOccurred('success');
    } catch (e) {
      status.className = 'msg err';
      status.replaceChildren(...errorText(e).map(x => h('div', { text: x })));
    }
    save.disabled = errors.length > 0;
  });

  // Address -> power group (only for regions that support it). Saved separately from the form below.
  const addrBox = h('div', { class: 'addr' });
  function renderAddr() {
    const r = regionName(s.regionId);
    if (!r || !r.hasGroups) { addrBox.replaceChildren(); return; }
    const saved = chat.settings;
    if (saved.regionId !== s.regionId) {
      addrBox.replaceChildren(h('h2', { text: 'Адреса' }),
        h('p', { class: 'hint', text: 'Збережіть регіон, щоб вибрати адресу й отримувати сповіщення по своїй черзі.' }));
      return;
    }
    const msg = h('div', { class: 'msg' });
    const apply = c => { chat = c; state.editing = c; const i = state.chats.findIndex(x => x.chatId === c.chatId); if (i >= 0) state.chats[i] = c; renderAddr(); };
    if (saved.groupId) {
      addrBox.replaceChildren(h('h2', { text: 'Адреса' }),
        h('p', { text: `${saved.addressLabel || 'Адреса'} — черга ${saved.groupId}` }),
        h('p', { class: 'hint', text: 'Повідомлення надходитимуть, коли для цієї черги почнуться екстрені відключення.' }),
        h('button', { type: 'button', class: 'btn sec', text: 'Прибрати адресу', onclick: async () => {
          try { apply(await api('DELETE', '/api/chats/' + encodeURIComponent(chat.chatId) + '/address')); }
          catch (e) { msg.className = 'msg err'; msg.textContent = errorText(e).join(' '); }
        } }), msg);
      return;
    }

    let street = null, seq = 0, timer = 0;
    const list = h('div', { class: 'picks' });
    const input = h('input', { type: 'text', placeholder: 'Почніть вводити вулицю', autocomplete: 'off', 'aria-label': 'Вулиця' });
    const setList = (items, onPick) => list.replaceChildren(...items.map(i =>
      h('button', { type: 'button', class: 'pick', text: i.name, onclick: () => onPick(i) })));

    async function search() {
      const my = ++seq, q = input.value.trim();
      msg.textContent = '';
      if (q.length < (street ? 1 : 2)) { list.replaceChildren(); return; }
      try {
        const url = street
          ? `/api/address/houses?regionId=${encodeURIComponent(s.regionId)}&streetId=${street.id}&query=${encodeURIComponent(q)}`
          : `/api/address/streets?regionId=${encodeURIComponent(s.regionId)}&query=${encodeURIComponent(q)}`;
        const items = await api('GET', url);
        if (my !== seq) return;
        if (!items.length) { list.replaceChildren(h('p', { class: 'hint', text: 'Нічого не знайдено.' })); return; }
        setList(items, street ? pickHouse : pickStreet);
      } catch (e) { if (my === seq) { msg.className = 'msg err'; msg.textContent = errorText(e).join(' '); } }
    }
    function pickStreet(i) {
      street = i; input.value = ''; input.placeholder = `Номер будинку (${i.name})`; input.setAttribute('aria-label', 'Будинок');
      list.replaceChildren(); input.focus();
    }
    async function pickHouse(i) {
      list.replaceChildren(); msg.className = 'msg'; msg.textContent = 'Визначаю чергу…';
      try {
        apply(await api('PUT', '/api/chats/' + encodeURIComponent(chat.chatId) + '/address',
          { regionId: s.regionId, streetId: street.id, houseId: i.id, label: `${street.name}, ${i.name}`, street: street.name, house: i.name }));
        tg.HapticFeedback && tg.HapticFeedback.notificationOccurred('success');
      } catch (e) { msg.className = 'msg err'; msg.textContent = errorText(e).join(' '); }
    }
    input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, 300); });
    addrBox.replaceChildren(h('h2', { text: 'Адреса' }),
      h('p', { class: 'hint', text: 'Оберіть адресу, щоб отримувати сповіщення по своїй черзі (за даними Yasno).' }),
      input, list, msg);
  }
  renderAddr();
  regionSel.addEventListener('change', renderAddr);

  // Linked groups and channels can be taken off the list; the private chat can only be unsubscribed.
  const removeBtn = chat.isPrivate ? null : h('button', { type: 'button', class: 'btn danger', text: 'Видалити зі списку', onclick: async () => {
    const ask = 'Видалити цей чат зі списку? Якщо ви єдиний, хто ним керує, сповіщення буде вимкнено, а збережену адресу видалено.';
    const ok = await new Promise(resolve => tg.showConfirm ? tg.showConfirm(ask, resolve) : resolve(confirm(ask)));
    if (!ok) return;
    removeBtn.disabled = true;
    try {
      await api('DELETE', '/api/chats/' + encodeURIComponent(chat.chatId));
      state.chats = state.chats.filter(x => x.chatId !== chat.chatId);
      renderList('Чат видалено зі списку.');
    } catch (e) {
      removeBtn.disabled = false;
      status.className = 'msg err';
      status.replaceChildren(...errorText(e).map(x => h('div', { text: x })));
    }
  } });

  show(
    h('button', { class: 'back', text: '← Назад', onclick: () => renderList() }),
    h('h1', { text: chat.title || 'Особистий чат' }),
    h('h2', { text: 'Регіон' }), regionSel, partSel, addrBox,
    h('h2', { text: 'Сповіщення' }),
    toggle('Підписка активна', 'isActive'),
    toggle('Пауза', 'isPaused'),
    toggle('Писати про завершення', 'notifyOnEnd'),
    toggle('Реагувати на «Світло зникло» (перевіряти адресу)', 'outageTrigger'),
    h('h2', { text: 'Які сповіщення' }),
    seg([['All', 'Усі'], ['EmergencyOnly', 'Лише екстрені та аварії']], () => s.filter, v => { s.filter = v; }),
    h('h2', { text: 'Оновлення' }),
    seg([[true, 'Редагувати те саме повідомлення'], [false, 'Щоразу нове повідомлення']], () => !!s.editInPlace, v => { s.editInPlace = v; }),
    h('h2', { text: 'Шаблон повідомлення' }),
    tpl, counter, errBox, chips, reset, help,
    h('h2', { text: 'Попередній перегляд' }),
    seg(KINDS, () => kind, v => { kind = v; }, schedulePreview),
    bubble, status, save,
    removeBtn ? h('h2', { text: 'Керування' }) : null, removeBtn);

  updateUi();
  runPreview();
}

// ---------- startup ----------
async function load() {
  show(h('p', { class: 'hint', text: 'Завантаження…' }));
  try {
    const [boot, chats] = await Promise.all([state.boot || api('GET', '/api/bootstrap'), api('GET', '/api/chats')]);
    state.boot = boot; state.chats = chats;
    renderList();
  } catch (e) { showFatal(e, load); }
}

if (!tg || !initData) {
  show(h('h1', { text: 'Налаштування DTEK' }),
    h('p', { class: 'msg', text: 'Відкрийте цей застосунок із Telegram (кнопка в меню бота).' }));
} else {
  tg.ready();
  tg.expand();
  tg.BackButton.onClick(() => renderList());
  load();
}
