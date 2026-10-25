import assert from "node:assert/strict"
import test from "node:test"

import { isEmergencyNotice, isStabilizationNotice } from "./helpers.js"

const STABILIZATION_WITH_ACCIDENT = `Шановні клієнти!
За наказом НЕК Укренерго 25.10 діють стабілізаційні відключення електроенергії.
У Бучанському, Обухівському та Фастівському районів зникло світло через аварію в мережі. Фахівці вже працюють над відновленням електропостачання.`

test("a stabilization notice that reports an accident is an emergency", () => {
  assert.equal(isStabilizationNotice(STABILIZATION_WITH_ACCIDENT), true)
  assert.equal(isEmergencyNotice(STABILIZATION_WITH_ACCIDENT), true)
})

test("a plain stabilization notice is not an emergency", () => {
  const notice =
    "За наказом НЕК Укренерго 25.10 діють стабілізаційні відключення електроенергії."
  assert.equal(isEmergencyNotice(notice), false)
})

test("a non-stabilization notice is still an emergency, and no notice is not", () => {
  assert.equal(isEmergencyNotice("У Києві застосовано екстрені відключення."), true)
  assert.equal(isEmergencyNotice(""), false)
  assert.equal(isEmergencyNotice(null), false)
})
