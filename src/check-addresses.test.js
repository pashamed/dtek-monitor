import { test } from "node:test"
import assert from "node:assert/strict"
import { execFile } from "node:child_process"
import { promisify } from "node:util"
import { fileURLToPath } from "node:url"

const KYIV_PENDING = [{ regionId: "kyiv", city: "", street: "вул. Тестова", houses: ["12"] }]

async function collect(body, ok = true, { pending = KYIV_PENDING, updateFact = null } = {}) {
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
    const updateFact = ${JSON.stringify(updateFact)};
    if (updateFact !== null)
      globalThis.document = { querySelector: (selector) => selector === 'input[name="updateFact"]' ? { value: updateFact } : null };
    const timeout = globalThis.setTimeout;
    globalThis.setTimeout = (fn, ms, ...args) => timeout(fn, ms === 1500 ? 0 : ms, ...args);
    globalThis.fetch = async (url, options) => {
      if (url === 'https://ingest.test/ingest/addresses/pending')
        return { ok: true, json: async () => ({ streets: ${JSON.stringify(pending)} }) };
      if (url === '/ua/ajax') {
        console.log('AJAX:' + options.body.toString());
        return { ok: ${ok}, status: ${ok ? 200 : 503}, json: async () => (${JSON.stringify(body)}) };
      }
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

const SAMPLE_REASON = (group) => ({
  sub_type: "", start_date: "", end_date: "", type: "", sub_type_reason: [group], voluntarily: null, cek: null,
})

function line(stdout, prefix) {
  return stdout.split("\n").find((entry) => entry.startsWith(prefix)).slice(prefix.length)
}

test("Kyiv request matches the site form: sequential fields and the page's updateFact", async () => {
  const { stdout } = await collect({ result: true, data: {} }, true, { updateFact: "08.11.2026 10:42" })
  const form = new URLSearchParams(line(stdout, "AJAX:"))
  assert.deepEqual([...form.entries()], [
    ["method", "getHomeNum"],
    ["data[0][name]", "street"], ["data[0][value]", "вул. Тестова"],
    ["data[1][name]", "updateFact"], ["data[1][value]", "08.11.2026 10:42"],
  ])
})

test("Kyiv oblast request sends city first and omits updateFact when the page has none", async () => {
  const { stdout } = await collect({ result: true, data: {} }, true, {
    pending: [{ regionId: "kyiv-oblast", city: "м. Бровари", street: "вул. Тестова", houses: ["1"] }],
  })
  const form = new URLSearchParams(line(stdout, "AJAX:"))
  assert.deepEqual([...form.entries()], [
    ["method", "getHomeNum"],
    ["data[0][name]", "city"], ["data[0][value]", "м. Бровари"],
    ["data[1][name]", "street"], ["data[1][value]", "вул. Тестова"],
  ])
})

test("sample getHomeNum response maps fractional and lettered houses to groups and keeps the source timestamp", async () => {
  const { stdout } = await collect({
    result: true,
    data: {
      "7": SAMPLE_REASON("GPV40.1"), "15В": SAMPLE_REASON("GPV40.1"), "1/5": SAMPLE_REASON("GPV40.1"),
      "3/А": SAMPLE_REASON("GPV37.1"), "3": SAMPLE_REASON("GPV37.1"), "2А": SAMPLE_REASON("GPV39.1"),
    },
    showCurOutageParam: true,
    updateTimestamp: "20:48 08.11.2026",
  }, true, {
    pending: [{ regionId: "kyiv", city: "", street: "вул. Крушельницької", houses: ["1/5", "3/А", "15В", "2А", "99"] }],
  })
  const result = JSON.parse(line(stdout, "RESULTS:")).results[0]
  const empty = { found: true, subType: "", type: "", startDate: "", endDate: "" }
  assert.equal(result.updateTimestamp, "20:48 08.11.2026")
  assert.deepEqual(result.houses, {
    "1/5": { ...empty, groupIds: ["40.1"] },
    "3/А": { ...empty, groupIds: ["37.1"] },
    "15В": { ...empty, groupIds: ["40.1"] },
    "2А": { ...empty, groupIds: ["39.1"] },
    "99": { found: false },
  })
})
