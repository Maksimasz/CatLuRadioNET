// Бейдж обновления на шестерёнке (просьба 2026-10-09 «при запуске проверялось
// обновление и возле шестерёнки появлялась 1»). Проверка GitHub при запуске уже
// есть — checkForUpdates() в init (якорь в settings-ui.test.js), здесь
// проверяем, что её результат виден БЕЗ открытия настроек:
//  1) бейдж лежит внутри кнопки шестерёнки и скрыт по умолчанию;
//  2) стили: absolute-позиция и правило [hidden] (авторский класс иначе
//     перебивает UA-правило [hidden], и бейдж светился бы всегда);
//  3) логика: показ при hasUpdate, скрытие когда обновлений нет, подсказка
//     с версией; ошибка сети бейдж не трогает.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert');

const root = path.join(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(root, ...p), 'utf8');

const index = read('wwwroot', 'index.html');
const css = read('wwwroot', 'styles.css');
const renderer = read('wwwroot', 'js', 'renderer.js');

// ===== 1. Разметка =====
const settingsBtn = index.match(/<button[^>]*data-tab="settings"[^>]*>[\s\S]*?<\/button>/);
assert.ok(settingsBtn, 'Кнопка шестерёнки (data-tab="settings") не найдена');
assert.match(settingsBtn[0], /id="updateBadge"[^>]*hidden[^>]*>1<\//,
  'Бейджа updateBadge нет внутри кнопки шестерёнки, он не скрыт по умолчанию или в нём не «1»');
assert.ok(settingsBtn[0].indexOf('id="updateBadge"') > settingsBtn[0].indexOf('close-icon'),
  'Бейдж должен лежать внутри кнопки — иначе клик по нему не откроет настройки');

// ===== 2. Стили =====
assert.match(css, /\.update-badge \{[^}]*position: absolute;/,
  'У бейджа нет position: absolute — он не привяжется к углу шестерёнки');
assert.match(css, /\.update-badge\[hidden\] \{\s*display: none;/,
  'Нет авторского .update-badge[hidden] { display: none } — бейдж не скроется через hidden');
assert.match(css, /\.tab-btn-icon \{[\s\S]{0,200}overflow: visible;/,
  'У .tab-btn-icon снова overflow: hidden — бейдж обрежется по краю кнопки');

// ===== 3. Логика показа/скрытия =====
const start = renderer.indexOf('async function checkForUpdates()');
const end = renderer.indexOf('async function installAvailableUpdate()');
assert.ok(start >= 0 && end > start, 'Функции checkForUpdates/installAvailableUpdate не найдены');
const check = renderer.slice(start, end);
assert.match(check, /badge\.hidden = false/,
  'checkForUpdates не показывает бейдж при найденном обновлении');
assert.match(check, /badge\.hidden = true/,
  'checkForUpdates не скрывает бейдж, когда обновлений нет');
assert.match(check, /badge\.title = t\('Доступна версия \{version\}\.'/,
  'На бейдже нет подсказки с доступной версией');
assert.ok(!/if \(!result\?\.success\)[\s\S]{0,200}badge\.hidden = true/.test(check),
  'Ошибка сети не должна гасить уже найденный бейдж — офлайн не отменяет обновление');

// ===== 4. Прогресс скачивания установщика (жалоба 2026-10-11 «после кнопки
// «Обновить» ничего не видно минутами»): полоса в Настройки → Система,
// push-событие хоста с процентами, защита от двойного клика. =====
const adapter = read('wwwroot', 'js', 'api-adapter.js');
const mainform = read('MainForm.cs');
const tm = read('wwwroot', 'modules', 'TranslationManager.js');

// Разметка: полоса под кнопкой «Обновить», скрыта по умолчанию, есть
// заливка и подпись.
assert.match(index, /id="installUpdateBtn"[\s\S]{0,250}?id="updateProgress" class="update-progress" hidden/,
  'Блок updateProgress отсутствует под кнопкой installUpdateBtn или не скрыт по умолчанию');
assert.match(index, /id="updateProgressBar" class="update-progress-fill"/,
  'Нет заливки updateProgressBar');
assert.match(index, /id="updateProgressLabel"/,
  'Нет подписи updateProgressLabel под полосой');

// Стили: скрытие через авторское [hidden] (UA-правило проигрывает классам),
// цвет темы, анимация для неопределённого размера.
assert.match(css, /\.update-progress\[hidden\] \{\s*display: none;/,
  'Нет авторского .update-progress[hidden] { display: none } — полоса не скроется через hidden');
assert.match(css, /\.update-progress-fill \{[^}]*background: var\(--md-primary\)/,
  'Заливка полосы не использует --md-primary — прогресс выбивается из темы');
assert.match(css, /\.update-progress-fill\.indeterminate \{[\s\S]{0,200}animation: updateProgressSlide/,
  'Нет неопределённого режима полосы — без Content-Length движения не будет видно');

// Мост: подписка на push-событие updateProgress.
assert.match(adapter, /onUpdateProgress: \(handler\) => WebView2API\.onHostEvent\('updateProgress', handler\)/,
  'api-adapter не пробрасывает push-событие updateProgress');

// Хост: событие с процентами, потоковое чтение (не буферизованное — иначе
// процентов нет вообще), известный размер, дроссель и чистка мусора.
assert.match(mainform, /@event = "updateProgress"/,
  'MainForm не шлёт push-событие updateProgress');
assert.match(mainform, /HttpCompletionOption\.ResponseHeadersRead/,
  'InstallUpdate не читает тело потоково — процентов не будет до конца закачки');
assert.match(mainform, /Headers\.ContentLength/,
  'InstallUpdate не берёт Content-Length — размер установщика неизвестен');
assert.match(mainform, /TotalMilliseconds >= 250/,
  'Нет дросселя прогресса — WebView2 затопят событие на каждый буфер');
assert.match(mainform, /File\.Delete\(installerPath\)/,
  'При ошибке не удаляется недокачанный установщик');

// Страница: защита от двойного клика, полоса прячется после закачки,
// кнопка возвращается при ошибке (можно повторить).
const install = renderer.slice(renderer.indexOf('async function installAvailableUpdate()'));
assert.match(install, /if \(!availableUpdate\?\.url \|\| installingUpdate\) return;/,
  'installAvailableUpdate не защищён от повторного клика во время закачки');
assert.match(install, /button\.disabled = true/,
  'Кнопка «Обновить» не блокируется на время скачивания');
assert.match(install, /hideUpdateProgress\(\);[\s\S]{0,120}installingUpdate = false/,
  'После закачки полоса прогресса не прячется или флаг не снимается');
assert.match(install, /button\.disabled = false/,
  'При ошибке кнопка «Обновить» не возвращается — повторить обновление нельзя');
assert.ok(/register\('onUpdateProgress', handleUpdateProgress\)/.test(renderer),
  'handleUpdateProgress не подписан на события хоста');

// Переводы: строки скачивания обязаны переводиться на все языки — иначе
// в LT/HE диалог и статус показывают русский текст.
for (const key of ['Скачать и установить версию {version}?', 'Скачивание установщика…', 'Не удалось запустить обновление.']) {
  const count = tm.split(`'${key}'`).length - 1;
  assert.ok(count >= 3, `Ключ «${key}» есть меньше чем в трёх словарях (${count}) — два языка останутся по-русски`);
}

console.log('Update badge: OK (бейдж в кнопке шестерёнки, скрыт по умолчанию, показ/скрытие по результату проверки; полоса прогресса скачивания с процентами)');
