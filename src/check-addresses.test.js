import { test } from "node:test"
import assert from "node:assert/strict"
import { execFile } from "node:child_process"
import { promisify } from "node:util"
import { fileURLToPath } from "node:url"

async function collect(body, ok = true) {
  const preload = `
    import { registerHooks } from 'node:module';
    const browser = \`
      export const chromium = { launch: async () => ({
        newContext: async () => ({
          newPage: async () => ({
            goto: async () => {},
            waitForSelector: async () => ({ getAttribute: async () => 'test-csrf' }),
            evaluate: async (fn, args) => fn(args)
          }),
          close: async () => {}
        }),
        close: async () => {}
      }) };
    \`;
    registerHooks({
      resolve(specifier, context, nextResolve) {
        if (specifier === 'playwright') return { url: 'data:text/javascript,' + encodeURIComponent(browser), shortCircuit: true };
        return nextResolve(specifier, context);
      }
    });
    const timeout = globalThis.setTimeout;
    globalThis.setTimeout = (fn, ms, ...args) => timeout(fn, ms === 1500 ? 0 : ms, ...args);
    globalThis.fetch = async (url, options) => {
      if (url === 'https://ingest.test/ingest/addresses/pending')
        return { ok: true, json: async () => ({ streets: [{ regionId: 'kyiv', city: '', street: 'вул. Тестова', houses: ['12'] }] }) };
      if (url === '/ua/ajax')
        return { ok: ${ok}, status: ${ok ? 200 : 503}, json: async () => (${JSON.stringify(body)}) };
      if (url === 'https://ingest.test/ingest/addresses') {
        console.log('RESULTS:' + options.body);
        return { ok: true, status: 200, text: async () => '{}' };
      }
      throw new Error('Unexpected network request: ' + url);
    };
  `
  return await promisify(execFile)(process.execPath, [
    "--import", "data:text/javascript," + encodeURIComponent(preload),
    fileURLToPath(new URL("./check-addresses.js", import.meta.url)),
  ], {
    env: { ...process.env, INGEST_URL: "https://ingest.test/ingest", INGEST_SECRET: "test" },
    timeout: 10000,
  })
}

for (const [name, body, ok] of [
  ["HTTP failure", { result: true, data: {} }, false],
  ["rejected source request", { result: false, data: {} }, true],
  ["missing success marker", { data: {} }, true],
  ["missing data", { result: true }, true],
  ["null data", { result: true, data: null }, true],
  ["array data", { result: true, data: [] }, true],
  ["invalid house", { result: true, data: { "12": null } }, true],
]) {
  test(`address collector does not post results for ${name}`, async () => {
    await assert.rejects(collect(body, ok), (error) => {
      assert.equal(error.code, 1)
      assert.doesNotMatch(error.stdout, /RESULTS:/)
      assert.match(error.stderr, /street failed/)
      return true
    })
  })
}

test("successful empty house data remains a genuine address-not-found result", async () => {
  const { stdout } = await collect({ result: true, data: {} })
  const results = JSON.parse(stdout.split("\n").find((line) => line.startsWith("RESULTS:")).slice(8))
  assert.deepEqual(results.results[0].houses, { "12": { found: false } })
})

test("successful house data preserves outage details and detected groups", async () => {
  const { stdout } = await collect({
    result: true,
    data: { "12": { sub_type: "Аварія", type: "2", start_date: "10:00", end_date: "12:00", sub_type_reason: ["GPV1.1"] } },
  })
  const results = JSON.parse(stdout.split("\n").find((line) => line.startsWith("RESULTS:")).slice(8))
  assert.deepEqual(results.results[0].houses["12"], {
    found: true, subType: "Аварія", type: "2", startDate: "10:00", endDate: "12:00", groupIds: ["1.1"],
  })
})
