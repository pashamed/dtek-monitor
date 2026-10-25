import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import test from "node:test"

import { classifyKind, findParts, keywordsUnderstand, normalize } from "./popup-keywords.js"

const settings = JSON.parse(
  readFileSync(
    new URL("../bot/src/DtekBot.Api/dtek-settings.json", import.meta.url),
    "utf8"
  )
)
const rules = settings.Dtek.Classification
const region = (id) => settings.Dtek.Regions.find((r) => r.Id === id)

test("a popup naming known districts is understood", () => {
  const popup =
    "Шановні клієнти! Через атаку на енергообʼєкти запроваджені <b>екстрені відключення</b> в <b>Обухівському</b>, <b>Фастівському</b> і <b>Бучанському районах</b>.<br>В інших районах діють графіки."
  assert.equal(keywordsUnderstand(popup, region("kyiv-oblast"), rules), true)
})

test("an emergency popup with no district words is understood as region-wide", () => {
  const popup =
    "Шановні клієнти! За наказом НЕК Укренерго введені екстрені відключення електроенергії. Графіки відключень при цьому не діють."
  assert.equal(keywordsUnderstand(popup, region("kyiv"), rules), true)
})

test("an alert naming a district the keywords do not know is unfamiliar", () => {
  const popup =
    "Увага! Екстрені відключення у Макарівському районі через пошкодження мережі."
  assert.equal(keywordsUnderstand(popup, region("kyiv-oblast"), rules), false)
})

test("a text with no recognised kind is unfamiliar", () => {
  assert.equal(
    keywordsUnderstand("Просимо відвідувати наш сайт частіше.", region("kyiv"), rules),
    false
  )
})

test("stabilization and cancellation notices never need the model", () => {
  assert.equal(
    keywordsUnderstand("Сьогодні діють стабілізаційні відключення в районах.", region("kyiv"), rules),
    true
  )
  assert.equal(
    keywordsUnderstand("Екстрені відключення скасовано в усіх районах.", region("kyiv"), rules),
    true
  )
})

test("an empty popup is understood (nothing to ask)", () => {
  assert.equal(keywordsUnderstand("", region("kyiv"), rules), true)
})

test("a stabilization notice reporting a district accident is an accident for those districts", () => {
  const popup =
    "<h6>Шановні клієнти!</h6><p>За наказом НЕК Укренерго <strong>25.10</strong> діють<strong> стабілізаційні </strong>відключення електроенергії.&nbsp;&nbsp;</p>" +
    "<p>У <strong>Бучанському, Обухівському та Фастівському</strong> районів зникло світло через аварію в мережі. Фахівці вже працюють над відновленням електропостачання.</p>"
  const text = normalize(popup)
  assert.equal(classifyKind(text, rules), "accident")
  assert.deepEqual(findParts(text, region("kyiv-oblast")), ["bucha", "obukhiv", "fastiv"])
  assert.equal(keywordsUnderstand(popup, region("kyiv-oblast"), rules), true)
})

test("a stabilization notice reporting an accident applies to any named district", () => {
  for (const r of settings.Dtek.Regions) {
    for (const part of r.Parts) {
      const popup =
        "<p>За наказом НЕК Укренерго діють стабілізаційні відключення електроенергії.</p>" +
        `<p>${part.Name}: зникло світло через аварію в мережі.</p>`
      const text = normalize(popup)
      assert.equal(classifyKind(text, rules), "accident", `${r.Id}/${part.Id}`)
      assert.deepEqual(findParts(text, r), [part.Id], `${r.Id}/${part.Id}`)
      assert.equal(keywordsUnderstand(popup, r, rules), true, `${r.Id}/${part.Id}`)
    }
  }
})
