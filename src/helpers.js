import fs from "node:fs"
import path from "node:path"

import { LAST_MESSAGE_FILE } from "./constants.js"
import { normalize } from "./popup-keywords.js"

export function capitalize(str) {
  if (typeof str !== "string") return ""
  return str[0].toUpperCase() + str.slice(1).toLowerCase()
}

// The same accident phrases the shared bot uses (Dtek.Classification.Accident).
const ACCIDENT_PHRASES = JSON.parse(
  fs.readFileSync(
    new URL("../bot/src/DtekBot.Api/dtek-settings.json", import.meta.url),
    "utf8"
  )
).Dtek.Classification.Accident

export function isEmergencyNotice(notice) {
  return (
    typeof notice === "string" &&
    notice.length > 0 &&
    // A stabilization notice can also report an accident; the accident is the alert.
    (!isStabilizationNotice(notice) || isAccidentNotice(notice))
  )
}

export function isAccidentNotice(notice) {
  if (typeof notice !== "string") return false
  const text = normalize(notice)
  return ACCIDENT_PHRASES.some((phrase) => text.includes(phrase.toLowerCase()))
}

export function isStabilizationNotice(notice) {
  return (
    typeof notice === "string" &&
    /(?<!не\s+)діють\s+стабілізаційні\s+відключ/iu.test(notice)
  )
}

export function isEmergencyMessage(message) {
  return (
    message?.messageType === "emergency" ||
    Boolean(
      message?.outageText?.startsWith(
        "🚨 <b>Екстрені відключення електроенергії:</b>"
      )
    )
  )
}

export function loadLastMessage() {
  if (!fs.existsSync(LAST_MESSAGE_FILE)) return null

  const lastMessage = JSON.parse(
    fs.readFileSync(LAST_MESSAGE_FILE, "utf8").trim()
  )

  if (lastMessage?.date) {
    const messageDay = new Date(lastMessage.date * 1000).toLocaleDateString(
      "en-CA",
      { timeZone: "Europe/Kyiv" }
    )
    const today = new Date().toLocaleDateString("en-CA", {
      timeZone: "Europe/Kyiv",
    })

    if (
      messageDay < today &&
      !isEmergencyMessage(lastMessage) &&
      lastMessage.messageType !== "emergency-canceled"
    ) {
      deleteLastMessage()
      return null
    }
  }

  return lastMessage
}

export function saveLastMessage({
  date,
  message_id,
  outageText,
  messageType,
} = {}) {
  fs.mkdirSync(path.dirname(LAST_MESSAGE_FILE), { recursive: true })
  fs.writeFileSync(
    LAST_MESSAGE_FILE,
    JSON.stringify({
      message_id,
      date,
      outageText,
      messageType,
    })
  )
}

export function deleteLastMessage() {
  fs.rmSync(LAST_MESSAGE_FILE, { force: true })
}

export function getCurrentTime() {
  const now = new Date()

  const date = now.toLocaleDateString("uk-UA", {
    timeZone: "Europe/Kyiv",
  })

  const time = now.toLocaleTimeString("uk-UA", {
    timeZone: "Europe/Kyiv",
    hour: "2-digit",
    minute: "2-digit",
  })

  return `${date} ${time}`
}

export function checkIsNight() {
  const hours = new Date().toLocaleString("en-US", {
    timeZone: "Europe/Kyiv",
    hour: "numeric",
    hour12: false,
  })
  return hours >= 22 || hours < 8
}
