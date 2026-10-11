// Чистка списка станций при старте — чистые функции, чтобы их можно было
// проверить в tests/stations-persistence.test.js.
//
// ВАЖНО: id предустановленных станций из stations.js тоже начинаются с
// user-/imported_ — каталог экспортирован из пользовательского списка. Поэтому
// «предустановленная» определяется не префиксом, а принадлежностью к каталогу
// (id входит в defaultIds): именно по этому признаку идут приоритеты ниже.
//
// Два правила, из-за нарушения которых сохранённые станции пропадали
// после перезапуска:
// 1) «Пользовательская» — всё, что добавил владелец: ручное добавление
//    и онлайн-поиск (user-), импорт (imported_), а также любая станция
//    из избранного — избранное хранит только id, и пропавшая станция
//    оставляет ссылку в никуда.
// 2) При дубликатах по id или URL выигрывает пользовательская станция,
//    а не предустановленная: старая логика выбрасывала пользовательскую
//    копию, а следующей чисткой удаляла и предустановленную —
//    терялись обе.
const StationCleanup = (function () {
  // Префиксы id, под которыми хранятся станции, добавленные пользователем.
  // Префикс 'yt-' (YouTube-плейлисты) больше не пользовательский: YouTube
  // из приложения убран, такие станции вычищаются в loadData() до чистки.
  const USER_ID_PREFIXES = ['user-', 'imported_'];

  // Пользовательская станция — или избранная? Чистка такие не трогает.
  function isUserAdded(station, favoriteIds) {
    const id = station && station.id;
    if (!id) return false;
    if (USER_ID_PREFIXES.some(prefix => id.startsWith(prefix))) return true;
    return !!(favoriteIds && typeof favoriteIds.has === 'function' && favoriteIds.has(id));
  }

  // Предустановленная — станция каталога stations.js. Префикс user-/imported_
  // здесь не показатель (см. ВАЖНО выше), поэтому каталог передаётся
  // отдельным набором defaultIds.
  function isPreset(station, favoriteIds, defaultIds) {
    if (defaultIds instanceof Set && defaultIds.has(station && station.id)) return true;
    return !isUserAdded(station, favoriteIds);
  }

  // Дедупликация по id и по URL с приоритетом пользовательских станций.
  // defaultIds — id каталога stations.js (без него каталог считается
  // пользовательским из-за префиксов и выигрывает у настоящих новых станций).
  // Порядок списка сохраняется; dropped — сколько дубликатов отброшено.
  function dedupe(stations, favoriteIds, defaultIds) {
    const dropped = new Set();
    const byId = new Map();
    const byUrl = new Map();

    // Выбросить станцию и освободить её ключи: id освободившейся станции
    // сможет занять следующая, а не уйдёт вместе с ней.
    const drop = (station) => {
      dropped.add(station);
      for (const [key, holder] of byId) if (holder === station) byId.delete(key);
      for (const [key, holder] of byUrl) if (holder === station) byUrl.delete(key);
    };

    for (const station of stations) {
      const id = station.id || '';
      const url = String(station.url || '').toLowerCase().trim();
      const idHolder = id ? byId.get(id) : undefined;
      const urlHolder = url ? byUrl.get(url) : undefined;
      const holders = [idHolder, urlHolder].filter(Boolean);

      if (!holders.length) {
        if (id) byId.set(id, station);
        if (url) byUrl.set(url, station);
        continue;
      }

      // Пользовательская выигрывает, только если все, с кем она конфликтует,
      // — предустановленные (каталог stations.js или вовсе без префикса).
      // Между собой пользовательские побеждает первый.
      const wins = !isPreset(station, favoriteIds, defaultIds)
        && holders.every(holder => isPreset(holder, favoriteIds, defaultIds));
      if (wins) {
        holders.forEach(drop);
        if (id) byId.set(id, station);
        if (url) byUrl.set(url, station);
      } else {
        dropped.add(station);
      }
    }

    return {
      stations: stations.filter(station => !dropped.has(station)),
      dropped: dropped.size
    };
  }

  return { USER_ID_PREFIXES, isUserAdded, isPreset, dedupe };
})();

// Классический скрипт: браузеру — глобальный модуль, Node — на экспорт
// для тестов (тот же приём, что у stations.js и TranslationManager.js).
if (typeof window !== 'undefined') window.StationCleanup = StationCleanup;
if (typeof module !== 'undefined' && module.exports) module.exports = StationCleanup;
