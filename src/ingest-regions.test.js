import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

test('browser fallback checks selected regions concurrently and returns correlated observations', async () => {
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
        if (specifier === './popup-llm.js') return {url:'data:text/javascript,export async function structurePopup(){return null}', shortCircuit:true};
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
      DTEK_COLLECTION_ID: 'cycle-test', DTEK_REGION_IDS: '["kyiv","kyiv-oblast"]',
    },
    timeout: 10000,
  });
  assert.match(stdout, /ACTIVE:2/);
  const observations = stdout.split('\n').filter(line => line.startsWith('OBSERVATION:'))
    .map(line => JSON.parse(line.slice('OBSERVATION:'.length)));
  assert.deepEqual(observations.map(r => r.regionId).sort(), ['kyiv', 'kyiv-oblast']);
  assert.ok(observations.every(r => r.collectionId === 'cycle-test' && r.popupText === 'Екстрені відключення'));
});
