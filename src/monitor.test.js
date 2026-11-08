import assert from "node:assert/strict"
import { execFile } from "node:child_process"
import { mkdtempSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import path from "node:path"
import { promisify } from "node:util"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const execFileAsync = promisify(execFile)
const monitorPath = fileURLToPath(new URL("./monitor.js", import.meta.url))

async function runMonitor(subType, regionalNotice = null) {
  const directory = mkdtempSync(path.join(tmpdir(), "dtek-monitor-test-"))
  const browser = `
    export const chromium = {
      launch: async () => ({
        newPage: async () => ({
          goto: async () => {},
          locator: () => ({
            evaluateAll: async () => ${JSON.stringify(regionalNotice)}
          }),
          waitForSelector: async () => ({ getAttribute: async () => 'test-csrf' }),
          waitForLoadState: async () => {},
          evaluate: async () => ({
            result: true,
            updateTimestamp: '06.11.2026 20:51',
            data: {
              '10': {
                sub_type: ${JSON.stringify(subType)},
                start_date: '19:34 06.11.2026',
                end_date: '23:34 06.11.2026',
                type: '2'
              }
            }
          })
        }),
        close: async () => {}
      })
    };
  `
  const preload = `
    import { registerHooks } from 'node:module';
    const browser = ${JSON.stringify(browser)};
    registerHooks({
      resolve(specifier, context, nextResolve) {
        if (specifier === 'playwright')
          return { url: 'data:text/javascript,' + encodeURIComponent(browser), shortCircuit: true };
        return nextResolve(specifier, context);
      }
    });
    globalThis.fetch = async (url, options) => {
      if (!url.startsWith('https://api.telegram.org/'))
        throw new Error('Unexpected request: ' + url);
      console.log('MESSAGE:' + JSON.parse(options.body).text);
      return {
        json: async () => ({
          ok: true,
          result: { date: 1793991060, message_id: 1 }
        })
      };
    };
  `

  try {
    return await execFileAsync(process.execPath, [
      "--import",
      `data:text/javascript,${encodeURIComponent(preload)}`,
      monitorPath,
    ], {
      cwd: directory,
      env: {
        ...process.env,
        TELEGRAM_BOT_TOKEN: "test",
        TELEGRAM_CHAT_ID: "-1",
        REGION: "kr",
        CITY: "м. Ірпінь",
        STREET: "вул. Західна",
        HOUSE: "10",
        CHECK_ADDRESS: "true",
        CHECK_GRID_STATUS: "false",
      },
      timeout: 10000,
    })
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

test("reports the DTEK emergency address result and disabled stabilization schedules", async () => {
  const { stdout } = await runMonitor(
    "Екстренні відключення (Аварійне без застосування графіку погодинних відключень)"
  )

  assert.match(stdout, /🚨 <b>Локальне аварійне відключення:<\/b>/)
  assert.match(stdout, /Час початку: 19:34 06\.11\.2026/)
  assert.match(stdout, /Орієнтовне відновлення: до 23:34 06\.11\.2026/)
  assert.doesNotMatch(stdout, /м\. Ірпінь|вул\. Західна|За адресою наразі/)
  assert.doesNotMatch(stdout, /Екстренні відключення \(аварійне/)
  assert.match(stdout, /Графіки стабілізаційних відключень не діють/)
  assert.match(stdout, /Час відновлення може змінитися/)
  assert.match(stdout, /20:51 06\.11\.2026/)
})

test("does not report a scheduled address outage", async () => {
  const { stdout } = await runMonitor("Графіки погодинних відключень")
  assert.doesNotMatch(stdout, /MESSAGE:/)
})

test("keeps the regional popup emergency message separate from local address emergencies", async () => {
  const notice =
    "За наказом НЕК Укренерго введені екстрені відключення електроенергії."
  const { stdout } = await runMonitor("", notice)

  assert.match(stdout, /🚨 <b>Екстрені \/ аварійні відключення:<\/b>/)
  assert.match(stdout, new RegExp(notice))
  assert.doesNotMatch(stdout, /Локальне аварійне відключення/)
  assert.doesNotMatch(stdout, /Час початку:|Орієнтовне відновлення:/)
})
