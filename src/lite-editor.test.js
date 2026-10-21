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
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
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
      else if (path.endsWith('/addresses') && route.request().method() === 'PUT') {
        const body = route.request().postDataJSON();
        chat = { ...chat, settings: { ...chat.settings, addresses: body.addresses, groupId: body.addresses[0]?.groups[0]?.groupId } };
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
    await page.getByRole('button', { name: 'Зберегти адреси', exact: true }).click();
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
    assert.equal(chat.settings.addresses.length, 0);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});
