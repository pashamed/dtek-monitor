import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';
import { join } from 'node:path';

const placeholder = (key, name, example) => ({ key, name, example, description: name });
const common = [
  placeholder('emoji', 'значок', '🚨'), placeholder('message', 'текст', 'м. Київ: діють екстрені відключення.'),
  placeholder('region', 'регіон', 'м. Київ'), placeholder('address', 'адреса', 'вул. Тестова, 1'),
  placeholder('site', 'сайт', 'https://example.test'),
  placeholder('city', 'місто', 'Київ'), placeholder('group', 'група', '45.1'),
  placeholder('status', 'статус', 'Екстрені відключення'), placeholder('statusCode', 'код_статусу', 'Emergency'),
  placeholder('regionId', 'код_регіону', 'kyiv'),
];
const defaultAlert = '{значок} {текст}\n\nДеталі: {сайт}';
const slots = [
  ...['emergency', 'regional', 'ended', 'local'].map(id => ({
    id, group: id === 'local' ? 'local' : 'emergency', title: id, description: '',
    defaultTemplate: defaultAlert, placeholders: common,
  })),
  { id: 'schedule', group: 'stabilization', title: 'Графік', description: '', defaultTemplate: '{шапка}', placeholders: [] },
  { id: 'schedule-heading', group: 'stabilization', title: 'Заголовок графіка', description: '',
    defaultTemplate: '❗ {шапка}\n📅 {день_тижня} {дата}',
    placeholders: [placeholder('header', 'шапка', 'Графік відключень'), placeholder('weekday', 'день_тижня', 'Середа'),
      placeholder('date', 'дата', '21.10.2026'), ...common.filter(p => ['region', 'address'].includes(p.key))] },
  { id: 'schedule-line', group: 'stabilization', title: 'Інтервал', description: '', defaultTemplate: '{початок}–{кінець} ({тривалість_інтервалу})',
    placeholders: [placeholder('start', 'початок', '14:00'), placeholder('end', 'кінець', '16:00'),
      placeholder('duration', 'тривалість_інтервалу', '2 год')] },
  { id: 'schedule-light-line', group: 'stabilization', title: 'Інтервал зі світлом за графіком', description: '',
    defaultTemplate: '🟢 {початок}–{кінець} ({тривалість_інтервалу})',
    placeholders: [placeholder('start', 'початок', '03:00'), placeholder('end', 'кінець', '09:30'),
      placeholder('duration', 'тривалість_інтервалу', '6 год 30 хв')] },
  { id: 'before-off', group: 'stabilization', title: 'Попередження', description: '',
    defaultTemplate: '⏳ За графіком групи {група} відключення почнеться о {час} — через {хвилин} хв.',
    placeholders: [placeholder('group', 'група', '45.1'), placeholder('time', 'час', '14:00'),
      placeholder('minutes', 'хвилин', '15'), placeholder('region', 'регіон', 'м. Київ')] },
];
const names = slots.flatMap(s => s.placeholders);
const canonical = text => text.replace(/{{|}}|{([#/]?)([^{}]*)}/g, (token, marker, name) =>
  name === undefined ? token : '{' + marker + (names.find(p => p.name === name)?.key || name) + '}');
const display = text => text.replace(/{{|}}|{([#/]?)([^{}]*)}/g, (token, marker, name) =>
  name === undefined ? token : '{' + marker + (names.find(p => p.key === name)?.name || name) + '}');

async function openStudio(t, templates = {}) {
  const [source, styles] = await Promise.all([
    readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8'),
    readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/style.css', import.meta.url), 'utf8'),
  ]);
  const browser = await chromium.launch({ headless: true });
  t.after(() => browser.close());
  const page = await browser.newPage({ viewport: { width: 360, height: 740 } });
  const errors = [], writes = [], previews = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    window.confirmResult = true;
    window.confirmations = [];
    window.Telegram = { WebApp: {
      initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick() {} },
      showConfirm(text, callback) { window.confirmations.push(text); callback(window.confirmResult); },
    } };
  });
  let chat = {
    chatId: -77001, title: 'Тестовий канал', isPrivate: false,
    settings: {
      regionId: 'kyiv', partIds: [], isActive: true, templates,
      addresses: [
        { id: 1, name: 'Дім', regionId: 'kyiv', street: 'Тестова', house: '1', groups: [{ groupId: '45.1' }] },
        { id: 2, name: 'Робота', regionId: 'kyiv-oblast', street: 'Тестова', house: '2', groups: [{ groupId: '45.1' }] },
      ],
    },
  };
  let failPreview = false;
  await page.route('https://lite.test/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<meta name="viewport" content="width=device-width"><link rel="stylesheet" href="/style.css"><div id="app"></div><script type="module" src="/app.js"></script>' });
    if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
    if (path === '/style.css') return route.fulfill({ contentType: 'text/css', body: styles });
    if (path.startsWith('/preview-image-')) return route.fulfill({ contentType: 'image/png',
      body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=', 'base64') });
    let data = {};
    if (path === '/api/bootstrap') data = {
      maxTemplateLength: 1000, templateSlots: slots,
      regions: ['kyiv', 'kyiv-oblast'].map(id => ({ id, name: id, hasGroups: true, hasSchedules: true, parts: [] })),
    };
    else if (path === '/api/chats') data = [chat];
    else if (path === '/api/schedules/groups') data = [{ id: '45.1', name: '45.1' }];
    else if (path === '/api/preview') {
      const body = request.postDataJSON();
      previews.push(body);
      if (failPreview) return route.fulfill({ status: 503, json: { error: 'Сервіс недоступний' } });
      data = { html: 'м. Київ · Група 45.1<br>Приклад сповіщення', errors: body.template?.includes('{невідомо}') ? ['Невідомий параметр {невідомо}.'] : [] };
      if (body.slot === 'schedule') {
        const messages = body.imageKind === 'None'
          ? [{ html: 'Спільний графік', imageUrl: null }]
          : [1, 2].map(id => ({ html: `Група ${id}`, imageUrl: `https://lite.test/preview-image-${body.imageKind}-${id}.png` }));
        data = { ...data, ...messages[0], messages };
      }
    } else if (request.method() !== 'GET') {
      writes.push({ path, body: request.postDataJSON() });
      if (path === '/api/chats/-77001') {
        chat = { ...chat, settings: { ...chat.settings, ...request.postDataJSON() } };
        data = chat;
      }
    }
    await route.fulfill({ json: data });
  });
  await page.goto('https://lite.test/');
  await page.getByRole('tab', { name: 'Повідомлення', exact: true }).click();
  await page.locator('.studio .pvlabel').waitFor();
  return { page, errors, writes, previews, chat: () => chat, failPreview: value => { failPreview = value; } };
}

const mode = (page, name) => page.getByRole('button', { name, exact: true }).click();
const visual = page => page.locator('.visual-template');
const raw = page => page.locator('#tpl');
const save = page => page.getByRole('button', { name: 'Зберегти', exact: true });
async function waitTemplate(f, expected) {
  await f.page.waitForRequest(request => request.url().endsWith('/api/preview') && request.postDataJSON().template === canonical(expected));
}

test('ready-made styles are drafts; mode changes, Cancel and Save do not send', async t => {
  const f = await openStudio(t), { page } = f;
  assert.equal(await save(page).isDisabled(), true);
  assert.equal(await page.getByRole('button', { name: 'Стиль: Стандартний' }).getAttribute('aria-pressed'), 'true');
  if (process.env.DTEK_STUDIO_SCREENSHOT_DIR)
    await page.locator('.template-editor').screenshot({ path: join(process.env.DTEK_STUDIO_SCREENSHOT_DIR, 'ready-made-styles.png'),
      style: '.topbar,.tabs,.savebar{visibility:hidden!important}' });
  await mode(page, 'Налаштувати');
  assert.equal(await visual(page).locator('[data-template-token]').count(), 3);
  if (process.env.DTEK_STUDIO_SCREENSHOT_DIR)
    await page.locator('.template-editor').screenshot({ path: join(process.env.DTEK_STUDIO_SCREENSHOT_DIR, 'visual-editor.png'),
      style: '.topbar,.tabs,.savebar{visibility:hidden!important}' });
  await mode(page, 'Розширений');
  assert.equal(await raw(page).inputValue(), defaultAlert);
  assert.equal(await save(page).isDisabled(), true);
  await mode(page, 'Готові стилі');
  await page.getByRole('button', { name: 'Стиль: З адресою' }).click();
  assert.equal(await save(page).isEnabled(), true);
  assert.deepEqual(f.chat().settings.templates, {});
  await mode(page, 'Налаштувати');
  assert.equal(await visual(page).locator('.conditional-token').count(), 2);
  assert.equal(await visual(page).locator('[contenteditable="false"]').count(), 5);
  assert.equal(await page.getByText(/Кнопка тесту надсилає повідомлення у «Тестовий канал»/).isVisible(), true);
  await page.getByRole('button', { name: 'Скасувати', exact: true }).click();
  assert.equal(await save(page).isDisabled(), true);
  await mode(page, 'Готові стилі');
  await page.getByRole('button', { name: 'Стиль: Короткий' }).click();
  await save(page).click();
  await page.getByText('Збережено', { exact: false }).waitFor();
  assert.equal(f.chat().settings.templates.emergency, '{emoji} {message}');
  assert.equal(f.chat().settings.addresses.length, 2);
  assert.deepEqual(f.previews.at(-1).addresses.map(a => a.regionId), ['kyiv', 'kyiv-oblast']);
  assert.equal(f.writes.length, 1);
  assert.equal(f.writes[0].path, '/api/chats/-77001');
  assert.deepEqual(f.errors, []);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
});

test('legacy aliases display in Ukrainian and retain formatting with English API keys', async t => {
  const source = '<strong>{emoji}</strong> {текст}{#address}\n<b>{address}</b>{/address}\n{{message}} {{текст}} message &amp; &lt;текст&gt;\n<a href="{site}">ДТЕК</a>';
  const f = await openStudio(t, { emergency: source, local: 'Незмінний {message}' }), { page } = f;
  await visual(page).waitFor();
  await mode(page, 'Розширений');
  assert.equal(await raw(page).inputValue(), display(source));
  const visibleTokens = await page.locator('.template-editor .ph code, .template-editor .chips .chip span').allTextContents();
  assert.equal(visibleTokens.some(token => /{[#/]?[a-z]/i.test(token)), false);
  await mode(page, 'Налаштувати');
  assert.equal(await save(page).isDisabled(), true);
  const expected = source + '!';
  const request = waitTemplate(f, expected);
  await visual(page).focus();
  await page.keyboard.press('Control+End');
  await page.keyboard.type('!');
  await request;
  await mode(page, 'Розширений');
  const edited = await raw(page).inputValue();
  assert.equal(edited, display(expected));
  await save(page).click();
  await page.getByText('Збережено', { exact: false }).waitFor();
  assert.equal(f.chat().settings.templates.local, 'Незмінний {message}');
  assert.equal(f.chat().settings.templates.emergency, canonical(expected));
  assert.equal(f.previews.at(-1).template, canonical(expected));
  assert.equal(f.writes.at(-1).body.templates.emergency, canonical(expected));
  assert.deepEqual(f.errors, []);
});

test('visual formatting, links, undo, paste escaping and data insertion serialize to existing syntax', async t => {
  const f = await openStudio(t, { emergency: '{текст} Оновлення' }), { page } = f;
  await visual(page).waitFor();
  await visual(page).evaluate(el => {
    el.focus();
    const text = el.lastChild, range = document.createRange();
    range.setStart(text, 1); range.setEnd(text, text.textContent.length);
    getSelection().removeAllRanges(); getSelection().addRange(range);
    el.dispatchEvent(new MouseEvent('mouseup'));
  });
  const bold = waitTemplate(f, '{текст} <b>Оновлення</b>');
  await mode(page, 'Жирний текст');
  await bold;
  const italic = waitTemplate(f, '{текст} <b><i>Оновлення</i></b>');
  await mode(page, 'Курсив');
  await italic;
  await mode(page, 'Посилання');
  await page.getByRole('textbox', { name: 'Адреса посилання' }).fill('javascript:alert(1)');
  await mode(page, 'Додати посилання');
  await page.getByText('Введіть адресу https://, http:// або tg://.', { exact: true }).waitFor();
  await page.getByRole('textbox', { name: 'Адреса посилання' }).fill('https://example.test/?a=1&b=2');
  const linked = waitTemplate(f, '{текст} <b><i><a href="https://example.test/?a=1&amp;b=2">Оновлення</a></i></b>');
  await mode(page, 'Додати посилання');
  await linked;
  await mode(page, 'Скасувати зміну тексту');
  assert.equal(await visual(page).locator('a').count(), 0);
  await mode(page, 'Повторити зміну тексту');
  assert.equal(await visual(page).locator('a').count(), 1);
  await visual(page).focus();
  await page.keyboard.press('Control+End');
  await page.getByText('+ Додати дані', { exact: true }).click();
  await mode(page, 'Додати: Регіон');
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 1);
  await page.keyboard.press('Backspace');
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 0);
  await mode(page, 'Скасувати зміну тексту');
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 1);
  const beforePaste = await raw(page).inputValue();
  await visual(page).evaluate(el => {
    const range = document.createRange(); range.selectNodeContents(el); range.collapse(false);
    getSelection().removeAllRanges(); getSelection().addRange(range);
    el.dispatchEvent(new MouseEvent('mouseup'));
    const data = new DataTransfer();
    data.setData('text/plain', '<img onerror="alert(1)"> & {literal}');
    data.setData('text/html', '<img src="https://bad.test" onerror="alert(1)">');
    el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  assert.equal(await raw(page).inputValue(), beforePaste + '&lt;img onerror="alert(1)"&gt; &amp; {{literal}}');
  assert.equal(await visual(page).locator('img').count(), 0);
  await mode(page, 'Розширений');
  assert.equal((await raw(page).inputValue()).includes('data-template'), false);
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.errors, []);
});

test('invalid legacy templates stay untouched in Advanced, validation blocks Save, preset replacement is confirmed', async t => {
  const source = '{невідомо}<img src="https://bad.test" onerror="alert(1)">';
  const f = await openStudio(t, { emergency: source }), { page } = f;
  await raw(page).waitFor();
  assert.equal(await raw(page).inputValue(), source);
  assert.equal(await page.getByRole('button', { name: 'Налаштувати', exact: true }).isDisabled(), true);
  await mode(page, 'Готові стилі');
  await page.evaluate(() => { window.confirmResult = false; });
  await page.getByRole('button', { name: 'Стиль: Короткий' }).click();
  assert.equal(f.chat().settings.templates.emergency, source);
  assert.equal(await save(page).isDisabled(), true);
  await mode(page, 'Розширений');
  const invalid = waitTemplate(f, '{невідомо}');
  await raw(page).fill('{невідомо}');
  await invalid;
  await page.getByText('Невідомий параметр {невідомо}.', { exact: true }).waitFor();
  assert.equal(await save(page).isDisabled(), true);
  const valid = waitTemplate(f, '{текст}');
  await raw(page).fill('{текст}');
  await valid;
  await mode(page, 'Налаштувати');
  assert.equal(await visual(page).locator('[data-template-token="{текст}"]').count(), 1);
  await page.evaluate(() => { window.confirmResult = true; });
  await mode(page, 'Готові стилі');
  await page.getByRole('button', { name: 'Стиль: Короткий' }).click();
  assert.equal(await raw(page).inputValue(), '{значок} {текст}');
  assert.equal((await page.evaluate(() => window.confirmations)).length, 2);
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.errors, []);
});

test('schedule parts keep their contract; visual length limit, newlines and preview failures are explicit', async t => {
  const f = await openStudio(t), { page } = f;
  await page.locator('#tab-sched').click();
  await mode(page, 'Редагувати шаблон');
  await page.getByRole('button', { name: 'Стиль: З адресою' }).click();
  await mode(page, 'Налаштувати');
  assert.equal(await visual(page).locator('.conditional-token').count(), 2);
  await page.getByRole('button', { name: 'Інтервал {відключення}', exact: true }).click();
  await page.getByRole('button', { name: 'Стиль: Короткий' }).click();
  await mode(page, 'Налаштувати');
  assert.equal(await page.getByRole('button', { name: 'Жирний текст', exact: true }).count(), 0);
  await visual(page).focus();
  await page.keyboard.press('Control+A');
  await page.keyboard.press('Control+B');
  await page.getByText('Інтервал графіка підтримує лише текст і дані, без форматування.', { exact: true }).waitFor();
  await page.keyboard.press('Control+End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Текст');
  assert.equal(await raw(page).inputValue(), '{початок}–{кінець}\nТекст');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Ще');
  assert.equal(await raw(page).inputValue(), '{початок}–{кінець}\nТекст\n\nЩе');
  await visual(page).evaluate(el => {
    const data = new DataTransfer(); data.setData('text/plain', 'x'.repeat(1001));
    el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  await page.getByText('Текст задовгий. Скоротіть його; останню зміну скасовано.', { exact: true }).waitFor();
  assert.equal(await raw(page).inputValue(), '{початок}–{кінець}\nТекст\n\nЩе');
  assert.equal(f.previews.some(p => p.slot === 'schedule-heading' || p.slot === 'schedule-line'), false);
  assert.equal(f.previews.at(-1).scheduleHeadingTemplate.includes('{#address}'), true);
  f.failPreview(true);
  await mode(page, 'Розширений');
  await page.getByText(/Не вдалося отримати попередній перегляд: Сервіс недоступний/).waitFor();
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.errors, []);
});

test('light periods are an opt-in draft; separate light rows preview and persist without sending', async t => {
  const f = await openStudio(t), { page } = f;
  await page.locator('#tab-sched').click();
  await mode(page, 'Редагувати шаблон');
  const toggle = () => page.getByRole('switch', { name: 'Показувати періоди зі світлом', exact: true });
  assert.equal(await toggle().isChecked(), false);
  const preview = page.waitForRequest(r => r.url().endsWith('/api/preview') && r.postDataJSON().scheduleShowLightPeriods === true);
  await toggle().check();
  assert.equal((await preview).postDataJSON().slot, 'schedule');
  assert.deepEqual(f.writes, []);
  await page.getByRole('button', { name: 'Інтервал зі світлом', exact: true }).click();
  await mode(page, 'Налаштувати');
  assert.equal(await page.getByRole('button', { name: 'Жирний текст', exact: true }).count(), 0);
  await mode(page, 'Розширений');
  const text = '🟢 {початок} → {кінець} · {тривалість_інтервалу}';
  const rowPreview = page.waitForRequest(r => r.url().endsWith('/api/preview')
    && r.postDataJSON().scheduleLightLineTemplate === canonical(text));
  await raw(page).fill(text);
  assert.equal((await rowPreview).postDataJSON().scheduleShowLightPeriods, true);
  assert.deepEqual(f.writes, []);
  await save(page).click();
  await page.getByText('Збережено ✓', { exact: true }).waitFor();
  assert.equal(f.chat().settings.scheduleShowLightPeriods, true);
  assert.equal(f.chat().settings.templates['schedule-light-line'], canonical(text));
  assert.deepEqual(f.writes.map(w => w.path), ['/api/chats/-77001']);
  await page.reload();
  await page.locator('#tab-msg').click();
  await page.locator('#slot-schedule').click();
  assert.equal(await toggle().isChecked(), true);
  await toggle().uncheck();
  await page.getByRole('button', { name: 'Скасувати', exact: true }).click();
  await page.locator('#slot-schedule').click();
  assert.equal(await toggle().isChecked(), true);
  assert.deepEqual(f.errors, []);
});

test('Send test names its destination and confirms saving drafts; all other editor actions stay preview-only', async t => {
  const f = await openStudio(t), { page } = f;
  await page.getByRole('button', { name: 'Стиль: Короткий' }).click();
  await page.evaluate(() => { window.confirmResult = false; });
  await mode(page, 'Надіслати тест у канал');
  assert.deepEqual(f.writes, []);
  assert.equal((await page.evaluate(() => window.confirmations)).at(-1), 'Зберегти всі зміни й надіслати тест у «Тестовий канал»?');
  await page.evaluate(() => { window.confirmResult = true; });
  await mode(page, 'Надіслати тест у канал');
  await page.getByText('Надіслано ✓', { exact: true }).waitFor();
  assert.deepEqual(f.writes.map(w => w.path), ['/api/chats/-77001', '/api/chats/-77001/templates/emergency/send']);
  assert.equal(f.writes[1].body.template, '{emoji} {message}');
  assert.deepEqual(f.errors, []);
});

test('formatting buttons can remove formatting from only the selected text', async t => {
  const f = await openStudio(t, { emergency: '{текст} <b>Перший Другий</b>' }), { page } = f;
  await visual(page).waitFor();
  await visual(page).evaluate(el => {
    el.focus();
    const text = el.querySelector('b').firstChild, range = document.createRange();
    range.setStart(text, 7); range.setEnd(text, text.textContent.length);
    getSelection().removeAllRanges(); getSelection().addRange(range);
    el.dispatchEvent(new MouseEvent('mouseup'));
  });
  await mode(page, 'Жирний текст');
  assert.equal(await raw(page).inputValue(), '{текст} <b>Перший </b>Другий');
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.errors, []);
});

test('structured-only style and backend data chips save without popup or prepared text', async t => {
  const f = await openStudio(t), { page } = f;
  await page.getByRole('button', { name: 'Стиль: Лише дані' }).click();
  assert.equal(await raw(page).inputValue(), '{регіон} · {місто}\nГрупа: {група}\n{статус}');
  await mode(page, 'Налаштувати');
  await page.getByText('+ Додати дані', { exact: true }).click();
  await mode(page, 'Додати: Код статусу');
  await mode(page, 'Додати: Код регіону');
  assert.equal(await visual(page).locator('[data-template-token="{код_статусу}"]').count(), 1);
  assert.equal(await visual(page).locator('[data-template-token="{код_регіону}"]').count(), 1);
  await save(page).click();
  await page.getByText('Збережено', { exact: false }).waitFor();
  const saved = f.chat().settings.templates.emergency;
  for (const token of ['{region}', '{city}', '{group}', '{status}', '{statusCode}', '{regionId}'])
    assert.equal(saved.includes(token), true);
  assert.equal(saved.includes('{текст}'), false);
  assert.equal(saved.includes('{текст_сайту}'), false);
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.errors, []);
});

test('all schedule image choices remain available for multiple addresses and preview separate posts', async t => {
  const f = await openStudio(t), { page } = f;
  await page.locator('#tab-sched').click();
  const selector = page.getByRole('group', { name: 'Картинка', exact: true });
  await selector.getByRole('button', { name: 'Таблиця на 2 дні', exact: true }).waitFor();
  assert.equal(await selector.getByRole('button').count(), 3);
  await page.locator('.tgphoto img').first().waitFor();
  assert.equal(await page.locator('.tgmsg:visible').count(), 2);
  assert.equal(f.writes.length, 0);
  await selector.getByRole('button', { name: 'Коротка картинка', exact: true }).click();
  await page.waitForFunction(() => [...document.querySelectorAll('.tgphoto img')].some(img => img.src.includes('Card')));
  assert.equal(f.previews.at(-1).imageKind, 'Card');
  assert.equal(f.writes.length, 0);
  await page.getByRole('button', { name: 'Скасувати', exact: true }).click();
  await page.locator('#tab-sched').click();
  assert.equal(await selector.getByRole('button', { name: 'Таблиця на 2 дні', exact: true }).getAttribute('aria-pressed'), 'true');
  await selector.getByRole('button', { name: 'Без картинки', exact: true }).click();
  await page.getByText('Спільний графік', { exact: true }).waitFor();
  assert.equal(await page.locator('.tgphoto img').count(), 0);
  assert.equal(await page.locator('.tgmsg:visible').count(), 1);
  await save(page).click();
  await page.waitForFunction(() => document.querySelector('.savebar button')?.disabled);
  assert.equal(f.writes.at(-1).body.schedulePostImage, 'None');
  assert.equal(f.writes.some(w => w.path.endsWith('/send')), false);
  assert.deepEqual(f.errors, []);
});

test('mobile input deletes protected tokens without keydown or losing editor focus', async t => {
  const f = await openStudio(t, { emergency: 'Текст <b>{регіон}</b>{місто}' }), { page } = f;
  await visual(page).waitFor();
  await visual(page).locator('[data-template-token="{регіон}"]').dispatchEvent('pointerdown', { pointerType: 'touch' });
  assert.equal(await visual(page).evaluate(el => document.activeElement === el), true);
  const deleted = await visual(page).evaluate(el => el.dispatchEvent(new InputEvent('beforeinput', {
    inputType: 'deleteContentBackward', bubbles: true, cancelable: true,
  })));
  assert.equal(deleted, false);
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 0);
  assert.equal(await visual(page).evaluate(el => document.activeElement === el), true);
  assert.equal(await raw(page).inputValue(), 'Текст {місто}');
  await mode(page, 'Скасувати зміну тексту');
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 1);
  await visual(page).evaluate(el => {
    el.focus();
    const token = el.querySelector('[data-template-token="{місто}"]');
    const caret = document.createRange(); caret.setStartBefore(token); caret.collapse(true);
    getSelection().removeAllRanges(); getSelection().addRange(caret);
    el.dispatchEvent(new InputEvent('beforeinput', { inputType: 'deleteContentForward', bubbles: true, cancelable: true }));
  });
  assert.equal(await visual(page).locator('[data-template-token="{місто}"]').count(), 0);
  assert.equal(await visual(page).locator('[data-template-token="{регіон}"]').count(), 1);
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.errors, []);
});
