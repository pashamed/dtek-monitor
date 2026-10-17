import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import test from "node:test"

import { keywordsUnderstand } from "./popup-keywords.js"

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
