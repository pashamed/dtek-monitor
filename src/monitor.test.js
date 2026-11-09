import assert from "node:assert/strict"
import { execFile } from "node:child_process"
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import path from "node:path"
import { promisify } from "node:util"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const execFileAsync = promisify(execFile)
const monitorPath = fileURLToPath(new URL("./monitor.js", import.meta.url))

const MIXED_EMERGENCY_NOTICE = `Шановні клієнти!

В Білоцерківському, Бучанському, Фастівському та Обухівському районах введені екстрені відключення.

В інших раонах області діють стабілізаційні відключення електроенергії.

Будь ласка, розумно споживайте електроенергію — це допоможе зменшити навантаження на мережу та уникнути екстрених відключень.

Дякуємо за ваше розуміння!`

async function runMonitor(
  subType,
  regionalNotice = null,
  lastMessage = null,
  channelTrigger = false,
  triggerChatId = "-1",
  targetChatId = "-1",
  triggerMessageId = "42",
  editChannelTrigger = false,
  {
    endDate = "23:34 06.11.2026",
    houseFound = true,
    telegramError = null,
    addressMessage = null,
  } = {}
) {
  const directory = mkdtempSync(path.join(tmpdir(), "dtek-monitor-test-"))
  if (lastMessage) {
    const artifacts = path.join(directory, "artifacts")
    mkdirSync(artifacts)
    writeFileSync(
      path.join(artifacts, "last-message.json"),
      JSON.stringify(lastMessage)
    )
  }
  if (addressMessage) {
    mkdirSync(path.join(directory, "artifacts"), { recursive: true })
    writeFileSync(
      path.join(directory, "artifacts", "last-address-message.json"),
      JSON.stringify(addressMessage)
    )
  }
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
          evaluate: async () => {
            console.log('ADDRESS_LOOKUP');
            const subType = ${JSON.stringify(subType)};
            return {
              result: true,
              updateTimestamp: '06.11.2026 20:51',
              data: ${houseFound} ? {
                '10': {
                  sub_type: subType,
                  start_date: subType ? '19:34 06.11.2026' : '',
                  end_date: subType ? ${JSON.stringify(endDate)} : '',
                  type: subType ? '2' : ''
                }
              } : {}
            };
          }
        }),
        close: async () => {}
      })
    };
  `
  const preload = `
    import { registerHooks } from 'node:module';
    const browser = ${JSON.stringify(browser)};
    const nativeSetTimeout = globalThis.setTimeout;
    globalThis.setTimeout = (fn, ms, ...args) =>
      nativeSetTimeout(fn, ms === 30000 ? 0 : ms, ...args);
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
      const body = JSON.parse(options.body);
      console.log('METHOD:' + url.split('/').pop());
      console.log('MESSAGE:' + body.text);
      console.log('CHAT_TO:' + body.chat_id);
      console.log('EDIT_TO:' + (body.message_id ?? 'none'));
      console.log('REPLY:' + Boolean(body.reply_parameters));
      console.log('REPLY_TO:' + (body.reply_parameters?.message_id ?? 'none'));
      return {
        json: async () => ({
          ok: ${!telegramError},
          description: ${JSON.stringify(telegramError)},
          result: { date: 1793991060, message_id: 1 }
        })
      };
    };
  `

  try {
    const result = await execFileAsync(process.execPath, [
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
        CHANNEL_TRIGGER: String(channelTrigger),
        CHANNEL_TRIGGER_CHAT_ID: channelTrigger ? triggerChatId : "",
        CHANNEL_TRIGGER_MESSAGE_ID: channelTrigger ? triggerMessageId : "",
        CHANNEL_TRIGGER_EDIT: String(editChannelTrigger),
        STANDALONE_MONITOR_CHAT_ID: channelTrigger ? targetChatId : "",
      },
      timeout: 10000,
    })
    result.addressFileExists = existsSync(
      path.join(directory, "artifacts", "last-address-message.json")
    )
    return result
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

test("reports the DTEK emergency address result and disabled stabilization schedules", async () => {
  const { stdout } = await runMonitor(
    "Екстренні відключення (Аварійне без застосування графіку погодинних відключень)"
  )

  assert.match(stdout, /🚨 <b>Локальне аварійне відключення:<\/b>/)
  assert.match(stdout, /🔴 Час початку: 19:34 06\.11\.2026/)
  assert.match(stdout, /🕓 Орієнтовне відновлення: до 23:34 06\.11\.2026/)
  assert.doesNotMatch(stdout, /<code>/)
  assert.doesNotMatch(stdout, /м\. Ірпінь|вул\. Західна|За адресою наразі/)
  assert.doesNotMatch(stdout, /Екстренні відключення \(аварійне/)
  assert.match(stdout, /Графіки стабілізаційних відключень не діють/)
  assert.match(stdout, /Час відновлення може змінитися/)
  assert.doesNotMatch(stdout, /📢 <i>20:51 06\.11\.2026<\/i>/)
  assert.match(stdout, /🤖 <i>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}<\/i>/)
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

test("clears a local emergency after two successful no-outage address checks", async () => {
  const { stdout } = await runMonitor("", null, {
    message_id: 1,
    date: 1,
    outageText: "🚨 <b>Локальне аварійне відключення:</b>",
    messageType: "local-emergency",
  })

  assert.equal((stdout.match(/ADDRESS_LOOKUP/g) ?? []).length, 2)
  assert.match(stdout, /REPLY:true/)
  assert.match(stdout, /✅ <b>Локальне аварійне відключення більше не відображається на сайті ДТЕК\.<\/b>/)
  assert.match(stdout, /Якщо світла досі немає, перевірте статус через 15 хвилин/)
})

test("publishes a mixed regional emergency popup without querying the address", async () => {
  const { stdout } = await runMonitor("", MIXED_EMERGENCY_NOTICE)

  assert.match(stdout, /METHOD:sendMessage/)
  assert.match(stdout, /🚨 <b>Екстрені \/ аварійні відключення:<\/b>/)
  assert.ok(stdout.includes(MIXED_EMERGENCY_NOTICE))
  assert.doesNotMatch(stdout, /ADDRESS_LOOKUP/)
})

test("a mixed regional popup updates an active emergency instead of clearing it", async () => {
  const { stdout } = await runMonitor("", MIXED_EMERGENCY_NOTICE, {
    message_id: 77,
    date: 1,
    outageText: "Previous regional emergency",
    messageType: "emergency",
  })

  assert.match(stdout, /METHOD:editMessageText/)
  assert.match(stdout, /EDIT_TO:77/)
  assert.ok(stdout.includes(MIXED_EMERGENCY_NOTICE))
  assert.doesNotMatch(stdout, /ADDRESS_LOOKUP|re-checking|✅/)
})

test("does not repeat a local emergency clearance", async () => {
  const { stdout } = await runMonitor("", null, {
    message_id: 2,
    date: 1,
    outageText: "✅ <b>Локальне аварійне відключення більше не відображається на сайті ДТЕК.</b>",
    messageType: "local-emergency-canceled",
  })

  assert.doesNotMatch(stdout, /MESSAGE:/)
})

test("announces a local emergency from an automatic channel trigger as a channel message", async () => {
  const { stdout } = await runMonitor(
    "Екстренні відключення (Аварійне без застосування графіку погодинних відключень)",
    null,
    null,
    true,
    "-1",
    "-1",
    "416"
  )

  assert.match(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /METHOD:sendMessage/)
  assert.match(stdout, /REPLY:false/)
  assert.match(stdout, /EDIT_TO:none/)
  assert.match(stdout, /CHAT_TO:-1/)
  assert.match(stdout, /🚨 <b>Локальне аварійне відключення:<\/b>/)
  assert.doesNotMatch(stdout, /Зафіксовано відключення/)
})

test("announces a non-emergency, non-stabilization address outage from a channel trigger", async () => {
  const { stdout } = await runMonitor("Незаплановане відключення", null, null, true)

  assert.match(stdout, /METHOD:sendMessage/)
  assert.match(stdout, /REPLY:false/)
  assert.match(stdout, /Зафіксовано відключення/)
})

test("edits its own address message when the outage text changes", async () => {
  const { stdout, addressFileExists } = await runMonitor(
    "Незаплановане відключення",
    null,
    null,
    true,
    "-1",
    "-1",
    "42",
    false,
    { addressMessage: { message_id: 77, outageText: "old" } }
  )

  assert.match(stdout, /METHOD:editMessageText/)
  assert.match(stdout, /EDIT_TO:77/)
  assert.match(stdout, /REPLY:false/)
  assert.equal(addressFileExists, true)
})

test("replies to the original post and closes the address thread when the address is normal", async () => {
  const { stdout, addressFileExists } = await runMonitor(
    "",
    null,
    null,
    true,
    "-1",
    "-1",
    "42",
    false,
    { addressMessage: { message_id: 77, outageText: "old" } }
  )

  assert.match(stdout, /METHOD:sendMessage/)
  assert.match(stdout, /REPLY_TO:42/)
  assert.equal(addressFileExists, false)
})

test("edits a manually selected post and checks its address despite regional emergency notices", async () => {
  const notice =
    "За наказом НЕК Укренерго введені екстрені відключення електроенергії."
  const { stdout } = await runMonitor(
    "Екстренні відключення (Аварійне без застосування графіку погодинних відключень)",
    notice,
    null,
    true,
    "-1",
    "-1",
    "416",
    true,
    { endDate: "03:20 07.11.2026" }
  )

  assert.match(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /METHOD:editMessageText/)
  assert.match(stdout, /EDIT_TO:416/)
  assert.match(stdout, /CHAT_TO:-1/)
  assert.match(stdout, /🚨 <b>Локальне аварійне відключення:<\/b>/)
  assert.match(stdout, /Час початку: 19:34 06\.11\.2026/)
  assert.match(stdout, /Орієнтовне відновлення: до 03:20 07\.11\.2026/)
  assert.doesNotMatch(stdout, /METHOD:sendMessage|REPLY:true/)
  assert.doesNotMatch(stdout, /регіональні екстрені відключення/)
})

test("manual edits use the emergency popup when the address has no listed outage", async () => {
  const notice =
    "За наказом НЕК Укренерго введені екстрені відключення електроенергії."
  const { stdout } = await runMonitor(
    "", notice, null, true, "-1", "-1", "416", true
  )

  assert.match(stdout, /EDIT_TO:416/)
  assert.match(stdout, /🚨 <b>Екстрені \/ аварійні відключення:<\/b>/)
  assert.match(stdout, new RegExp(notice))
  assert.doesNotMatch(stdout, /ДТЕК зараз не відображає|METHOD:sendMessage/)
})

test("manual edits preserve a non-emergency popup and escape its HTML", async () => {
  const { stdout } = await runMonitor(
    "", "Діють стабілізаційні відключення. <ДТЕК> & клієнти",
    null, true, "-1", "-1", "416", true
  )

  assert.match(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /EDIT_TO:416/)
  assert.match(stdout, /Діють стабілізаційні відключення\. &lt;ДТЕК&gt; &amp; клієнти/)
  assert.doesNotMatch(stdout, /ДТЕК зараз не відображає|METHOD:sendMessage/)
})

test("manual checks leave the post unchanged when neither outage nor popup is available", async () => {
  const { stdout, stderr } = await runMonitor(
    "", null, null, true, "-1", "-1", "416", true
  )

  assert.match(stderr, /leaving the channel post unchanged/)
  assert.doesNotMatch(stdout, /METHOD:|MESSAGE:/)
})

test("manual checks fail without editing when the configured house is missing", async () => {
  await assert.rejects(
    runMonitor("", null, null, true, "-1", "-1", "416", true, {
      houseFound: false,
    }),
    (error) => {
      assert.match(error.stderr, /House 10 not found/)
      assert.doesNotMatch(error.stdout, /METHOD:|MESSAGE:/)
      return true
    }
  )
})

test("failed manual Telegram edits never fall back to sending another message", async () => {
  await assert.rejects(
    runMonitor(
      "Екстренні відключення", null, null, true, "-1", "-1", "416", true,
      { telegramError: "Bad Request: message can't be edited" }
    ),
    (error) => {
      assert.match(error.stderr, /message can't be edited/)
      assert.match(error.stdout, /METHOD:editMessageText/)
      assert.doesNotMatch(error.stdout, /METHOD:sendMessage/)
      return true
    }
  )
})

test("does not query the address while a regional stabilization notice is active", async () => {
  const { stdout } = await runMonitor(
    "",
    "Діють стабілізаційні відключення.",
    null,
    true
  )

  assert.doesNotMatch(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /REPLY_TO:42/)
  assert.match(stdout, /Адресну перевірку не запускаю/)
})

test("does not query the address while a regional emergency notice is active", async () => {
  const { stdout } = await runMonitor(
    "",
    "За наказом НЕК Укренерго введені екстрені відключення електроенергії.",
    null,
    true
  )

  assert.doesNotMatch(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /регіональні екстрені відключення/)
  assert.match(stdout, /REPLY_TO:42/)
})

test("reports a scheduled DTEK address outage without calling it a local emergency", async () => {
  const { stdout } = await runMonitor(
    "Графіки погодинних відключень",
    null,
    null,
    true
  )

  assert.match(stdout, /ADDRESS_LOOKUP/)
  assert.match(stdout, /За адресою на сайті ДТЕК відображається планове відключення/)
  assert.doesNotMatch(stdout, /Локальне аварійне відключення/)
})

test("does not claim power restoration when DTEK lists no address outage", async () => {
  const { stdout } = await runMonitor("", null, null, true)

  assert.match(stdout, /ДТЕК зараз не відображає відключення за адресою/)
  assert.doesNotMatch(stdout, /світло відновлено|електроенергію відновлено/i)
})

test("rejects a channel-trigger payload for a different configured chat", async () => {
  const { stdout } = await runMonitor("", null, null, true, "-2")

  assert.doesNotMatch(stdout, /ADDRESS_LOOKUP|MESSAGE:/)
  assert.match(stdout, /not for the configured standalone monitor/)
})

test("fails visibly when the standalone target chat ID is not configured", async () => {
  await assert.rejects(
    runMonitor("", null, null, true, "-1", ""),
    /Set the STANDALONE_MONITOR_CHAT_ID GitHub Actions secret or variable/
  )
})
