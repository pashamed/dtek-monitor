// Lite settings editor for the DTEK alert bot (vanilla JS, no deps).
const tg = window.Telegram && window.Telegram.WebApp;
const app = document.getElementById('app');
const initData = tg ? tg.initData : '';
const UI_VERSION = new URL(import.meta.url).searchParams.get('v') || 'dev';
const APP_VERSION = (() => {
  const value = document.querySelector('meta[name="app-version"]')?.content || '';
  return value && !value.startsWith('__') ? value : 'dev';
})();

const SLOT_GROUPS = [['alerts', 'Екстрені відключення'], ['local', 'Локальні відключення'], ['stabilization', 'Графік (стабілізація)']];
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
const TABS = [['main', 'Основне'], ['sched', 'Графік'], ['msg', 'Повідомлення']];
const EDITOR_POSITION_KEY = 'dtek-editor-position';
const state = { boot: null, chats: [], editing: null };
const groupsCache = new Map(); // regionId -> Promise of schedule groups
const PRIVATE_LABEL = 'Цей чат із ботом';
const CREDIT = 'Дані графіків: outage-data-ua (ДТЕК).';

const HELP = {
  region: ['Регіон', 'Регіон, за яким бот стежить за відключеннями. Від нього залежать доступні адреси, черги та графіки.'],
  parts: ['Райони', 'Оберіть принаймні один район, щоб отримувати екстрені сповіщення, у яких ДТЕК називає район. Можна обрати всі райони.'],
  status: ['Поточний стан', 'Показує, що зараз повідомляє сайт ДТЕК для вашого регіону та що кажуть графіки для вашої черги.'],
  check: ['Світла немає?', 'Бот перевірить причину: спершу графік вашої черги, потім загальне повідомлення ДТЕК про екстрені відключення чи аварію, і лише потім адресу на сайті ДТЕК. Результат також надійде вам у чат із ботом.'],
  active: ['Підписка активна', 'Поки підписка активна, бот надсилає сюди сповіщення. Після збереження вимкненої підписки сповіщення припиняться, а збережені адреси буде видалено.'],
  pause: ['Пауза', 'Тимчасово зупиняє власну підписку цього чату, не видаляючи адреси. Копії з каналів — додаткові: їх зупиняє пауза в каналі або видалення каналу зі списку.'],
  slotOn: ['Надсилати це сповіщення', 'Якщо вимкнути, бот не надсилатиме цей вид сповіщень у цей чат. Шаблон можна й надалі редагувати.'],
  slotEnded: ['Сповіщати про зняття аварійного запису', 'Коли екстрений запис зникає з сайту ДТЕК, бот повідомляє про це (спосіб обираєте нижче). Можна вимкнути лише це повідомлення, а саме сповіщення й його оновлення залишаться. Зникнення запису не означає, що світло вже повернулося.'],
  slotEndStyle: ['Як показувати', 'Відповіддю на сповіщення — бот відповідає на початкове сповіщення (так за замовчуванням). Окремим повідомленням — надсилається нове повідомлення без привʼязки до початкового. Змінити початкове повідомлення — його текст замінюється на повідомлення про зняття; якщо повідомлення вже видалено або його не можна змінити, бот відповість на нього новим.'],
  slotEdit: ['Оновлення', 'Редагувати те саме повідомлення — одне повідомлення оновлюється на місці. Щоразу нове — кожна зміна приходить окремим повідомленням зі сповіщенням.'],
  addrManual: ['Адреса для перевірки', 'Вкажіть населений пункт (місто, село чи селище), вулицю та номер будинку так, як вони написані на сайті ДТЕК: з «м.», «с.» або «смт», з «вул.» або «просп.», і номер будинку в тому ж вигляді (наприклад 12 або 12А). Бот сам не може перевірити написання: чи знайшов він адресу, стане видно після першої перевірки.'],
  address: ['Адреса', 'Потрібна, щоб визначити вашу чергу й перевіряти саме вашу адресу на сайті ДТЕК. Зберігається, поки діє підписка, і видаляється при відписці.'],
  group: ['Черга', 'Номер черги в графіку відключень, наприклад 45.1. Бот підставляє її, якщо в базі є одна відома черга для цієї вулиці; якщо її немає або черг кілька, оберіть вручну. Від черги залежать повідомлення за графіком і сповіщення про екстрені відключення черги.'],
  outage: ['Реагувати на «Світло зникло»', 'Якщо застосунок моніторингу світла пише в канал «Світло зникло», бот знайде причину: спершу перевіряє графік черги, потім загальне повідомлення ДТЕК про екстрені відключення чи аварію, і лише тоді адресу на сайті ДТЕК.'],
  triggerText: ['Текст повідомлення «світла немає»', 'Це текст, який ваш застосунок моніторингу світла пише в канал, коли світло зникає, наприклад «зараз світло зникло». Регістр не враховується. Можна вказати регулярний вираз, наприклад світло\\s+(зникло|вимкнули). Бот порівнює його з новими публікаціями каналу й перевіряє адресу.'],
  image: ['Картинка', 'Таблиця на 2 дні — графік на сьогодні й завтра. Коротка картинка — картка з годинами відключень. Без картинки — лише текст.'],
  schedulePost: ['Публікація графіка', 'Бот публікує в канал окремий пост із графіком відключень вашої черги (картинка з підписом) і оновлює його, коли графік змінюється. Візуальний графік також можна надіслати негайно окремою кнопкою на початку цього розділу. Видалення попереднього повідомлення потребує права «Видаляти повідомлення».'],
  scheduleReminder: ['Попередити перед відключенням', 'Надсилає окреме повідомлення один раз за вибраний час до запланованого початку відключення вашої черги. Час перевірки залежить від оновлення графіків (зазвичай кожні кілька хвилин). Текст редагується у вкладці «Повідомлення».'],
  quietHours: ['Тихі години', 'Автоматичні повідомлення надходитимуть одразу, але без звукового сповіщення протягом цього щоденного проміжку за київським часом. Ручні дії та тестові повідомлення не приглушуються.'],
  postMode: ['Як публікувати', 'Нове повідомлення — кожне оновлення графіка це новий пост, старі лишаються. Видалити старе й опублікувати нове — бот публікує новий пост і видаляє попередній (потрібне право «Видаляти повідомлення», інакше старий пост лишиться). Редагувати те саме — бот змінює один і той самий пост.'],
  slots: ['Шаблони повідомлень', 'Кожен вид повідомлень, які бот публікує, має свій шаблон. Оберіть вид, змініть текст і стиль та перегляньте, як це виглядатиме в Telegram. Змінені шаблони позначені фіолетовою цяткою.'],
  alertCleanup: ['Видаляти попереднє аварійне сповіщення', 'Після надсилання нового повідомлення про зміну аварійного стану бот спробує видалити попереднє. Для каналів потрібне право видаляти повідомлення; Telegram дозволяє видаляти їх лише протягом 48 годин. Старі повідомлення не очищаються під час увімкнення опції. Графіки налаштовуються окремо.'],
  template: ['Шаблон повідомлення', 'Ваш текст для цього виду повідомлень. Параметри у {фігурних дужках} замінюються даними, теги Telegram дозволені: <b>, <i>, <u>, <s>, <code>, <pre>, <a href>, <tg-spoiler>, <blockquote>. Щоб вивести фігурні дужки, пишіть {{ і }}.'],
  cond: ['Умовні блоки', 'Фрагмент {#назва}…{/назва} лишається в тексті, лише якщо значення параметра «назва» не порожнє. Наприклад: {#світло_було}🕓 Воно було {світло_було}{/світло_було}. Блоки не можна вкладати.'],
  test: ['Тестове повідомлення', 'Бот надішле це повідомлення в канал (або в чат із ботом) зі зразковими чи вашими даними й позначкою 🧪 Тест, щоб ви побачили його справжній вигляд. Кількість тестів обмежена: якщо зʼявиться повідомлення про ліміт, зачекайте трохи й повторіть.'],
  connect: ['Підключити канал', 'Додайте бота адміністратором у канал із правом публікувати повідомлення — канал зʼявиться тут автоматично (за потреби натисніть «Оновити список»). Підтримуються лише канали, не групи. Для публічного каналу також можна вставити https://t.me/імʼя або @імʼя. Приватні посилання-запрошення (t.me/+…) не підходять.'],
  svb: ['Світлобот', 'Ключ каналу потрібен, щоб бот міг регулярно оновлювати тижневий графік Світлоботу для обраної черги. Коли дані графіка зʼявляються або змінюються, бот передає актуальний тижневий розклад автоматично. Повідомлення «світло зникло / зʼявилося» та публікаціями у каналі керує сам Світлобот. Ключ зберігається на сервері в зашифрованому вигляді; нікому його не передавайте.'],
  svbKey: ['Ключ каналу', 'Ключ channel_key потрібен, щоб передавати актуальний тижневий графік у Світлобот. Він доступний у налаштуваннях вашого каналу в чат-боті Світлобот. Бот перевірить ключ, збереже його зашифрованим і більше не покаже.'],
  svbCheck: ['Перевірити ключ', 'Показує, який саме ключ збережено, не розкриваючи його: вісім крапок і останні два символи, а також короткий «відбиток». Відбиток однаковий для того самого ключа, тож ним можна звірити ключ зі Світлобота. Сам ключ ніколи не повертається.'],
  svbForget: ['Забути ключ', 'Видаляє збережений ключ лише тут, у боті. Графік і пост у каналі лишаються як є. Щоб знову керувати Світлоботом, введіть ключ ще раз.'],
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

function versionFooter() {
  return h('footer', { class: 'ui-version', text: 'Версія застосунку: ' + APP_VERSION + ' · Версія інтерфейсу: ' + UI_VERSION });
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
  const { back, btn, dispose } = sheet;
  sheet = null;
  if (dispose) dispose();
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

// The viewer has no subscription state or write actions; closing it preserves the editor draft.
function scheduleViewerButton(regionId, groupId) {
  const btn = h('button', { type: 'button', class: 'btn', text: 'Переглянути графік', 'aria-haspopup': 'dialog', 'aria-expanded': 'false' });
  btn.addEventListener('click', () => openScheduleViewer(btn, regionId, groupId));
  return btn;
}

function openScheduleViewer(btn, initialRegion, initialGroup) {
  closeSheet(true);
  let request = 0, tick = 0, disposed = false;
  const regions = state.boot.regions.filter(r => r.hasSchedules);
  const region = h('select', { id: 'viewer-region', 'aria-label': 'Регіон для перегляду' },
    regions.map(r => h('option', { value: r.id, text: r.name })));
  region.value = regions.some(r => r.id === initialRegion) ? initialRegion : regions[0]?.id || '';
  const group = h('select', { id: 'viewer-group', 'aria-label': 'Черга для перегляду', disabled: true });
  const output = h('div', { class: 'stlines', role: 'status', 'aria-live': 'polite' });
  const retry = h('button', { type: 'button', class: 'btn ghost sm', text: 'Оновити графік' });
  const close = h('button', { type: 'button', class: 'btn', text: 'Закрити перегляд', onclick: () => closeSheet() });
  const content = h('div', { class: 'sheet glass', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'viewer-title' },
    h('h2', { id: 'viewer-title', text: 'Переглянути графік' }),
    h('p', { class: 'hint', text: 'Лише перегляд плану на сьогодні й завтра. Без збереження підписки та без публікацій. Графік не підтверджує фактичну наявність світла.' }),
    h('div', { class: 'fld' }, h('label', { for: region.id, text: 'Регіон' }), region),
    h('div', { class: 'fld' }, h('label', { for: group.id, text: 'Черга' }), group),
    output, retry, h('p', { class: 'hint credit', text: CREDIT }), close);
  const back = h('div', { class: 'backdrop', onclick: e => { if (e.target === back) closeSheet(); } }, content);
  sheet = { back, btn, dispose: () => { disposed = true; request++; clearInterval(tick); } };
  btn.setAttribute('aria-expanded', 'true');
  document.body.append(back);
  const valid = my => !disposed && my === request;
  const time = value => new Date(value).toLocaleTimeString('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const date = value => new Date(value).toLocaleString('uk-UA', { timeZone: 'Europe/Kyiv' });
  const dayKey = value => {
    const parts = new Intl.DateTimeFormat('en', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date(value));
    const get = key => parts.find(p => p.type === key).value;
    return `${get('year')}-${get('month')}-${get('day')}`;
  };
  async function loadPlan() {
    const my = ++request;
    clearInterval(tick);
    if (!region.value || !group.value) return;
    retry.disabled = true;
    output.replaceChildren(h('p', { text: 'Завантажую графік…' }));
    try {
      const d = await api('GET', '/api/schedules/view?regionId=' + encodeURIComponent(region.value) + '&groupId=' + encodeURIComponent(group.value));
      if (!valid(my)) return;
      const countdown = h('p', { class: 'hint' });
      const today = dayKey(d.serverNow);
      output.replaceChildren(
        h('h3', { text: `${d.regionName} · черга ${d.groupId}` }),
        ...(d.warnings || []).map(text => h('p', { class: 'note warn', role: 'alert', text })),
        d.warnings?.length ? h('p', { class: 'note warn', text: 'Під час аварійних або екстрених відключень план може не діяти. Відлік нижче — лише за планом, не прогноз фактичного світла.' }) : h('p', { class: 'hint', text: 'Час наведено за Києвом.' }),
        h('p', { class: 'hint', text: 'Перевірено джерело: ' + date(d.checkedAt) + ' (час Києва).' }),
        ...(d.isCached ? [h('p', { class: 'note warn', text: 'Кешовані дані: джерело зараз недоступне або ще не оновилося. План може змінитися.' })] : []),
        ...(d.days || []).map(day => h('section', {},
          h('h3', { text: `${day.date === today ? 'Сьогодні' : 'Завтра'} · ${day.date}` }),
          !day.available ? h('p', { class: 'note warn', text: 'Повного графіка на цей день ще немає. Це не означає відсутність відключень.' })
            : day.intervals.length ? h('ul', {}, day.intervals.map(i => h('li', { text: 'Планове відключення: ' + time(i.start) + '–' + (dayKey(i.end) > day.date ? '24:00' : time(i.end)) })))
              : h('p', { text: 'За опублікованим планом відключень немає.' }))),
        countdown);
      const received = performance.now(), serverNow = Date.parse(d.serverNow), next = Date.parse(d.nextChangeAt);
      const paintCountdown = () => {
        if (!d.nextChangeAt || !Number.isFinite(next) || !Number.isFinite(serverNow) || d.nextChangeIsOff == null) {
          countdown.textContent = 'Наступну планову зміну не визначено.';
          return;
        }
        const seconds = Math.ceil((next - serverNow - (performance.now() - received)) / 1000);
        countdown.textContent = seconds <= 0 ? 'Час планової зміни настав. Оновіть графік; фактичний стан невідомий.'
          : `До планового ${d.nextChangeIsOff ? 'відключення' : 'завершення відключення'}: ${Math.floor(seconds / 3600)} год ${Math.floor(seconds % 3600 / 60)} хв ${seconds % 60} с.`;
        if (seconds <= 0) clearInterval(tick);
      };
      paintCountdown();
      if (d.nextChangeAt && next > serverNow) tick = setInterval(paintCountdown, 1000);
    } catch (e) {
      if (valid(my)) output.replaceChildren(h('p', { class: 'msg err', role: 'alert', text: errorText(e).join(' ') }));
    } finally {
      if (valid(my)) retry.disabled = false;
    }
  }
  async function loadViewerGroups(preferred) {
    const my = ++request;
    clearInterval(tick);
    group.replaceChildren(); group.disabled = true; retry.disabled = true;
    if (!region.value) {
      output.replaceChildren(h('p', { text: 'Підтримуваних регіонів поки немає.' }));
      return;
    }
    output.replaceChildren(h('p', { text: 'Завантажую черги…' }));
    try {
      const groups = await loadGroups(region.value);
      if (!valid(my)) return;
      group.replaceChildren(...groups.map(g => h('option', { value: g.id, text: g.name || g.id })));
      if (groups.some(g => g.id === preferred)) group.value = preferred;
      group.disabled = !groups.length;
      if (groups.length) await loadPlan();
      else output.replaceChildren(h('p', { text: 'Для цього регіону черг поки немає.' }));
    } catch (e) {
      if (valid(my)) output.replaceChildren(h('p', { class: 'msg err', role: 'alert', text: errorText(e).join(' ') }));
    } finally {
      if (valid(my)) retry.disabled = false;
    }
  }
  region.addEventListener('change', () => loadViewerGroups());
  group.addEventListener('change', loadPlan);
  retry.addEventListener('click', () => group.value ? loadPlan() : loadViewerGroups());
  content.addEventListener('keydown', e => {
    if (e.key !== 'Tab') return;
    const controls = [...content.querySelectorAll('button, select')].filter(el => !el.disabled);
    if (e.shiftKey && document.activeElement === controls[0]) { e.preventDefault(); controls.at(-1).focus(); }
    else if (!e.shiftKey && document.activeElement === controls.at(-1)) { e.preventDefault(); controls[0].focus(); }
  });
  close.focus();
  loadViewerGroups(initialGroup);
}

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

function readEditorPosition() {
  try {
    const saved = JSON.parse(localStorage.getItem(EDITOR_POSITION_KEY) || 'null');
    return saved && typeof saved.chatId === 'string' && typeof saved.tab === 'string' ? saved : null;
  } catch (error) {
    console.warn('Could not restore the last editor position.', error);
    return null;
  }
}

function saveEditorPosition(chatId, tab) {
  try {
    localStorage.setItem(EDITOR_POSITION_KEY, JSON.stringify({ chatId: String(chatId), tab }));
  } catch (error) {
    console.warn('Could not save the editor position.', error);
  }
}

function confirmSetting(message) {
  return new Promise(resolve => {
    try {
      if (tg && tg.showConfirm) tg.showConfirm(message, resolve);
      else resolve(window.confirm(message));
    } catch (error) {
      console.warn('Could not show the setting confirmation.', error);
      resolve(false);
    }
  });
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
    card(scheduleViewerButton(), h('p', { class: 'hint', text: 'Графік будь-якої підтримуваної черги без підключення каналу.' })),
    h('div', { class: 'sechead' }, h('h2', { text: 'Ваші канали' }), refresh),
    state.chats.length ? state.chats.map(chatCard) : card(h('p', { class: 'hint', text: 'Каналів поки немає. Підключіть перший за інструкцією нижче.' })),
    card(
      h('div', { class: 'fl big' }, h('h2', { class: 'ct', text: 'Підключити канал' }), help('connect')),
      h('ol', { class: 'steps' },
        h('li', { text: 'Додайте бота адміністратором у ваш канал з правом публікувати повідомлення.' }),
        h('li', { text: 'Канал зʼявиться тут автоматично.' }),
        h('li', { text: 'Оберіть регіон і адресу.' })),
      h('div', { class: 'or', text: 'Або вставте посилання t.me чи @імʼя публічного каналу' }),
      h('div', { class: 'linkrow' }, input, btn), msg),
    versionFooter());
}

// ---------- chat editor ----------
function renderEditor(chat) {
  const { boot } = state;
  const isPriv = !!chat.isPrivate;
  const slots = (boot.templateSlots || []).filter(x => x.id !== 'off' && x.id !== 'on');
  const hiddenEventTemplates = Object.fromEntries(
    ['off', 'on'].filter(id => chat.settings.templates && chat.settings.templates[id])
      .map(id => [id, chat.settings.templates[id]]));
  const slotOf = id => slots.find(x => x.id === id) || null;
  const s = {
    ...chat.settings, partIds: [...(chat.settings.partIds || [])],
    addresses: JSON.parse(JSON.stringify(chat.settings.addresses?.length ? chat.settings.addresses
      : chat.settings.groupId || chat.settings.addressLabel
        ? [{ id: 0, regionId: chat.settings.regionId, name: 'Дім', city: chat.settings.addressCity || '', street: '', house: '', label: chat.settings.addressLabel || '', groups: chat.settings.groupId ? [{ groupId: chat.settings.groupId }] : [] }]
        : [])),
    scheduleImage: chat.settings.scheduleImage || 'None', schedulePostImage: chat.settings.schedulePostImage || 'Grid',
    schedulePostMode: chat.settings.schedulePostMode || 'New', scheduleEventMode: chat.settings.scheduleEventMode || 'Edit',
    deletePreviousAlert: !!chat.settings.deletePreviousAlert,
    quietHoursEnabled: !!chat.settings.quietHoursEnabled,
    quietHoursStartMinute: Number.isInteger(chat.settings.quietHoursStartMinute) ? chat.settings.quietHoursStartMinute : 1320,
    quietHoursEndMinute: Number.isInteger(chat.settings.quietHoursEndMinute) ? chat.settings.quietHoursEndMinute : 480,
    slotSettings: slotSettingsOf(chat.settings),
  };
  state.editing = chat;
  try { tg.BackButton.show(); } catch { /* optional */ }
  const savedPosition = readEditorPosition();
  let seq = 0, timer = 0, busy = false,
    tab = savedPosition?.chatId === String(chat.chatId) && TABS.some(([id]) => id === savedPosition.tab) ? savedPosition.tab : 'main',
    uid = 0, cs = slots.length ? slots[0].id : null;
  // Per-slot editor state: current text (the default text when not customised), errors from preview / from the server.
  const tpls = Object.fromEntries(slots.map(x => [x.id, (chat.settings.templates || {})[x.id] || x.defaultTemplate]));
  const perr = {}, serr = {};

  const groupOf = () => s.addresses.flatMap(a => a.groups || []).map(g => g.groupId).sort()[0] || null;
  const customised = (x, v) => !!(x && v && v.trim() && v !== x.defaultTemplate);
  const isCustom = id => customised(slotOf(id), tpls[id]);
  const tplMap = get => ({
    ...hiddenEventTemplates,
    ...Object.fromEntries(slots.filter(x => customised(x, get(x.id))).map(x => [x.id, get(x.id)])),
  });
  // The API validates and commits settings, region and address drafts together.
  const form = (src, templates) => {
    const slotSettings = slotSettingsOf(src);
    return {
      regionId: src.regionId || null, partIds: partIdsOf(src), isActive: !!src.isActive, isPaused: !!src.isPaused,
      addresses: src.regionId ? src.addresses.map(a => ({ ...a, label: [a.city, a.street, a.house].filter(Boolean).join(', ') || a.label || null })) : null,
      slotSettings, notifyOnEnd: slotSettings.ended.enabled, editInPlace: slotSettings.emergency.editInPlace, // the two mirror the old fields for older servers
      outageTrigger: !!src.outageTrigger, outageTriggerPattern: (src.outageTriggerPattern || '').trim() || null, templates,
      scheduleNotify: !!src.scheduleNotify, scheduleImage: src.scheduleImage || 'None', scheduleEventMode: src.scheduleEventMode || 'Edit',
      scheduleReminder: !!src.scheduleReminder, scheduleReminderMinutes: Number(src.scheduleReminderMinutes) || 15,
      schedulePost: !!src.schedulePost, schedulePostMode: src.schedulePostMode || 'New', schedulePostImage: src.schedulePostImage || 'Grid',
      deletePreviousAlert: !!src.deletePreviousAlert,
      quietHoursEnabled: !!src.quietHoursEnabled,
      quietHoursStartMinute: Number(src.quietHoursStartMinute), quietHoursEndMinute: Number(src.quietHoursEndMinute),
    };
  };

  const cur = () => form(s, tplMap(id => tpls[id]));
  let base = JSON.stringify(form(s, tplMap(id => (chat.settings.templates || {})[id])));
  const isDirty = () => JSON.stringify(cur()) !== base;
  const slotErrs = id => [...(perr[id] || []), ...(serr[id] || [])];
  const blocking = () => Object.values(perr).some(a => a && a.length);
  const districtSelectionError = () => {
    const r = regionOf(s.regionId);
    return s.isActive && r && r.parts.length > 0 && partIdsOf(s).length === 0
      ? 'Оберіть принаймні один район для активної підписки.'
      : null;
  };
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
  const cancelBtn = h('button', { type: 'button', class: 'btn ghost sm', text: 'Скасувати', onclick: async () => {
    if (!isDirty() || await confirmSetting('Скасувати всі незбережені зміни? Збережені адреси й налаштування залишаться без змін.')) renderEditor(chat);
  } });
  const bar = h('div', { class: 'savebar glass' }, h('div', { class: 'savein' }, errLine,
    h('div', { class: 'saverow' }, h('div', { class: 'stat', role: 'status' }, stDot, stTxt), cancelBtn, saveBtn)));
  const setErr = lines => errLine.replaceChildren(...lines.map(t => h('div', { text: t })));

  function setTab(id) {
    tab = id;
    saveEditorPosition(chat.chatId, id);
    TABS.forEach(([t], i) => {
      panels[t].hidden = t !== id;
      tabBtns[i].setAttribute('aria-selected', t === id);
      tabBtns[i].tabIndex = t === id ? 0 : -1;
    });
    window.scrollTo(0, 0);
    try { tabBtns[TABS.findIndex(x => x[0] === id)].scrollIntoView({ inline: 'center', block: 'nearest' }); } catch { /* optional */ }
    if (id === 'sched') loadPv();
    if (id === 'msg') runPreview();
  }

  // ----- widgets bound to form state -----
  function toggle(title, key, helpKey, opts = {}) {
    const id = 'sw' + (++uid);
    const cb = h('input', { type: 'checkbox', role: 'switch', id, disabled: !!opts.disabled });
    cb.checked = !!s[key] && !opts.disabled;
    cb.addEventListener('change', async () => {
      const value = cb.checked;
      if (value && opts.confirm) {
        cb.checked = false;
        cb.disabled = true;
        const confirmed = await confirmSetting(opts.confirm);
        cb.disabled = !!opts.disabled;
        if (!confirmed) return;
        cb.checked = true;
      }
      s[key] = value;
      touch();
    });
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
  const addressPanel = h('div', { id: 'addressPanel' });
  const activeRow = toggle('Підписка активна', 'isActive', 'active');

  // Districts: active subscriptions with district options must select at least one.
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
    dList.replaceChildren(chip('Обрати всі райони', sel.size === r.parts.length, () => { s.partIds = r.parts.map(p => p.id); touch(); renderDistricts(); syncStatus(); }),
      ...r.parts.filter(p => !q || p.name.toLowerCase().includes(q)).map(p => chip(p.name, sel.has(p.id), () => flip(p.id))));
    dList.scrollTop = top;
    const names = partNames(s);
    dSum.textContent = names.length === r.parts.length
      ? 'Сповіщення про екстрені відключення для всіх районів.'
      : names.length
        ? 'Сповіщення про екстрені відключення для: ' + names.join(', ')
        : 'Оберіть принаймні один район, щоб отримувати районні екстрені сповіщення.';
  }

  // Current state card (what DTEK and the schedules say right now).
  const stLines = h('div', { class: 'stlines', 'aria-live': 'polite' });
  const diagnosticNote = h('p', { class: 'hint' });
  const diagnosticLines = h('div', { class: 'stlines' });
  const diagnostics = h('div', { hidden: true },
    h('h3', { class: 'ct', text: 'Діагностика доставки' }), diagnosticNote, diagnosticLines);
  const stBtn = h('button', { type: 'button', class: 'btn ghost sm', text: 'Оновити', onclick: () => loadStatus() });
  const stCard = card(h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Поточний стан' }), help('status')), stLines, diagnostics, stBtn);
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
      const details = d?.diagnostics?.lines;
      diagnostics.hidden = !Array.isArray(details) || !details.length;
      diagnosticLines.replaceChildren(...(Array.isArray(details) ? details : []).map(text => h('div', { class: 'stl', text })));
      updateDiagnosticNote();
    } catch (e) {
      if (my === stSeq) {
        stLines.replaceChildren(h('div', { class: 'msg err', role: 'alert', text: errorText(e).join(' ') + ' Спробуйте оновити.' }));
        diagnostics.hidden = true;
      }
    }
    if (my === stSeq) stBtn.disabled = false;
  }
  function syncStatus() {
    const regionId = s.regionId || null;
    const parts = partIdsOf(s).join(',');
    if (stFor !== regionId || stPartsFor !== parts) {
      stFor = regionId;
      stPartsFor = parts;
      loadStatus();
    }
  }

  regionSel.addEventListener('change', async () => {
    if (regionSel.value !== s.regionId && s.addresses.length) {
      const nextRegion = regionSel.value;
      regionSel.value = s.regionId || '';
      if (!await confirmSetting('Змінити регіон у чернетці? Поточні адреси й черги буде видалено лише після «Зберегти».')) return;
      s.addresses = [];
      regionSel.value = nextRegion;
    }
    s.regionId = regionSel.value || null; s.partIds = []; dq = ''; dqIn.value = '';
    if (s.regionId && !s.isActive) { s.isActive = true; activeRow.cb.checked = true; }
    setErr([]); refreshAll();
  });

  panels.main.append(
    stCard,
    card(h('h3', { class: 'ct', text: 'Копії в чат із ботом' }),
      h('p', { class: 'hint', text: isPriv
        ? 'Копії з підключених каналів надходять додатково до цієї підписки. Пауза тут зупиняє лише її; пауза в каналі або видалення каналу зі списку зупиняє його копії.'
        : 'Повідомлення цього каналу також надходять вам у чат із ботом із позначенням джерела. Пауза в каналі або видалення його зі списку зупиняє копії.' })),
    card(h('div', { class: 'fld' }, fieldLabel('Регіон', 'region', 'regionSel'), regionSel), distBox),
    addressPanel,
    card(activeRow, toggle('Пауза', 'isPaused', 'pause')));

  const presetNote = h('p', { class: 'hint', role: 'status' });
  const compactTemplates = () => Object.fromEntries(slots.flatMap(slot => {
    const token = key => {
      const p = (slot.placeholders || []).find(p => (p.key || p.name) === key);
      return p ? '{' + p.name + '}' : '';
    };
    if (ALERT_SLOTS.includes(slot.id) && token('message')) {
      return [[slot.id, [
        [token('emoji'), token('title')].filter(Boolean).join(' '),
        token('where') || token('region'), token('address'), token('message'),
      ].filter(Boolean).join('\n')]];
    }
    if (slot.id === 'before-off' && token('time')) {
      return [[slot.id, [
        '⏳ Планове відключення о ' + token('time') + (token('minutes') ? ' (через ' + token('minutes') + ' хв)' : ''),
        [token('region'), token('group') ? 'черга ' + token('group') : ''].filter(Boolean).join(' · '),
      ].filter(Boolean).join('\n')]];
    }
    return [];
  }));
  const emergencyPreset = h('button', { type: 'button', class: 'btn', text: 'Лише аварійні та екстрені', onclick: async () => {
    if (!await confirmSetting('Підготувати режим «Лише аварійні та екстрені»? Публікації графіка та планові попередження буде вимкнено, аварійні й екстрені — увімкнено. Сповіщення про зняття запису, шаблони, адреси, пауза й активність не зміняться. Потрібно натиснути «Зберегти».')) return;
    s.schedulePost = false; s.scheduleNotify = false; s.scheduleReminder = false;
    for (const id of ['emergency', 'regional', 'local']) s.slotSettings[id].enabled = true;
    refreshAll();
    if (tab === 'msg') renderStudio();
    presetNote.textContent = 'Режим підготовлено. Перевірте зміни й натисніть «Зберегти». Повідомлень не надіслано.';
  } });
  const compactPreset = h('button', { type: 'button', class: 'btn', text: 'Стислі тексти сповіщень', onclick: async () => {
    const templates = compactTemplates();
    if (!Object.keys(templates).length) {
      presetNote.textContent = 'Сервер не надав потрібних підстановок для стислих шаблонів.';
      return;
    }
    if (!await confirmSetting('Замінити тексти аварійних / екстрених сповіщень і планового попередження на стислі? Власні тексти цих видів буде замінено лише у чернетці. Шаблони графіка та режими публікації не зміняться. Застосування — лише після «Зберегти».')) return;
    Object.assign(tpls, templates);
    for (const id of Object.keys(templates)) { delete perr[id]; delete serr[id]; }
    renderPills(); renderStudio(); touch();
    presetNote.textContent = 'Стислі тексти підготовлено. Перегляньте їх у «Повідомленнях» та збережіть за бажанням.';
  } });
  panels.main.append(card(h('h3', { class: 'ct', text: 'Готові режими — лише за вашим вибором' }),
    h('p', { class: 'hint', text: 'Кожна дія потребує підтвердження й лише готує зміни для кнопки «Зберегти». Пауза та неактивна підписка не вмикаються автоматично.' }),
    emergencyPreset, compactPreset, presetNote));

  // ----- save logic -----
  function applyChat(c) {
    chat = c; state.editing = c;
    const i = state.chats.findIndex(x => x.chatId === c.chatId);
    if (i >= 0) state.chats[i] = c;
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

  // Shared by the save bar and explicit sends. Resolves to null on success or the Error.
  async function doSave(silent) {
    if (!silent) setErr([]);
    if (s.isActive && !s.regionId) {
      const e = new Error('Оберіть регіон або вимкніть підписку.');
      if (!silent) setErr([e.message]);
      return e;
    }
    const districtError = districtSelectionError();
    if (districtError) {
      const e = new Error(districtError);
      if (!silent) setErr([e.message]);
      return e;
    }
    busy = true; updateUi();
    try {
      const { partId: _p, template: _t, ...saved } = chat.settings; // legacy fields are never sent
      applyChat(await api('PUT', '/api/chats/' + encodeURIComponent(chat.chatId), { ...saved, ...cur() }));
      Object.assign(s, chat.settings, {
        addresses: JSON.parse(JSON.stringify(chat.settings.addresses || [])),
        slotSettings: slotSettingsOf(chat.settings),
      });
      base = JSON.stringify(cur());
      for (const k of Object.keys(serr)) delete serr[k];
      if (!silent) { haptic('success'); busy = false; refreshAll(); if (tab === 'msg') renderStudio(); }
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

  // ----- address and group settings, shown on the main tab -----
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
  if (!isPriv) {
    const trigger = toggle('Реагувати на «Світло зникло»', 'outageTrigger', 'outage');
    trigger.cb.addEventListener('change', syncTrigger);
    const triggerCard = card(trigger, trigBox,
      h('p', { class: 'hint', text: 'Якщо в каналі зʼявиться таке повідомлення, бот перевірить графік, регіональні сповіщення й адресу.' }));
    panels.main.insertBefore(triggerCard, panels.main.lastChild);
  }

  // "Світла немає?" check (private chat only): runs the outage assessment for the saved address.
  const chkOut = h('div', { 'aria-live': 'polite' });
  const chkBtn = h('button', { type: 'button', class: 'btn amber', text: 'Перевірити зараз' });
  const chkCard = h('section', { class: 'card glass warnc' },
    h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Світла немає?' }), help('check')),
    h('p', { class: 'hint', text: 'Перевіряємо збережені адреси. Чернетку спершу збережіть.' }), chkBtn);
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

  // Address search and manual address entry are both shown on «Основне».
  function makeAddrCtx() {
    const msg = h('div', { class: 'msg', role: 'status' });
    const fail = e => { msg.className = 'msg err'; msg.textContent = errorText(e).join(' '); };
    const clear = () => { msg.className = 'msg'; msg.textContent = ''; };

    return { msg, fail, clear };
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
        list.replaceChildren(...items.map(i => h('button', {
          type: 'button',
          class: 'pick',
          text: street && i.groupId ? `${i.name} · черга ${i.groupId}` : i.name,
          onclick: () => (street ? pickHouse : pickStreet)(i),
        })));
      } catch (e) { if (my === sseq) fail(e); }
    }
    const pickStreet = i => {
      street = i; input.value = ''; input.placeholder = `Номер будинку (${i.name})`; input.setAttribute('aria-label', 'Будинок');
      list.replaceChildren(); input.focus();
    };
    const pickHouse = i => {
      list.replaceChildren();
      commit('PUT', '/address', { regionId, streetId: street.id, houseId: i.id, label: `${street.name}, ${i.name}`, street: street.name, house: i.name, groupId: i.groupId }, true);
    };
    input.addEventListener('input', () => { clearTimeout(stimer); stimer = setTimeout(search, 300); });
    return h('div', { class: 'fld' }, fieldLabel('Адреса', 'address', inId), input, list,
      h('p', { class: 'hint', text: 'Знайдіть вулицю, потім номер будинку. Перевірте чергу й натисніть «Зберегти».' }));
  }

  // Manual entry and lookup both stage their fields in the same address draft.
  function manualAddress(regionId, ctx) {
    const field = (title, placeholder) => {
      const id = 'addrM' + (++uid);
      const input = h('input', { type: 'text', id, placeholder, autocomplete: 'off', spellcheck: 'false', 'aria-label': title });
      return { input, node: h('div', { class: 'fld' }, h('label', { for: id, text: title }), input) };
    };
    const city = field('Населений пункт', 'м. Буча'), street = field('Вулиця', 'вул. Вокзальна'), house = field('Будинок', '12');
    const ins = [city, street, house].map(x => x.input);
    const save = h('button', { type: 'button', class: 'btn primary', text: 'Обрати адресу' });
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

  function dtekAddressPicker(regionId, ctx) {
    let city = null, street = null, citySeq = 0, streetSeq = 0, houseSeq = 0;
    const cityList = h('div', { class: 'picks', role: 'listbox', 'aria-label': 'Населені пункти' });
    const streetList = h('div', { class: 'picks', role: 'listbox', 'aria-label': 'Вулиці' });
    const houseList = h('div', { class: 'picks', role: 'listbox', 'aria-label': 'Будинки та черги' });
    const cityInput = h('input', { type: 'text', inputmode: 'search', placeholder: 'Наприклад, Одеса', autocomplete: 'off', 'aria-label': 'Населений пункт' });
    const streetInput = h('input', { type: 'text', inputmode: 'search', placeholder: 'Оберіть населений пункт спочатку', autocomplete: 'off', 'aria-label': 'Вулиця', disabled: true });
    const houseInput = h('input', { type: 'text', inputmode: 'search', placeholder: 'Оберіть вулицю спочатку', autocomplete: 'off', 'aria-label': 'Номер будинку', disabled: true });
    const stepper = h('ol', { class: 'address-steps', 'aria-label': 'Кроки вибору адреси' });
    const steps = [
      h('li', { class: 'address-step current' }, h('span', { class: 'step-number', text: '1' }), h('span', { text: 'Населений пункт' })),
      h('li', { class: 'address-step' }, h('span', { class: 'step-number', text: '2' }), h('span', { text: 'Вулиця' })),
      h('li', { class: 'address-step' }, h('span', { class: 'step-number', text: '3' }), h('span', { text: 'Будинок' })),
    ];
    stepper.append(...steps);
    const field = (label, input, list, id) => h('div', { class: 'fld address-field' },
      h('label', { for: id, text: label }), input, list);
    cityInput.id = 'cityInput' + (++uid);
    streetInput.id = 'streetInput' + (++uid);
    houseInput.id = 'houseInput' + (++uid);
    cityList.id = 'cityResults' + (++uid);
    cityInput.setAttribute('aria-controls', cityList.id);
    const resultStatus = h('p', { class: 'address-status', role: 'status', 'aria-live': 'polite' });
    const setStep = current => steps.forEach((step, index) => {
      step.classList.toggle('done', index < current);
      step.classList.toggle('current', index === current);
    });
    const searching = (list, message) => {
      list.replaceChildren(h('p', { class: 'pick-state', text: message }));
      resultStatus.textContent = message;
    };
    const setOptions = (list, items, label, select) => list.replaceChildren(...(items.length
      ? items.map(item => h('button', { type: 'button', class: 'pick', role: 'option', text: label(item), onclick: () => select(item) }))
      : [h('p', { class: 'pick-state', text: 'Нічого не знайдено. Перевірте написання або спробуйте інший запит.' })]));

    async function searchCities() {
      const my = ++citySeq, q = cityInput.value.trim();
      city = null; street = null;
      streetInput.value = ''; streetInput.disabled = true;
      houseInput.value = ''; houseInput.disabled = true;
      streetList.replaceChildren(); houseList.replaceChildren();
      setStep(0);
      ctx.clear();
      resultStatus.textContent = '';
      if (q.length < 2) { cityList.replaceChildren(); return; }
      searching(cityList, 'Шукаємо населені пункти…');
      try {
        const items = await api('GET', `/api/address/cities?regionId=${encodeURIComponent(regionId)}&query=${encodeURIComponent(q)}`);
        if (my !== citySeq) return;
        setOptions(cityList, items, item => item.name, item => {
          city = item.name; cityInput.value = city; cityList.replaceChildren();
          streetInput.disabled = false; streetInput.focus(); setStep(1);
          resultStatus.textContent = 'Населений пункт обрано. Тепер знайдіть вулицю.';
        });
        if (!items.length) resultStatus.textContent = 'Населених пунктів за цим запитом не знайдено.';
      } catch (e) {
        if (my === citySeq) { cityList.replaceChildren(); resultStatus.textContent = ''; ctx.fail(e); }
      }
    }

    async function searchStreets() {
      const my = ++streetSeq, q = streetInput.value.trim();
      street = null; houseInput.value = ''; houseInput.disabled = true; houseList.replaceChildren();
      setStep(1);
      ctx.clear();
      resultStatus.textContent = '';
      if (!city || q.length < 2) { streetList.replaceChildren(); return; }
      searching(streetList, 'Шукаємо вулиці…');
      try {
        const items = await api('GET', `/api/address/streets?regionId=${encodeURIComponent(regionId)}&city=${encodeURIComponent(city)}&query=${encodeURIComponent(q)}`);
        if (my !== streetSeq) return;
        setOptions(streetList, items, item => item.name, item => {
          street = item.name; streetInput.value = street; streetList.replaceChildren();
          houseInput.disabled = false; houseInput.focus(); setStep(2);
          resultStatus.textContent = 'Вулицю обрано. Тепер введіть номер будинку.';
        });
        if (!items.length) resultStatus.textContent = 'Вулиць за цим запитом не знайдено.';
      } catch (e) {
        if (my === streetSeq) { streetList.replaceChildren(); ctx.fail(e); }
      }
    }

    async function searchHouses() {
      const my = ++houseSeq, q = houseInput.value.trim();
      ctx.clear();
      if (!city || !street || q.length < 1) { houseList.replaceChildren(); return; }
      searching(houseList, 'Шукаємо будинки та черги…');
      try {
        const items = await api('GET', `/api/address/houses?regionId=${encodeURIComponent(regionId)}&city=${encodeURIComponent(city)}&street=${encodeURIComponent(street)}&query=${encodeURIComponent(q)}`);
        if (my !== houseSeq) return;
        setOptions(houseList, items,
          item => item.groupId
            ? `Будинок ${item.house} · черга ${item.groupId}`
            : `Будинок ${item.house} · чергу можна обрати нижче`,
          async item => {
            const label = item.groupId ? `${item.house}, черга ${item.groupId}` : item.house;
            resultStatus.textContent = `Обрано: ${city}, ${street}, ${label}.`;
            houseInput.disabled = true;
            houseList.replaceChildren();
            setStep(3);
            const saved = await ctx.commit(
              'PUT',
              '/address/dtek',
              { regionId, city, street, house: item.house, groupId: item.groupId, groupIds: item.groupIds || null },
              true);
            if (!saved) houseInput.disabled = false;
          });
        if (!items.length) resultStatus.textContent = 'Будинок не знайдено. Перевірте номер і спробуйте ще раз.';
      } catch (e) {
        if (my === houseSeq) { houseList.replaceChildren(); ctx.fail(e); }
      }
    }

    let cityTimer = 0, streetTimer = 0, houseTimer = 0;
    cityInput.addEventListener('input', () => {
      city = null; street = null; resultStatus.textContent = '';
      citySeq++; streetSeq++; houseSeq++;
      clearTimeout(streetTimer); clearTimeout(houseTimer);
      cityList.replaceChildren(); streetList.replaceChildren(); houseList.replaceChildren();
      streetInput.value = ''; streetInput.disabled = true;
      houseInput.value = ''; houseInput.disabled = true;
      setStep(0);
      clearTimeout(cityTimer); cityTimer = setTimeout(searchCities, 250);
    });
    streetInput.addEventListener('input', () => {
      street = null; resultStatus.textContent = '';
      streetSeq++; houseSeq++; clearTimeout(houseTimer);
      streetList.replaceChildren(); houseList.replaceChildren();
      houseInput.value = ''; houseInput.disabled = true;
      setStep(1);
      clearTimeout(streetTimer); streetTimer = setTimeout(searchStreets, 250);
    });
    houseInput.addEventListener('input', () => {
      resultStatus.textContent = '';
      houseSeq++; houseList.replaceChildren();
      clearTimeout(houseTimer); houseTimer = setTimeout(searchHouses, 250);
    });

    const manual = h('details', { class: 'fold address-manual' },
      h('summary', { text: 'Не вдається знайти адресу? Ввести вручну' }),
      manualAddress(regionId, ctx));

    return h('div', { class: 'fld dtek-address' },
      h('div', { class: 'address-intro' },
        h('div', { class: 'address-icon', 'aria-hidden': 'true', text: '⌖' }),
        h('div', null, h('strong', { text: 'Знайдіть свою адресу' }),
          h('p', { class: 'hint', text: 'Оберіть населений пункт і вулицю зі списків ДТЕК, а номер будинку введіть самостійно. Черга підставиться, якщо для цієї вулиці в базі є однозначний збіг.' }))),
      stepper,
      field('Населений пункт', cityInput, cityList, cityInput.id),
      field('Вулиця', streetInput, streetList, streetInput.id),
      field('Номер будинку', houseInput, houseList, houseInput.id),
      resultStatus,
      manual);
  }

  function addressCollection() {
    const region = regionOf(s.regionId), ctx = makeAddrCtx();
    const list = h('div'), info = h('p', { class: 'hint', text: 'До 3 адрес одного регіону. Усі зміни застосуються разом після «Зберегти».' });
    const add = h('button', { type: 'button', class: 'btn ghost sm', text: '+ Додати адресу' });
    const paint = () => {
      add.disabled = s.addresses.length >= 3;
      list.replaceChildren(...s.addresses.map((address, index) => {
        const fields = [['Назва', 'name', 64], ['Населений пункт', 'city', 128], ['Вулиця', 'street', 256], ['Будинок', 'house', 64]];
        const inputs = fields.map(([label, key, max]) => {
          const input = h('input', { type: 'text', value: address[key] || '', maxlength: String(max), 'aria-label': label });
          input.addEventListener('input', () => { address[key] = input.value; touch(); });
          return h('div', { class: 'fld' }, h('label', { text: label }), input);
        });
        const searchBox = h('div');
        const searchContext = {
          ...ctx,
          commit: async (_method, _path, body) => {
            address.city = body.city || '';
            address.street = body.street;
            address.house = body.house;
            address.label = body.label || [address.city, address.street, address.house].filter(Boolean).join(', ');
            const ids = body.groupIds?.length ? body.groupIds : body.groupId ? [body.groupId] : [];
            if (ids.length) {
              address.groups = [...new Set(ids)].map(groupId => address.groups.find(g => g.groupId === groupId) || { groupId, label: '' });
            }
            paint(); renderSched(); touch();
            ctx.clear();
            ctx.msg.textContent = 'Адресу обрано. Натисніть «Зберегти», щоб застосувати всі зміни.';
            return true;
          },
        };
        const search = h('button', { type: 'button', class: 'btn ghost sm', text: 'Знайти адресу', onclick: () => {
          searchBox.replaceChildren(region.hasGroups
            ? addressPicker(s.regionId, searchContext)
            : dtekAddressPicker(s.regionId, searchContext));
        } });
        if (!address.street && region?.siteUrl) {
          searchBox.append(region.hasGroups
            ? addressPicker(s.regionId, searchContext)
            : dtekAddressPicker(s.regionId, searchContext));
        }
        const districts = h('select', { 'aria-label': 'Район адреси' }, h('option', { value: '', text: 'Райони з основних налаштувань' }),
          ...(region?.parts || []).map(part => h('option', { value: part.id, text: part.name })));
        districts.value = address.partIds || '';
        districts.addEventListener('change', () => { address.partIds = districts.value || null; touch(); });
        const groups = h('div');
        const paintGroups = () => groups.replaceChildren(...address.groups.map(group => {
          const label = h('input', { type: 'text', value: group.label || '', maxlength: '64', placeholder: "Наприклад, Під'їзд 1", 'aria-label': 'Підпис черги ' + group.groupId });
          label.addEventListener('input', () => { group.label = label.value; touch(); });
          return h('div', { class: 'fld' }, h('span', { class: 'chip purple', text: 'Черга ' + group.groupId }), label,
            h('button', { type: 'button', class: 'btn ghost sm', text: 'Прибрати чергу', onclick: () => { address.groups = address.groups.filter(g => g !== group); paintGroups(); renderSched(); touch(); } }));
        }));
        paintGroups();
        const select = h('select', { 'aria-label': 'Додати чергу' }, h('option', { value: '', text: '+ Додати чергу' }));
        if (region?.hasSchedules) loadGroups(s.regionId).then(items => select.append(...items.map(g => h('option', { value: g.id, text: g.name }))), ctx.fail);
        else select.disabled = true;
        select.addEventListener('change', () => {
          if (select.value && !address.groups.some(g => g.groupId === select.value)) address.groups.push({ groupId: select.value, label: '' });
          select.value = ''; paintGroups(); renderSched(); touch();
        });
        return card(h('h3', { class: 'ct', text: `Адреса ${index + 1} · ${region?.name || ''}` }),
          region?.siteUrl ? search : null, searchBox, ...inputs,
          h('div', { class: 'fld' }, h('label', { text: 'Район' }), districts), groups, select,
          h('button', { type: 'button', class: 'btn ghost sm', text: 'Прибрати адресу', onclick: () => { s.addresses = s.addresses.filter(a => a !== address); paint(); renderSched(); touch(); } }));
      }));
    };
    add.addEventListener('click', () => {
      if (s.addresses.length >= 3) return;
      s.addresses.push({ id: 0, regionId: s.regionId, name: s.addresses.length ? 'Адреса ' + (s.addresses.length + 1) : 'Дім', city: '', street: '', house: '', groups: [] }); paint(); touch();
    });
    paint();
    return card(h('h3', { class: 'ct', text: 'Адреси та черги' }), info, list, add, ctx.msg);
  }

  function renderAddr() {
    const r = regionOf(s.regionId);
    regionSel.disabled = false;
    regionSel.title = '';
    if (r) {
      addressPanel.replaceChildren(addressCollection(), ...(isPriv ? [chkCard, chkOut] : []));
      return;
    }
    addressPanel.replaceChildren(empty('Оберіть регіон, щоб указати адресу та чергу.'));
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
  const imageKindOf = id => id === 'schedule' ? s.schedulePostImage : 'None';
  const previewBody = id => ({
    slot: ['schedule-heading', 'schedule-line'].includes(id) ? 'schedule' : id,
    template: ['schedule-heading', 'schedule-line'].includes(id) ? null : customised(slotOf(id), tpls[id]) ? tpls[id] : null, regionId: s.regionId, partIds: partIdsOf(s),
    addresses: s.addresses, groupId: groupOf(), kind: KIND_OF[id] || 'Emergency',
    imageKind: imageKindOf(['schedule-heading', 'schedule-line'].includes(id) ? 'schedule' : id),
    scheduleLineTemplate: tpls['schedule-line'] || null,
    scheduleHeadingTemplate: tpls['schedule-heading'] || null,
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
  const svb = { hasKey: !!chat.settings.hasSvitlobotKey, busy: '', msg: null, scrollMsg: false };
  const svbNode = isPriv ? null : h('section', { class: 'card glass', id: 'svb' });
  const keyIn = h('input', { type: 'password', id: 'svbKey', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', placeholder: 'channel_key', 'aria-label': 'Ключ каналу (channel_key)' });
  keyIn.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); svbConnect(); } });
  const svbBase = () => `/api/chats/${encodeURIComponent(chat.chatId)}/svitlobot`;
  const svbText = e => e.status === 429 ? 'Забагато дій, спробуйте за хвилину' : e.message;
  async function svbRun(name, call) {
    if (svb.busy) return;
    svb.busy = name; svb.msg = { kind: 'ok', text: 'Виконую…' }; svbPaint();
    try {
      const d = await call();
      if (d && 'hasKey' in d) {
        svb.hasKey = !!d.hasKey;
        chat.settings.hasSvitlobotKey = svb.hasKey;
      }
      svb.msg = { kind: 'ok', text: (d && d.message) || 'Готово ✓' };
      svb.scrollMsg = true;
      haptic('success');
    } catch (e) { svb.msg = { kind: 'err', text: svbText(e) }; svb.scrollMsg = true; haptic('error'); } finally { svb.busy = ''; svbPaint(); }
  }
  const svbAsk = ask => new Promise(resolve => {
    try { tg.showConfirm ? tg.showConfirm(ask, resolve) : resolve(confirm(ask)); }
    catch { resolve(confirm(ask)); } // an old Telegram client without showConfirm
  });
  function svbConnect() {
    const key = keyIn.value.trim();
    if (!key) { svb.msg = { kind: 'err', text: 'Введіть ключ каналу.' }; svbPaint(); return; }
    svbRun('key', async () => {
      const d = await api('PUT', svbBase() + '/key', { key });
      keyIn.value = '';
      return {
        ...d,
        message: groupOf()
          ? 'Ключ збережено. Тижневий графік основної черги оновлюватиметься автоматично.'
          : 'Ключ збережено. Оберіть адресу або чергу — тоді бот зможе передавати графік Світлоботу.',
      };
    });
  }
  // Saved groups of the chat with the addresses that use them; Svitlobot follows exactly one of them.
  function svbGroups() {
    const map = new Map();
    for (const a of chat.settings.addresses || []) {
      for (const g of a.groups || []) {
        const name = [a.name, g.label].filter(Boolean).join(' · ');
        const item = map.get(g.groupId) || { id: g.groupId, names: [] };
        if (name && !item.names.includes(name)) item.names.push(name);
        map.set(g.groupId, item);
      }
    }
    return [...map.values()];
  }
  function svbSetGroup(groupId) {
    svbRun('group:' + groupId, async () => {
      const d = await api('PUT', svbBase() + '/group', { groupId });
      chat.settings.svitlobotGroupId = d.groupId;
      return d;
    });
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

  function svbPaint() {
    if (!svbNode) return;
    const busy = !!svb.busy;
    const btn = (name, cls, text, fn, off) => h('button', { type: 'button', class: 'btn ' + cls + (svb.busy === name ? ' busy' : ''), text, disabled: busy || !!off, onclick: fn });
    const head = h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Світлобот' }), help('svb'));
    const msg = svb.msg ? h('div', { class: 'toast ' + (svb.msg.kind === 'ok' ? 'ok' : 'err'), role: svb.msg.kind === 'ok' ? 'status' : 'alert', text: svb.msg.text }) : null;
    keyIn.disabled = busy;
    let body;
    if (!svb.hasKey) {
      body = [
        h('div', { class: 'svb-key-entry' },
          h('div', { class: 'fld' }, fieldLabel('Ключ каналу', 'svbKey', 'svbKey'), keyIn),
          btn('key', 'primary', 'Зберегти ключ', svbConnect)),
        h('p', { class: 'hint', text: 'Ключ потрібен, щоб бот регулярно перевіряв нові графіки та передавав тижневий розклад у Світлобот. Знайдіть channel_key у налаштуваннях потрібного каналу в Світлоботі.' }),
      ];
    } else {
      const groups = svbGroups();
      const main = chat.settings.svitlobotGroupId;
      body = [
        h('div', { class: 'svb-key-status' }, h('span', { class: 'chip green', text: 'Ключ каналу підключено' })),
        h('p', { class: 'hint', text: !groupOf()
          ? 'Оберіть адресу або чергу на вкладці «Основне», щоб увімкнути автоматичне оновлення графіка.'
          : main ? `Світлобот отримує тижневий графік черги ${main} автоматично.`
            : 'Тижневий графік надсилається Світлоботу автоматично.' }),
        groups.length > 1 ? h('p', { class: 'hint', text: 'Світлобот зберігає один графік на канал. Оберіть основну чергу:' }) : null,
        ...(groups.length > 1 ? groups.map(g => h('div', { class: 'fl svb-group' },
          h('span', { text: `Черга ${g.id}` + (g.names.length ? ` · ${g.names.join(', ')}` : '') }),
          g.id === main
            ? h('span', { class: 'chip green', text: 'Основна' })
            : btn('group:' + g.id, 'ghost sm', 'Зробити основною', () => svbSetGroup(g.id)))) : []),
        h('div', { class: 'svb-key-actions' },
          btn('check', 'ghost sm', 'Перевірити ключ', svbCheck),
          btn('forget', 'ghost sm', 'Забути ключ', svbForget)),
      ];
    }
    svbNode.replaceChildren(...[head, msg, ...body].filter(Boolean));
    if (msg && svb.scrollMsg) {
      svb.scrollMsg = false;
      try { msg.scrollIntoView({ block: 'center', behavior: 'smooth' }); } catch { /* optional */ }
    }
  }

  const goToAddress = () => {
    setTab('main');
    window.setTimeout(() => addressPanel.scrollIntoView({ block: 'start', behavior: 'smooth' }), 0);
  };

  let scheduleSendBusy = false;
  const scheduleSendStatus = h('div', { class: 'msg', role: 'status', 'aria-live': 'polite' });
  async function sendScheduleNow(button) {
    if (scheduleSendBusy) return;
    scheduleSendBusy = true;
    button.disabled = true;
    button.classList.add('busy');
    scheduleSendStatus.className = 'msg';
    scheduleSendStatus.textContent = 'Зберігаю налаштування…';
    try {
      if (isDirty()) {
        const error = await doSave(true);
        if (error) throw error;
      }
      scheduleSendStatus.textContent = isPriv ? 'Надсилаю графік у цей чат…' : 'Надсилаю графік у канал…';
      const response = await api('POST', `/api/chats/${encodeURIComponent(chat.chatId)}/schedules/send`);
      scheduleSendStatus.className = 'msg ok';
      scheduleSendStatus.textContent = response.message || (isPriv ? 'Графік надіслано в цей чат.' : 'Графік надіслано в канал.');
      haptic('success');
    } catch (error) {
      scheduleSendStatus.className = 'msg err';
      scheduleSendStatus.textContent = errorText(error).join(' ');
      haptic('error');
    } finally {
      scheduleSendBusy = false;
      button.classList.remove('busy');
      button.disabled = !groupOf();
    }
  }

  function renderSched() {
    if (svbNode) {
      panels.main.append(svbNode);
      svbPaint();
    }
    const r = regionOf(s.regionId);
    const viewer = card(scheduleViewerButton(s.regionId, groupOf()),
      h('p', { class: 'hint', text: 'Будь-який регіон і черга. Збереження налаштувань не потрібне.' }));
    if (!r || !r.hasSchedules) {
      pvSeq++; pv.key = '';
      panels.sched.replaceChildren(viewer, empty(r ? 'Графіки відключень для цього регіону поки недоступні.' : 'Для налаштування публікацій оберіть регіон на вкладці «Основне».', r ? null : 'main'));
      return;
    }
    const has = !!groupOf();
    const lock = () => has ? null : h('div', { class: 'lockhint' }, h('span', { class: 'hint', text: 'Спершу оберіть адресу або чергу на вкладці «Основне».' }),
      h('button', { type: 'button', class: 'btn ghost sm', text: 'Перейти до адреси й черги', onclick: goToAddress }));
    const edit = (id, label) => slotOf(id) ? h('button', { type: 'button', class: 'btn ghost sm', text: label, onclick: () => openSlot(id) }) : null;
    const sendNow = h('button', { type: 'button', class: 'btn primary', text: isPriv ? 'Надіслати графік у цей чат зараз' : 'Надіслати графік у канал зараз', disabled: !has || scheduleSendBusy });
    sendNow.addEventListener('click', () => sendScheduleNow(sendNow));
    if (!has) {
      // Without a group there is no schedule to preview: one clear step instead of two locked sections.
      pvSeq++; pv.key = '';
      panels.sched.replaceChildren(
        viewer,
        card(
          h('p', { class: 'empty', text: 'Щоб налаштувати публікації, вкажіть свою адресу або чергу. Для перегляду скористайтеся кнопкою вище.' }),
          h('button', { type: 'button', class: 'btn', text: 'Вказати адресу або чергу', onclick: goToAddress })),
        h('p', { class: 'hint credit', text: CREDIT }));
      return;
    }
    panels.sched.replaceChildren(
      viewer,
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: isPriv ? 'Графік у чаті з ботом' : 'Графік у Telegram-каналі' }), help('schedulePost')),
        h('p', { class: 'hint', text: isPriv ? 'Усі черги — в одному повідомленні.' : 'Усі черги — в одному дописі. Основну чергу для Світлобота оберіть у «Основне».' }),
        sendNow, scheduleSendStatus),
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Публікація графіка' }), help('schedulePost')),
        toggle('Публікувати графік', 'schedulePost', null, {
          disabled: !has,
          confirm: 'Надсилати й оновлювати спільний графік усіх черг у цьому чаті?',
        }), lock(),
        h('div', { class: 'fld' }, fieldLabel('Як публікувати', 'postMode'), ...modeSeg('post', () => s.schedulePostMode, v => { s.schedulePostMode = v; }, 'Як публікувати')),
        h('div', { class: 'fld' }, fieldLabel('Картинка', 'image'), (s.addresses.length > 1 || (s.addresses[0]?.groups?.length || 0) > 1) ? h('p', { class: 'hint', text: 'Об’єднаний графік надсилаємо текстом.' }) : imgSeg(() => s.schedulePostImage, v => { s.schedulePostImage = v; })),
        edit('schedule', 'Редагувати шаблон'),
        edit('schedule-line', 'Редагувати рядок графіка'),
        pvx.node),
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Попередження перед відключенням' }), help('scheduleReminder')),
        toggle('Надсилати попередження', 'scheduleReminder', null, { disabled: !has }),
        h('div', { class: 'fld' }, fieldLabel('За скільки хвилин', 'scheduleReminder'),
          (() => {
            const options = [5, 10, 15, 30, 60];
            const select = h('select', { 'aria-label': 'За скільки хвилин попередити' },
              options.map(minutes => h('option', { value: String(minutes), text: `${minutes} хвилин` })));
            select.value = String(options.includes(Number(s.scheduleReminderMinutes)) ? Number(s.scheduleReminderMinutes) : 15);
            select.disabled = !has;
            select.addEventListener('change', () => { s.scheduleReminderMinutes = Number(select.value); touch(); });
            return select;
          })()),
        edit('before-off', 'Редагувати шаблон попередження'),
        lock()),
      h('p', { class: 'hint credit', text: CREDIT }));
    svbPaint();
    if (tab === 'sched') loadPv();
  }

  // ----- tab: Повідомлення (template studio) -----
  const minuteToTime = minute => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`;
  const timeToMinute = value => {
    const [hours, minutes] = value.split(':').map(Number);
    return hours * 60 + minutes;
  };
  const quietToggleId = 'quietHours' + (++uid);
  const quietToggle = h('input', { type: 'checkbox', role: 'switch', id: quietToggleId });
  quietToggle.checked = s.quietHoursEnabled;
  const quietStart = h('input', { type: 'time', step: '60', 'aria-label': 'Початок тихих годин' });
  const quietEnd = h('input', { type: 'time', step: '60', 'aria-label': 'Завершення тихих годин' });
  quietStart.value = minuteToTime(s.quietHoursStartMinute);
  quietEnd.value = minuteToTime(s.quietHoursEndMinute);
  const quietTimes = h('div', { class: 'quiet-times' },
    h('div', { class: 'fld' }, fieldLabel('Початок', 'quietHours'), quietStart),
    h('div', { class: 'fld' }, fieldLabel('Завершення', 'quietHours'), quietEnd));
  const paintQuietHours = () => {
    quietTimes.classList.toggle('dim', !s.quietHoursEnabled);
    quietStart.disabled = !s.quietHoursEnabled;
    quietEnd.disabled = !s.quietHoursEnabled;
  };
  quietToggle.addEventListener('change', () => { s.quietHoursEnabled = quietToggle.checked; paintQuietHours(); touch(); });
  quietStart.addEventListener('change', () => { s.quietHoursStartMinute = timeToMinute(quietStart.value); touch(); });
  quietEnd.addEventListener('change', () => { s.quietHoursEndMinute = timeToMinute(quietEnd.value); touch(); });
  paintQuietHours();
  const quietCard = card(
    h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Тихі години' }), help('quietHours')),
    h('div', { class: 'row' },
      fieldLabel('Надсилати автоматичні повідомлення без звуку', 'quietHours', quietToggleId),
      h('label', { class: 'sw' }, quietToggle, h('i', { class: 'trk' }))),
    quietTimes,
    h('p', { class: 'hint', text: 'Щодня за київським часом. Повідомлення надходять одразу, але без звукового сповіщення. Діє на сповіщення та автоматичні повідомлення графіка.' }));
  const alertCleanupCard = card(
    h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Менше зайвих повідомлень' }), help('alertCleanup')),
    toggle('Видаляти попереднє аварійне сповіщення', 'deletePreviousAlert', 'alertCleanup'),
    h('p', { class: 'hint', text: 'Спершу надійде нове повідомлення, потім бот спробує видалити попереднє. Налаштовується для кожного чату окремо. Графіки налаштовуються окремо.' }));
  const slotBar = h('div', { class: 'slotbar', role: 'tablist', 'aria-label': 'Види повідомлень' });
  const studio = h('div', { class: 'studio' });
  // Editor widgets of the current slot (rebuilt whenever the slot changes).
  let tpl, counter, errBox, resetBtn, paramNote, testBtn, testOut, prevx;
  const previewCache = {};

  function renderPills() {
    const groupKey = group => group === 'emergency' || group === 'regional' ? 'alerts' : group;
    const groups = [...SLOT_GROUPS, ...[...new Set(slots.map(x => groupKey(x.group)))].filter(g => !SLOT_GROUPS.some(y => y[0] === g)).map(g => [g, g])];
    slotBar.replaceChildren(...groups.map(([g, title]) => {
      const list = slots.filter(x => groupKey(x.group) === g && !['regional', 'ended', 'schedule-heading', 'schedule-line'].includes(x.id));
      if (!list.length) return null;
      return h('div', { class: 'sgrp', role: 'presentation' }, h('div', { class: 'sgh', text: title }),
        h('div', { class: 'sgp' }, list.map(x => h('button', {
          type: 'button', role: 'tab', class: 'spill' + (x.id === cs || x.id === 'emergency' && ['regional', 'ended'].includes(cs) || x.id === 'schedule' && ['schedule-heading', 'schedule-line'].includes(cs) ? ' on' : ''), id: 'slot-' + x.id,
          'aria-selected': String(x.id === cs || x.id === 'emergency' && ['regional', 'ended'].includes(cs) || x.id === 'schedule' && ['schedule-heading', 'schedule-line'].includes(cs)),
          onclick: () => openSlot(x.id, true),
        }, h('span', { text: x.title }), isCustom(x.id) ? h('i', { class: 'pd', title: 'Змінений шаблон' }) : null,
          slotErrs(x.id).length ? h('i', { class: 'rd2', title: 'Є помилки' }) : null))));
    }));
  }

  function openSlot(id, stay) {
    cs = id === 'schedule' && slotOf('schedule-heading') ? 'schedule-heading' : id;
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
      ['schedule', 'schedule-heading', 'schedule-line'].includes(cs) ? card(
        h('h3', { class: 'ct', text: 'Вигляд графіка' }),
        h('p', { class: 'hint', text: 'Один допис: сьогодні й завтра, черги окремо. Редагуйте заголовок або один інтервал — бот повторить його для всіх відключень. Додайте {зміни} в заголовок, щоб розмістити там підсумок доданих і скасованих інтервалів; без нього підсумок додається автоматично.' }),
        seg([['schedule-heading', 'Заголовок і дата'], ['schedule-line', 'Інтервал {відключення}']],
          () => cs, value => openSlot(value, true), 'Частина графіка')) : null,
      ['emergency', 'regional', 'ended'].includes(cs) ? card(
        h('h3', { class: 'ct', text: 'Екстрені відключення' }),
        h('p', { class: 'hint', text: 'Один тип сповіщення: початок, районна аварія та зняття. Райони підставляє бот.' }),
        seg([['emergency', 'Початок'], ['regional', 'Аварія / район'], ['ended', 'Знято']],
          () => cs, value => openSlot(value, true), 'Стан сповіщення')) : null,
      card(
        h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: x.title }), help('template')),
        h('p', { class: 'hint', text: x.description }),
        slotOpts,
        slotOpts ? slotOpts.wrap : h('div', { class: 'tawrap' }, tpl, counter), errBox,
        h('div', { class: 'chips' }, ['🔴', '🟢', '⚠️', '📅', '🕒', '📍', '→', '–'].map(text =>
          h('button', { type: 'button', class: 'chip', text, onmousedown: keep, onclick: () => put(text) })),
          cs === 'schedule-line' ? null : h('button', { type: 'button', class: 'chip', text: 'Жирний', onmousedown: keep,
            onclick: () => { const selected = tpl.value.slice(tpl.selectionStart, tpl.selectionEnd); put('<b>' + selected + '</b>', 3 + selected.length); } })),
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
      const target = ['schedule-heading', 'schedule-line'].includes(id) ? 'schedule' : id;
      const imageKind = target === 'schedule' || target === 'off' || target === 'on' ? imageKindOf(target) : null;
      await api('POST', `/api/chats/${encodeURIComponent(chat.chatId)}/templates/${encodeURIComponent(target)}/send`,
        { template: target === 'schedule' ? null : customised(x, tpls[id]) ? tpls[id] : null, imageKind });
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
    alertCleanupCard,
    quietCard,
    card(h('div', { class: 'fl big sm' }, h('span', { text: 'Шаблони повідомлень' }), help('slots')),
      h('div', { class: 'slotwrap' }, slotBar)),
    studio);
  if (removeBtn) panels.main.append(card(h('div', { class: 'fl big' }, h('h3', { class: 'ct', text: 'Керування' }), help('remove')), removeBtn));
  renderPills(); renderStudio();

  // ----- wiring -----
  function updateDiagnosticNote() {
    diagnosticNote.className = isDirty() ? 'note warn' : 'hint';
    diagnosticNote.textContent = 'Діагностика за збереженими налаштуваннями підписки.' + (isDirty()
      ? ' Є незбережена чернетка: її зміни ще не впливають на доставку. Поточний стан вище може враховувати обраний у чернетці регіон.'
      : '');
  }
  function updateUi() {
    const d = isDirty(), districtError = districtSelectionError(), bad = blocking() || !!districtError;
    saveBtn.disabled = busy || bad || !d;
    cancelBtn.disabled = busy || !d;
    const mode = bad ? 'err' : d ? 'dirty' : 'ok';
    bar.dataset.mode = mode;
    stTxt.textContent = districtError ? districtError : blocking() ? 'Виправте помилки шаблону' : mode === 'dirty' ? 'Є незбережені зміни' : 'Збережено ✓';
    rd.hidden = !anyErr();
    const [c, t] = statusOf(s);
    topPill.className = 'pill ' + c; topPill.textContent = t;
    updateDiagnosticNote();
  }

  function refreshAll() { renderDistricts(); syncStatus(); renderAddr(); renderSched(); updateUi(); if (tab === 'msg') runPreview(); }

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
    TABS.map(([id]) => panels[id]), versionFooter(), bar));
  refreshAll();
  setTab(tab);
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
    const saved = readEditorPosition();
    const previous = saved && chats.find(c => String(c.chatId) === saved.chatId);
    if (previous) renderEditor(previous);
    else if (chats.length === 1) renderEditor(chats[0]);
    else renderList();
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
  tg.BackButton.onClick(() => { if (sheet) closeSheet(); else if (state.editing && state.goBack) state.goBack(); else renderList(); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden && state.boot) refreshChats(false); });
  try { tg.onEvent && tg.onEvent('activated', () => { if (state.boot) refreshChats(false); }); } catch { /* optional */ }
  load();
}
