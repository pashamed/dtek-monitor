import crypto from "node:crypto"
import path from "node:path"
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"

import { isStabilizationNotice } from "./helpers.js"

// Provider-neutral: any OpenAI-compatible chat completions endpoint works. The defaults point at the Google
// Gemini free tier (Flash-Lite); set LLM_BASE_URL / LLM_MODEL / LLM_API_KEY to use another provider.
const DEFAULT_BASE_URL =
  "https://generativelanguage.googleapis.com/v1beta/openai"
const DEFAULT_MODEL = "gemini-3.1-flash-lite"

// Results are kept next to the other persisted state, so a popup is only sent to the model when its text changes.
const CACHE_PATH = path.resolve("artifacts", "popup-structure.json")
const RETRY_AFTER_FAILURE_MS = 30 * 60 * 1000

const SCOPES = new Set(["all", "districts", "unknown"])

export function isLlmConfigured() {
  return Boolean(process.env.LLM_API_KEY?.trim())
}

function textKey(regionId, popupText) {
  const normalized = popupText.replace(/\s+/g, " ").trim().toLowerCase()
  return crypto
    .createHash("sha256")
    .update(`${regionId}\n${normalized}`)
    .digest("hex")
}

function loadCache() {
  try {
    return JSON.parse(readFileSync(CACHE_PATH, "utf8"))
  } catch {
    return {}
  }
}

function saveCache(cache) {
  try {
    mkdirSync(path.dirname(CACHE_PATH), { recursive: true })
    writeFileSync(CACHE_PATH, JSON.stringify(cache, null, 2))
  } catch (error) {
    console.error(`⚠️ Could not save the popup cache: ${error.message}`)
  }
}

function buildMessages(region, popupText) {
  const districts = region.Parts.map((part) => {
    const names = [part.Name, ...(part.Aliases ?? [])].join(", ")
    return `- ${part.Id}: ${names}`
  }).join("\n")

  const system = [
    "You read short Ukrainian notices published by a power distribution company and extract WHERE an outage applies.",
    'Answer with ONE JSON object and nothing else: {"scope":"all"|"districts"|"unknown","districts":["id", ...]}.',
    "Rules:",
    '- scope "districts": the notice names specific areas from the allowed list as affected; list exactly their ids.',
    '- scope "all": the notice concerns the whole region or city without naming any allowed area as the place affected.',
    '- scope "unknown": the notice does not say where, or it is not about an outage.',
    '- Never include an area that is mentioned as unaffected, as an exception ("крім", "не стосується") or only as context.',
    "- A district may be written in any grammatical case (Бучанському, Бучанського, Бучанський район).",
    '- Use only ids from the allowed list. Never invent ids. If scope is not "districts", use an empty array.',
  ].join("\n")

  const user = [
    `Region: ${region.Name}`,
    "Allowed areas (id: name, matching word stems):",
    districts,
    "",
    "Notice:",
    popupText,
  ].join("\n")

  return [
    { role: "system", content: system },
    { role: "user", content: user },
  ]
}

// Returns { scope, districts } with only known ids, or null when the answer is unusable.
export function validateAnswer(content, region) {
  const match = String(content ?? "").match(/\{[\s\S]*\}/)
  if (!match) return null

  let parsed
  try {
    parsed = JSON.parse(match[0])
  } catch {
    return null
  }

  const scope = String(parsed?.scope ?? "").toLowerCase()
  if (!SCOPES.has(scope)) return null

  const known = new Set(region.Parts.map((part) => part.Id))
  const districts = Array.isArray(parsed.districts)
    ? [...new Set(parsed.districts.filter((id) => known.has(id)))]
    : []

  if (scope === "districts" && districts.length === 0) {
    return { scope: "unknown", districts: [] }
  }

  return { scope, districts: scope === "districts" ? districts : [] }
}

async function askModel(region, popupText) {
  const baseUrl = (
    process.env.LLM_BASE_URL?.trim() || DEFAULT_BASE_URL
  ).replace(/\/+$/, "")
  const model = process.env.LLM_MODEL?.trim() || DEFAULT_MODEL

  const call = async (withJsonMode) => {
    const response = await fetch(`${baseUrl}/chat/completions`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: `Bearer ${process.env.LLM_API_KEY.trim()}`,
      },
      body: JSON.stringify({
        model,
        temperature: 0,
        messages: buildMessages(region, popupText),
        ...(withJsonMode ? { response_format: { type: "json_object" } } : {}),
      }),
      signal: AbortSignal.timeout(30000),
    })
    return response
  }

  let response = await call(true)
  // Some providers do not support JSON mode; the prompt already asks for JSON only.
  if (response.status === 400) response = await call(false)

  if (!response.ok) {
    // The status only: the body can echo the request.
    throw new Error(`LLM returned HTTP ${response.status}`)
  }

  const data = await response.json()
  return data?.choices?.[0]?.message?.content
}

// Turns the popup text into a predefined state the backend understands. Never throws: on any problem it returns
// null and the backend falls back to its keyword matching.
export async function structurePopup(region, popupText) {
  if (!isLlmConfigured() || !popupText?.trim() || !region.Parts?.length)
    return null

  // Stabilization notices carry no district alerts, so they never need the model.
  if (isStabilizationNotice(popupText)) return null

  const cache = loadCache()
  const key = textKey(region.Id, popupText)
  const entry = cache[region.Id]

  if (entry?.key === key) {
    if (entry.result) return entry.result
    if (Date.now() - (entry.failedAt ?? 0) < RETRY_AFTER_FAILURE_MS) return null
  }

  try {
    const result = validateAnswer(await askModel(region, popupText), region)
    cache[region.Id] = result
      ? { key, result, at: new Date().toISOString() }
      : { key, result: null, failedAt: Date.now() }
    saveCache(cache)
    console.log(
      result
        ? `🤖 ${region.Id}: popup structured (${result.scope}${result.districts.length ? `: ${result.districts.join(", ")}` : ""})`
        : `🟡 ${region.Id}: the model's answer was unusable, using keywords`
    )
    return result
  } catch (error) {
    cache[region.Id] = { key, result: null, failedAt: Date.now() }
    saveCache(cache)
    console.error(`⚠️ ${region.Id}: popup parsing skipped, ${error.message}`)
    return null
  }
}
