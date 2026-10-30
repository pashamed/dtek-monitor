import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

for (const collectionId of ['cycle-test', '']) test(`shared collector returns raw observations without Yasno requests (${collectionId || 'backup'})`, async () => {
  const preload = `
    import { registerHooks } from 'node:module';
    const browser = \`
      let active = 0;
      export const chromium = { launch: async () => ({
        newContext: async () => ({
          newPage: async () => ({
            goto: async () => { active++; console.log('ACTIVE:' + active); await new Promise(r => setTimeout(r, 20)); active--; },
            waitForSelector: async () => {},
            waitForLoadState: async () => {},
            locator: () => ({ evaluateAll: async () => 'Екстрені відключення' }),
            evaluate: async () => null
          }),
          close: async () => {}
        }),
        close: async () => {}
      }) };
    \`;
    registerHooks({
      resolve(specifier, context, nextResolve) {
        if (specifier === 'playwright') return {url:'data:text/javascript,' + encodeURIComponent(browser), shortCircuit:true};
        if (specifier === './popup-llm.js') throw new Error('Regional collection must not invoke the legacy LLM parser');
        return nextResolve(specifier, context);
      }
    });
    globalThis.fetch = async (url, options) => {
      if (url !== 'https://ingest.test/ingest') throw new Error('Unexpected network request: ' + url);
      console.log('OBSERVATION:' + options.body);
      return {ok:true,status:200,text:async () => '{}'};
    };
  `;
  const { stdout } = await promisify(execFile)(process.execPath, [
    '--import', 'data:text/javascript,' + encodeURIComponent(preload),
    fileURLToPath(new URL('./ingest-regions.js', import.meta.url)),
  ], {
    env: {
      ...process.env,
      INGEST_URL: 'https://ingest.test/ingest', INGEST_SECRET: 'test',
      DTEK_COLLECTION_ID: collectionId, DTEK_REGION_IDS: '["kyiv","kyiv-oblast"]',
    },
    timeout: 10000,
  });
  if (collectionId) assert.match(stdout, /ACTIVE:2/);
  const observations = stdout.split('\n').filter(line => line.startsWith('OBSERVATION:'))
    .map(line => JSON.parse(line.slice('OBSERVATION:'.length)));
  assert.deepEqual(observations.map(r => r.regionId).sort(), ['kyiv', 'kyiv-oblast']);
  assert.ok(observations.every(r => (r.collectionId ?? '') === collectionId && r.popupText === 'Екстрені відключення'));
  assert.ok(observations.every(r => !Object.hasOwn(r, 'structured')));
});

test('address directory drops cities without streets before ingest', async () => {
  const directory = { 'м. Добре': ['вул. Шкільна', ' '], 'с. Порожнє': [], 'с. Без даних': null, ' ': ['вул. Зайва'] };
  const preload = `
    import { registerHooks } from 'node:module';
    const browser = \`
      export const chromium = { launch: async () => ({
        newContext: async () => ({
          newPage: async () => ({
            goto: async () => {},
            waitForSelector: async () => {},
            waitForLoadState: async () => {},
            locator: () => ({ evaluateAll: async () => '' }),
            evaluate: async () => (${JSON.stringify(directory)})
          }),
          close: async () => {}
        }),
        close: async () => {}
      }) };
    \`;
    registerHooks({
      resolve(specifier, context, nextResolve) {
        if (specifier === 'playwright') return {url:'data:text/javascript,' + encodeURIComponent(browser), shortCircuit:true};
        if (specifier === './popup-llm.js') throw new Error('Regional collection must not invoke the legacy LLM parser');
        return nextResolve(specifier, context);
      }
    });
    globalThis.fetch = async (url, options) => {
      if (url === 'https://ingest.test/ingest/address-directory') console.log('DIRECTORY:' + options.body);
      else if (url !== 'https://ingest.test/ingest') throw new Error('Unexpected network request: ' + url);
      return {ok:true,status:200,text:async () => '{}'};
    };
  `;
  const { stdout } = await promisify(execFile)(process.execPath, [
    '--import', 'data:text/javascript,' + encodeURIComponent(preload),
    fileURLToPath(new URL('./ingest-regions.js', import.meta.url)),
  ], {
    env: { ...process.env, INGEST_URL: 'https://ingest.test/ingest', INGEST_SECRET: 'test', DTEK_COLLECTION_ID: '', DTEK_REGION_IDS: '["donetsk"]' },
    timeout: 10000,
  });
  const line = stdout.split('\n').find(l => l.startsWith('DIRECTORY:'));
  assert.ok(line, stdout);
  assert.deepEqual(JSON.parse(line.slice('DIRECTORY:'.length)), {
    regionId: 'donetsk',
    streets: { 'м. Добре': ['вул. Шкільна'] },
  });
  assert.match(stdout, /skipped 3 cities without streets/);
});
