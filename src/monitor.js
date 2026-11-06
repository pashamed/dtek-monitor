import { chromium } from "playwright"

import {
  TELEGRAM_BOT_TOKEN,
  TELEGRAM_CHAT_ID,
  REGION,
  CITY,
  STREET,
  HOUSE,
  SHUTDOWNS_PAGE,
  SHOULD_CHECK_ADDRESS,
  IS_CHANNEL_TRIGGER,
  CHANNEL_TRIGGER_CHAT_ID,
  CHANNEL_TRIGGER_MESSAGE_ID,
  STANDALONE_MONITOR_CHAT_ID,
  RETRIES_MAX_COUNT,
  RETRIES_TIMEOUT,
  CANCEL_RECHECK_DELAY,
  GRID_STATUS_URL,
  GRID_STATUS_REGION,
  SHOULD_CHECK_GRID_STATUS,
} from "./constants.js"

import {
  capitalize,
  checkIsNight,
  deleteLastMessage,
  getCurrentTime,
  isEmergencyMessage,
  isEmergencyNotice,
  isLocalEmergencyMessage,
  isStabilizationNotice,
  loadLastMessage,
  saveLastMessage,
} from "./helpers.js"

let getInfoRetries = 0
let sendNotificationRetries = 0

async function getInfo({ skipAddressForStabilization = false } = {}) {
  console.log("🌀 Getting info...")

  const browser = await chromium.launch({ headless: true })

  try {
    const browserPage = await browser.newPage()
    await browserPage.goto(SHUTDOWNS_PAGE, {
      waitUntil: "load",
    })

    const readRegionalNotice = () =>
      browserPage
        .locator(".m-attention__container.modal__container--firstPopup")
        .evaluateAll((modals) =>
          modals
            .map((modal) => {
              const title = modal.querySelector(".modal__title")?.textContent
              const textNode = modal
                .querySelector(".m-attention__text")
                ?.cloneNode(true)
              textNode
                ?.querySelectorAll("br")
                .forEach((br) => br.replaceWith("\n"))
              const text = textNode?.textContent
              return [title, text]
                .filter(Boolean)
                .join("\n\n")
                .replace(/[ \t]+/g, " ")
                .replace(/\s*\n\s*/g, "\n")
                .trim()
            })
            .find(Boolean)
        )

    // The popup may render slightly after the load event
    await browserPage
      .waitForSelector(".m-attention__container.modal__container--firstPopup", {
        state: "attached",
        timeout: 5000,
      })
      .catch(() => {})

    let regionalNotice
    for (let attempt = 1; ; attempt++) {
      try {
        await browserPage.waitForLoadState("load")
        regionalNotice = await readRegionalNotice()
        break
      } catch (error) {
        // The site may reload itself while the popup is being read
        const isNavigation = error.message.includes("Execution context")
        if (!isNavigation || attempt >= 3) throw error
        console.log("🔁 Page navigated, reading popup again...")
      }
    }

    const emergencyNotice = isEmergencyNotice(regionalNotice)
      ? regionalNotice
      : null

    if (emergencyNotice) {
      console.log("🚨 Regional outage notice detected!")
      return { emergencyNotice, regionalNotice }
    } else if (regionalNotice) {
      console.log("ℹ️ Regional stabilization notice detected.")
    }

    if (skipAddressForStabilization && isStabilizationNotice(regionalNotice)) {
      return { regionalNotice, skipReason: "stabilization" }
    }

    // Without the token the page is not the real DTEK page (Cloudflare,
    // maintenance, half-rendered), so "no popup" must not be trusted.
    const csrfTokenTag = await browserPage.waitForSelector(
      'meta[name="csrf-token"]',
      { state: "attached", timeout: 20000 }
    )
    const csrfToken = await csrfTokenTag.getAttribute("content")

    if (!SHOULD_CHECK_ADDRESS) {
      console.log("✅ Emergency popup check finished.")
      return { emergencyNotice, regionalNotice }
    }

    const info = await browserPage.evaluate(
      async ({ REGION, CITY, STREET, csrfToken }) => {
        const formData = new URLSearchParams()
        formData.append("method", "getHomeNum")

        if (REGION !== "k") {
          formData.append("data[0][name]", "city")
          formData.append("data[0][value]", CITY)
        }

        formData.append("data[1][name]", "street")
        formData.append("data[1][value]", STREET)
        formData.append("data[2][name]", "updateFact")
        formData.append("data[2][value]", new Date().toLocaleString("uk-UA"))

        const response = await fetch("/ua/ajax", {
          method: "POST",
          headers: {
            "x-requested-with": "XMLHttpRequest",
            "x-csrf-token": csrfToken,
          },
          body: formData,
        })
        return await response.json()
      },
      { REGION, CITY, STREET, csrfToken }
    )

    if (!info?.data) {
      throw Error(
        `power outage info missed (${JSON.stringify(info)?.slice(0, 200)})`
      )
    }

    console.log("✅ Getting info finished.")
    return { emergencyNotice, regionalNotice, info }
  } catch (error) {
    console.error(`❌ Getting info failed: ${error.message}.`)
  } finally {
    await browser.close()
  }

  if (getInfoRetries < RETRIES_MAX_COUNT) {
    console.log("🌀 Try getting info again...")
    await new Promise((resolve) => setTimeout(resolve, RETRIES_TIMEOUT))
    getInfoRetries++
    return await getInfo({ skipAddressForStabilization })
  }

  throw Error(`❌ Getting info failed after ${RETRIES_MAX_COUNT} retries.`)
}

function checkIsOutage(info) {
  console.log("🌀 Checking power outage...")

  const house = info.data[HOUSE]
  if (!house) throw Error(`❌ House ${HOUSE} not found.`)

  const { sub_type, start_date, end_date, type } = house
  const isOutageDetected =
    sub_type !== "" || start_date !== "" || end_date !== "" || type !== ""

  isOutageDetected
    ? console.log("🚨 Power outage detected!")
    : console.log("⚡️ No power outage!")

  return isOutageDetected
}

function checkIsScheduled(info) {
  console.log("🌀 Checking whether power outage scheduled...")

  const { sub_type } = info?.data?.[HOUSE] || {}
  const isScheduled =
    !sub_type.toLowerCase().includes("авар") &&
    !sub_type.toLowerCase().includes("екст")

  isScheduled
    ? console.log("🗓️ Power outage scheduled!")
    : console.log("⚠️ Power outage not scheduled!")

  return isScheduled
}

async function generateMessage(info) {
  console.log("🌀 Generating message...")

  const { sub_type, start_date, end_date } = info?.data?.[HOUSE] || {}
  const { updateTimestamp } = info || {}
  const update = updateTimestamp?.split(" ").reverse().join(" ")

  const reason = capitalize(sub_type)
  const [beginTime, ...beginDateParts] = start_date.trim().split(/\s+/)
  const [endTime, ...endDateParts] = end_date.trim().split(/\s+/)
  const beginDate = beginDateParts.join(" ")
  const endDate = endDateParts.join(" ")
  const isEmergencyAddressOutage = /авар|екстр/iu.test(sub_type)
  let outageText

  if (isEmergencyAddressOutage) {
    const hasDisabledStabilization =
      /без застосування графіку погодинних відключень/iu.test(sub_type)
    outageText = [
      "🚨 <b>Локальне аварійне відключення:</b>",
      "",
      `🔴 Час початку: ${[beginTime, beginDate].filter(Boolean).join(" ")}`,
      `🕓 Орієнтовне відновлення: до ${[endTime, endDate]
        .filter(Boolean)
        .join(" ")}`,
      hasDisabledStabilization
        ? "⚠️ Графіки стабілізаційних відключень не діють."
        : null,
      hasDisabledStabilization
        ? "ℹ️ Час відновлення може змінитися."
        : null,
    ]
      .filter((line) => line !== null)
      .join("\n")
  } else {
    const timeRange =
      beginDate && beginDate === endDate
        ? `${beginTime} — ${endTime} ${beginDate}`
        : `${[beginTime, beginDate].filter(Boolean).join(" ")} — ${[
            endTime,
            endDate,
          ]
            .filter(Boolean)
            .join(" ")}`
    outageText = [
      `🪫 <code>${timeRange}</code>`,
      "",
      `⚠️ <i>${reason}.</i>`,
    ].join("\n")
  }

  const gridStatusLine = isEmergencyAddressOutage
    ? await getGridStatusLine()
    : null
  if (gridStatusLine) outageText = `${outageText}\n\n${gridStatusLine}`

  const text = isEmergencyAddressOutage
    ? [outageText, "", `🤖 <i>${getCurrentTime()}</i>`].join("\n")
    : [
        "⚡️ <b>Зафіксовано відключення:</b>",
        outageText,
        "",
        `📢 <i>${update}</i>`,
        `🤖 <i>${getCurrentTime()}</i>`,
      ].join("\n")

  return {
    text,
    outageText,
    messageType: isEmergencyAddressOutage ? "local-emergency" : "outage",
  }
}

function generateLocalEmergencyCancellationMessage() {
  const outageText = [
    "✅ <b>Локальне аварійне відключення більше не відображається на сайті ДТЕК.</b>",
    "Якщо світла досі немає, перевірте статус через 15 хвилин.",
  ].join("\n")
  const text = [outageText, "", `🤖 <i>${getCurrentTime()}</i>`].join("\n")

  return { text, outageText, messageType: "local-emergency-canceled" }
}

async function getGridStatusLine() {
  if (!SHOULD_CHECK_GRID_STATUS) return null

  try {
    const response = await fetch(GRID_STATUS_URL, {
      signal: AbortSignal.timeout(10000),
    })
    const data = await response.json()
    const region = data?.regions?.find((r) => r.slug === GRID_STATUS_REGION)
    if (!region) return null

    const since = region.since
      ? ` (з ${new Date(region.since).toLocaleString("uk-UA", {
          timeZone: "Europe/Kyiv",
          day: "2-digit",
          month: "2-digit",
          hour: "2-digit",
          minute: "2-digit",
        })})`
      : ""

    return `${region.emoji} <b>${region.name_uk}:</b> ${region.title_uk}${since} (за даними НЕК «Укренерго»)`
  } catch (error) {
    // The grid status is only a supplement, never block the notification
    console.error(`⚠️ Grid status unavailable: ${error.message}.`)
    return null
  }
}

function generateEmergencyMessage(emergencyNotice, gridStatusLine) {
  console.log("🌀 Generating emergency outage message...")

  const escapedNotice = emergencyNotice
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")

  const outageText = [
    `🚨 <b>Екстрені / аварійні відключення:</b>\n\n${escapedNotice}`,
    "⚠️ Через аварійні відключення стабілізаційні графіки можуть тимчасово не діяти.",
    gridStatusLine,
  ]
    .filter(Boolean)
    .join("\n\n")
  const text = [outageText, "", `🤖 <i>${getCurrentTime()}</i>`].join("\n")

  return { text, outageText, messageType: "emergency" }
}

function generateEmergencyCancellationMessage(regionalNotice, gridStatusLine) {
  console.log("🌀 Generating emergency outage cancellation message...")

  const stabilizationStatus = isStabilizationNotice(regionalNotice)
    ? "<b>Діють стабілізаційні відключення.</b>"
    : "<b>Стабілізаційних відключень наразі немає.</b>"
  const outageText = [
    "✅ <b>Екстрені / аварійні відключення скасовано.</b>",
    stabilizationStatus,
    gridStatusLine,
  ]
    .filter(Boolean)
    .join("\n\n")
  const text = [outageText, "", `🤖 <i>${getCurrentTime()}</i>`].join("\n")

  return { text, outageText, messageType: "emergency-canceled" }
}

async function sendNotification({ text, outageText, messageType }) {
  if (!TELEGRAM_BOT_TOKEN) throw Error("❌ Missing telegram bot token.")
  if (!TELEGRAM_CHAT_ID) throw Error("❌ Missing telegram chat id.")

  console.log("🌀 Sending notification...")

  const lastMessage = loadLastMessage() || {}
  const isThreadClosed = [
    "emergency-canceled",
    "local-emergency-canceled",
  ].includes(lastMessage.messageType)
  const isOutageChanged = lastMessage.outageText !== outageText
  const hasMessageId = Boolean(lastMessage.message_id)
  const hasOpenMessage = hasMessageId && !isThreadClosed
  // A closed thread's cancellation message keeps getting its status refreshed
  const isCancellationUpdate =
    isThreadClosed && hasMessageId && messageType === "emergency-canceled"

  if (isCancellationUpdate && !isOutageChanged) {
    console.log("🟡 Notification not changed.")
    return
  }

  // An open emergency message is updated in place when the notice changes
  const isEmergencyUpdate =
    messageType === "emergency" && isEmergencyMessage(lastMessage)
  const isEdit =
    isCancellationUpdate ||
    (hasOpenMessage && (!isOutageChanged || isEmergencyUpdate))
  const isReply = hasOpenMessage && !isEdit

  try {
    const response = await fetch(
      `https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/${
        isEdit ? "editMessageText" : "sendMessage"
      }`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          chat_id: TELEGRAM_CHAT_ID,
          text,
          parse_mode: "HTML",
          disable_notification: checkIsNight(),
          message_id: isEdit ? lastMessage.message_id : undefined,
          reply_parameters: isReply
            ? {
                message_id: lastMessage.message_id,
                allow_sending_without_reply: true,
              }
            : undefined,
        }),
      }
    )

    const data = await response.json()

    if (data.description?.includes("not modified")) {
      console.log("🟡 Notification not changed.")
      return
    }
    if (!data.ok) throw Error(data.description)

    saveLastMessage({ ...data.result, outageText, messageType })

    console.log("🟢 Notification sent.")
    return
  } catch (error) {
    console.error(`❌ Sending notification failed: ${error.message}.`)
    if (isEdit && error.message.includes("message to edit not found")) {
      deleteLastMessage()
    }
  }

  if (sendNotificationRetries < RETRIES_MAX_COUNT) {
    console.log("🌀 Try sending notification again...")
    await new Promise((resolve) => setTimeout(resolve, RETRIES_TIMEOUT))
    sendNotificationRetries++
    return await sendNotification({ text, outageText, messageType })
  }

  throw Error(
    `❌ Sending notification failed after ${RETRIES_MAX_COUNT} retries.`
  )
}

async function sendChannelTriggerReply(chatId, messageId, text) {
  if (!TELEGRAM_BOT_TOKEN) throw Error("❌ Missing telegram bot token.")

  const response = await fetch(
    `https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/sendMessage`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        chat_id: chatId,
        text,
        parse_mode: "HTML",
        disable_notification: checkIsNight(),
        reply_parameters: {
          message_id: messageId,
          allow_sending_without_reply: true,
        },
      }),
    }
  )

  const data = await response.json()
  if (!data.ok) throw Error(data.description || "Telegram reply failed.")
  console.log("🟢 Address-check result replied to the channel post.")
}

async function runChannelTrigger() {
  const chatId = Number(CHANNEL_TRIGGER_CHAT_ID)
  const targetChatIdText = STANDALONE_MONITOR_CHAT_ID?.trim()
  const targetChatId = Number(targetChatIdText)
  const messageId = Number(CHANNEL_TRIGGER_MESSAGE_ID)
  if (
    !targetChatIdText ||
    !Number.isSafeInteger(chatId) ||
    !Number.isSafeInteger(targetChatId)
  ) {
    throw Error(
      "❌ Set the STANDALONE_MONITOR_CHAT_ID GitHub Actions secret or variable."
    )
  }
  if (chatId !== targetChatId) {
    console.log("🟡 Channel post is not for the configured standalone monitor.")
    return
  }
  if (
    !Number.isSafeInteger(messageId) ||
    messageId <= 0
  ) {
    throw Error("❌ Invalid channel-trigger payload.")
  }
  if (!SHOULD_CHECK_ADDRESS) {
    throw Error("❌ Address checking is disabled for the channel trigger.")
  }

  let text
  try {
    const result = await getInfo({ skipAddressForStabilization: true })
    if (result.emergencyNotice) {
      text =
        "ℹ️ На сайті ДТЕК діють регіональні екстрені відключення.\nАдресну перевірку не запускаю."
    } else if (result.skipReason === "stabilization") {
      text =
        "🗓 На сайті ДТЕК відображаються стабілізаційні відключення.\nАдресну перевірку не запускаю."
    } else if (!result.info) {
      throw Error("Address status was not returned by DTEK.")
    } else if (!checkIsOutage(result.info)) {
      text =
        "ℹ️ ДТЕК зараз не відображає відключення за адресою.\nЯкщо світла немає, перевірте статус ще раз через 15 хвилин."
    } else if (checkIsScheduled(result.info)) {
      text =
        "🗓 За адресою на сайті ДТЕК відображається планове відключення.\nЛокальну аварію не підтверджено."
    } else {
      text = (await generateMessage(result.info)).text
    }
  } catch (error) {
    console.error(`❌ Channel address check failed: ${error.message}.`)
    text =
      "⚠️ Не вдалося перевірити статус ДТЕК зараз.\nСпробуйте повторити запит пізніше."
  }

  await sendChannelTriggerReply(chatId, messageId, text)
}

async function run() {
  if (IS_CHANNEL_TRIGGER) {
    await runChannelTrigger()
    return
  }

  let result = await getInfo()

  const lastMessage = loadLastMessage()
  const isConfirmingRegionalClearance =
    !result.emergencyNotice && isEmergencyMessage(lastMessage)
  const isConfirmingLocalClearance =
    !result.emergencyNotice &&
    isLocalEmergencyMessage(lastMessage) &&
    result.info &&
    !checkIsOutage(result.info)

  if (isConfirmingRegionalClearance || isConfirmingLocalClearance) {
    // Re-check after a pause so a single glitchy page load can't cancel
    console.log(
      `🟡 No active emergency detected, re-checking in ${CANCEL_RECHECK_DELAY / 1000}s...`
    )
    await new Promise((resolve) => setTimeout(resolve, CANCEL_RECHECK_DELAY))
    result = await getInfo()
  }

  const { emergencyNotice, regionalNotice, info } = result

  if (emergencyNotice) {
    const message = generateEmergencyMessage(
      emergencyNotice,
      await getGridStatusLine()
    )
    await sendNotification(message)
    return
  }

  if (isEmergencyMessage(lastMessage)) {
    const message = generateEmergencyCancellationMessage(
      regionalNotice,
      await getGridStatusLine()
    )
    await sendNotification(message)
    return
  }

  if (lastMessage?.messageType === "emergency-canceled") {
    // Keep the closed thread's status current until a new emergency starts
    const gridStatusLine = await getGridStatusLine()
    if (gridStatusLine) {
      await sendNotification(
        generateEmergencyCancellationMessage(regionalNotice, gridStatusLine)
      )
    }
  }

  if (!info) return

  const isOutage = checkIsOutage(info)

  if (!isOutage) {
    if (isLocalEmergencyMessage(lastMessage)) {
      await sendNotification(generateLocalEmergencyCancellationMessage())
    }
    return
  }

  const isScheduled = checkIsScheduled(info)
  if (isOutage && !isScheduled) {
    const message = await generateMessage(info)
    await sendNotification(message)
  }
}

run().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})
