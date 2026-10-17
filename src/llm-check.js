// Checks the LLM connection from your own machine, printing everything to the console (never the key):
//   $env:LLM_API_KEY = "<key>"; node src/llm-check.js
// Optional: LLM_BASE_URL and LLM_MODEL, the same variables the workflow uses.
import { readFileSync } from "node:fs"

import { askModel, groundAnswer, isLlmConfigured, validateAnswer } from "./popup-llm.js"

const DEFAULT_BASE_URL = "https://generativelanguage.googleapis.com/v1beta/openai"
const baseUrl = (process.env.LLM_BASE_URL?.trim() || DEFAULT_BASE_URL).replace(/\/+$/, "")
const model = process.env.LLM_MODEL?.trim() || "gemini-3.1-flash-lite"

if (!isLlmConfigured()) {
  console.error("❌ LLM_API_KEY is empty. Set it in this shell first.")
  process.exit(1)
}

const key = process.env.LLM_API_KEY.trim()
const settings = JSON.parse(
  readFileSync(new URL("../bot/src/DtekBot.Api/dtek-settings.json", import.meta.url), "utf8")
)
const region = settings.Dtek.Regions.find((r) => r.Id === "kyiv-oblast")

console.log(`ℹ️ Endpoint: ${baseUrl}`)
console.log(`ℹ️ Model:    ${model}`)

// 1. Is the key valid, and does the model exist? (OpenAI-compatible providers list their models.)
try {
  const response = await fetch(`${baseUrl}/models`, {
    headers: { authorization: `Bearer ${key}` },
    signal: AbortSignal.timeout(20000),
  })
  console.log(`ℹ️ GET /models -> HTTP ${response.status}`)
  if (response.ok) {
    const ids = ((await response.json())?.data ?? []).map((m) => String(m.id).replace(/^models\//, ""))
    const lite = ids.filter((id) => /flash/i.test(id)).sort()
    console.log(`ℹ️ ${ids.length} models available. Flash models: ${lite.join(", ") || "none"}`)
    if (!ids.includes(model)) {
      console.error(
        `❌ Model "${model}" is not in the list. Set LLM_MODEL to one of the names above (a "flash-lite" one is the cheapest).`
      )
      process.exit(2)
    }
    console.log(`✅ Model "${model}" exists.`)
  }
} catch (error) {
  console.error(`⚠️ Could not list models: ${error.message.split(key).join("<key>")}`)
}

// 2. The real request the workflow makes, on a typical popup.
const popup =
  "Шановні клієнти! Через атаку на енергообʼєкти за командою НЕК Укренерго запроваджені екстрені відключення в Обухівському, Фастівському і Бучанському районах. В інших районах діють графіки."
try {
  const answer = await askModel(region, popup)
  const result = groundAnswer(validateAnswer(answer, region), popup, region)
  console.log("ℹ️ Parsed:", JSON.stringify(result))
  const ok =
    result?.scope === "districts" &&
    ["obukhiv", "fastiv", "bucha"].every((id) => result.districts.includes(id)) &&
    result.districts.length === 3
  console.log(ok ? "✅ The model understood the sample popup." : "🟡 The answer differs from the expected obukhiv, fastiv, bucha.")
  process.exit(ok ? 0 : 3)
} catch (error) {
  console.error(`❌ ${error.message}`)
  process.exit(4)
}
