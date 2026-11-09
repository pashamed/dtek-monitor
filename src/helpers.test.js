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

test("a mixed district emergency and stabilization popup is an emergency", () => {
  const notice = `Шановні клієнти!

В Білоцерківському, Бучанському, Фастівському та Обухівському районах введені екстрені відключення.

В інших раонах області діють стабілізаційні відключення електроенергії.

Будь ласка, розумно споживайте електроенергію — це допоможе зменшити навантаження на мережу та уникнути екстрених відключень.

Дякуємо за ваше розуміння!`

  assert.equal(isStabilizationNotice(notice), true)
  assert.equal(isEmergencyNotice(notice), true)
})

test("active emergency announcements override stabilization in either order", () => {
  for (const announcement of [
    "Введено екстрені відключення.",
    "Застосовані екстрені відключення.",
    "Застосовано екстрені відключення.",
    "Діють екстрені відключення.",
    "Тривають екстрені відключення.",
  ]) {
    const stabilization = "В інших районах діють стабілізаційні відключення."
    for (const notice of [
      `${announcement}\n${stabilization}`,
      `${stabilization}\n${announcement}`,
    ]) {
      assert.equal(isEmergencyNotice(notice), true, notice)
    }
  }
})

test("stabilization with prevention advice or inactive emergencies stays non-emergency", () => {
  for (const statement of [
    "Розумно споживайте електроенергію, щоб уникнути екстрених відключень.",
    "Екстрені відключення не застосовуються.",
    "Не введені екстрені відключення.",
    "Не застосовано екстрені відключення.",
    "Екстрені відключення скасовано.",
  ]) {
    const notice = `Діють стабілізаційні відключення. ${statement}`
    assert.equal(isEmergencyNotice(notice), false, notice)
  }
})

test("a non-stabilization notice is still an emergency, and no notice is not", () => {
  assert.equal(isEmergencyNotice("У Києві застосовано екстрені відключення."), true)
  assert.equal(isEmergencyNotice(""), false)
  assert.equal(isEmergencyNotice(null), false)
})
