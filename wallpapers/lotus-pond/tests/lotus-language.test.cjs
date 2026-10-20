// Lotus Pond widgets: the card's language follows the app unless "Card language" forces one.
const test = require('node:test');
const assert = require('node:assert/strict');
globalThis.window = globalThis;
require('../lotus-widgets.js');
const W = window.PondWidgets;

test('follows the app language by default (Chinese until the host says otherwise)', () => {
  assert.equal(W.isChinese(), true);
  W.setLanguage('en');
  assert.equal(W.isChinese(), false);
  W.setLanguage('zh');
  assert.equal(W.isChinese(), true);
  W.setLanguage('fr'); // ignored
  assert.equal(W.isChinese(), true);
});

test('"Same as Chao Desk" follows the app; 中文 and English force a language', () => {
  W.apply({ clockLanguage: 'Same as Chao Desk' });
  W.setLanguage('en');
  assert.equal(W.isChinese(), false);
  W.apply({ clockLanguage: '中文' });
  assert.equal(W.isChinese(), true);
  W.setLanguage('zh');
  W.apply({ clockLanguage: 'English' });
  assert.equal(W.isChinese(), false);
  W.apply({ clockLanguage: 'Same as Chao Desk' });
  assert.equal(W.isChinese(), true);
});
