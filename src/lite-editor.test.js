import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

test('read-only viewer, saved diagnostics and opt-in presets preserve subscription drafts', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = [], writes = [], viewRequests = [];
    page.on('pageerror', error => errors.push(error.message));
    const settings = {
      regionId: 'kyiv', groupId: '1.1', isActive: false, isPaused: true,
      partIds: [], addresses: [], schedulePost: true, scheduleNotify: true, scheduleReminder: true,
      schedulePostMode: 'ReplaceDelete', scheduleEventMode: 'New', schedulePostImage: 'None',
      templates: { emergency: 'Власний текст', 'schedule-heading': 'Власний графік', 'before-off': 'Власне попередження' },
      slotSettings: Object.fromEntries(['emergency', 'regional', 'local', 'ended'].map(id => [id, { enabled: false, editInPlace: false, ...(id === 'ended' ? { endStyle: 'Edit' } : {}) }])),
    };
    let chat = { chatId: -123, title: 'Тест режимів', isPrivate: false, settings };
    let failView = true, failStatus = false, releaseOld, oldStarted;
    const oldRequest = new Promise(resolve => { oldStarted = resolve; });
    await page.addInitScript(() => {
      window.confirmResult = true;
      window.viewerTimers = new Set();
      const set = window.setInterval.bind(window), clear = window.clearInterval.bind(window);
      window.setInterval = (...args) => { const id = set(...args); window.viewerTimers.add(id); return id; };
      window.clearInterval = id => { window.viewerTimers.delete(id); clear(id); };
      window.Telegram = { WebApp: {
        initData: 'test', ready() {}, expand() {},
        BackButton: { show() {}, hide() {}, onClick(callback) { window.telegramBack = callback; } },
        showConfirm(_text, callback) { callback(window.confirmResult); },
      } };
    });
    const placeholders = [
      { name: 'значок', key: 'emoji' }, { name: 'заголовок', key: 'title' },
      { name: 'місце', key: 'where' }, { name: 'текст', key: 'message' },
    ];
    const slots = [
      ...['emergency', 'regional', 'local', 'ended'].map(id => ({ id, group: id === 'regional' ? 'regional' : id === 'local' ? 'local' : 'emergency', title: id, placeholders })),
      { id: 'before-off', group: 'stabilization', title: 'Попередження', placeholders: [{ name: 'час', key: 'time' }, { name: 'хвилин', key: 'minutes' }] },
      { id: 'schedule-heading', group: 'stabilization', title: 'Заголовок графіка', placeholders: [] },
    ].map(s => ({ ...s, description: '', defaultTemplate: 'Типовий текст' }));
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<meta name="app-version" content="3.0.0"><div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (request.method() !== 'GET') writes.push({ path, body: request.postDataJSON() });
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000, templateSlots: slots,
        regions: [
          { id: 'kyiv', name: 'Київ', hasSchedules: true, hasGroups: false, parts: [] },
          { id: 'odesa', name: 'Одеса', hasSchedules: true, hasGroups: false, parts: [] },
          { id: 'unsupported', name: 'Без графіків', hasSchedules: false, hasGroups: false, parts: [] },
        ],
      };
      else if (path === '/api/chats') data = [chat, { ...chat, chatId: -456, title: 'Інший канал', settings: { isActive: false, partIds: [], addresses: [], templates: {} } }];
      else if (path === '/api/schedules/groups') data = [{ id: '1.1', name: '1.1' }, { id: '2.1', name: '2.1' }];
      else if (path === '/api/schedules/view') {
        viewRequests.push(url.search);
        assert.equal(request.headers().authorization, 'tma test');
        if (failView) { failView = false; return route.fulfill({ status: 503, json: { error: 'Джерело недоступне', details: ['Спробуйте ще раз'] } }); }
        if (url.searchParams.get('regionId') === 'kyiv' && url.searchParams.get('groupId') === '2.1') {
          oldStarted();
          await new Promise(resolve => { releaseOld = resolve; });
        }
        data = {
          regionId: url.searchParams.get('regionId'), regionName: url.searchParams.get('regionId') === 'kyiv' ? 'Київ' : 'Одеса',
          groupId: url.searchParams.get('groupId'), serverNow: '2026-10-21T09:00:00+03:00',
          checkedAt: '2026-10-21T08:55:00+03:00', isCached: true, warnings: ['Екстрені відключення: <не HTML>'],
          days: [
            { date: '2026-10-21', available: true, intervals: [{ start: '2026-10-21T10:00:00+03:00', end: '2026-10-21T12:00:00+03:00' }] },
            { date: '2026-10-22', available: false, intervals: [] },
          ],
          nextChangeAt: '2026-10-21T10:00:00+03:00', nextChangeIsOff: true,
        };
      }
      else if (path.endsWith('/status')) {
        if (path.includes('-456')) return route.fulfill({ json: { lines: ['Регіон не обрано'] } });
        if (failStatus) return route.fulfill({ status: 503, json: { error: 'Діагностика недоступна', details: ['Технічна перерва'] } });
        data = { lines: ['Поточний стан регіону'], diagnostics: { lines: ['Збережена підписка неактивна', 'Публікація графіка увімкнена'] } };
      }
      else if (path === '/api/preview') data = { html: 'Приклад', errors: [] };
      else if (path === '/api/chats/-123' && request.method() === 'PUT') {
        chat = { ...chat, settings: { ...chat.settings, ...request.postDataJSON() } };
        data = chat;
      }
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByText('Версія застосунку: 3.0.0 · Версія інтерфейсу: dev', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Переглянути графік', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Переглянути графік', exact: true });
    await dialog.getByText('Джерело недоступне Спробуйте ще раз', { exact: true }).waitFor();
    assert.equal(await dialog.getByLabel('Регіон для перегляду').locator('option').count(), 2);
    await dialog.getByRole('button', { name: 'Оновити графік', exact: true }).click();
    await dialog.getByText('Екстрені відключення: <не HTML>', { exact: true }).waitFor();
    await dialog.getByText('Планове відключення: 10:00–12:00', { exact: true }).waitFor();
    await dialog.getByText(/Повного графіка на цей день ще немає/).waitFor();
    await dialog.getByText(/До планового відключення/).waitFor();
    assert.equal(await page.evaluate(() => window.viewerTimers.size), 1);
    await dialog.getByLabel('Черга для перегляду').selectOption('2.1');
    await oldRequest;
    await dialog.getByLabel('Регіон для перегляду').selectOption('odesa');
    await dialog.getByRole('heading', { name: 'Одеса · черга 1.1', exact: true }).waitFor();
    releaseOld();
    // Ensure the delayed old response completes before checking that it cannot overwrite the new plan.
    await page.waitForResponse(r => r.url().includes('/api/schedules/view?regionId=kyiv&groupId=2.1'));
    assert.equal(await dialog.getByRole('heading', { name: 'Київ · черга 2.1', exact: true }).count(), 0);
    assert.equal(writes.length, 0);
    await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => window.viewerTimers.size), 0);
    await page.getByRole('button', { name: /Тест режимів/ }).click();
    await page.getByText('Збережена підписка неактивна', { exact: true }).waitFor();
    await page.evaluate(() => { window.confirmResult = false; });
    await page.getByRole('button', { name: 'Лише аварійні та екстрені', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
    await page.evaluate(() => { window.confirmResult = true; });
    await page.getByRole('button', { name: 'Лише аварійні та екстрені', exact: true }).click();
    await page.getByText(/Є незбережена чернетка: її зміни ще не впливають/).waitFor();
    assert.equal(writes.length, 0);
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByRole('button', { name: 'Переглянути графік', exact: true }).click();
    await dialog.getByRole('heading', { name: 'Київ · черга 1.1', exact: true }).waitFor();
    await page.evaluate(() => window.telegramBack());
    assert.equal(await page.evaluate(() => window.viewerTimers.size), 0);
    assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isEnabled(), true);
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    const saved = writes.find(w => w.path === '/api/chats/-123').body;
    assert.equal(saved.isActive, false);
    assert.equal(saved.isPaused, true);
    assert.equal(saved.schedulePost, false);
    assert.equal(saved.scheduleNotify, false);
    assert.equal(saved.scheduleReminder, false);
    assert.equal(saved.schedulePostMode, 'ReplaceDelete');
    assert.equal(saved.scheduleEventMode, 'New');
    assert.deepEqual(saved.templates, settings.templates);
    assert.equal(saved.slotSettings.emergency.enabled, true);
    assert.equal(saved.slotSettings.ended.enabled, false);
    assert.equal(saved.slotSettings.ended.endStyle, 'Edit');
    await page.getByRole('tab', { name: 'Основне', exact: true }).click();
    const count = writes.length;
    await page.evaluate(() => { window.confirmResult = false; });
    await page.getByRole('button', { name: 'Стислі тексти сповіщень', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
    await page.evaluate(() => { window.confirmResult = true; });
    await page.getByRole('button', { name: 'Стислі тексти сповіщень', exact: true }).click();
    assert.equal(writes.filter(w => w.path !== '/api/preview').length, 1);
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    const compact = writes.slice(count).find(w => w.path === '/api/chats/-123').body;
    assert.equal(compact.templates.emergency, '{значок} {заголовок}\n{місце}\n{текст}');
    assert.equal(compact.templates['before-off'], '⏳ Планове відключення о {час} (через {хвилин} хв)');
    assert.equal(compact.templates['schedule-heading'], 'Власний графік');
    assert.equal(compact.isActive, false);
    assert.equal(compact.isPaused, true);
    failStatus = true;
    await page.getByRole('button', { name: 'Оновити', exact: true }).click();
    await page.getByText(/Діагностика недоступна Технічна перерва/).waitFor();
    await page.getByRole('button', { name: 'Назад', exact: true }).click();
    await page.getByRole('button', { name: /Інший канал/ }).click();
    await page.getByText('Регіон не обрано', { exact: true }).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Діагностика доставки', exact: true }).isVisible(), false);
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByRole('button', { name: 'Переглянути графік', exact: true }).click();
    await dialog.getByRole('heading', { name: 'Київ · черга 1.1', exact: true }).waitFor();
    await dialog.getByRole('button', { name: 'Закрити перегляд', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
    assert.equal(await page.evaluate(() => window.viewerTimers.size), 0);
    assert.deepEqual(errors, []);
    assert.ok(viewRequests.length >= 4);
  } finally {
    await browser.close();
  }
});

test('address search, region changes and editor restoration', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const address = { id: 1, regionId: 'kyiv-oblast', name: 'Дім', city: '', street: '', house: '', groups: [] };
    let chat = {
      chatId: -100123, title: 'Тест', isPrivate: false,
      settings: { regionId: 'kyiv-oblast', isActive: true, addresses: [address], partIds: [], templates: {} },
    };
    const regions = [
      { id: 'kyiv-oblast', name: 'Київська область', siteUrl: 'https://example.test', hasSchedules: true, hasGroups: false, parts: [] },
      { id: 'odesa', name: 'Одеська область', siteUrl: 'https://example.test', hasSchedules: true, hasGroups: false, parts: [] },
    ];
    await page.addInitScript(() => {
      window.Telegram = { WebApp: {
        initData: 'test', ready() {}, expand() {},
        BackButton: { show() {}, hide() {}, onClick() {} },
        showConfirm(_text, callback) { callback(true); },
      } };
    });
    const previews = [];
    await page.route('https://lite.test/**', async route => {
      const url = new URL(route.request().url());
      const path = url.pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      let data;
      if (path === '/api/bootstrap') data = {
        regions, maxTemplateLength: 1000,
        templateSlots: [
          ['emergency', 'emergency', 'Екстрені відключення'],
          ['regional', 'regional', 'Аварія в регіоні'],
          ['ended', 'emergency', 'Запис про відключення знято'],
          ['schedule', 'stabilization', 'Графік відключень'],
          ['schedule-heading', 'stabilization', 'Заголовок графіка'],
          ['schedule-line', 'stabilization', 'Рядок графіка'],
        ].map(([id, group, title]) => ({ id, group, title, description: '', defaultTemplate: '{текст}', placeholders: [] })),
      };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/preview') {
        previews.push(route.request().postDataJSON());
        data = { html: 'Попередній перегляд', errors: [] };
      }
      else if (path === '/api/schedules/groups') data = [{ id: '3.2', name: '3.2' }, { id: '4.1', name: '4.1' }];
      else if (path === '/api/address/cities') data = [{ name: 'м. Ірпінь' }];
      else if (path === '/api/address/streets') data = [{ name: 'вул. Західна' }];
      else if (path === '/api/address/houses') data = [{ house: '14', groupId: '3.2', groupIds: ['3.2', '4.1'] }];
      else if (path === '/api/chats/-100123' && route.request().method() === 'PUT') {
        const body = route.request().postDataJSON();
        chat = { ...chat, settings: { ...chat.settings, ...body, groupId: body.addresses[0]?.groups[0]?.groupId } };
        data = chat;
      } else data = {};
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByRole('heading', { name: 'Тест', exact: true }).waitFor();
    await page.getByRole('heading', { name: 'Керування', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Видалити зі списку', exact: true }).isVisible(), true);
    assert.equal(await page.locator('#regionSel').isEnabled(), true);
    // The collection must use the real city/street/house lookup, not only manual inputs.
    const city = page.locator('input[placeholder="Наприклад, Одеса"]');
    await city.fill('Ір');
    await page.getByRole('option', { name: 'м. Ірпінь', exact: true }).click();
    await page.locator('input[placeholder="Оберіть населений пункт спочатку"]').fill('Зах');
    await page.getByRole('option', { name: 'вул. Західна', exact: true }).click();
    await page.locator('input[placeholder="Оберіть вулицю спочатку"]').fill('14');
    await page.getByRole('option', { name: 'Будинок 14 · черга 3.2', exact: true }).click();
    assert.equal(await page.getByText('Черга 4.1', { exact: true }).count(), 1);
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.getByRole('button', { name: 'Знайти адресу', exact: true }).waitFor();
    assert.equal(chat.settings.addresses[0].street, 'вул. Західна');
    assert.equal(chat.settings.addresses[0].groups.length, 2);
    await page.getByRole('tab', { name: 'Повідомлення', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Видалити зі списку', exact: true }).isVisible(), false);
    assert.equal(await page.getByRole('tab', { name: 'Екстрені відключення', exact: true }).count(), 1);
    assert.equal(await page.getByRole('tab', { name: 'Аварія в регіоні', exact: true }).count(), 0);
    assert.equal(await page.getByRole('tab', { name: 'Запис про відключення знято', exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Знято', exact: true }).click();
    await page.getByRole('textbox', { name: 'Шаблон: Запис про відключення знято', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Аварія / район', exact: true }).click();
    await page.getByRole('textbox', { name: 'Шаблон: Аварія в регіоні', exact: true }).waitFor();
    await page.getByRole('tab', { name: 'Графік відключень', exact: true }).click();
    assert.equal(await page.getByRole('tab', { name: 'Графік відключень', exact: true }).getAttribute('aria-selected'), 'true');
    await page.getByRole('textbox', { name: 'Шаблон: Заголовок графіка', exact: true }).fill('⚡ {шапка}\n{день_тижня} {дата}');
    await page.getByRole('button', { name: 'Інтервал {відключення}', exact: true }).click();
    const previewRequest = page.waitForRequest(request => request.url().endsWith('/api/preview')
      && request.postDataJSON().scheduleLineTemplate === '🕒 {початок}  →  {кінець}');
    await page.getByRole('textbox', { name: 'Шаблон: Рядок графіка', exact: true }).fill('🕒 {початок}  →  {кінець}');
    const body = (await previewRequest).postDataJSON();
    assert.equal(body.slot, 'schedule');
    assert.equal(body.scheduleHeadingTemplate, '⚡ {шапка}\n{день_тижня} {дата}');
    assert.equal(body.template, null);
    assert.equal(previews.some(p => p.slot === 'schedule-heading' || p.slot === 'schedule-line'), false);
    await page.getByRole('textbox', { name: 'Шаблон: Рядок графіка', exact: true }).fill('{текст}');
    await page.getByRole('button', { name: 'Заголовок і дата', exact: true }).click();
    await page.getByRole('textbox', { name: 'Шаблон: Заголовок графіка', exact: true }).fill('{текст}');
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByRole('button', { name: 'Редагувати рядок графіка', exact: true }).click();
    await page.getByRole('textbox', { name: 'Шаблон: Рядок графіка', exact: true }).waitFor();
    assert.equal(await page.getByRole('tab', { name: 'Повідомлення', exact: true }).getAttribute('aria-selected'), 'true');
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.reload();
    await page.getByRole('tab', { name: 'Графік', exact: true }).waitFor();
    assert.equal(await page.getByRole('tab', { name: 'Графік', exact: true }).getAttribute('aria-selected'), 'true');
    await page.getByRole('tab', { name: 'Основне', exact: true }).click();
    await page.evaluate(() => { window.Telegram.WebApp.showConfirm = (_text, callback) => callback(false); });
    await page.locator('#regionSel').selectOption('odesa');
    assert.equal(await page.locator('#regionSel').inputValue(), 'kyiv-oblast');
    assert.equal(chat.settings.addresses.length, 1);
    await page.evaluate(() => { window.Telegram.WebApp.showConfirm = (_text, callback) => callback(true); });
    await page.locator('#regionSel').selectOption('odesa');
    await page.waitForFunction(() => document.querySelector('#regionSel').value === 'odesa');
    assert.equal(chat.settings.addresses.length, 1, 'Region draft must not clear saved addresses');
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    assert.equal(chat.settings.addresses.length, 0);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});

test('schedule setup opens the address tab and manual fallback edits only its address draft', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 360, height: 740 } });
    const errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    let chat = {
      chatId: -100888, title: 'Дві адреси', isPrivate: false,
      settings: {
        regionId: 'kyiv-oblast', isActive: true, partIds: [], templates: {},
        addresses: [
          { id: 1, regionId: 'kyiv-oblast', name: 'Дім', city: 'м. Буча', street: 'вул. Вокзальна', house: '12', groups: [] },
          { id: 2, regionId: 'kyiv-oblast', name: 'Офіс', city: '', street: '', house: '', groups: [] },
        ],
      },
    };
    await page.addInitScript(() => {
      window.Telegram = { WebApp: {
        initData: 'test', ready() {}, expand() {},
        BackButton: { show() {}, hide() {}, onClick() {} },
        showConfirm(_text, callback) { callback(true); },
      } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (request.method() !== 'GET') writes.push({ path, body: request.postDataJSON() });
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000,
        regions: [{ id: 'kyiv-oblast', name: 'Київська область', siteUrl: 'https://example.test', hasSchedules: true, hasGroups: false, parts: [] }],
        templateSlots: [{ id: 'emergency', group: 'emergency', title: 'Екстрені', defaultTemplate: '{текст}', placeholders: [] }],
      };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/schedules/groups') data = [{ id: '3.2', name: '3.2' }];
      else if (path === '/api/chats/-100888') {
        chat = { ...chat, settings: { ...chat.settings, ...request.postDataJSON() } };
        data = chat;
      }
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByRole('button', { name: 'Вказати адресу або чергу', exact: true }).click();
    assert.equal(await page.getByRole('tab', { name: 'Основне', exact: true }).getAttribute('aria-selected'), 'true');
    await page.getByLabel('Додати чергу', { exact: true }).nth(1).selectOption('3.2');
    await page.getByText('Не вдається знайти адресу? Ввести вручну', { exact: true }).click();
    const manual = page.locator('details.address-manual');
    await manual.getByRole('textbox', { name: 'Населений пункт', exact: true }).fill('м. Ірпінь');
    await manual.getByRole('textbox', { name: 'Вулиця', exact: true }).fill('вул. Західна');
    await manual.getByRole('textbox', { name: 'Будинок', exact: true }).fill('14');
    await manual.getByRole('button', { name: 'Обрати адресу', exact: true }).click();
    await page.getByText('Адресу обрано. Натисніть «Зберегти», щоб застосувати всі зміни.', { exact: true }).waitFor();
    assert.equal(writes.length, 0, 'Manual fallback must not call the legacy primary-address endpoint');
    assert.equal(chat.settings.addresses[0].house, '12');
    assert.equal(chat.settings.addresses[1].house, '');
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('button').length
      && !document.body.textContent.includes('Адресу обрано. Натисніть «Зберегти», щоб застосувати всі зміни.'));
    assert.equal(writes.length, 1);
    assert.equal(writes[0].path, '/api/chats/-100888');
    assert.equal(chat.settings.addresses[0].street, 'вул. Вокзальна');
    assert.equal(chat.settings.addresses[1].street, 'вул. Західна');
    assert.equal(chat.settings.addresses[1].house, '14');
    assert.equal(chat.settings.addresses[1].groups[0].groupId, '3.2', 'Manual entry preserves the chosen group');
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});

test('changing parent address fields ignores delayed street and house results', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  const releases = [];
  try {
    const page = await browser.newPage();
    const errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    let streetStarted, houseStarted;
    const streetRequest = new Promise(resolve => { streetStarted = resolve; });
    const houseRequest = new Promise(resolve => { houseStarted = resolve; });
    await page.addInitScript(() => {
      window.Telegram = { WebApp: { initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick() {} } } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (request.method() !== 'GET') writes.push(path);
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000,
        regions: [{ id: 'kyiv-oblast', name: 'Київська область', siteUrl: 'https://example.test', hasSchedules: false, hasGroups: false, parts: [] }],
        templateSlots: [],
      };
      else if (path === '/api/chats') data = [{
        chatId: -100999, title: 'Пошук', isPrivate: false,
        settings: { regionId: 'kyiv-oblast', isActive: true, partIds: [], templates: {},
          addresses: [{ id: 1, regionId: 'kyiv-oblast', name: 'Дім', city: '', street: '', house: '', groups: [] }] },
      }];
      else if (path === '/api/address/cities') data = [{ name: 'м. Ірпінь' }];
      else if (path === '/api/address/streets') {
        if (url.searchParams.get('query') === 'Зат') {
          streetStarted();
          await new Promise(resolve => releases.push(resolve));
          data = [{ name: 'вул. Затримана' }];
        } else data = [{ name: 'вул. Західна' }];
      }
      else if (path === '/api/address/houses') {
        houseStarted();
        await new Promise(resolve => releases.push(resolve));
        data = [{ house: '14', groupId: '3.2' }];
      }
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    const city = page.locator('input[placeholder="Наприклад, Одеса"]');
    const street = page.locator('input[placeholder="Оберіть населений пункт спочатку"]');
    const house = page.locator('input[placeholder="Оберіть вулицю спочатку"]');
    await city.fill('Ір');
    await page.getByRole('option', { name: 'м. Ірпінь', exact: true }).click();
    await street.fill('Зат');
    await streetRequest;
    await city.fill('Бу');
    const streetResponse = page.waitForResponse(response => response.url().includes('/api/address/streets'));
    releases.shift()();
    await streetResponse;
    await page.getByRole('option', { name: 'м. Ірпінь', exact: true }).waitFor();
    assert.equal(await page.getByRole('option', { name: 'вул. Затримана', exact: true }).count(), 0);
    assert.equal(await street.isDisabled(), true);
    await page.getByRole('option', { name: 'м. Ірпінь', exact: true }).click();
    await street.fill('Зах');
    await page.getByRole('option', { name: 'вул. Західна', exact: true }).click();
    await house.fill('14');
    await houseRequest;
    await street.fill('Ін');
    const houseResponse = page.waitForResponse(response => response.url().includes('/api/address/houses'));
    releases.shift()();
    await houseResponse;
    await page.getByRole('option', { name: 'вул. Західна', exact: true }).waitFor();
    assert.equal(await page.getByRole('option', { name: 'Будинок 14 · черга 3.2', exact: true }).count(), 0);
    assert.equal(await house.isDisabled(), true);
    assert.deepEqual(writes, []);
    assert.deepEqual(errors, []);
  } finally {
    for (const release of releases) release();
    await browser.close();
  }
});

test('one atomic Save covers address and region drafts; Cancel, back and failures preserve saved state', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const styles = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/style.css', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 360, height: 740 } });
    const errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    let failSave = false;
    let chat = {
      chatId: -101010, title: 'Чернетки', isPrivate: false,
      settings: { regionId: 'kyiv', isActive: true, isPaused: false, groupId: '1.1', partIds: [], templates: {},
        addresses: [{ id: 1, regionId: 'kyiv', name: 'Дім', city: 'Київ', street: 'Вулиця', house: '12', groups: [{ groupId: '1.1' }] }] },
    };
    await page.addInitScript(() => {
      window.confirmResult = true;
      window.Telegram = { WebApp: { initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick(callback) { window.telegramBack = callback; } },
        showConfirm(_text, callback) { callback(window.confirmResult); } } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<link rel="stylesheet" href="/style.css"><div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (path === '/style.css') return route.fulfill({ contentType: 'text/css', body: styles });
      if (request.method() !== 'GET') writes.push({ path, body: request.postDataJSON() });
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000, templateSlots: [],
        regions: ['kyiv', 'odesa'].map(id => ({ id, name: id, hasSchedules: true, hasGroups: false, parts: [] })),
      };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/schedules/groups') data = [{ id: '1.1', name: '1.1' }, { id: '2.1', name: '2.1' }];
      else if (path === '/api/chats/-101010') {
        if (failSave) return route.fulfill({ status: 400, json: { error: 'Перевірте адресу.' } });
        const body = request.postDataJSON();
        chat = { ...chat, settings: { ...chat.settings, ...body, groupId: body.addresses[0]?.groups[0]?.groupId || null } };
        data = chat;
      }
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    const save = page.getByRole('button', { name: 'Зберегти', exact: true });
    const cancel = page.getByRole('button', { name: 'Скасувати', exact: true });
    await page.getByRole('textbox', { name: 'Назва', exact: true }).fill('Чернетка');
    assert.equal(await save.isEnabled(), true);
    await page.evaluate(() => { window.confirmResult = false; window.telegramBack(); });
    assert.equal(await page.getByRole('textbox', { name: 'Назва', exact: true }).inputValue(), 'Чернетка');
    await page.evaluate(() => { window.confirmResult = true; });
    await cancel.click();
    assert.equal(await page.getByRole('textbox', { name: 'Назва', exact: true }).inputValue(), 'Дім');
    await page.locator('#regionSel').selectOption('odesa');
    assert.equal(chat.settings.regionId, 'kyiv');
    assert.equal(chat.settings.addresses.length, 1);
    assert.deepEqual(writes, []);
    await cancel.click();
    assert.equal(await page.locator('#regionSel').inputValue(), 'kyiv');
    assert.equal(await page.getByRole('textbox', { name: 'Назва', exact: true }).inputValue(), 'Дім');
    await page.getByRole('textbox', { name: 'Назва', exact: true }).fill('Оновлений дім');
    await page.getByRole('switch', { name: 'Пауза', exact: true }).check();
    failSave = true;
    await save.click();
    await page.getByText('Перевірте адресу.', { exact: true }).waitFor();
    assert.equal(chat.settings.addresses[0].name, 'Дім');
    assert.equal(chat.settings.isPaused, false);
    assert.equal(await page.getByRole('textbox', { name: 'Назва', exact: true }).inputValue(), 'Оновлений дім');
    failSave = false;
    await save.click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    assert.equal(chat.settings.addresses[0].name, 'Оновлений дім');
    assert.equal(chat.settings.isPaused, true);
    assert.equal(writes.length, 2);
    assert.ok(writes.every(w => w.path === '/api/chats/-101010'));
    await page.locator('#regionSel').selectOption('odesa');
    await page.getByRole('button', { name: '+ Додати адресу', exact: true }).click();
    await page.getByRole('textbox', { name: 'Назва', exact: true }).fill('Офіс');
    await page.getByLabel('Додати чергу', { exact: true }).selectOption('2.1');
    assert.equal(chat.settings.regionId, 'kyiv');
    await save.click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    assert.equal(chat.settings.regionId, 'odesa');
    assert.equal(chat.settings.addresses[0].name, 'Офіс');
    assert.equal(chat.settings.groupId, '2.1');
    assert.equal(writes.length, 3);
    const bounds = await save.boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 360, 'Save must fit a small viewport with Cancel');
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
});

test('unrelated settings edits preserve an existing legacy schedule preference without exposing controls', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const writes = [], errors = [];
    page.on('pageerror', error => errors.push(error.message));
    let chat = { chatId: 456, title: 'Private', isPrivate: true,
      settings: { regionId: 'kyiv', groupId: '1.1', isActive: true, scheduleNotify: true, addresses: [], partIds: [], templates: {} } };
    await page.addInitScript(() => {
      window.Telegram = { WebApp: { initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick() {} } } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      let data = {};
      if (path === '/api/bootstrap') data = { maxTemplateLength: 1000, templateSlots: [],
        regions: [{ id: 'kyiv', name: 'Київ', hasSchedules: true, hasGroups: false, parts: [] }] };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/schedules/groups') data = [{ id: '1.1', name: '1.1' }];
      else if (request.method() === 'PUT') {
        const body = request.postDataJSON(); writes.push(body);
        chat = { ...chat, settings: { ...chat.settings, ...body } }; data = chat;
      }
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    const events = page.getByRole('switch', { name: 'Сповіщати про плановий початок і кінець відключення', exact: true });
    assert.equal(await events.count(), 0);
    await page.getByRole('tab', { name: 'Повідомлення', exact: true }).click();
    await page.getByRole('switch', { name: 'Надсилати автоматичні повідомлення без звуку', exact: true }).check();
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    await page.getByText('Збережено ✓', { exact: true }).waitFor();
    assert.ok(writes.every(body => body.scheduleNotify === true));
    assert.equal(writes.length, 1);
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
});

test('private schedule actions identify the private destination and viewing sends nothing', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const writes = [], errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.Telegram = { WebApp: { initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick() {} } } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (request.method() !== 'GET') writes.push(path);
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000, templateSlots: [],
        regions: [{ id: 'kyiv', name: 'Київ', hasSchedules: true, hasGroups: false, parts: [] }],
      };
      else if (path === '/api/chats') data = [{
        chatId: 123, title: 'Private', isPrivate: true,
        settings: { regionId: 'kyiv', groupId: '1.1', isActive: true, addresses: [], partIds: [], templates: {} },
      }];
      else if (path === '/api/schedules/groups') data = [{ id: '1.1', name: '1.1' }];
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByRole('heading', { name: 'Графік у чаті з ботом', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Надіслати графік у канал зараз', exact: true }).count(), 0);
    assert.deepEqual(writes, []);
    await page.getByRole('button', { name: 'Надіслати графік у цей чат зараз', exact: true }).click();
    await page.getByText('Графік надіслано в цей чат.', { exact: true }).waitFor();
    assert.deepEqual(writes, ['/api/chats/123/schedules/send']);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});

test('svitlobot main group buttons pick one group of a multi-address channel', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    const addresses = [
      { id: 1, regionId: 'kyiv', name: 'Дім', groups: [{ groupId: '45.1' }] },
      { id: 2, regionId: 'kyiv', name: 'Офіс', groups: [{ groupId: '2.2', label: 'під’їзд 2' }] },
    ];
    const chat = {
      chatId: -100777, title: 'Канал', isPrivate: false,
      settings: { regionId: 'kyiv', groupId: '45.1', isActive: true, addresses, partIds: [], templates: {}, hasSvitlobotKey: true, svitlobotGroupId: '45.1' },
    };
    await page.addInitScript(() => {
      window.Telegram = { WebApp: { initData: 'test', ready() {}, expand() {}, BackButton: { show() {}, hide() {}, onClick() {} } } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (request.method() !== 'GET') writes.push({ method: request.method(), path, body: request.postDataJSON() });
      let data = {};
      if (path === '/api/bootstrap') data = {
        maxTemplateLength: 1000,
        regions: [{ id: 'kyiv', name: 'Київ', hasSchedules: true, hasGroups: false, parts: [] }],
        templateSlots: [['emergency', 'emergency'], ['ended', 'emergency']]
          .map(([id, group]) => ({ id, group, title: id, description: '', defaultTemplate: '{текст}', placeholders: [] })),
      };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/schedules/groups') data = [{ id: '45.1', name: '45.1' }, { id: '2.2', name: '2.2' }];
      else if (path === '/api/preview') data = { html: 'Приклад', errors: [] };
      else if (path.endsWith('/svitlobot/group')) data = { groupId: '2.2', message: 'Світлобот тепер отримує графік черги 2.2. Оновимо його під час наступної перевірки.' };
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    const card = page.locator('#svb');
    await card.getByText('Черга 45.1 · Дім', { exact: true }).waitFor();
    await card.getByText('Черга 2.2 · Офіс · під’їзд 2', { exact: true }).waitFor();
    assert.equal(await card.getByText('Основна', { exact: true }).count(), 1);
    assert.equal(await card.getByRole('button', { name: 'Зробити основною', exact: true }).count(), 1);

    await card.getByRole('button', { name: 'Зробити основною', exact: true }).click();
    await card.getByText(/тепер отримує графік черги 2\.2/).waitFor();
    assert.deepEqual(writes, [{ method: 'PUT', path: '/api/chats/-100777/svitlobot/group', body: { groupId: '2.2' } }]);
    await card.getByText('Світлобот отримує тижневий графік черги 2.2 автоматично.', { exact: true }).waitFor();
    assert.equal(await card.getByRole('button', { name: 'Зробити основною', exact: true }).count(), 1);
    await page.getByRole('tab', { name: 'Графік', exact: true }).click();
    await page.getByText('Усі черги — в одному дописі. Основну чергу для Світлобота оберіть у «Основне».', { exact: true }).waitFor();
    assert.equal(await page.getByText(/Світлобот отримує спільний тижневий графік/).count(), 0);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});
