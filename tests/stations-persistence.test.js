// Сохранённые станции обязаны переживать перезапуск.
//
// Пойманные баги:
// 1) Хранилище затиралось предустановленными при каждом старте (loadStations
//    писал в файл ДО того, как loadData успевал прочитать список), а чистка
//    выкидывала всё, что не совпадает с id из stations.js — в частности,
//    станции, добавленные вручную (user-) и импорт (imported_). Симптом:
//    добавил станцию — она есть до выключения, при следующем запуске пропала.
// 2) Жалоба 2026-10-11: «станции редактируются только при работе плеера,
//    после выключения всё возвращается» — как с предустановленными, так и с
//    новыми. Причина: id каталога stations.js тоже начинаются с user-/imported_
//    (экспорт из пользовательского списка), а loadData при каждом старте
//    воскрешал «недостающие предустановленные» и пересобирал список из свежего
//    каталога — вместе с удалёнными возвращалась исходная версия отредактированных,
//    а дедупликация без каталога побеждала новую станцию каталожной копией
//    того же URL. Лечение: журнал правок stationMods ('removed'/'edited')
//    плюс defaultIds в StationCleanup.dedupe.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert');

const root = path.join(__dirname, '..');
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), 'utf8');

// ——— Модуль чистки: проверяем поведением, а не строками ———
const StationCleanup = require(path.join(root, 'wwwroot', 'js', 'station-cleanup.js'));

// Пользовательские: ручное/онлайн-поиск (user-) и импорт
for (const id of ['user-1', 'imported_1', 'user-online-radio-1']) {
  assert.ok(
    StationCleanup.isUserAdded({ id }, new Set()),
    `${id} должна считаться пользовательской и не выкидываться чисткой`
  );
}

// Старый предустановленный (id не из stations.js) — под чистку
assert.ok(
  !StationCleanup.isUserAdded({ id: 'rb-1' }, new Set()),
  'старый предустановленный должен уходить в «старые»'
);

// Любая избранная станция под защитой, даже с нестандартным id
assert.ok(
  StationCleanup.isUserAdded({ id: 'preview-online-x' }, new Set(['preview-online-x'])),
  'избранная станция обязана быть под защитой — иначе избранное станет ссылкой в никуда'
);

// Дедупликация: при конфликте URL побеждает пользовательская станция
{
  const preset = { id: 'rb-1', url: 'https://stream.example/one' };
  const user = { id: 'user-online-1', url: 'https://stream.example/one' };
  const result = StationCleanup.dedupe([preset, user], new Set());
  assert.deepStrictEqual(
    result.stations.map(s => s.id),
    ['user-online-1'],
    'должна остаться пользовательская станция, а не предустановленная'
  );
  assert.strictEqual(result.dropped, 1);
}

// Обычный дубль без пользовательских — побеждает первый, как раньше
{
  const a = { id: 'x', url: 'u' };
  const b = { id: 'y', url: 'u' };
  const result = StationCleanup.dedupe([a, b], new Set());
  assert.deepStrictEqual(result.stations.map(s => s.id), ['x']);
}

// Непересекающаяся пользовательская станция не конфликтует и сохраняется,
// порядок списка не меняется
{
  const preset = { id: 'rb-1', url: 'https://a.example/1' };
  const user = { id: 'user-2', url: 'https://b.example/2' };
  const preset2 = { id: 'rb-2', url: 'https://a.example/2' };
  const result = StationCleanup.dedupe([preset, user, preset2], new Set());
  assert.deepStrictEqual(
    result.stations.map(s => s.id),
    ['rb-1', 'user-2', 'rb-2'],
    'порядок и чужие станции должны сохраняться'
  );
}

// Каталог stations.js имеет префиксы user-/imported_ в id (экспорт из
// пользовательского списка) — поэтому дедупликация получает defaultIds.
// Без них каталожная копия считалась пользовательской, стояла в списке
// первой и побеждала настоящую новую станцию с тем же URL: после
// перезапуска «моя» станция схлопывалась в каталожную.
{
  const catalog = { id: 'imported_1_1', url: 'https://stream.example/x' };
  const mine = { id: 'user-2', url: 'https://stream.example/x' };
  const result = StationCleanup.dedupe([catalog, mine], new Set(), new Set(['imported_1_1']));
  assert.deepStrictEqual(
    result.stations.map(s => s.id),
    ['user-2'],
    'новая станция обязана побеждать каталожную копию с тем же URL (каталог — предустановленный)'
  );
  assert.ok(
    StationCleanup.isPreset(catalog, new Set(), new Set(['imported_1_1'])),
    'станция каталога должна считаться предустановленной, несмотря на префикс imported_ в id'
  );
  assert.ok(
    !StationCleanup.isPreset(mine, new Set(), new Set(['imported_1_1'])),
    'станция вне каталога с префиксом user- — не предустановленная'
  );
}

// Между собой две станции каталога побеждает первая — как и прежде
{
  const a = { id: 'imported_1', url: 'u' };
  const b = { id: 'imported_2', url: 'u' };
  const result = StationCleanup.dedupe([a, b], new Set(), new Set(['imported_1', 'imported_2']));
  assert.deepStrictEqual(result.stations.map(s => s.id), ['imported_1']);
}

// ——— Страница (renderer) ———
const renderer = read('wwwroot', 'js', 'renderer.js');

// 1) loadStations больше не пишет в хранилище: именно его запись на
//    DOMContentLoaded (до loadData) затирала сохранённый список.
const loadStationsBody = renderer.match(/function loadStations\(\) \{[\s\S]*?\n\}/);
assert.ok(loadStationsBody, 'loadStations не найдена');
assert.ok(
  !loadStationsBody[0].includes('saveStations'),
  'loadStations не должна писать в хранилище — затрёт сохранённые станции до loadData'
);

// 2) Чистка в loadData идёт через StationCleanup; дедупликация получает
//    каталог (defaultIds): id предустановленных тоже начинаются с user-/imported_
assert.ok(
  renderer.includes('StationCleanup.dedupe(state.stations, favoriteIds, defaultIds)'),
  'loadData не использует StationCleanup.dedupe с каталогом — дубликаты снова выберут проигравшего'
);
const keepFilter = 'StationCleanup.isUserAdded(s, favoriteIds) && !defaultIds.has(s.id)';
assert.ok(
  renderer.split(keepFilter).length - 1 >= 1,
  'фильтр сохранения пользовательских станций должен использовать StationCleanup'
);

// 2б) Журнал правок каталога (жалоба 2026-10-11 «списки редактируются только
//     при работе плеера, после выключения всё возвращается»): удалённые не
//     воскресают, отредактированные не откатываются при пересборке каталога.
assert.ok(!renderer.includes('missingDefaults'),
  'В loadData осталась пересборка по «недостающим предустановленным» — удалённые станции снова вернутся после перезапуска');
assert.ok(!renderer.includes('oldDefaultStations'),
  'В loadData осталась пересборка по «старым предустановленным» — она пересобирала каталог и откатывала правки');
for (const [marker, what] of [
  ['getStationMods', 'чтения журнала правок'],
  ['removedIds', 'набора удалённых'],
  ['mineOrFresh', 'ветки сохранения отредактированных']
]) {
  assert.ok(renderer.includes(marker), `В loadData нет ${what} (stationMods)`);
}

// 2в) Правки фиксируются в журнале: удаление — 'removed', редактирование —
//     'edited'; ведётся только для id каталога stations.js.
assert.match(renderer, /markCatalogMod\(stationId, 'removed'\)/,
  'deleteStation не помечает удалённую предустановленную — она вернётся при следующем старте');
assert.match(renderer, /markCatalogMod\(stationId, 'edited'\)/,
  'updateStation не помечает отредактированную предустановленную — правка слетит при пересборке каталога');
assert.ok(renderer.includes('if (!catalog.some(s => s.id === stationId)) return;'),
  'markCatalogMod должен помечать только станции каталога stations.js');
{
  const adapter = read('wwwroot', 'js', 'api-adapter.js');
  for (const key of ['getStationMods', 'saveStationMods']) {
    assert.ok(adapter.includes(`${key}:`), `В api-adapter нет моста ${key} для журнала stationMods`);
  }
}

// 3) YouTube из приложения убран: легаси-плейлисты yt- вычищаются при старте
//    из всех трёх списков разом (станции, избранное, история) и перестают
//    считаться пользовательскими — иначе мёртвые записи останутся навсегда.
assert.ok(
  !StationCleanup.USER_ID_PREFIXES.includes('yt-'),
  "'yt-' всё ещё в USER_ID_PREFIXES — легаси-станции не уйдут под чистку"
);
assert.ok(
  !renderer.includes("stationId.startsWith('yt-')"),
  'isUserStation снова знает про yt- — YouTube в приложение вернулся'
);
const purgeAt = renderer.indexOf('const isLegacyYt =');
assert.ok(purgeAt !== -1, 'В loadData нет вычистки легаси-YouTube-плейлистов');
const purgeBlock = renderer.slice(purgeAt, purgeAt + 900);
for (const [marker, what] of [
  ['saveStations(state.stations)', 'станции'],
  ['saveFavorites(state.favorites)', 'избранное'],
  ['saveHistory(state.history)', 'история']
]) {
  assert.ok(purgeBlock.includes(marker), `Вычистка YouTube не сохраняет ${what} — записи вернутся после перезапуска`);
}
assert.ok(
  purgeBlock.includes("item.type === 'youtube-playlist'") && purgeBlock.includes("item.id.startsWith('yt-')"),
  'Вычистка ловит только один из двух форматов легаси-плейлистов'
);

// 4) Восстановление пропавшей избранной станции из истории
assert.ok(
  renderer.includes('Восстановлено из истории по избранному'),
  'нет восстановления станций из истории по избранному — старые потери не залечатся'
);

// 5) getStore обязан отдавать JSON: JValue.ToString() для stationsVersion
//    отдавал «1.0.3» без кавычек — JSON.parse падал на каждом старте, версия
//    читалась как null и merge станций гонялся даже при совпадении версий.
const mainForm = read('MainForm.cs');
assert.ok(
  mainForm.includes('JsonConvert.SerializeObject(storeData[key])'),
  'getStore должен сериализовать значение как JSON, а не ToString()'
);
assert.ok(
  !mainForm.includes('storeData[key]?.ToString()'),
  'getStore через ToString() вернулся — версии станций снова не будут читаться'
);

// ——— YouTube удалён из хоста и моста полностью ———
for (const file of ['MainForm.cs', 'wwwroot/js/api-adapter.js', 'wwwroot/js/renderer.js', 'wwwroot/index.html']) {
  const src = read(...file.split('/'));
  const leftover = src.match(/YtDlp|resolveYoutube|playYoutube|ytQueue|seekNative|nativeEnded/i);
  assert.ok(!leftover, `В ${file} остался код YouTube: ${leftover && leftover[0]}`);
}

// ——— Разметка: модуль подключён до renderer ———
const html = read('wwwroot', 'index.html');
const cleanupTag = html.indexOf('station-cleanup.js');
assert.ok(cleanupTag !== -1, 'в index.html не подключён station-cleanup.js');
assert.ok(
  cleanupTag < html.indexOf('js/renderer.js'),
  'station-cleanup.js должен подключаться до renderer.js'
);

console.log('Stations persistence: OK (чистка не затирает, дубликаты в пользу пользователя, легаси-YouTube вычищен)');
