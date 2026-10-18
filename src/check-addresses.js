import { readFileSync } from "node:fs"
import { chromium } from "playwright"

const SETTINGS_PATH = new URL(
  "../bot/src/DtekBot.Api/dtek-settings.json",
  import.meta.url
)
const PAUSE_BETWEEN_STREETS_MS = 1500

const INGEST_URL = process.env.INGEST_URL?.trim()
const INGEST_SECRET = process.env.INGEST_SECRET?.trim()
const BASE = INGEST_URL?.replace(/\/+$/, "")

const headers = { "x-ingest-secret": INGEST_SECRET }
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

// Latin lookalikes are mapped to Cyrillic so "12A" matches "12А".
const LOOKALIKES = {
  A: "А",
  B: "В",
  C: "С",
  E: "Е",
  H: "Н",
  I: "І",
  K: "К",
  M: "М",
  O: "О",
  P: "Р",
  T: "Т",
  X: "Х",
}
function normalizeHouse(value) {
  return String(value)
    .toUpperCase()
    .replace(/\s+/g, "")
    .replace(/[A-Z]/g, (letter) => LOOKALIKES[letter] ?? letter)
}

function loadRegions() {
  const settings = JSON.parse(readFileSync(SETTINGS_PATH, "utf8"))
  return new Map(settings.Dtek.Regions.map((region) => [region.Id, region]))
}

async function fetchPending() {
  const response = await fetch(`${BASE}/addresses/pending`, {
    headers,
    signal: AbortSignal.timeout(30000),
  })
  if (!response.ok) throw new Error(`pending list returned ${response.status}`)
  return (await response.json()).streets ?? []
}

async function openRegion(browser, region) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await page.goto(region.SiteUrl, { waitUntil: "load" })

  // Without the token the page is not the real DTEK page (Imperva, maintenance).
  const tag = await page
    .waitForSelector('meta[name="csrf-token"]', {
      state: "attached",
      timeout: 20000,
    })
    .catch(() => null)
  if (!tag) {
    await context.close()
    throw new Error("page is not the real site")
  }
  return { context, page, csrfToken: await tag.getAttribute("content") }
}

// One call returns every house on the street. For DTEK regional sites the city is required;
// Kyiv city uses the same form without a city field.
async function getStreetHouses(session, region, pending) {
  const info = await session.page.evaluate(
    async ({ street, city, csrfToken, needsCity }) => {
      const form = new URLSearchParams()
      form.append("method", "getHomeNum")
      if (needsCity) {
        form.append("data[0][name]", "city")
        form.append("data[0][value]", city)
      }
      form.append("data[1][name]", "street")
      form.append("data[1][value]", street)
      form.append("data[2][name]", "updateFact")
      form.append("data[2][value]", new Date().toLocaleString("uk-UA"))

      const response = await fetch("/ua/ajax", {
        method: "POST",
        headers: {
          "x-requested-with": "XMLHttpRequest",
          "x-csrf-token": csrfToken,
        },
        body: form,
      })
      return await response.json()
    },
    {
      street: pending.street,
      city: pending.city ?? "",
      csrfToken: session.csrfToken,
      needsCity: region.Id !== "kyiv",
    }
  )
  return info?.data && typeof info.data === "object" ? info.data : {}
}

function buildStreetResult(pending, houses) {
  const byNormalized = new Map(
    Object.entries(houses).map(([key, value]) => [normalizeHouse(key), value])
  )
  const result = {}
  for (const requested of pending.houses) {
    const house = byNormalized.get(normalizeHouse(requested))
    result[requested] = house
      ? {
          found: true,
          subType: house.sub_type ?? "",
          startDate: house.start_date ?? "",
          endDate: house.end_date ?? "",
          type: house.type ?? "",
        }
      : { found: false }
  }
  return { regionId: pending.regionId, city: pending.city ?? "", street: pending.street, houses: result }
}

async function postResults(results) {
  const response = await fetch(`${BASE}/addresses`, {
    method: "POST",
    headers: { ...headers, "content-type": "application/json" },
    body: JSON.stringify({ results }),
    signal: AbortSignal.timeout(60000),
  })
  const body = await response.text()
  console.log(
    `📡 address results: HTTP ${response.status} ${body.slice(0, 200)}`
  )
  if (!response.ok)
    throw new Error(`address results returned ${response.status}`)
}

async function run() {
  if (!INGEST_URL || !INGEST_SECRET) {
    console.log("INGEST_URL / INGEST_SECRET not set, skipping.")
    return
  }

  const pending = await fetchPending()
  if (pending.length === 0) {
    console.log("✅ No pending address checks.")
    return
  }
  console.log(`🌀 ${pending.length} street(s) to check.`)

  const regions = loadRegions()
  const byRegion = Map.groupBy(pending, (street) => street.regionId)
  const results = []
  let failures = 0

  const browser = await chromium.launch({ headless: true })
  try {
    for (const [regionId, streets] of byRegion) {
      const region = regions.get(regionId)
      if (!region) {
        failures++
        console.error(`⚠️ unknown region ${regionId}`)
        continue
      }

      let session
      try {
        session = await openRegion(browser, region)
      } catch (error) {
        failures++
        console.error(`⚠️ ${regionId}: ${error.message}`)
        continue
      }

      try {
        for (const street of streets) {
          try {
            const houses = await getStreetHouses(session, region, street)
            results.push(buildStreetResult(street, houses))
            console.log(
              `ℹ️ ${regionId}: street checked (${street.houses.length} house(s) requested, ${Object.keys(houses).length} on the street)`
            )
          } catch (error) {
            failures++
            console.error(`⚠️ ${regionId}: street failed: ${error.message}`)
          }
          await sleep(PAUSE_BETWEEN_STREETS_MS)
        }
      } finally {
        await session.context.close()
      }
    }
  } finally {
    await browser.close()
  }

  if (results.length > 0) await postResults(results)
  if (failures > 0) process.exitCode = 1
}

run().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})
