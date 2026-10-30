// Legacy geography-only replays; does not validate the API-hosted interpretation tool.
import { readFile, mkdir, writeFile } from "node:fs/promises"
import { chromium } from "playwright"
import { askModel, groundAnswer, validateAnswer, isLlmConfigured } from "./popup-llm.js"

if (process.env.DTEK_LIVE_LLM !== "1" || !isLlmConfigured()) {
  throw new Error("Explicit DTEK_LIVE_LLM=1 and LLM_API_KEY required")
}

const { Dtek: settings } = JSON.parse(await readFile(
  new URL("../bot/src/DtekBot.Api/dtek-settings.json", import.meta.url), "utf8"))
const retry = process.argv.includes("--retry-failures")
const previous = retry ? JSON.parse(await readFile("artifacts/live-routing-inputs.json", "utf8")) : null
const failedKeys = new Set(previous?.failures.map(f => `${f.regionId}/${f.partId}`) ?? [])
const cases = previous?.cases.filter(c => !failedKeys.has(`${c.regionId}/${c.partId}`)) ?? []
const sources = previous?.sources ?? []
const browser = retry ? null : await chromium.launch({ headless: true })
try {
  if (browser) await Promise.all(settings.Regions.map(async region => {
    const context = await browser.newContext()
    try {
      const page = await context.newPage()
      await page.goto(region.SiteUrl, { waitUntil: "domcontentloaded", timeout: 45000 })
      await page.waitForTimeout(4000)
      const real = await page.locator('meta[name="csrf-token"]').count() > 0
      const popup = await page.locator(".m-attention__container.modal__container--firstPopup")
        .evaluateAll(nodes => nodes.map(node => [
          node.querySelector(".modal__title")?.textContent,
          node.querySelector(".m-attention__text")?.textContent,
        ].filter(Boolean).join("\n").trim()).find(Boolean) ?? "")
      if (!real) {
        sources.push({ regionId: region.Id, ok: false, reason: "Site verification failed (challenge or non-DTEK page)" })
        return
      }
      sources.push({ regionId: region.Id, ok: true, popupText: popup, observedAt: new Date().toISOString() })
      if (popup) {
        const structured = groundAnswer(validateAnswer(await askModel(region, popup), region), popup, region)
        cases.push({ regionId: region.Id, popupText: popup, structured, live: true })
      }
    } catch (error) {
      sources.push({ regionId: region.Id, ok: false, reason: error.name })
    } finally {
      await context.close()
    }
  }))
} finally {
  if (browser) await browser.close()
}

// Two workers bound provider cost/concurrency. No private subscription data is used.
const pending = settings.Regions.flatMap(region => region.Parts.map(part => ({ region, part })))
  .filter(({ region, part }) => !retry || failedKeys.has(`${region.Id}/${part.Id}`))
const failures = []
await Promise.all((retry ? [0] : [0, 1]).map(async () => {
  for (;;) {
    const next = pending.shift()
    if (!next) break
    const { region, part } = next
    const popupText = `Через аварію в мережі знеструмлено ${part.Name}. Стабілізаційні графіки не діють.`
    try {
      const structured = groundAnswer(validateAnswer(await askModel(region, popupText), region), popupText, region)
      if (structured?.scope !== "districts" || !structured.districts.includes(part.Id)) {
        failures.push({ regionId: region.Id, partId: part.Id, structured, reason: "Expected affected district not returned" })
      }
      cases.push({ regionId: region.Id, partId: part.Id, popupText, structured, live: false })
    } catch (error) {
      failures.push({ regionId: region.Id, partId: part.Id, reason: error.message })
    }
  }
}))
await mkdir("artifacts", { recursive: true })
await writeFile("artifacts/live-routing-inputs.json", JSON.stringify({
  generatedAt: new Date().toISOString(), sources, cases, failures,
  previousFailures: [...(previous?.previousFailures ?? []), ...(previous?.failures ?? [])],
}, null, 2))
console.log(JSON.stringify({ sources: sources.map(s => ({ region: s.regionId, ok: s.ok, popup: !!s.popupText })),
  cases: cases.length, failures: failures.length }))
if (failures.length || sources.some(source => !source.ok)) process.exitCode = 1
