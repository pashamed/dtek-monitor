import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const base = process.env.DTEK_UI_URL;
const auth = process.env.DTEK_UI_AUTH;
assert.ok(base?.startsWith('http://127.0.0.1:') && auth, 'Local test harness required');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const errors = [];
  const apiWrites = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => {
    if (r.url().includes('/api/') && r.method() !== 'GET')
      apiWrites.push({ url: new URL(r.url()).pathname, method: r.method() });
  });
  await page.route('https://telegram.org/**', route => route.fulfill({ body: '' }));
  await page.addInitScript(initData => {
    if (!localStorage.getItem('dtek-editor-position'))
      localStorage.setItem('dtek-editor-position', JSON.stringify({ chatId: '-1004471996729', tab: 'main' }));
    window.__testConfirmations = [];
    window.__testConfirmAnswers = [];
    window.__testBack = null;
    window.Telegram = { WebApp: {
      initData, ready() {}, expand() {},
      BackButton: { show() {}, hide() {}, onClick(callback) { window.__testBack = callback; } },
      showConfirm(text, callback) {
        window.__testConfirmations.push(text);
        callback(window.__testConfirmAnswers.length ? window.__testConfirmAnswers.shift() : true);
      },
      showAlert(_text, callback) { callback?.(); },
    } };
  }, auth);
  await page.goto(`${base}/lite/index.html`);
  await page.getByRole('tab', { name: 'Основне', exact: true }).waitFor();
  const savedChannel = async () => {
    const response = await page.request.get(`${base}/api/chats`, { headers: { Authorization: `tma ${auth}` } });
    assert.equal(response.status(), 200);
    return (await response.json()).find(c => String(c.chatId) === '-1004471996729');
  };
  const save = async () => {
    const response = page.waitForResponse(r => r.url().endsWith('/api/chats/-1004471996729')
      && r.request().method() === 'PUT');
    await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
    assert.equal((await response).status(), 200);
  };
  const originalChannel = await savedChannel();
  const bootstrapResponse = await page.request.get(`${base}/api/bootstrap`, { headers: { Authorization: `tma ${auth}` } });
  assert.equal(bootstrapResponse.status(), 200);
  const defaults = new Map((await bootstrapResponse.json()).templateSlots.map(s => [s.id, s.defaultTemplate]));
  const effectiveTemplate = (channel, slot) => channel.settings.templates[slot] ?? defaults.get(slot);
  await page.getByRole('heading', { name: 'Діагностика доставки', exact: true }).waitFor();
  await page.getByRole('switch', { name: 'Пауза', exact: true }).check({ force: true });
  await page.getByRole('tab', { name: 'Графік', exact: true }).click();
  const beforeViewerWrites = apiWrites.length;
  const viewer = page.getByRole('dialog', { name: 'Переглянути графік', exact: true });
  const openViewer = async () => {
    const response = page.waitForResponse(r => r.url().includes('/api/schedules/view?'));
    await page.getByRole('button', { name: 'Переглянути графік', exact: true }).click();
    assert.equal((await response).status(), 200);
    await viewer.getByRole('heading', { name: 'Сьогодні · 2026-10-15', exact: true }).waitFor();
  };
  await openViewer();
  await viewer.getByText('Планове відключення: 11:00–14:00', { exact: true }).waitFor();
  await viewer.getByText(/До планового завершення відключення:/).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.route('**/api/schedules/view?*', async route => {
    const response = await route.fetch();
    const payload = await response.json();
    payload.isCached = true;
    payload.warnings = ['ТЕСТ: екстрені обмеження; план може не діяти'];
    payload.days[1] = { ...payload.days[1], available: false, intervals: [] };
    payload.nextChangeAt = new Date(Date.parse(payload.serverNow) + 1500).toISOString();
    payload.nextChangeIsOff = true;
    await route.fulfill({ response, json: payload });
  });
  await viewer.getByRole('button', { name: 'Оновити графік', exact: true }).click();
  await viewer.getByRole('alert').filter({ hasText: 'ТЕСТ: екстрені обмеження' }).waitFor();
  await viewer.getByText(/Кешовані дані:/).waitFor();
  await viewer.getByText('Повного графіка на цей день ще немає. Це не означає відсутність відключень.', { exact: true }).waitFor();
  await viewer.getByText('Час планової зміни настав. Оновіть графік; фактичний стан невідомий.', { exact: true }).waitFor();
  await page.unroute('**/api/schedules/view?*');
  await page.route('**/api/schedules/view?*', route => route.fulfill({
    status: 503, contentType: 'application/json',
    body: JSON.stringify({ error: 'ТЕСТ: джерело графіка недоступне' }),
  }));
  await viewer.getByRole('button', { name: 'Оновити графік', exact: true }).click();
  await viewer.getByRole('alert').filter({ hasText: 'ТЕСТ: джерело графіка недоступне' }).waitFor();
  await page.unroute('**/api/schedules/view?*');
  const retryView = page.waitForResponse(r => r.url().includes('/api/schedules/view?'));
  await viewer.getByRole('button', { name: 'Оновити графік', exact: true }).click();
  assert.equal((await retryView).status(), 200);
  await viewer.getByText(/До планового завершення відключення:/).waitFor();
  await viewer.getByRole('combobox', { name: 'Регіон для перегляду', exact: true }).selectOption('kyiv-oblast');
  await viewer.getByRole('alert').filter({ hasText: /Графіки тимчасово недоступні/ }).waitFor();
  await viewer.getByRole('combobox', { name: 'Регіон для перегляду', exact: true }).selectOption('kyiv');
  await viewer.getByRole('heading', { name: /м. Київ · черга/ }).waitFor();
  await page.keyboard.press('Escape');
  await viewer.waitFor({ state: 'detached' });
  await openViewer();
  await page.evaluate(() => window.__testBack());
  await viewer.waitFor({ state: 'detached' });
  assert.equal(apiWrites.length, beforeViewerWrites);
  assert.deepEqual(await savedChannel(), originalChannel);
  await page.getByRole('tab', { name: 'Основне', exact: true }).click();
  assert.equal(await page.getByRole('switch', { name: 'Пауза', exact: true }).isChecked(), true);
  await page.getByRole('switch', { name: 'Пауза', exact: true }).uncheck({ force: true });
  console.log('PASS real API viewer days/countdown; injected stale/emergency/missing-day/expiry; region errors/retry/Escape/Telegram Back; no writes and editor draft preserved');

  const beforePresets = await savedChannel();
  const beforePresetWrites = apiWrites.length;
  await page.evaluate(() => window.__testConfirmAnswers.push(false));
  await page.getByRole('button', { name: 'Стислі тексти сповіщень', exact: true }).click();
  assert.deepEqual(await savedChannel(), beforePresets);
  assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
  await page.evaluate(() => window.__testConfirmAnswers.push(true));
  await page.getByRole('button', { name: 'Стислі тексти сповіщень', exact: true }).click();
  assert.deepEqual(await savedChannel(), beforePresets);
  assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isEnabled(), true);
  assert.equal(apiWrites.filter(r => r.method === 'PUT').length,
    apiWrites.slice(0, beforePresetWrites).filter(r => r.method === 'PUT').length);
  await save();
  const compactSaved = await savedChannel();
  assert.notEqual(compactSaved.settings.templates.emergency, beforePresets.settings.templates.emergency);
  for (const slot of ['schedule', 'schedule-heading', 'schedule-line', 'off', 'on'])
    assert.equal(effectiveTemplate(compactSaved, slot), effectiveTemplate(beforePresets, slot));
  assert.equal(compactSaved.settings.schedulePostMode, beforePresets.settings.schedulePostMode);
  await page.reload();
  await page.getByRole('tab', { name: 'Основне', exact: true }).waitFor();
  await page.evaluate(() => window.__testConfirmAnswers.push(false));
  await page.getByRole('button', { name: 'Лише аварійні та екстрені', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
  await page.evaluate(() => window.__testConfirmAnswers.push(true));
  await page.getByRole('button', { name: 'Лише аварійні та екстрені', exact: true }).click();
  assert.deepEqual(await savedChannel(), compactSaved);
  await save();
  const emergencySaved = await savedChannel();
  assert.equal(emergencySaved.settings.schedulePost, false);
  assert.equal(emergencySaved.settings.scheduleReminder, false);
  assert.deepEqual(emergencySaved.settings.templates, compactSaved.settings.templates);
  assert.equal(emergencySaved.settings.isPaused, compactSaved.settings.isPaused);
  await page.reload();
  await page.getByRole('tab', { name: 'Основне', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => window.__testConfirmations.length), 0);
  console.log('PASS both presets require explicit confirm, cancel is inert, staging does not save; save/reload preserves schedule/custom templates and modes');

  await page.getByRole('switch', { name: 'Пауза', exact: true }).check({ force: true });
  await save();
  await page.reload();
  assert.equal(await page.getByRole('switch', { name: 'Пауза', exact: true }).isChecked(), true);
  await page.getByRole('switch', { name: 'Пауза', exact: true }).uncheck({ force: true });
  await save();
  console.log('PASS pause save/reload/resume through browser');
  await page.getByRole('button', { name: 'Дарницький район', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: 'Зберегти', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: 'Дарницький район', exact: true }).click();
  await page.getByRole('switch', { name: 'Реагувати на «Світло зникло»', exact: true }).check({ force: true });
  await page.getByRole('textbox', { name: 'Текст повідомлення «світла немає»', exact: true }).fill('ТЕСТ світло зникло');
  await save();
  await page.reload();
  assert.equal(await page.getByRole('textbox', { name: 'Текст повідомлення «світла немає»', exact: true }).inputValue(), 'ТЕСТ світло зникло');
  await page.getByRole('switch', { name: 'Пауза', exact: true }).check({ force: true });
  await page.route('**/api/chats/-1004471996729', async route => {
    if (route.request().method() !== 'PUT') return route.continue();
    await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'ТЕСТ: тимчасова помилка збереження' }) });
  });
  const failedSave = page.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 503);
  await page.getByRole('button', { name: 'Зберегти', exact: true }).click();
  await failedSave;
  await page.getByRole('alert').filter({ hasText: 'ТЕСТ: тимчасова помилка збереження' }).waitFor();
  await page.unroute('**/api/chats/-1004471996729');
  await save();
  await page.reload();
  assert.equal(await page.getByRole('switch', { name: 'Пауза', exact: true }).isChecked(), true);
  await page.getByRole('switch', { name: 'Пауза', exact: true }).uncheck({ force: true });
  await save();
  console.log('PASS empty district selection blocked; trigger saved; injected failed save shown and retry persisted');
  await page.getByRole('textbox', { name: 'Назва', exact: true }).fill('Тест будинок UI');
  await page.getByRole('combobox', { name: 'Район адреси', exact: true }).selectOption('darnytskyi');
  const addresses = page.waitForResponse(r => r.url().endsWith('/addresses') && r.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Зберегти адреси', exact: true }).click();
  assert.equal((await addresses).status(), 200);
  await page.reload();
  assert.equal(await page.getByRole('textbox', { name: 'Назва', exact: true }).inputValue(), 'Тест будинок UI');
  assert.equal(await page.getByRole('combobox', { name: 'Район адреси', exact: true }).inputValue(), 'darnytskyi');
  await page.getByRole('button', { name: '+ Додати адресу', exact: true }).click();
  await page.getByRole('button', { name: '+ Додати адресу', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: '+ Додати адресу', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: 'Прибрати адресу', exact: true }).last().click();
  await page.getByRole('button', { name: 'Прибрати адресу', exact: true }).last().click();
  console.log('PASS address name/district save/reload, three-address limit and draft removal');

  await page.getByRole('tab', { name: 'Графік', exact: true }).click();
  await page.getByRole('switch', { name: 'Публікувати графік', exact: true }).check({ force: true });
  await page.getByRole('button', { name: 'Видалити старе й опублікувати нове', exact: true }).click();
  await page.getByRole('switch', { name: 'Надсилати попередження', exact: true }).check({ force: true });
  await page.getByRole('combobox', { name: 'За скільки хвилин попередити', exact: true }).selectOption('30');
  await save();
  await page.reload();
  assert.equal(await page.getByRole('switch', { name: 'Публікувати графік', exact: true }).isChecked(), true);
  assert.equal(await page.getByRole('combobox', { name: 'За скільки хвилин попередити' }).inputValue(), '30');
  console.log('PASS schedule publishing/reminder controls persist after reload');

  await page.getByRole('tab', { name: 'Повідомлення', exact: true }).click();
  await page.getByRole('switch', { name: 'Надсилати автоматичні повідомлення без звуку', exact: true }).check({ force: true });
  await page.getByLabel('Початок тихих годин', { exact: true }).fill('22:15');
  await page.getByLabel('Завершення тихих годин', { exact: true }).fill('07:45');
  await page.getByRole('switch', { name: 'Видаляти попереднє аварійне сповіщення', exact: true }).check({ force: true });
  await page.getByRole('button', { name: 'Знято', exact: true }).click();
  await page.getByRole('button', { name: 'Змінити початкове повідомлення', exact: true }).click();
  await save();
  await page.reload();
  assert.equal(await page.getByLabel('Початок тихих годин', { exact: true }).inputValue(), '22:15');
  assert.equal(await page.getByLabel('Завершення тихих годин', { exact: true }).inputValue(), '07:45');
  console.log('PASS quiet-hours/cleanup/clearance mode saved through real API');

  await page.locator('#slot-emergency').click();
  const template = page.getByRole('textbox', { name: 'Шаблон: Екстрені відключення', exact: true });
  await template.fill('🧪 ТЕСТ UI {невідома_змінна}');
  await page.getByRole('button', { name: 'Зберегти', exact: true }).waitFor();
  await page.waitForFunction(() => document.querySelector('.savebar button.primary')?.disabled === true);
  await template.fill('🧪 ТЕСТ UI\n{регіон}\n{район}\n{текст}');
  const preview = await page.waitForResponse(r => r.url().endsWith('/api/preview') && r.request().method() === 'POST');
  assert.equal(preview.status(), 200);
  await save();
  const sent = page.waitForResponse(r => r.url().endsWith('/templates/emergency/send'));
  await page.getByRole('button', { name: 'Надіслати тест у канал', exact: true }).click();
  assert.equal((await sent).status(), 200);
  console.log('PASS invalid placeholder blocked; valid geographic template preview/save/test-send');

  await page.getByRole('tab', { name: 'Графік', exact: true }).click();
  const schedule = page.waitForResponse(r => r.url().endsWith('/schedules/send'));
  await page.getByRole('button', { name: 'Надіслати графік у канал зараз', exact: true }).click();
  assert.equal((await schedule).status(), 200);
  await mkdir('artifacts', { recursive: true });
  await page.screenshot({ path: 'artifacts/live-ui-mobile.png', fullPage: true });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: 'artifacts/live-ui-desktop.png', fullPage: true });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  assert.deepEqual(errors, []);
  console.log('PASS manual schedule delivery to Telegram and mobile render without JavaScript errors');
} finally {
  await browser.close();
}
