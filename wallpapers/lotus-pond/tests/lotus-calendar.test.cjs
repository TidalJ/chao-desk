// Lotus Pond widgets: lunar date, solar terms and festivals shown on the clock card.
const test = require('node:test');
const assert = require('node:assert/strict');
globalThis.window = globalThis;
require('../lotus-widgets.js');
const { termOn, nextTerm, lunar, festival } = window.PondWidgets._calendar;

const day = (y, m, d) => new Date(y, m - 1, d, 12);

test('solar terms land on the right day (2026)', () => {
  assert.equal(termOn(day(2026, 2, 4)), '立春');
  assert.equal(termOn(day(2026, 4, 5)), '清明');
  assert.equal(termOn(day(2026, 6, 21)), '夏至');
  assert.equal(termOn(day(2026, 9, 23)), '秋分');
  assert.equal(termOn(day(2026, 12, 22)), '冬至');
  assert.equal(termOn(day(2026, 9, 29)), null);
});

test('counts down to the next solar term', () => {
  assert.deepEqual(nextTerm(day(2026, 9, 29)), { name: '寒露', days: 9 });
});

test('lunar date via the Chinese calendar', () => {
  const l = lunar(day(2026, 9, 25));
  assert.equal(l.month, 8);
  assert.equal(l.day, 15);
  assert.match(l.text, /八月十五$/);
  assert.match(lunar(day(2026, 9, 29)).text, /八月十九$/);
  assert.match(lunar(day(2026, 2, 17)).text, /正月初一$/);
});

test('festivals', () => {
  assert.equal(festival(day(2026, 9, 25), lunar(day(2026, 9, 25))), '中秋节');
  assert.equal(festival(day(2026, 2, 17), lunar(day(2026, 2, 17))), '春节');
  assert.equal(festival(day(2026, 2, 16), lunar(day(2026, 2, 16))), '除夕');
  assert.equal(festival(day(2026, 4, 5), lunar(day(2026, 4, 5))), '清明节');
  assert.equal(festival(day(2026, 9, 29), lunar(day(2026, 9, 29))), null);
});
