import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

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
    await page.route('https://lite.test/**', async route => {
      const url = new URL(route.request().url());
      const path = url.pathname;
      if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="app"></div><script type="module" src="/app.js"></script>' });
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      let data;
      if (path === '/api/bootstrap') data = { regions, templateSlots: [] };
      else if (path === '/api/chats') data = [chat];
      else if (path === '/api/schedules/groups') data = [{ id: '3.2', name: '3.2' }, { id: '4.1', name: '4.1' }];
      else if (path === '/api/address/cities') data = [{ name: 'м. Ірпінь' }];
      else if (path === '/api/address/streets') data = [{ name: 'вул. Західна' }];
      else if (path === '/api/address/houses') data = [{ house: '14', groupId: '3.2', groupIds: ['3.2', '4.1'] }];
      else if (path.endsWith('/addresses') && route.request().method() === 'PUT') {
        const body = route.request().postDataJSON();
        chat = { ...chat, settings: { ...chat.settings, addresses: body.addresses } };
        data = chat;
      } else data = {};
      await route.fulfill({ json: data });
    });
    await page.goto('https://lite.test/');
    await page.getByRole('heading', { name: 'Тест', exact: true }).waitFor();
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
