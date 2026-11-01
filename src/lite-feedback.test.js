import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

test('feedback is sent only on submit, includes app/UI versions and shows delivery errors', async () => {
  const source = await readFile(new URL('../bot/src/DtekBot.Api/wwwroot/lite/app.js', import.meta.url), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = [], requests = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.Telegram = { WebApp: {
        initData: 'test', ready() {}, expand() {},
        BackButton: { show() {}, hide() {}, onClick() {} },
        HapticFeedback: { notificationOccurred() {} },
      } };
    });
    await page.route('https://lite.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname;
      if (path === '/') {
        return route.fulfill({ contentType: 'text/html', body:
          '<meta name="app-version" content="3.2.1"><div id="app"></div><script type="module" src="/app.js?v=0123456789abcdef"></script>' });
      }
      if (path === '/app.js') return route.fulfill({ contentType: 'text/javascript', body: source });
      if (path === '/api/bootstrap') return route.fulfill({ json: { regions: [], templateSlots: [] } });
      if (path === '/api/chats') return route.fulfill({ json: [] });
      if (path === '/api/feedback' && request.method() === 'POST') {
        requests.push({ body: request.postDataJSON(), authorization: request.headers().authorization });
        if (requests.length === 1) {
          return route.fulfill({ status: 502, json: { error: 'Адміністратор має відкрити бота й натиснути /start.' } });
        }
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ json: {} });
    });

    await page.goto('https://lite.test/');
    await page.getByText('Версія застосунку: 3.2.1 · Версія інтерфейсу: 0123456789abcdef', { exact: true }).waitFor();
    assert.deepEqual(requests, []);
    await page.getByRole('button', { name: 'Надіслати відгук', exact: true }).click();
    assert.deepEqual(requests, []);
    await page.getByLabel('Текст відгуку', { exact: true }).fill('Please clarify the schedule preview.');

    const failed = page.waitForResponse(response => response.url().endsWith('/api/feedback') && response.status() === 502);
    await page.getByRole('button', { name: 'Надіслати', exact: true }).click();
    await failed;
    await page.locator('.feedback-status').getByText('Адміністратор має відкрити бота й натиснути /start.', { exact: true }).waitFor();
    assert.equal(requests.length, 1);
    assert.equal(requests[0].authorization, 'tma test');
    assert.deepEqual(requests[0].body, {
      text: 'Please clarify the schedule preview.',
      appVersion: '3.2.1',
      uiVersion: '0123456789abcdef',
    });

    const delivered = page.waitForResponse(response => response.url().endsWith('/api/feedback') && response.status() === 204);
    await page.getByRole('button', { name: 'Надіслати', exact: true }).click();
    await delivered;
    await page.locator('.feedback-status').getByText('Відгук надіслано. Дякуємо!', { exact: true }).waitFor();
    assert.equal(requests.length, 2);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
  }
});
