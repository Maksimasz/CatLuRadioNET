const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.join(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const StationCleanup = require('../wwwroot/js/station-cleanup.js');
const renderer = read('wwwroot/js/renderer.js');
const quiet = { log() {}, warn() {}, error() {} };
const noop = () => {};
function rendererContext(state, api, names) {
  const notices = [];
  const context = vm.createContext({
    state, window: { AppAPI: api }, URL, StationCleanup, console: quiet,
    performance, setTimeout, clearTimeout,
    t: (text, vars = {}) => text.replace(/\{(\w+)\}/g, (_, key) => vars[key]),
    showToast: (message, type) => notices.push({ message, type }),
    logError: noop, loadStations: noop, updateNowPlaying: noop,
    updatePlayButton: noop, renderStations: noop, renderFavorites: noop,
    clearLastStation: noop, rememberLastStation: noop, nativeEqualizerValues: () => null,
    playInBrowserPlayer: () => { context.browserStarts++; }, browserStarts: 0
  });
  for (const name of names) {
    const body = renderer.match(new RegExp(`(?:async )?function ${name}\\([^]*?^\\}`, 'm'));
    assert.ok(body, name);
    vm.runInContext(body[0], context);
  }
  context.notices = notices;
  return context;
}
const station = { id: 'user-a', name: 'A', url: 'https://example.test/RadioA' };
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

(async () => {
  // Регистр хоста незначим, регистр пути и токена значим.
  assert.equal(StationCleanup.urlKey('HTTPS://EXAMPLE.test/RadioA'), station.url);
  for (const url of ['https://example.test/radioa', 'https://example.test/RadioA?token=A', 'https://example.test/RadioA?token=a']) {
    const result = StationCleanup.dedupe([station, { id: 'user-b', url }], new Set(), new Set());
    assert.equal(result.stations.length, 2);
  }
  assert.notEqual(StationCleanup.urlKey('https://example.test/?token=A'), StationCleanup.urlKey('https://example.test/?token=a'));

  const adapter = vm.createContext({ window: {}, URL, console: quiet, setTimeout, clearTimeout });
  vm.runInContext(read('wwwroot/js/api-adapter.js'), adapter);
  vm.runInContext(`WebView2API.showSaveDialog = async () => ({canceled:false,filePath:'test.json'});
    WebView2API.writeFile = async () => false;`, adapter);
  assert.equal((await adapter.window.AppAPI.exportStations([station])).success, false);
  vm.runInContext('WebView2API.writeFile = async () => true;', adapter);
  assert.equal((await adapter.window.AppAPI.exportStations([station])).success, true);
  vm.runInContext('WebView2API.showSaveDialog = async () => ({canceled:true});', adapter);
  assert.equal((await adapter.window.AppAPI.exportStations([station])).canceled, true);

  adapter.content = JSON.stringify({ stations: {
    old: { title: 'A', url: station.url },
    fresh: { title: 'B', url: 'https://example.test/radioa' },
    duplicate: { title: 'B duplicate', url: 'https://example.test/radioa' }
  } });
  vm.runInContext(`WebView2API.showOpenDialog = async () => ({canceled:false,filePaths:['test.json']});
    WebView2API.readFile = async () => content;`, adapter);
  let stored = [station];
  let writes = 0;
  const state = { stations: [station] };
  const api = {
    importStations: adapter.window.AppAPI.importStations,
    saveStations: async stations => { writes++; stored = JSON.parse(JSON.stringify(stations)); return true; }
  };
  const imports = rendererContext(state, api, ['importStations']);
  await imports.importStations();
  assert.equal(writes, 1);
  assert.equal(stored.length, 2);
  assert.match(imports.notices.at(-1).message, /Импортировано станций: 1\nВсего станций: 2/);
  assert.match(imports.notices.at(-1).message, /Пропущено дубликатов: 2/);
  state.stations = stored; // Повторный импорт после чтения сохранённых данных.
  await imports.importStations();
  assert.equal(writes, 1);
  state.stations = [station];
  api.saveStations = async () => false;
  await imports.importStations();
  assert.equal(state.stations.length, 1);
  assert.equal(imports.notices.at(-1).type, 'error');
  for (const bad of [{}, { stations: { x: { url: 'file:///secret' } } }, { stations: { x: null } }]) {
    adapter.content = JSON.stringify(bad);
    assert.equal((await adapter.window.AppAPI.importStations()).success, false);
  }

  const saves = ['saveStations', 'saveFavorites', 'saveHistory', 'saveSettings'];
  for (const failure of [...saves, null]) {
    const api = Object.fromEntries(saves.map(name => [name, async () => name !== failure]));
    const ctx = rendererContext({ stations: [], favorites: [], history: [], settings: {}, volume: 0.5 }, api, ['saveData']);
    assert.equal(await ctx.saveData(), failure === null);
    assert.equal(ctx.notices.length, failure === null ? 0 : 1);
  }

  // Отмена в каждой точке ожидания не возобновляет звук и не запускает резерв.
  for (const stage of ['stopNative', 'playNative', 'setNativeEqualizer']) {
    const pending = deferred();
    let reached = false;
    const api = Object.fromEntries(['stopNative', 'playNative', 'setNativeEqualizer'].map(name => [name, async () => {
      if (name === stage && !reached) { reached = true; return pending.promise; }
      return { success: true };
    }]));
    const state = { playbackRequest: 0, settings: {}, volume: 0.5, stations: [], isPlaying: false };
    const ctx = rendererContext(state, api, ['playStation', 'stopPlay']);
    const playing = ctx.playStation(station);
    for (let i = 0; i < 20 && !reached; i++) await Promise.resolve();
    assert.ok(reached, stage);
    ctx.stopPlay();
    pending.resolve({ success: false });
    await playing;
    assert.equal(state.isPlaying, false, stage);
    assert.equal(state.nativeAudio, false, stage);
    assert.equal(ctx.browserStarts, 0, stage);
  }

  // Ответ старой станции после Stop + Play не меняет новую станцию.
  const old = deferred();
  let nativeCalls = 0;
  const api2 = { stopNative: async () => ({}), setNativeEqualizer: async () => ({}),
    playNative: async () => ++nativeCalls === 1 ? old.promise : { success: true } };
  const state2 = { playbackRequest: 0, settings: {}, volume: 0.5, stations: [] };
  const ctx2 = rendererContext(state2, api2, ['playStation', 'stopPlay']);
  const first = ctx2.playStation(station);
  for (let i = 0; i < 20 && !nativeCalls; i++) await Promise.resolve();
  ctx2.stopPlay();
  const next = { ...station, id: 'user-next' };
  await ctx2.playStation(next);
  old.resolve({ success: false });
  await first;
  assert.equal(state2.currentStation.id, next.id);
  assert.equal(state2.isPlaying, true);
  assert.equal(ctx2.browserStarts, 0);

  // Поиск резервного потока тоже прекращается после Stop.
  for (const stage of ['searchOnlineStations', 'checkStream', 'saveData']) {
    const pending = deferred();
    let reached = false;
    let starts = 0;
    const result = name => name === 'searchOnlineStations'
      ? [{ name: 'A', url: 'https://example.test/backup' }]
      : name === 'saveData' ? true : { success: true };
    const call = async name => {
      if (name === stage) { reached = true; return pending.promise; }
      return result(name);
    };
    const recovery = rendererContext({ playbackRequest: 0, stationHealth: {} }, {
      searchOnlineStations: () => call('searchOnlineStations'), checkStream: () => call('checkStream')
    }, ['tryRecoverStation']);
    recovery.normalizedStationName = name => name.toLowerCase();
    recovery.saveData = () => call('saveData');
    recovery.playStation = () => { starts++; };
    const attempt = recovery.tryRecoverStation({ ...station }, station.url);
    for (let i = 0; i < 30 && !reached; i++) await Promise.resolve();
    assert.ok(reached, stage);
    recovery.state.playbackRequest++;
    pending.resolve(result(stage));
    assert.equal(await attempt, false);
    assert.equal(starts, 0);
    assert.equal(recovery.state.isRecoveringStream, false);
  }
  console.log('Data/playback regressions: OK');
})().catch(error => { console.error(error); process.exitCode = 1; });
