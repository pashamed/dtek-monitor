// A JavaScript twin of the backend's keyword parser (bot/src/DtekBot.Api/Parsing/PopupClassifier.cs), kept just big
// enough to answer one question: did the keywords already understand this popup? If they did, the LLM is not asked.

const DISTRICT_WORDS = /район|громад|населен/u
const APOSTROPHES = /[\u02BC\u2019\u2018`]/gu

export function normalize(text) {
  return (text ?? "")
    .replace(/<br\s*\/?>|<\/p>/giu, " ")
    .replace(/<[^>]+>/gu, "")
    .replace(/&nbsp;/giu, " ")
    .replace(/&amp;/giu, "&")
    .replace(/\u00A0/gu, " ")
    .replace(/\s+/gu, " ")
    .trim()
    .toLowerCase()
    .replace(APOSTROPHES, "'")
}

const fold = (phrase) => String(phrase).toLowerCase().replace(APOSTROPHES, "'")
const containsAny = (text, phrases) =>
  (phrases ?? []).some((phrase) => text.includes(fold(phrase)))

function containsNotNegated(text, phrase) {
  const needle = fold(phrase)
  let index = text.indexOf(needle)
  while (index >= 0) {
    if (!(index >= 3 && text.slice(index - 3, index) === "не ")) return true
    index = text.indexOf(needle, index + 1)
  }
  return false
}

export function classifyKind(text, rules) {
  if (containsAny(text, rules.Cancelled)) return "cancelled"
  // A stabilization notice can also report an accident in some districts; the accident is the alert.
  if (containsAny(text, rules.Accident)) return "accident"
  if ((rules.Stabilization ?? []).some((p) => containsNotNegated(text, p)))
    return "stabilization"
  if (containsAny(text, rules.Emergency)) return "emergency"
  return "unknown"
}

export function findParts(text, region) {
  return (region.Parts ?? [])
    .filter((part) => containsAny(text, part.Aliases))
    .map((part) => part.Id)
}

// "Understood" = a known kind, and districts found whenever the text talks about districts at all.
export function keywordsUnderstand(popupText, region, rules) {
  const text = normalize(popupText)
  if (!text) return true

  const kind = classifyKind(text, rules)
  if (kind === "unknown") return false

  // Stabilization and cancellation notices carry no district alert worth asking a model about.
  if (kind === "stabilization" || kind === "cancelled") return true

  const parts = findParts(text, region)
  return parts.length > 0 || !DISTRICT_WORDS.test(text)
}
