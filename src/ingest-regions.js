import { readFileSync } from "node:fs"
import { chromium } from "playwright"

const SETTINGS_PATH = new URL(
  "../bot/src/DtekBot.Api/dtek-settings.json",
  import.meta.url
)
const POPUP_SELECTOR = ".m-attention__container.modal__container--firstPopup"

const INGEST_URL = process.env.INGEST_URL?.trim()
const INGEST_SECRET = process.env.INGEST_SECRET?.trim()

function loadRegions() {
  const settings = JSON.parse(readFileSync(SETTINGS_PATH, "utf8"))
  return settings.Dtek.Regions.filter((region) => region.SiteUrl)
}

// Returns { ok: false } when the page is not the real DTEK page, so the caller
// can skip it instead of reporting "no popup" (which would cancel an alert).
async function readRegion(browser, region) {
  const context = await browser.newContext()
  try {
    const page = await context.newPage()
    await page.goto(region.SiteUrl, { waitUntil: "load" })

    await page
      .waitForSelector(POPUP_SELECTOR, { state: "attached", timeout: 5000 })
      .catch(() => {})

    let popupText
    for (let attempt = 1; ; attempt++) {
      try {
        await page.waitForLoadState("load")
        popupText = await page.locator(POPUP_SELECTOR).evaluateAll((modals) =>
          modals
            .map((modal) => {
              const title = modal.querySelector(".modal__title")?.textContent
              const textNode = modal
                .querySelector(".m-attention__text")
                ?.cloneNode(true)
              textNode
                ?.querySelectorAll("br")
                .forEach((br) => br.replaceWith("\n"))
              return [title, textNode?.textContent]
                .filter(Boolean)
                .join("\n\n")
                .replace(/[ \t]+/g, " ")
                .replace(/\s*\n\s*/g, "\n")
                .trim()
            })
            .find(Boolean)
        )
        break
      } catch (error) {
        const isNavigation = error.message.includes("Execution context")
        if (!isNavigation || attempt >= 3) throw error
      }
    }

    if (!popupText) {
      const hasToken = await page
        .waitForSelector('meta[name="csrf-token"]', {
          state: "attached",
          timeout: 20000,
        })
        .then(() => true)
        .catch(() => false)
      if (!hasToken) return { ok: false, reason: "page is not the real site" }
    }

    return { ok: true, popupText: popupText ?? "" }
  } finally {
    await context.close()
  }
}

async function ingest(regionId, popupText) {
  const response = await fetch(INGEST_URL, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-ingest-secret": INGEST_SECRET,
    },
    body: JSON.stringify({
      regionId,
      popupText,
      observedAt: new Date().toISOString(),
    }),
    signal: AbortSignal.timeout(60000),
  })
  const body = await response.text()
  console.log(`📡 ${regionId}: HTTP ${response.status} ${body.slice(0, 300)}`)
  if (!response.ok) throw new Error(`ingest returned ${response.status}`)
}

async function run() {
  if (!INGEST_URL || !INGEST_SECRET) {
    console.log("INGEST_URL / INGEST_SECRET not set, skipping.")
    return
  }

  const browser = await chromium.launch({ headless: true })
  let failures = 0

  try {
    for (const region of loadRegions()) {
      console.log(`🌀 ${region.Id}: ${region.SiteUrl}`)
      try {
        const result = await readRegion(browser, region)
        if (!result.ok) {
          failures++
          console.log(`🟡 ${region.Id}: skipped, ${result.reason}`)
          continue
        }
        console.log(
          result.popupText
            ? `ℹ️ ${region.Id}: popup found (${result.popupText.length} chars)`
            : `✅ ${region.Id}: no popup`
        )
        await ingest(region.Id, result.popupText)
      } catch (error) {
        failures++
        console.error(`⚠️ ${region.Id}: ${error.message}`)
      }
    }
  } finally {
    await browser.close()
  }

  if (failures > 0) process.exitCode = 1
}

run().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})
