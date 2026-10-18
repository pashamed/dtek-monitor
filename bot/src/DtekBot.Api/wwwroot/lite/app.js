// Lite settings editor for the DTEK alert bot (vanilla JS, no deps).
const tg = window.Telegram && window.Telegram.WebApp;
const app = document.getElementById('app');
const initData = tg ? tg.initData : '';

const SLOT_GROUPS = [['emergency', 'Екстрені відключення'], ['regional', 'Аварія в регіоні'], ['local', 'Локальні відключення'], ['stabilization', 'Графік (стабілізація)']];
const KIND_OF = { emergency: 'Emergency', regional: 'Accident', local: 'Emergency', ended: 'Ended' };
const ALERT_SLOTS = ['emergency', 'regional', 'local', 'ended'];
// How the «ended» notice is posted: [value, button label, one-line explanation].
const END_STYLES = [
  ['Reply', 'Відповіддю на сповіщення', 'Бот відповість на початкове сповіщення, і воно лишиться без змін.'],
  ['New', 'Окремим повідомленням', 'Бот надішле окреме нове повідомлення без привʼязки до початкового.'],
  ['Edit', 'Змінити початкове повідомлення', 'Текст початкового сповіщення буде замінено на повідомлення про зняття. Якщо його не можна змінити, бот відповість на нього.'],
];
// Per-alert settings with fallbacks for a server that only knows the older notifyOnEnd / editInPlace fields.
const slotSettingsOf = st => Object.fromEntries(ALERT_SLOTS.map(id => {
  const o = (st.slotSettings || {})[id];
  return [id, {
    enabled: o ? !!o.enabled : id === 'ended' ? st.notifyOnEnd !== false : true,
    editInPlace: o ? !!o.editInPlace : st.editInPlace !== false,
    ...(id === 'ended' ? { endStyle: END_STYLES.some(e => e[0] === (o && o.endStyle)) ? o.endStyle : 'Reply' } : {}),
  }];
}));
const MODES = [['New', 'Нове повідомлення'], ['ReplaceDelete', 'Видалити старе й опублікувати нове'], ['Edit', 'Редагувати те саме']];
const MODE_TEXT = {
  post: { New: 'Кожне оновлення графіка — новий пост. Старі пости лишаються в каналі.', ReplaceDelete: 'Бот публікує новий пост і видаляє попередній. Потрібне право «Видаляти повідомлення», інакше старий пост лишиться.', Edit: 'Бот змінює один і той самий пост — нових публікацій не буде.' },
  event: { New: 'Кожна подія — окреме повідомлення. Старі лишаються в каналі.', ReplaceDelete: 'Нова подія видаляє попереднє повідомлення. Потрібне право «Видаляти повідомлення», інакше воно лишиться.', Edit: 'Одне повідомлення оновлюється на місці, нові не зʼявляються.' },
};
const TABS = [['main', 'Основне'], ['addr', 'Адреса і черга'], ['sched', 'Графік'], ['msg', 'Повідомлення']];
const state = { boot: null, chats: [], editing: null };
const groupsCache = new Map(); // regionId -> Promise of schedule groups
const PRIVATE_LABEL = 'Цей чат із ботом';
const CREDIT = 'Дані графіків: outage-data-ua (ДТЕК).';

const HELP = {
  region: ['Регіон', 'Регіон, за яким бот стежить за відключеннями. Від нього залежать доступні адреси, черги та графіки.'],
  parts: ['Райони', 'Бот надсилатиме екстрені сповіщення, лише якщо у повідомленні ДТЕК названо один із обраних районів, або якщо повідомлення стосується всього регіону. Якщо нічого не обрано, сповіщення приходять для всього регіону.'],
  status: ['Поточний стан', 'Показує, що зараз повідомляє сайт ДТЕК для вашого регіону та що кажуть графіки для вашої черги.'],
  check: ['Світла немає?', 'Бот перевірить причину: спершу графік вашої черги, потім загальне повідомлення ДТЕК про екстрені відключення чи аварію, і лише потім адресу на сайті ДТЕК. Результат також надійде вам у чат із ботом.'],
  active: ['Підписка активна', 'Поки підписка активна, бот надсилає сюди сповіщення. Якщо її вимкнути, сповіщення припиняються, а збережена адреса видаляється.'],
  pause: ['Пауза', 'Тимчасово зупиняє сповіщення, не скасовуючи підписку й не видаляючи адресу. Зніміть паузу, щоб відновити їх.'],
  slotOn: ['Надсилати це сповіщення', 'Якщо вимкнути, бот не надсилатиме цей вид сповіщень у цей чат. Шаблон можна й надалі редагувати.'],
  slotEnded: ['Сповіщати про зняття аварійного запису', 'Коли екстрений запис зникає з сайту ДТЕК, бот повідомляє про це (спосіб обираєте нижче). Можна вимкнути лише це повідомлення, а саме сповіщення й його оновлення залишаться. Зникнення запису не означає, що світло вже повернулося.'],
  slotEndStyle: ['Як показувати', 'Відповіддю на сповіщення — бот відповідає на початкове сповіщення (так за замовчуванням). Окремим повідомленням — надсилається нове повідомлення без привʼязки до початкового. Змінити початкове повідомлення — його текст замінюється на повідомлення про зняття; якщо повідомлення вже видалено або його не можна змінити, бот відповість на нього новим.'],
  slotEdit: ['Оновлення', 'Редагувати те саме повідомлення — одне повідомлення оновлюється на місці. Щоразу нове — кожна зміна приходить окремим повідомленням зі сповіщенням.'],
  addrManual: ['Адреса для перевірки', 'Вкажіть населений пункт (місто, село чи селище), вулицю та номер будинку так, як вони написані на сайті ДТЕК: з «м.», «с.» або «смт», з «вул.» або «просп.», і номер будинку в тому ж вигляді (наприклад 12 або 12А). Бот сам не може перевірити написання: чи знайшов він адресу, стане видно після першої перевірки.'],
  address: ['Адреса', 'Потрібна, щоб визначити вашу чергу й перевіряти саме вашу адресу на сайті ДТЕК. Зберігається, поки діє підписка, і видаляється при відписці.'],
  group: ['Черга', 'Номер черги в графіку відключень, наприклад 45.1. Визначається за адресою, але її можна змінити вручну. Від черги залежать повідомлення за графіком і сповіщення про екстрені відключення черги.'],
  outage: ['Реагувати на «Світло зникло»', 'Якщо застосунок моніторингу світла пише в канал «Світло зникло», бот знайде причину: спершу перевіряє графік черги, потім загальне повідомлення ДТЕК про екстрені відключення чи аварію, і лише тоді адресу на сайті ДТЕК.'],
  triggerText: ['Текст повідомлення «світла немає»', 'Це текст, який ваш застосунок моніторингу світла пише в канал, коли світло зникає, наприклад «зараз світло зникло». Регістр не враховується. Можна вказати регулярний вираз, наприклад світло\\s+(зникло|вимкнули). Бот порівнює його з новими публікаціями каналу й перевіряє адресу.'],
  schedule: ['Сповіщення за графіком', 'Бот пише в канал, коли за графіком світло вимикається й коли воно має зʼявитися, з тривалістю та очікуваним часом. Використовує дані outage-data-ua.'],
  image: ['Картинка', 'Таблиця на 2 дні — графік на сьогодні й завтра. Коротка картинка — картка з годинами відключень. Без картинки — лише текст.'],
  schedulePost: ['Публікація графіка', 'Бот публікує в канал окремий пост із графіком відключень вашої черги (картинка з підписом) і оновлює його, коли графік змінюється. Те, як саме зʼявляються нові пости, задає режим нижче. Видалення попереднього повідомлення потребує в бота права адміністратора «Видаляти повідомлення»; без нього старе повідомлення просто лишається.'],
  postMode: ['Як публікувати', 'Нове повідомлення — кожне оновлення графіка це новий пост, старі лишаються. Видалити старе й опублікувати нове — бот публікує новий пост і видаляє попередній (потрібне право «Видаляти повідомлення», інакше старий пост лишиться). Редагувати те саме — бот змінює один і той самий пост.'],
  events: ['Події: світло вимкнено / зʼявилося', 'Бот пише, коли за графіком світло вимикається й коли воно має зʼявитися: з тривалістю, очікуваним часом і статистикою. Для кожної події є свій шаблон. Видалення попереднього повідомлення потребує права «Видаляти повідомлення».'],
  eventMode: ['Як показувати події', 'Нове повідомлення — кожна подія окремим постом, старі лишаються. Видалити старе й опублікувати нове — попереднє повідомлення про подію видаляється (потрібне право «Видаляти повідомлення», інакше воно лишиться). Редагувати те саме — один пост оновлюється на місці.'],
  slots: ['Шаблони повідомлень', 'Кожен вид повідомлень, які бот публікує, має свій шаблон. Оберіть вид, змініть текст і стиль та перегляньте, як це виглядатиме в Telegram. Змінені шаблони позначені фіолетовою цяткою.'],
  template: ['Шаблон повідомлення', 'Ваш текст для цього виду повідомлень. Параметри у {фігурних дужках} замінюються даними, теги Telegram дозволені: <b>, <i>, <u>, <s>, <code>, <pre>, <a href>, <tg-spoiler>, <blockquote>. Щоб вивести фігурні дужки, пишіть {{ і }}.'],
  cond: ['Умовні блоки', 'Фрагмент {#назва}…{/назва} лишається в тексті, лише якщо значення параметра «назва» не порожнє. Наприклад: {#світло_було}🕓 Воно було {світло_було}{/світло_було}. Блоки не можна вкладати.'],
  test: ['Тестове повідомлення', 'Бот надішле це повідомлення в канал (або в чат із ботом) зі зразковими чи вашими даними й позначкою 🧪 Тест, щоб ви побачили його справжній вигляд. Кількість тестів обмежена: якщо зʼявиться повідомлення про ліміт, зачекайте трохи й повторіть.'],
  connect: ['Підключити канал', 'Додайте бота адміністратором у канал із правом публікувати повідомлення — канал зʼявиться тут автоматично (за потреби натисніть «Оновити список»). Запасний варіант для публічного каналу: вставте посилання на нього (https://t.me/імʼя) або @імʼя, бот сам перетворить його на імʼя каналу. Приватні посилання-запрошення (t.me/+…) не підходять.'],
  svb: ['Світлобот', 'Світлобот — сервіс, який публікує графік відключень у вашому Telegram-каналі. Канал привʼязаний до секретного ключа channel_key: його можна взяти в налаштуваннях каналу в чат-боті Світлобот. Ключ секретний: він зберігається на сервері в зашифрованому вигляді й більше ніде не показується. Той, хто має ключ, може змінювати графік вашого каналу, тому нікому його не передавайте.'],
  svbKey: ['Ключ каналу', 'Цей ключ (channel_key) потрібен, щоб бот міг керувати справжнім розкладом вашого каналу у Світлоботі: надсилати графік, підключати шаблон чи видаляти розклад. Без нього Світлобот не дозволить нічого змінити. Ключ видно в налаштуваннях каналу в чат-боті Світлобот. Бот перевірить його, збереже зашифрованим і більше не покаже.'],
  svbCheck: ['Перевірити ключ', 'Показує, який саме ключ збережено, не розкриваючи його: вісім крапок і останні два символи, а також короткий «відбиток». Відбиток однаковий для того самого ключа, тож ним можна звірити ключ зі Світлобота. Сам ключ ніколи не повертається.'],
  svbForget: ['Забути ключ', 'Видаляє збережений ключ лише тут, у боті. Графік і пост у каналі лишаються як є. Щоб знову керувати Світлоботом, введіть ключ ще раз.'],
  svbPush: ['Оновити графік у Світлоботі', 'Бот збереже у Світлоботі тижневий графік обраної тут черги. Світлобот сам керує публікацією та закріпленням за своїм розкладом; ця дія не надсилає негайне повідомлення в канал.'],
  svbMaybe: ['Можливі відключення', 'Якщо увімкнено, «можливі» відключення (коли світло може бути, а може не бути) вважаються відключеннями. Якщо вимкнено, враховуються лише точні відключення.'],
  svbTpl: ['Готові шаблони', 'Готовий вигляд публікації з графіком у Світлоботі. Спочатку автоматично обирається шаблон вашої черги; його можна змінити вручну.'],
  svbDel: ['Видалити розклад', 'Видаляє розклад у Світлоботі, а також його допис із графіком у вашому каналі. Дію не можна скасувати, але графік можна надіслати знову.'],
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
    const msg = res.status === 401 ? 'Відкрийте цей застосунок із Telegram.'
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

function partIdsOf(s) {
  const r = regionOf(s.regionId);
  return r ? [...new Set(s.partIds || [])].filter(id => r.parts.some(p => p.id === id)).sort() : [];
}
function partNames(s) {
  const r = regionOf(s.regionId);
  return r ? r.parts.filter(p => partIdsOf(s).includes(p.id)).map(p => p.name) : [];
}

function regionLine(s) {
  const r = regionOf(s.regionId);
  if (!s.regionId) return 'Регіон не обрано';
  return r ? r.name : 'Невідомий регіон';
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
  const names = partNames(s);
  const chips = [
    ...names.slice(0, 2).map(n => h('span', { class: 'chip grey', text: n })),
    names.length > 2 ? h('span', { class: 'chip grey', text: '+' + (names.length - 2) }) : null,
    s.groupId ? h('span', { class: 'chip purple', text: 'Черга ' + s.groupId }) : null,
    s.schedulePost ? h('span', { class: 'chip purple', text: 'Графік' }) : null,
    s.scheduleNotify ? h('span', { class: 'chip purple', text: 'Події' }) : null,
    s.outageTrigger ? h('span', { class: 'chip blue', text: 'Реакція на «Світло зникло»' }) : null,
    Object.values(s.templates || {}).some(v => v && v.trim()) ? h('span', { class: 'chip grey', text: 'Свої шаблони' }) : null,
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
  const input = h('input', { type: 'text', value: linkDraft, placeholder: 'https://t.me/імʼя_каналу або @імʼя', autocapitalize: 'none', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Посилання або імʼя публічного каналу' });
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
      h('p', { class: 'lead', text: 'Керуйте каналами зі сповіщеннями про відключення світла. Усі налаштування — тут, у редакторі.' })),
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
      h('div', { class: 'or', text: 'Або вставте посилання t.me чи @імʼя публічного каналу' }),
      h('div', { class: 'linkrow' }, input, btn), msg));
}

// ---------- chat editor ----------
function renderEditor(chat) {
  const { boot } = state;
  const isPriv = !!chat.isPrivate;
  const slots = boot.templateSlots || [];
  const slotOf = id => slots.find(x => x.id === id) || null;
  const s = {
    ...chat.settings, partIds: [...(chat.settings.partIds || [])],
    scheduleImage: chat.settings.scheduleImage || 'None', schedulePostImage: chat.settings.schedulePostImage || 'Grid',
    schedulePostMode: chat.settings.schedulePostMode || 'New', scheduleEventMode: chat.settings.scheduleEventMode || 'Edit',
    slotSettings: slotSettingsOf(chat.settings),
  };
  state.editing = chat;
  try { tg.BackButton.show(); } catch { /* optional */ }
  let seq = 0, timer = 0, busy = false, tab = 'main', hintFor = null, inlineFor = null, inlineNode = null, manualMainFor = null, manualMainNode = null, manualTabFor = null, manualTabNode = null, uid = 0, cs = slots.length ? slots[0].id : null;
  // Per-slot editor state: current text (the default text when not customised), errors from preview / from the server.
  const tpls = Object.fromEntries(slots.map(x => [x.id, (chat.settings.templates || {})[x.id] || x.defaultTemplate]));
  const perr = {}, serr = {};

  const groupOf = () => (chat.settings.regionId === s.regionId && chat.settings.groupId) || null;
  const addrOf = () => (groupOf() && chat.settings.addressLabel) || null;
  // A hand-typed address (regions without the address search) does not depend on the group.
  const manualOf = () => (chat.settings.regionId === s.regionId && chat.settings.addressLabel) || null;
  const customised = (x, v) => !!(x && v && v.trim() && v !== x.defaultTemplate);
  const isCustom = id => customised(slotOf(id), tpls[id]);
  const tplMap = get => Object.fromEntries(slots.filter(x => customised(x, get(x.id))).map(x => [x.id, get(x.id)]));
  // Form fields only; schedule flags count only when a group is saved for the chosen region.
  const form = (src, templates, saved) => {
    const r = regionOf(src.regionId), ok = !!(r && r.hasSchedules && saved.regionId === src.regionId && saved.groupId);
    const slotSettings = slotSettingsOf(src);
    return {
      regionId: src.regionId || null, partIds: partIdsOf(src), isActive: !!src.isActive, isPaused: !!src.isPaused,
      slotSettings, notifyOnEnd: slotSettings.ended.enabled, editInPlace: slotSettings.emergency.editInPlace, // the two mirror the old fields for older servers
      outageTrigger: !!src.outageTrigger, outageTriggerPattern: (src.outageTriggerPattern || '').trim() || null, templates,
      scheduleNotify: !!(src.scheduleNotify && ok), scheduleImage: src.scheduleImage || 'None', scheduleEventMode: src.scheduleEventMode || 'Edit',
      schedulePost: !!(src.schedulePost && ok), schedulePostMode: src.schedulePostMode || 'New', schedulePostImage: src.schedulePostImage || 'Grid',
    };
  };

  const cur = () => form(s, tplMap(id => tpls[id]), chat.settings);
  let base = JSON.stringify(form(s, tplMap(id => (chat.settings.templates || {})[id]), chat.settings));
  const isDirty = () => JSON.stringify(cur()) !== base;
  const slotErrs = id => [...(perr[id] || []), ...(serr[id] || [])];
  const blocking = () => Object.values(perr).some(a => a && a.length);
  const anyErr = () => blocking() || Object.values(serr).some(a => a && a.length);

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
    if (id === 'sched') { loadPv(); svbEnsure(); }
    if (id === 'msg') runPreview();
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
  function seg(options, get, set, label, cls) {
    const box = h('div', { class: 'seg' + (cls ? ' ' + cls : ''), role: 'group', 'aria-label': label });
    const paint = () => [...box.children].forEach((b, i) => { const on = options[i][0] === get(); b.classList.toggle('on', on); b.setAttribute('aria-pressed', on); });
    options.forEach(([v, l]) => box.append(h('button', { type: 'button', text: l, onclick: () => { set(v); paint(); } })));
    paint();
    return box;
  }

  function touch() { setErr([]); updateUi(); }

  // ----- tab: Основне -----
  const regionSel = h('select', { 'aria-label': 'Регіон', id: 'regionSel' });
  regionSel.replaceChildren(h('option', { value: '', text: '— Оберіть регіон —' }), ...boot.regions.map(r => h('option', { value: r.id, text: r.name })));
  regionSel.value = s.regionId || '';
  const hintBox = h('div');
  const activeRow = toggle('Підписка активна', 'isActive', 'active');

  // Districts: multi-select chips; empty selection = whole region.
  let dq = '';
  const dqIn = h('input', { type: 'text', placeholder: 'Пошук району', autocomplete: 'off', 'aria-label': 'Пошук району', hidden: true });
  const dList = h('div', { class: 'dchips' });
  const dSum = h('p', { class: 'hint' });
  const distBox = h('div', { class: 'fld' }, fieldLabel('Райони', 'parts'), dqIn, dList, dSum);
  dqIn.addEventListener('input', () => { dq = dqIn.value; renderDistricts(); });

  function renderDistricts() {
    const r = regionOf(s.regionId);
    distBox.hidden = !r || !r.parts.length;
    if (distBox.hidden) return;
    const many = r.parts.length > 12, sel = new Set(partIdsOf(s)), q = many ? dq.trim().toLowerCase() : '';
    dqIn.hidden = !many;
    dList.classList.toggle('scroll', many);
    const chip = (label, on, fn) => h('button', { type: 'button', class: 'dchip' + (on ? ' on' : ''), 'aria-pressed': String(on), text: label, onclick: fn });
    const flip = id => { sel.has(id) ? sel.delete(id) : sel.add(id); s.partIds = [...sel]; touch(); renderDistricts(); syncStatus(); };
    const top = dList.scrollTop;
    dList.replaceChildren(chip('Усі райони', !sel.size, () => { s.partIds = []; touch(); renderDistricts(); syncStatus(); }),
      ...r.parts.filter(p => !q || p.name.toLowerCase().includes(q)).map(p => chip(p.name, sel.has(p.id), () => flip(p.id))));
    dList.scrollTop = top;
    const names = partNames(s);
    dSum.textContent = names.length ? 'Сповіщення про екстрені відключення лише для: ' + names.join(', ') : 'Сповіщення для всього регіону';
  }

  // Current state card (what DTEK and the schedules say right now).
  const stLines = h('div', { class: 'stlines', 'aria-live': 'polite' });
  const stBtn = h('button', { type: 'button', class: 'btn ghost sm', text: 'Оновити', onclick: () => loadStatus() });
  const stCard = card(h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Поточний стан' }), help('status')), stLines, stBtn);
  let stSeq = 0, stFor, stPartsFor;
  async function loadStatus() {
    const my = ++stSeq;
    stBtn.disabled = true;
    stLines.replaceChildren(h('div', { class: 'sk sk-line' }), h('div', { class: 'sk sk-line s' }));
    try {
      const regionId = s.regionId;
      const parts = partIdsOf(s).join(',');
      const suffix = regionId ? '?regionId=' + encodeURIComponent(regionId) + '&partIds=' + encodeURIComponent(parts) : '';
      const d = await api('GET', '/api/chats/' + encodeURIComponent(chat.chatId) + '/status' + suffix);
      if (my !== stSeq) return;
      const lines = (d && d.lines) || [];
      stLines.replaceChildren(...(lines.length ? lines.map(t => h('div', { class: 'stl', text: t })) : [h('div', { class: 'hint', text: 'Поки немає даних.' })]));
    } catch {
      if (my === stSeq) stLines.replaceChildren(h('div', { class: 'hint', text: 'Стан недоступний' }));
    }
    if (my === stSeq) stBtn.disabled = false;
  }
  function syncStatus() {
    const regionId = s.regionId;
    const has = !!regionId;
    stCard.hidden = !has;
    if (!has) {
      stFor = undefined;
      stPartsFor = undefined;
      stSeq++;
      stBtn.disabled = false;
      stLines.replaceChildren();
    } else {
      const parts = partIdsOf(s).join(',');
      if (stFor !== regionId || stPartsFor !== parts) {
        stFor = regionId;
        stPartsFor = parts;
        loadStatus();
      }
    }
  }

  function renderHint() {
    const r = regionOf(s.regionId);
    if (r && !r.hasGroups) {
      const nodes = [];
      if (!manualOf()) {
        if (manualMainFor !== s.regionId) {
          manualMainNode = card(h('div', { class: 'regline' }, h('span', { class: 'chip purple', text: 'Адреса для: ' + r.name })), manualAddress(s.regionId));
          manualMainFor = s.regionId;
        }
        nodes.push(manualMainNode);
      } else manualMainFor = null;
      if (!groupOf() && hintFor === s.regionId && r.hasSchedules) nodes.push(scheduleNudge());
      inlineFor = null; hintBox.replaceChildren(...nodes);
      return;
    }
    manualMainFor = null;
    if (!r || groupOf()) { inlineFor = null; hintBox.replaceChildren(); return; }

    // A region with the address search: the address is typed right here, no need to find another tab.
    if (r.hasGroups) {
      if (inlineFor !== s.regionId) {
        const ctx = makeAddrCtx();
        inlineNode = card(
          h('div', { class: 'regline' }, h('span', { class: 'chip purple', text: 'Адреса для: ' + r.name })),
          addressPicker(s.regionId, ctx), ctx.msg);
        inlineFor = s.regionId;
      }
      hintBox.replaceChildren(inlineNode);
      return;
    }

    if (hintFor !== s.regionId || !r.hasSchedules) { hintBox.replaceChildren(); return; }
    hintBox.replaceChildren(scheduleNudge());
  }

  const scheduleNudge = () => h('div', { class: 'nudge glass' },
    h('div', {}, h('b', { text: 'Тепер оберіть чергу' }),
      h('p', { class: 'hint', text: 'Це потрібно, щоб отримувати сповіщення саме для вашої черги.' })),
    h('button', { type: 'button', class: 'btn sm', text: 'Адреса і черга', onclick: () => setTab('addr') }));

  regionSel.addEventListener('change', () => {
    s.regionId = regionSel.value || null; s.partIds = []; dq = ''; dqIn.value = '';
    if (s.regionId && !s.isActive) { s.isActive = true; activeRow.cb.checked = true; }
    const r = regionOf(s.regionId);
    hintFor = r && (r.hasGroups || r.hasSchedules) ? s.regionId : null;
    setErr([]); refreshAll();
  });

  panels.main.append(
    stCard,
    card(h('div', { class: 'fld' }, fieldLabel('Регіон', 'region', 'regionSel'), regionSel), distBox),
    hintBox,
    card(activeRow, toggle('Пауза', 'isPaused', 'pause')));

  // ----- save logic -----
  function applyChat(c) {
    chat = c; state.editing = c;
    const i = state.chats.findIndex(x => x.chatId === c.chatId);
    if (i >= 0) state.chats[i] = c;
    if (!c.settings.groupId) { s.scheduleNotify = false; s.schedulePost = false; }
    const r = regionOf(c.settings.regionId);
    const ok = !!(c.settings.groupId && r && r.hasSchedules);
    const b = JSON.parse(base);
    b.scheduleNotify = !!(c.settings.scheduleNotify && ok);
    b.schedulePost = !!(c.settings.schedulePost && ok);
    base = JSON.stringify(b);
  }

  // Server template errors are plain strings that name the slot; show each next to its slot when we can tell which.
  function attributeErrors(e) {
    for (const k of Object.keys(serr)) delete serr[k];
    const rest = [];
    for (const d of Array.isArray(e.details) ? e.details.map(String) : []) {
      const low = d.toLowerCase();
      const x = slots.find(z => low.includes(z.title.toLowerCase())) || slots.find(z => new RegExp(`(^|[^a-zа-яіїєґ_])${z.id}([^a-zа-яіїєґ_]|$)`, 'i').test(d));
      if (x) (serr[x.id] = serr[x.id] || []).push(d); else rest.push(d);
    }
    return [e.message, ...rest];
  }

  // Shared by the save bar, ensureSaved() and the test send. Resolves to null on success or the Error.
  async function doSave(silent) {
    if (!silent) setErr([]);
    if (s.isActive && !s.regionId) {
      const e = new Error('Оберіть регіон або вимкніть підписку.');
      if (!silent) setErr([e.message]);
      return e;
    }
    busy = true; updateUi();
    try {
      const { partId: _p, template: _t, ...saved } = chat.settings; // legacy fields are never sent
      applyChat(await api('PUT', '/api/chats/' + encodeURIComponent(chat.chatId), { ...saved, ...cur() }));
      base = JSON.stringify(cur());
      for (const k of Object.keys(serr)) delete serr[k];
      if (!silent) { haptic('success'); busy = false; refreshAll(); }
      return null;
    } catch (e) {
      const lines = attributeErrors(e);
      if (!silent) setErr(lines);
      renderPills(); paintSlotErrors();
      return Object.assign(e, { lines });
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

  // Custom «power is off» phrase for the channel trigger (a regex; the live check is advice only and never blocks saving).
  const trigIn = h('input', { type: 'text', id: 'trigIn', maxlength: 200, placeholder: 'світло зникло', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Текст повідомлення «світла немає»' });
  trigIn.value = s.outageTriggerPattern || '';
  const trigStatus = h('div', { 'aria-live': 'polite' });
  const trigBox = h('div', { class: 'fld' }, fieldLabel('Текст повідомлення «світла немає»', 'triggerText', 'trigIn'), trigIn, trigStatus);
  let trigSeq = 0, trigTimer = 0;
  async function checkTrigger() {
    const my = ++trigSeq;
    try {
      const r = await api('GET', `/api/chats/${encodeURIComponent(chat.chatId)}/trigger-check?pattern=${encodeURIComponent(trigIn.value.trim())}`);
      if (my !== trigSeq) return;
      trigStatus.replaceChildren(r.warning ? h('div', { class: 'note warn', text: r.warning })
        : r.matched > 0 ? h('div', { class: 'trigok', text: `Знайдено в ${r.matched} із ${r.seen} останніх публікацій ✓` }) : null);
    } catch { if (my === trigSeq) trigStatus.replaceChildren(); }
  }
  trigIn.addEventListener('input', () => { s.outageTriggerPattern = trigIn.value; touch(); clearTimeout(trigTimer); trigTimer = setTimeout(checkTrigger, 500); });
  const syncTrigger = () => { trigBox.hidden = !s.outageTrigger; if (s.outageTrigger) checkTrigger(); else { trigSeq++; trigStatus.replaceChildren(); } };
  trigBox.hidden = !s.outageTrigger;
  if (!isPriv && s.outageTrigger) checkTrigger();

  // "Світла немає?" check (private chat only): runs the outage assessment for the saved address.
  const chkOut = h('div', { 'aria-live': 'polite' });
  const chkBtn = h('button', { type: 'button', class: 'btn amber', text: 'Перевірити зараз' });
  const chkCard = h('section', { class: 'card glass warnc' },
    h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Світла немає?' }), help('check')),
    h('p', { class: 'hint', text: 'Перевірте причину відключення за вашою адресою.' }), chkBtn);
  chkBtn.addEventListener('click', async () => {
    chkBtn.disabled = true; chkBtn.classList.add('busy'); chkBtn.textContent = 'Перевіряю…';
    chkOut.replaceChildren();
    try {
      const d = await api('POST', '/api/chats/' + encodeURIComponent(chat.chatId) + '/check');
      chkOut.replaceChildren(card(h('h3', { class: 'ct', text: 'Результат перевірки' }), h('div', { class: 'tgb', text: (d && d.text) || 'Немає даних.' })));
    } catch (e) {
      chkOut.replaceChildren(h('div', { class: 'note ' + (e.status === 429 ? 'warn' : 'err'), role: 'alert', text: errorText(e).join(' ') }));
    }
    chkBtn.disabled = false; chkBtn.classList.remove('busy'); chkBtn.textContent = 'Перевірити зараз';
  });

  // The address search is used in two places: right under the region on «Основне», and on «Адреса і черга».
  function makeAddrCtx() {
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
        haptic('success'); inlineFor = null;
        if (path.startsWith('/address')) { manualMainFor = null; manualTabFor = null; }
        refreshAll();
        return true;
      } catch (e) { fail(e); return false; }
    }
    return { msg, fail, commit };
  }

  function addressPicker(regionId, { msg, fail, commit }) {
    let street = null, sseq = 0, stimer = 0;
    const list = h('div', { class: 'picks' });
    const inId = 'addrIn' + (++uid);
    const input = h('input', { type: 'text', id: inId, placeholder: 'Почніть вводити вулицю', autocomplete: 'off', 'aria-label': 'Вулиця' });
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
    return h('div', { class: 'fld' }, fieldLabel('Адреса', 'address', inId), input, list,
      h('p', { class: 'hint', text: 'Знайдіть вулицю, потім номер будинку: черга визначиться автоматично, а налаштування збережуться.' }));
  }

  // Address typed by hand for regions without the search; it saves the chat first (like the picker), then PUT /address/manual.
  function manualAddress(regionId) {
    const ctx = makeAddrCtx();
    const field = (title, placeholder) => {
      const id = 'addrM' + (++uid);
      const input = h('input', { type: 'text', id, placeholder, autocomplete: 'off', spellcheck: 'false', 'aria-label': title });
      return { input, node: h('div', { class: 'fld' }, h('label', { for: id, text: title }), input) };
    };
    const city = field('Населений пункт', 'м. Буча'), street = field('Вулиця', 'вул. Вокзальна'), house = field('Будинок', '12');
    const ins = [city, street, house].map(x => x.input);
    const save = h('button', { type: 'button', class: 'btn primary', text: 'Зберегти адресу' });
    let working = false;
    const sync = () => { save.disabled = working || ins.some(i => !i.value.trim()); };
    async function submit() {
      if (save.disabled) return;
      working = true; save.classList.add('busy'); sync();
      const ok = await ctx.commit('PUT', '/address/manual',
        { regionId, city: city.input.value.trim(), street: street.input.value.trim(), house: house.input.value.trim() }, true);
      if (!ok) { working = false; save.classList.remove('busy'); sync(); }
    }
    ins.forEach(i => { i.addEventListener('input', sync); i.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } }); });
    save.addEventListener('click', submit);
    sync();
    return h('div', { class: 'fld' }, fieldLabel('Адреса для перевірки', 'addrManual'), city.node, street.node, house.node,
      h('p', { class: 'hint', text: 'Пишіть так, як на сайті ДТЕК: від цього залежить, чи знайде бот адресу.' }), save, ctx.msg);
  }

  function renderAddr() {
    const r = regionOf(s.regionId);
    const nodes = [];
    if (!r) nodes.push(empty('Спершу оберіть регіон на вкладці «Основне».', 'main'));
    else if (!r.hasGroups && !r.hasSchedules) nodes.push(empty('Для цього регіону вибір адреси й черги недоступний.'));
    else {
      const regionId = s.regionId, grp = groupOf(), addr = addrOf();
      const ctx = makeAddrCtx();
      const { msg, fail, commit } = ctx;

      if (!r.hasGroups) {
        const saved = manualOf();
        if (saved) {
          manualTabFor = null;
          nodes.push(card(fieldLabel('Адреса', 'addrManual'),
            h('div', { class: 'result' }, h('div', { class: 'rl', text: saved }), grp ? h('span', { class: 'chip purple', text: 'Черга ' + grp }) : null),
            h('button', { type: 'button', class: 'btn ghost sm', text: 'Прибрати адресу', onclick: () => commit('DELETE', '/address/manual') })));
        } else {
          if (manualTabFor !== regionId) {
            manualTabNode = card(h('div', { class: 'regline' }, h('span', { class: 'chip purple', text: 'Адреса для: ' + r.name })), manualAddress(regionId));
            manualTabFor = regionId;
          }
          nodes.push(manualTabNode);
        }
      }

      if (r.hasGroups && !addr) nodes.push(h('div', { class: 'regline' }, h('span', { class: 'chip purple', text: 'Адреса для: ' + r.name })));
      if (r.hasGroups) {
        if (addr) {
          nodes.push(card(fieldLabel('Адреса', 'address'),
            h('div', { class: 'result' }, h('div', { class: 'rl', text: addr }), h('span', { class: 'chip purple', text: 'Черга ' + grp })),
            h('button', { type: 'button', class: 'btn ghost sm', text: 'Прибрати адресу', onclick: () => commit('DELETE', '/address') })));
        } else {
          nodes.push(card(addressPicker(regionId, ctx)));
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
      if (isPriv && (addrOf() || (!r.hasGroups && manualOf()))) nodes.push(chkCard, chkOut);
    }
    if (!isPriv) {
      const trig = toggle('Реагувати на «Світло зникло»', 'outageTrigger', 'outage');
      trig.cb.addEventListener('change', syncTrigger);
      nodes.push(card(trig, trigBox,
        h('p', { class: 'hint', text: 'Для каналу перевірка запускається повідомленням «Світло зникло» (див. «Реагувати на «Світло зникло»»).' })));
    }
    panels.addr.replaceChildren(...nodes);
  }

  // ----- Telegram-style message preview (the only place server html is injected) -----
  const channelName = isPriv ? 'Чат із ботом' : (chat.title || 'Канал');
  function tgPreview() {
    const photo = h('div', { class: 'tgphoto' });
    const text = h('div', { class: 'tgtext' });
    const note = h('p', { class: 'hint' });
    const box = h('div', { class: 'tgmsg' }, h('div', { class: 'tgname', text: channelName }), photo, text,
      h('div', { class: 'tgmeta' }, h('span', { text: '👁 1,2 тис.' }), h('span', { text: '14:24' })));
    const wrap = h('div', { class: 'tgchat' }, box);
    return {
      node: h('div', { class: 'pvbox' }, h('div', { class: 'pvlabel', text: 'Приклад із тестовими даними' }), wrap, note),
      set(st, d, msg) {
        wrap.classList.toggle('loading', st === 'loading' || st === 'stale');
        box.hidden = st === 'idle' || (st === 'err' && !text.childNodes.length);
        note.className = 'hint' + (st === 'err' ? ' err' : ''); note.textContent = st === 'err' ? msg : st === 'idle' ? msg : '';
        if (st === 'loading' && !text.childNodes.length) { photo.replaceChildren(h('div', { class: 'sk sk-img' })); text.replaceChildren(h('div', { class: 'sk sk-line' }), h('div', { class: 'sk sk-line s' })); }
        if (st !== 'ok') return;
        photo.replaceChildren();
        if (d.imageUrl && /^(https?:\/\/|\/)/.test(d.imageUrl)) {
          const img = h('img', { src: d.imageUrl, loading: 'lazy', alt: 'Картинка до повідомлення' });
          img.addEventListener('error', () => img.replaceWith(h('p', { class: 'hint err', text: 'Не вдалося завантажити картинку' })));
          photo.append(img);
        }
        text.innerHTML = d.html || ''; // pre-validated Telegram HTML from the server, whitelisted tags only
      },
    };
  }
  const imageKindOf = id => id === 'schedule' ? s.schedulePostImage : (id === 'off' || id === 'on') ? s.scheduleImage : 'None';
  const previewBody = id => ({
    slot: id, template: customised(slotOf(id), tpls[id]) ? tpls[id] : null, regionId: s.regionId, partIds: partIdsOf(s),
    groupId: groupOf(), kind: KIND_OF[id] || 'Emergency', imageKind: imageKindOf(id),
  });

  // ----- tab: Графік -----
  const pv = { key: '', state: 'idle' };
  let pvSeq = 0;
  const pvx = tgPreview();

  function loadPv() {
    const r = regionOf(s.regionId), g = groupOf();
    if (!r || !r.hasSchedules || !g || !slotOf('schedule')) { pv.key = ''; pv.state = 'idle'; pvx.set('idle', null, 'Оберіть чергу, щоб побачити приклад публікації.'); return; }
    const body = previewBody('schedule'), k = JSON.stringify(body);
    if (k === pv.key && pv.state !== 'err') return;
    pv.key = k; pv.state = 'loading'; pvx.set('loading');
    const my = ++pvSeq;
    api('POST', '/api/preview', body).then(d => {
      if (my !== pvSeq) return;
      if (d.errors && d.errors.length) { pv.state = 'err'; pvx.set('err', null, d.errors.join(' ')); return; }
      pv.state = 'ok'; pvx.set('ok', d);
    }, e => {
      if (my !== pvSeq) return;
      pv.state = 'err'; pvx.set('err', null, e.message);
    });
  }

  const modeNote = (kindKey, get) => h('p', { class: 'hint modenote', text: MODE_TEXT[kindKey][get()] });
  function modeSeg(kindKey, get, set, label) {
    const note = modeNote(kindKey, get);
    return [seg(MODES, get, v => { set(v); note.textContent = MODE_TEXT[kindKey][v]; touch(); }, label), note];
  }
  const imgSeg = (get, set) => seg([['Grid', 'Таблиця на 2 дні'], ['Card', 'Коротка картинка'], ['None', 'Без картинки']], get, v => { set(v); touch(); loadPv(); }, 'Картинка');

  // ----- Світлобот (channels only) -----
  const svb = { hasKey: !!chat.settings.hasSvitlobotKey, loaded: false, loading: false, off: false, busy: '', templates: [], hasSchedule: null, tplId: null, templateGroup: undefined, manualTemplate: false, msg: null, has: false, lock: null };
  s.svbMaybe = true;
  const svbNode = isPriv ? null : h('section', { class: 'card glass', id: 'svb' });
  const keyIn = h('input', { type: 'password', id: 'svbKey', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', placeholder: 'channel_key', 'aria-label': 'Ключ каналу (channel_key)' });
  keyIn.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); svbConnect(); } });
  const svbBase = () => `/api/chats/${encodeURIComponent(chat.chatId)}/svitlobot`;
  const svbText = e => {
    if (e.status === 503) svb.off = true;
    return e.status === 429 ? 'Забагато дій, спробуйте за хвилину' : e.message;
  };
  function svbSyncTemplate() {
    const group = groupOf();
    if (svb.templateGroup !== group) {
      svb.templateGroup = group;
      svb.manualTemplate = false;
    }
    const exists = svb.templates.some(t => String(t.id) === String(svb.tplId));
    if (svb.manualTemplate && exists) return;
    const match = group && svb.templates.find(t => {
      const candidate = String(t.name || '').match(/\b(\d{1,2}\.\d{1,2})\b/);
      return candidate && candidate[1] === group;
    });
    svb.tplId = match ? match.id : (exists ? svb.tplId : svb.templates.length ? svb.templates[0].id : null);
  }
  function svbApply(d) {
    if (!d) return;
    if ('hasKey' in d) {
      svb.hasKey = !!d.hasKey; chat.settings.hasSvitlobotKey = svb.hasKey;
      if (!svb.hasKey) { svb.templates = []; svb.hasSchedule = null; svb.loaded = false; }
    }
    if (Array.isArray(d.templates)) svb.templates = d.templates;
    if ('hasSchedule' in d) svb.hasSchedule = !!d.hasSchedule;
    svbSyncTemplate();
  }
  async function svbLoad() {
    if (svb.loading) return;
    svb.loading = true; svbPaint();
    try { svbApply(await api('GET', svbBase())); svb.loaded = true; } catch (e) { svb.msg = { kind: 'err', text: svbText(e) }; } finally { svb.loading = false; svbPaint(); }
  }
  function svbEnsure() { if (svbNode && svb.lock && svb.hasKey && !svb.loaded && !svb.loading && !svb.off) svbLoad(); }
  async function svbRun(name, call, refetch) {
    if (svb.busy) return;
    svb.busy = name; svb.msg = { kind: 'ok', text: 'Виконую…' }; svbPaint();
    try {
      const d = await call();
      svbApply(d);
      svb.msg = { kind: 'ok', text: (d && d.message) || 'Готово ✓' };
      svb.scrollMsg = true;
      haptic('success');
      if (refetch) { try { svbApply(await api('GET', svbBase())); svb.loaded = true; } catch { /* the action itself succeeded */ } }
    } catch (e) { svb.msg = { kind: 'err', text: svbText(e) }; svb.scrollMsg = true; haptic('error'); } finally { svb.busy = ''; svbPaint(); }
  }
  const svbAsk = ask => new Promise(resolve => {
    try { tg.showConfirm ? tg.showConfirm(ask, resolve) : resolve(confirm(ask)); }
    catch { resolve(confirm(ask)); } // an old Telegram client without showConfirm
  });
  function svbConnect() {
    const key = keyIn.value.trim();
    if (!key) { svb.msg = { kind: 'err', text: 'Введіть ключ каналу.' }; svbPaint(); return; }
    svbRun('key', async () => { const d = await api('PUT', svbBase() + '/key', { key }); keyIn.value = ''; return d; });
  }
  async function svbCheck() {
    // The server only returns a masked value and a short fingerprint, never the key.
    svbRun('check', async () => {
      const d = await api('GET', svbBase() + '/key-check');
      return { message: `Збережений ключ: ${d.masked} · відбиток ${d.fingerprint}` };
    });
  }
  async function svbForget() {
    if (!(await svbAsk('Забути ключ каналу тут? Графік у каналі лишиться, але керувати ним буде неможливо, доки ви знову не введете ключ.'))) return;
    svbRun('forget', () => api('DELETE', svbBase() + '/key'));
  }
  async function svbDelete() {
    if (!(await svbAsk('Видалити розклад у Світлоботі? Допис із графіком у каналі також буде видалено.'))) return;
    svbRun('del', () => api('DELETE', svbBase() + '/schedule'), true);
  }

  function svbPaint(has, lock) {
    if (!svbNode) return;
    if (lock) { svb.has = has; svb.lock = lock; }
    const busy = !!svb.busy;
    const btn = (name, cls, text, fn, off) => h('button', { type: 'button', class: 'btn ' + cls + (svb.busy === name ? ' busy' : ''), text, disabled: busy || !!off, onclick: fn });
    const head = h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Світлобот' }), help('svb'));
    const msg = svb.msg ? h('div', { class: 'toast ' + (svb.msg.kind === 'ok' ? 'ok' : 'err'), role: svb.msg.kind === 'ok' ? 'status' : 'alert', text: svb.msg.text }) : null;
    keyIn.disabled = busy;
    let body;
    if (svb.off) {
      body = [];
    } else if (!svb.hasKey) {
      body = [h('div', { class: 'fld' }, fieldLabel('Ключ каналу (channel_key)', 'svbKey', 'svbKey'), keyIn), btn('key', 'primary', 'Підключити', svbConnect)];
    } else {
      const sel = h('select', { id: 'svbTpl', 'aria-label': 'Готові шаблони', disabled: busy },
        svb.templates.map(t => h('option', { value: String(t.id), text: t.name })));
      sel.value = String(svb.tplId);
      sel.addEventListener('change', () => { svb.tplId = sel.value; svb.manualTemplate = true; });
      const mb = toggle('Враховувати можливі відключення', 'svbMaybe', 'svbMaybe', { disabled: !svb.has });
      mb.cb.disabled = busy || !svb.has;
      mb.cb.addEventListener('change', () => { svb.msg = null; });
      body = [
        h('div', { class: 'svbhead' }, h('span', { class: 'chip green', text: 'Ключ збережено' }),
          h('div', { class: 'fl' }, btn('check', 'ghost sm', 'Перевірити ключ', svbCheck), help('svbCheck')),
          h('div', { class: 'fl' }, btn('forget', 'ghost sm', 'Забути ключ', svbForget), help('svbForget'))),
        h('div', { class: 'svbblk' }, h('div', { class: 'fl big' }, h('span', { text: 'Графік вашої черги' }), help('svbPush')),
          mb, svb.has ? null : svb.lock(),
          btn('push', 'primary', 'Оновити графік у Світлоботі', () => svbRun('push', () => api('POST', svbBase() + '/push', { includeMaybe: !!s.svbMaybe }), true), !svb.has)),
        svb.templates.length ? h('div', { class: 'svbblk' }, fieldLabel('Готові шаблони', 'svbTpl', 'svbTpl'), sel,
          btn('tpl', 'ghost', 'Підключити шаблон', () => svbRun('tpl', () => api('POST', svbBase() + '/template', { templateId: Number(svb.tplId) }), true), svb.tplId == null)) : null,
        h('div', { class: 'svbblk' }, h('div', { class: 'fl big' }, h('span', { text: 'Розклад у Світлоботі' }), help('svbDel')),
          svb.hasSchedule == null ? null : h('p', { class: 'hint', text: svb.hasSchedule ? 'Розклад налаштовано у Світлоботі.' : 'Розкладу ще немає.' }),
          btn('del', 'danger', 'Видалити розклад у Світлоботі', svbDelete)),
      ];
    }
    svbNode.replaceChildren(...[head, msg, ...body].filter(Boolean));
    // The card is long, so the answer sits right under its title and is brought into view.
    if (msg && svb.scrollMsg) {
      svb.scrollMsg = false;
      try { msg.scrollIntoView({ block: 'center', behavior: 'smooth' }); } catch { /* optional */ }
    }
  }

  function renderSched() {
    const r = regionOf(s.regionId);
    if (!r || !r.hasSchedules) {
      pvSeq++; pv.key = '';
      panels.sched.replaceChildren(empty(r ? 'Графіки відключень для цього регіону поки недоступні.' : 'Спершу оберіть регіон на вкладці «Основне».', r ? null : 'main'));
      return;
    }
    const has = !!groupOf(), off = slotOf('off'), on = slotOf('on');
    svbSyncTemplate();
    const lock = () => has ? null : h('div', { class: 'lockhint' }, h('span', { class: 'hint', text: 'Спершу оберіть адресу або чергу.' }),
      h('button', { type: 'button', class: 'btn ghost sm', text: 'Адреса і черга', onclick: () => setTab('addr') }));
    const edit = (id, label) => slotOf(id) ? h('button', { type: 'button', class: 'btn ghost sm', text: label, onclick: () => openSlot(id) }) : null;
    if (!has) {
      // Without a group there is no schedule to preview: one clear step instead of two locked sections.
      pvSeq++; pv.key = '';
      panels.sched.replaceChildren(
        card(
          h('p', { class: 'empty', text: 'Щоб побачити графік і налаштувати публікації, вкажіть свою адресу або чергу.' }),
          h('button', { type: 'button', class: 'btn', text: 'Вказати адресу або чергу', onclick: () => setTab('addr') })),
        ...(svbNode ? [svbNode] : []),
        h('p', { class: 'hint credit', text: CREDIT }));
      svbPaint(has, lock);
      if (tab === 'sched') svbEnsure();
      return;
    }
    panels.sched.replaceChildren(
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Публікація графіка' }), help('schedulePost')),
        toggle('Публікувати графік', 'schedulePost', null, { disabled: !has }), lock(),
        h('div', { class: 'fld' }, fieldLabel('Як публікувати', 'postMode'), ...modeSeg('post', () => s.schedulePostMode, v => { s.schedulePostMode = v; }, 'Як публікувати')),
        h('div', { class: 'fld' }, fieldLabel('Картинка', 'image'), imgSeg(() => s.schedulePostImage, v => { s.schedulePostImage = v; })),
        edit('schedule', 'Редагувати шаблон'),
        pvx.node),
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Події: світло вимкнено / зʼявилося' }), help('events')),
        toggle('Сповіщення за графіком', 'scheduleNotify', null, { disabled: !has }), lock(),
        h('div', { class: 'fld' }, fieldLabel('Як показувати', 'eventMode'), ...modeSeg('event', () => s.scheduleEventMode, v => { s.scheduleEventMode = v; }, 'Як показувати')),
        h('div', { class: 'fld' }, fieldLabel('Картинка', 'image'),
          seg([['Grid', 'Таблиця на 2 дні'], ['Card', 'Коротка картинка'], ['None', 'Без картинки']], () => s.scheduleImage, v => { s.scheduleImage = v; touch(); }, 'Картинка подій')),
        off || on ? h('div', { class: 'btnrow' }, edit('off', 'Редагувати шаблон: ' + (off ? off.title : '')), edit('on', 'Редагувати шаблон: ' + (on ? on.title : ''))) : null),
      ...(svbNode ? [svbNode] : []),
      h('p', { class: 'hint credit', text: CREDIT }));
    svbPaint(has, lock);
    if (tab === 'sched') { loadPv(); svbEnsure(); }
  }

  // ----- tab: Повідомлення (template studio) -----
  const slotBar = h('div', { class: 'slotbar', role: 'tablist', 'aria-label': 'Види повідомлень' });
  const studio = h('div', { class: 'studio' });
  // Editor widgets of the current slot (rebuilt whenever the slot changes).
  let tpl, counter, errBox, resetBtn, paramNote, testBtn, testOut, prevx;
  const previewCache = {};

  function renderPills() {
    const groups = [...SLOT_GROUPS, ...[...new Set(slots.map(x => x.group))].filter(g => !SLOT_GROUPS.some(y => y[0] === g)).map(g => [g, g])];
    slotBar.replaceChildren(...groups.map(([g, title]) => {
      const list = slots.filter(x => x.group === g);
      if (!list.length) return null;
      return h('div', { class: 'sgrp', role: 'presentation' }, h('div', { class: 'sgh', text: title }),
        h('div', { class: 'sgp' }, list.map(x => h('button', {
          type: 'button', role: 'tab', class: 'spill' + (x.id === cs ? ' on' : ''), id: 'slot-' + x.id, 'aria-selected': String(x.id === cs),
          onclick: () => openSlot(x.id, true),
        }, h('span', { text: x.title }), isCustom(x.id) ? h('i', { class: 'pd', title: 'Змінений шаблон' }) : null,
          slotErrs(x.id).length ? h('i', { class: 'rd2', title: 'Є помилки' }) : null))));
    }));
  }

  function openSlot(id, stay) {
    cs = id;
    renderPills(); renderStudio();
    if (!stay) setTab('msg');
    try { document.getElementById('slot-' + id).scrollIntoView({ inline: 'center', block: 'nearest' }); } catch { /* optional */ }
    runPreview();
  }

  function paintSlotErrors() {
    if (!errBox) return;
    errBox.replaceChildren(...slotErrs(cs).map(t => h('div', { text: t })));
  }

  function onEdit() {
    delete serr[cs];
    counter.textContent = `${tpl.value.length} / ${boot.maxTemplateLength}`;
    resetBtn.disabled = !isCustom(cs);
    renderPills(); paintSlotErrors(); setErr([]); updateUi();
    clearTimeout(timer);
    timer = setTimeout(runPreview, 300);
  }

  // Insert text at the caret (replacing the selection); `caret` is the offset inside the inserted text to place the cursor at.
  function put(text, caret) {
    const a = tpl.selectionStart ?? tpl.value.length, b = tpl.selectionEnd ?? a;
    if (tpl.value.length - (b - a) + text.length > boot.maxTemplateLength) return false;
    tpl.setRangeText(text, a, b, 'end');
    if (caret != null) tpl.setSelectionRange(a + caret, a + caret);
    tpl.focus();
    tpl.dispatchEvent(new Event('input'));
    return true;
  }
  function renderStudio() {
    const x = slotOf(cs);
    if (!x) { studio.replaceChildren(card(h('p', { class: 'empty', text: 'Шаблони недоступні.' }))); return; }
    tpl = h('textarea', { maxlength: boot.maxTemplateLength, rows: 4, spellcheck: 'false', 'aria-label': 'Шаблон: ' + x.title, id: 'tpl' });
    tpl.value = tpls[cs];
    counter = h('div', { class: 'counter' });
    errBox = h('div', { class: 'errs', role: 'alert' });
    paramNote = h('p', { class: 'hint pnote', 'aria-live': 'polite', text: 'Торкніться параметра, щоб вставити його в текст.' });
    resetBtn = h('button', { type: 'button', class: 'btn ghost sm', text: 'Скинути до стандартного', onclick: () => { tpl.value = x.defaultTemplate; tpls[cs] = x.defaultTemplate; onEdit(); tpl.focus(); } });
    resetBtn.disabled = !isCustom(cs);
    tpl.addEventListener('input', () => { tpls[cs] = tpl.value; onEdit(); });
    counter.textContent = `${tpl.value.length} / ${boot.maxTemplateLength}`;

    const keep = e => e.preventDefault(); // keep the caret in the textarea when tapping a parameter chip
    const note = p => { paramNote.textContent = `{${p.name}} — ${p.description}${p.example ? '. Приклад: ' + p.example : ''}`; };
    const chips = h('div', { class: 'chips pchips' }, x.placeholders.map(p => h('button', {
      type: 'button', class: 'chip mono', title: p.description, 'aria-label': `{${p.name}} — ${p.description}`, onmousedown: keep,
      onclick: () => { put('{' + p.name + '}'); note(p); },
      onfocus: () => note(p),
    }, h('span', { text: '{' + p.name + '}' }))));

    const legend = h('details', { class: 'fold' }, h('summary', { text: 'Що підставляється' }),
      x.placeholders.map(p => h('div', { class: 'ph' }, h('code', { text: '{' + p.name + '}' }), ' — ' + p.description, p.example ? h('small', { text: 'У прикладі: ' + p.example }) : null)),
      h('div', { class: 'ph', text: 'Теги: <b>, <i>, <u>, <s>, <code>, <pre>, <a href>, <tg-spoiler>, <blockquote>. Фігурні дужки: {{ і }}.' }));

    prevx = tgPreview();
    testOut = h('div', { class: 'testout', 'aria-live': 'polite' });
    testBtn = h('button', { type: 'button', class: 'btn', onclick: sendTest });
    setTestLabel();

    const ss = s.slotSettings[x.id];
    let slotOpts = null;
    if (ss) {
      const swId = 'sw' + (++uid), isEnd = x.id === 'ended';
      const cb = h('input', { type: 'checkbox', role: 'switch', id: swId });
      cb.checked = ss.enabled;
      const wrap = h('div', { class: 'tawrap' + (ss.enabled ? '' : ' dim') }, tpl, counter);
      const endNote = h('p', { class: 'hint modenote' });
      const endFld = isEnd ? h('div', { class: 'fld endstyle' + (ss.enabled ? '' : ' dim') }, fieldLabel('Як показувати', 'slotEndStyle'),
        seg(END_STYLES.map(e => [e[0], e[1]]), () => ss.endStyle, v => { ss.endStyle = v; paintEndNote(); touch(); }, 'Як показувати', 'stack'), endNote) : null;
      function paintEndNote() { endNote.textContent = END_STYLES.find(e => e[0] === ss.endStyle)[2]; }
      if (isEnd) paintEndNote();
      cb.addEventListener('change', () => { ss.enabled = cb.checked; wrap.classList.toggle('dim', !ss.enabled); if (endFld) endFld.classList.toggle('dim', !ss.enabled); touch(); });
      slotOpts = h('div', { class: 'slotopts glass' },
        h('div', { class: 'row' }, fieldLabel(isEnd ? 'Сповіщати про зняття аварійного запису' : 'Надсилати це сповіщення', isEnd ? 'slotEnded' : 'slotOn', swId),
          h('label', { class: 'sw' }, cb, h('i', { class: 'trk' }))),
        endFld,
        isEnd ? null : h('div', { class: 'fld' }, fieldLabel('Оновлення', 'slotEdit'),
          seg([[true, 'Редагувати те саме повідомлення'], [false, 'Щоразу нове повідомлення']], () => ss.editInPlace, v => { ss.editInPlace = v; touch(); }, 'Оновлення')));
      slotOpts.wrap = wrap;
    }

    studio.replaceChildren(
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: x.title }), help('template')),
        h('p', { class: 'hint', text: x.description }),
        slotOpts,
        slotOpts ? slotOpts.wrap : h('div', { class: 'tawrap' }, tpl, counter), errBox,
        h('div', { class: 'fl sm' }, h('span', { text: 'Параметри' }), help('cond')), chips, paramNote,
        prevx.node,
        h('div', { class: 'testrow' }, testBtn, help('test')), testOut),
      card(resetBtn, legend));
    paintSlotErrors();
    const c = previewCache[cs];
    if (c) prevx.set('ok', c);
  }
  const setTestLabel = () => { if (testBtn) testBtn.textContent = isPriv ? 'Надіслати тест у цей чат' : 'Надіслати тест у канал'; };

  async function runPreview() {
    const x = slotOf(cs);
    if (!x || !prevx) return;
    const id = cs, my = ++seq;
    prevx.set(previewCache[id] ? 'stale' : 'loading');
    try {
      const r = await api('POST', '/api/preview', previewBody(id));
      if (my !== seq || id !== cs) return; // stale response
      perr[id] = r.errors || [];
      if (!perr[id].length) { previewCache[id] = r; prevx.set('ok', r); } else prevx.set(previewCache[id] ? 'stale' : 'idle', null, 'Виправте помилки, щоб побачити приклад.');
    } catch (e) {
      if (my !== seq || id !== cs) return;
      perr[id] = [];
      prevx.set('err', null, 'Не вдалося отримати попередній перегляд: ' + e.message);
    }
    paintSlotErrors(); renderPills(); updateUi();
    testBtn.disabled = slotErrs(id).length > 0 || testBusy;
  }

  let testBusy = false;
  async function sendTest() {
    const id = cs, x = slotOf(id);
    if (!x || testBusy) return;
    testBusy = true; testBtn.disabled = true; testBtn.classList.add('busy'); testOut.replaceChildren();
    const fail = lines => testOut.replaceChildren(...lines.map(t => h('div', { class: 'note err', role: 'alert', text: t })));
    try {
      if (isDirty()) {
        const err = await doSave(true);
        if (err) { fail(err.lines || errorText(err)); return; }
      }
      const imageKind = id === 'schedule' || id === 'off' || id === 'on' ? imageKindOf(id) : null;
      await api('POST', `/api/chats/${encodeURIComponent(chat.chatId)}/templates/${encodeURIComponent(id)}/send`, { template: customised(x, tpls[id]) ? tpls[id] : null, imageKind });
      haptic('success');
      testOut.replaceChildren(h('div', { class: 'toast ok', role: 'status', text: 'Надіслано ✓' }));
      setTimeout(() => testOut && testOut.querySelector('.toast') && testOut.replaceChildren(), 4000);
    } catch (e) {
      fail([e.status === 429 ? 'Забагато тестів. ' + e.message : e.message]);
    } finally {
      testBusy = false; testBtn.classList.remove('busy'); testBtn.disabled = slotErrs(cs).length > 0;
    }
  }
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
    card(h('div', { class: 'fl big sm' }, h('span', { text: 'Шаблони повідомлень' }), help('slots')),
      h('div', { class: 'slotwrap' }, slotBar)),
    studio,
    ...(removeBtn ? [card(h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Керування' }), help('remove')), removeBtn)] : []));
  renderPills(); renderStudio();

  // ----- wiring -----
  function updateUi() {
    const d = isDirty(), bad = blocking();
    saveBtn.disabled = busy || bad || !d;
    const mode = bad ? 'err' : d ? 'dirty' : 'ok';
    bar.dataset.mode = mode;
    stTxt.textContent = mode === 'err' ? 'Виправте помилки шаблону' : mode === 'dirty' ? 'Є незбережені зміни' : 'Збережено ✓';
    rd.hidden = !anyErr();
    const [c, t] = statusOf(s);
    topPill.className = 'pill ' + c; topPill.textContent = t;
  }

  function refreshAll() { renderDistricts(); syncStatus(); renderHint(); renderAddr(); renderSched(); updateUi(); if (tab === 'msg') runPreview(); }

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
    card(h('p', { class: 'msg', text: 'Відкрийте цей застосунок із Telegram.' })));
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
