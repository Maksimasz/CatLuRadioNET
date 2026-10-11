using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using LibVLCSharp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CatLuRadio
{
    public partial class MainForm : Form
    {
        private WebView2 webView = null!;
        private readonly string dataPath;
        private JObject storeData = new();
        private readonly HttpClient httpClient;
        // Сериализация записи хранилища: без неё параллельные setStore падали
        // с "file is being used by another process" и изменения терялись.
        private readonly SemaphoreSlim storeLock = new(1, 1);
        private long storeSaveSeq;   // последний выданный номер сохранения
        private long storeSavedSeq;  // последний реально записанный на диск
        // Идёт закачка установщика обновления: защита от второго клика
        // (второй параллельный закаччик = два установщика и «не срабатывает»).
        private volatile bool updateDownloading;

        // ВАЖНО: имя хранилища намеренно отличается от store.json.
        // Старая версия приложения использует тот же каталог
        // %LocalAppData%\CatLuRadio\data и тот же файл store.json — писать туда
        // из этой версии значит затирать данные, которыми пользуются другие.
        private const string StoreFileName = "store_v2.json";

        // Файлы, доступ к которым страница получила через системный диалог
        // (импорт/экспорт). Всё остальное за пределами каталога данных
        // приложения для AppAPI недоступно — иначе XSS в странице превращался
        // бы в запись произвольного файла на диске.
        private readonly HashSet<string> approvedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly LibVLC nativeVlc;
        private readonly MediaPlayer nativePlayer;
        private Media? nativeMedia;
        private Equalizer? nativeEqualizer;
        // Последние значения полос с страницы (null — полос нет) и усиление
        // вывода в дБ (0…+12): вместе собирают цепочку ApplyEqualizerFilter().
        private JArray? nativeEqValues;
        private double nativeGainDb = 6;
        private TaskCompletionSource<bool>? nativeStart;
        private const string UpdatesApiUrl = "https://api.github.com/repos/Maksimasz/CatLuRadioNET/releases/latest";
        private const string UpdateAssetPrefix = "https://github.com/Maksimasz/CatLuRadioNET/releases/download/";

        private class ApiResponse
        {
            public bool Success { get; set; }
            public object? Data { get; set; }
            public string? Error { get; set; }
            public string? FilePath { get; set; }
            public string? Content { get; set; }
            public int? Status { get; set; }
        }

        public MainForm()
        {
            dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CatLuRadio", "data");

            Directory.CreateDirectory(dataPath);

            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.Add("User-Agent", "CatLuRadio/" + Application.ProductVersion);
            Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
            // Фильтры audio-filter на уровне экземпляра LibVLC здесь НЕ работают:
            // SetEqualizer при каждом старте трека перезаписывает переменную
            // "audio-filter" на "equalizer", выкидывая из цепочки чужие фильтры
            // (проверено: --audio-filter=gain --gain-value=2.0 не менял
            // громкость). Поэтому усиление — только через Preamp самого
            // эквалайзера, ползунок 0…+12 дБ в настройках (см.
            // ApplyEqualizerFilter), громкость — nativePlayer.Volume.
            nativeVlc = new LibVLC("--no-video", "--network-caching=1000");
            nativePlayer = new MediaPlayer(nativeVlc);
            nativePlayer.Playing += (_, _) => nativeStart?.TrySetResult(true);
            nativePlayer.EncounteredError += (_, _) => { nativeStart?.TrySetResult(false); OnNativeError(); };
            LoadStore();
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            try
            {
                this.SuspendLayout();
                webView = new WebView2 { Dock = DockStyle.Fill };
                // Основное окно: 630×780 (просьба 2026-10-11); рабочая область
                // экрана — потолок, чтобы на низких мониторах окно не уезжало
                // за край.
                var startArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 630, 780);
                this.ClientSize = new Size(Math.Min(630, startArea.Width), Math.Min(780, startArea.Height));
                this.MinimumSize = new Size(500, 500);
                this.Text = $"CatLu Radio NET v{Application.ProductVersion}";
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                this.StartPosition = FormStartPosition.CenterScreen;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.Controls.Add(webView);
                InitializeWebView();
                this.ResumeLayout(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка инициализации формы", ex);
                MessageBox.Show("InitializeComponent error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.ResumeLayout(false);
            }
        }

        private async void InitializeWebView()
        {
            try
            {
                var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
                var fixedRuntime = Directory.Exists(runtimeRoot)
                    ? Directory.EnumerateDirectories(runtimeRoot).FirstOrDefault(folder => File.Exists(Path.Combine(folder, "msedgewebview2.exe")))
                    : null;
                AppLog.Info(fixedRuntime is null
                    ? "WebView2: используется встроенный в систему рантайм"
                    : "WebView2: фиксированный рантайм " + Path.GetFileName(fixedRuntime));
                // Автозапуск последней станции при старте: без этого флага Chromium
                // требует жеста пользователя до play(), и автостарт падает с
                // NotAllowedError («пользователь не взаимодействовал со страницей»).
                var envOptions = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
                };
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: fixedRuntime,
                    // Профиль тоже разделяем со старой версией: WebView2 блокирует
                    // userDataFolder на время работы, и без разделения вторая
                    // копия не смогла бы запуститься одновременно с первой.
                    userDataFolder: Path.Combine(dataPath, "WebView2-v2"),
                    options: envOptions);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                webView.CoreWebView2.Settings.IsScriptEnabled = true;
                webView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                webView.WebMessageReceived += WebView_WebMessageReceived;
                // Страница обязана оставаться в нашем wwwroot: иначе любая ссылка
                // уводила бы окно приложения наружу вместе со всем AppAPI.
                webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                string htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
                if (File.Exists(htmlPath)) {
                    webView.CoreWebView2.Navigate("file://" + htmlPath.Replace("\\", "/"));
                } else {
                    AppLog.Error("Главный файл интерфейса не найден: " + htmlPath);
                    MessageBox.Show("HTML file not found:\n" + htmlPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex) {
                AppLog.Error("Ошибка инициализации WebView2", ex);
                MessageBox.Show("WebView2 initialization error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsAppPage(e.Uri)) return;
            e.Cancel = true;
            AppLog.Warn("Заблокирована навигация страницы: " + e.Uri);
            // Ссылка без target="_blank" раньше просто уводила приложение с главной
            // страницы — теперь открывается во внешнем браузере.
            OpenInBrowser(e.Uri);
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // WebView2 по умолчанию открыл бы новое окно в этом же WebView —
            // то есть внешний ресурс получил бы прямой доступ к AppAPI.
            e.Handled = true;
            OpenInBrowser(e.Uri);
        }

        /// <summary>Страница лежит в нашем wwwroot (или это about:).</summary>
        private static bool IsAppPage(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
            if (!parsed.IsFile) return parsed.Scheme == "about";
            try
            {
                string wwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return Path.GetFullPath(parsed.LocalPath)
                    .StartsWith(wwwroot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void OpenInBrowser(string uri)
        {
            try
            {
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
                if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Error("Не удалось открыть ссылку во внешнем браузере: " + uri, ex); }
        }

        private void LoadStore()
        {
            string storePath = Path.Combine(dataPath, StoreFileName);
            if (File.Exists(storePath)) {
                try { storeData = JObject.Parse(File.ReadAllText(storePath)); }
                catch (Exception ex)
                {
                    // Файл хранилища повреждён — начинаем с пустого, но пишем
                    // об этом в лог, иначе пользователь не поймёт, куда делись станции.
                    AppLog.Error("Файл хранилища повреждён, начинаю с пустого: " + storePath, ex);
                    storeData = new JObject();
                }
            } else { storeData = new JObject(); }
        }

        /// <summary>
        /// Снимок текущего состояния и запуск записи на диск.
        /// Снимок берётся здесь же, в UI-потоке: storeData мутируется только в нём,
        /// а Newtonsoft JObject не потокобезопасен при одновременных чтении и записи.
        /// </summary>
        private void RequestSaveStore()
        {
            long seq = Interlocked.Increment(ref storeSaveSeq);
            string snapshot = storeData.ToString(Formatting.None);
            _ = SaveStoreAsync(seq, snapshot);
        }

        private async Task SaveStoreAsync(long seq, string snapshot)
        {
            // Уже есть запись более нового состояния — эта стала бы откатом.
            if (seq <= Volatile.Read(ref storeSavedSeq)) return;

            string storePath = Path.Combine(dataPath, StoreFileName);
            await storeLock.WaitAsync();
            try
            {
                if (seq <= Volatile.Read(ref storeSavedSeq)) return;

                // Пишем во временный файл и подменяем: падение посреди записи
                // больше не оставляет обрезанный файл хранилища.
                string tempPath = storePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, snapshot, Encoding.UTF8);
                File.Move(tempPath, storePath, overwrite: true);
                Volatile.Write(ref storeSavedSeq, seq);
            }
            catch (Exception ex)
            {
                // Задача выполняется без await: без этого catch исключение терялось бы
                // полностью (теперь ещё и ловится UnobservedTaskException в Program.cs).
                AppLog.Error("Не удалось сохранить хранилище " + storePath, ex);
            }
            finally
            {
                storeLock.Release();
            }
        }

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject? data = null;
            try
            {
                string message = e.TryGetWebMessageAsString();
                data = JObject.Parse(message);
                string action = data["action"]?.ToString() ?? "";
                string callbackId = data["callbackId"]?.ToString() ?? "";
                
                switch (action)
                {
                    case "getStore":
                        string key = data["key"]?.ToString() ?? "";
                        // Скалярные значения (например stationsVersion) у JValue
                        // ToString() отдаёт без кавычек — JSON.parse на странице
                        // падал и версия всегда читалась как null (лишний merge
                        // станций при каждом старте). Сериализуем как JSON.
                        SendCallback(callbackId, storeData[key] is null ? "null" : JsonConvert.SerializeObject(storeData[key]));
                        break;
                    case "setStore":
                        key = data["key"]?.ToString() ?? "";
                        storeData[key] = data["value"];
                        RequestSaveStore();
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "log":
                        // Диагностика со страницы: консоль WebView2 пользователю
                        // недоступна, а тайминги/таймауты воспроизведения нужны в
                        // app.log для поиска причин «долго подключается».
                        AppLog.Info("[page] " + (data["message"]?.ToString() ?? ""));
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "httpGet":
                        _ = HttpGet(data["url"]?.ToString() ?? "", callbackId);
                        break;
                    case "checkForUpdate":
                        _ = CheckForUpdate(callbackId);
                        break;
                    case "installUpdate":
                        _ = InstallUpdate(data["url"]?.ToString() ?? "", callbackId);
                        break;
                    case "checkStream":
                        _ = CheckStream(data["url"]?.ToString() ?? "", callbackId);
                        break;
                    case "playNative":
                        _ = PlayNative(data["url"]?.ToString() ?? "", data["volume"]?.Value<double>() ?? 0.5, callbackId);
                        break;
                    case "pauseNative":
                        nativePlayer.Pause();
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "resumeNative":
                        nativePlayer.Play();
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "stopNative":
                        StopNative();
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "setNativeVolume":
                        nativePlayer.Volume = (int)Math.Round(Math.Clamp(data["volume"]?.Value<double>() ?? 0.5, 0, 1) * 100);
                        // Смена громкости на лету перестраивает цепочку вывода
                        // звука и слетающие на ней полосы: без повторного
                        // SetEqualizer эквалайзер замолкал до следующего старта.
                        // Повторно навешиваем только реально включённый фильтр —
                        // снятый (null) не трогаем. Громкости это не касается:
                        // её даёт один Volume, Preamp — отдельная настройка.
                        if (nativeEqualizer is not null) nativePlayer.SetEqualizer(nativeEqualizer);
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "setNativeEqualizer":
                        SetNativeEqualizer(data["values"] as JArray, callbackId);
                        break;
                    case "setNativeGain":
                        SetNativeGain(data["db"]?.Value<double>() ?? 6, callbackId);
                        break;
                    case "showSaveDialog":
                        ShowSaveDialog(data["defaultPath"]?.ToString() ?? "file.json", callbackId);
                        break;
                    case "showOpenDialog":
                        ShowOpenDialog(callbackId);
                        break;
                    case "writeFile":
                        _ = WriteFile(data["path"]?.ToString() ?? "", data["content"]?.ToString() ?? "", callbackId);
                        break;
                    case "readFile":
                        _ = ReadFile(data["path"]?.ToString() ?? "", callbackId);
                        break;
                    case "setWindowSize":
                        SetWindowSize(data["width"]?.Value<int>() ?? Width, data["height"]?.Value<int>() ?? Height);
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "setWindowMinimumSize":
                        SetWindowMinimumSize(data["width"]?.Value<int>() ?? 0, data["height"]?.Value<int>() ?? 0);
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "setWindowResizable":
                        FormBorderStyle = data["resizable"]?.Value<bool>() == false ? FormBorderStyle.FixedSingle : FormBorderStyle.Sizable;
                        MaximizeBox = FormBorderStyle == FormBorderStyle.Sizable;
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "exitApp":
                        BeginInvoke(Close);
                        SendCallback(callbackId, new { success = true });
                        break;
                    case "setMiniPlayer":
                        SetMiniPlayer(data["enabled"]?.Value<bool>() == true);
                        SendCallback(callbackId, new { success = true });
                        break;
                    default:
                        // Раньше неизвестное действие молча игнорировалось: callback
                        // не отправлялся, и промис в странице висел навсегда.
                        AppLog.Warn($"Неизвестное действие из страницы: '{action}'");
                        SendCallback(callbackId, new { success = false, error = "Неизвестное действие: " + action });
                        break;
                }
            }
            catch (Exception ex)
            {
                // Раньше ошибка молча уходила в Console.WriteLine, которого в WinExe нет.
                AppLog.Error($"Ошибка обработки сообщения из страницы (action={data?["action"]})", ex);
            }
        }

        private void SetWindowSize(int width, int height) {
            WindowState = FormWindowState.Normal;
            // Размер меряется по фактическому контенту (ряды кнопок, полосы),
            // а рабочая область экрана — жёсткий потолок: иначе окно уезжает
            // за край монитора и его нельзя ни уместить, ни сдвинуть.
            var area = Screen.FromControl(this).WorkingArea;
            var w = Math.Min(Math.Max(width, MinimumSize.Width), area.Width);
            var h = Math.Min(Math.Max(height, MinimumSize.Height), area.Height);
            ClientSize = new Size(w, h);
        }
        private void SetWindowMinimumSize(int width, int height) {
            // То же для минимума: длинные подписи по-иному языку разгоняли
            // MinimumSize шире экрана, и контент всё равно обрезался.
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(
                Math.Clamp(width, 0, Math.Max(0, area.Width)),
                Math.Clamp(height, 0, Math.Max(0, area.Height)));
        }
        private void SetMiniPlayer(bool enabled) {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = enabled ? FormBorderStyle.FixedSingle : FormBorderStyle.Sizable;
            MaximizeBox = !enabled;
            MinimumSize = enabled ? Size.Empty : new Size(500, 500);
            if (enabled) {
                Size = new Size(600, 110);
            } else {
                // Возврат из мини-плеера — ровно тот же кадр, что и при старте
                // (ClientSize, а не Size: рамка добавляла бы пару пикселей).
                var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 630, 780);
                ClientSize = new Size(Math.Min(630, area.Width), Math.Min(780, area.Height));
            }
        }
        private void SendCallback(string callbackId, object result) {
            if (InvokeRequired) { Invoke(() => SendCallback(callbackId, result)); return; }
            webView.CoreWebView2.PostWebMessageAsString(JsonConvert.SerializeObject(new { callbackId, resultRaw = result }));
        }
        private async Task HttpGet(string url, string callbackId) {
            try {
                string response = await httpClient.GetStringAsync(url);
                SendCallback(callbackId, new ApiResponse { Success = true, Data = response });
            } catch (Exception ex) { SendCallback(callbackId, new ApiResponse { Success = false, Error = ex.Message }); }
        }
        private async Task CheckForUpdate(string callbackId) {
            try {
                var release = JObject.Parse(await httpClient.GetStringAsync(UpdatesApiUrl));
                var tag = release["tag_name"]?.ToString().TrimStart('v', 'V') ?? "";
                var asset = release["assets"]?.FirstOrDefault(item => item?["name"]?.ToString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
                var url = asset?["browser_download_url"]?.ToString() ?? "";
                // Сравниваем именно версию сборки: она всегда чисто числовая (3.1.12.0),
                // тогда как ProductVersion (InformationalVersion) может быть
                // дополнен хешем коммита вида 3.1.12+abcdef и не парсится.
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var hasUpdate = Version.TryParse(tag, out var latest)
                    && currentVersion is not null
                    && latest > currentVersion;
                SendCallback(callbackId, new { success = true, hasUpdate, version = tag, url });
            } catch (Exception ex) { SendCallback(callbackId, new { success = false, error = ex.Message }); }
        }
        // Скачивание установщика — большой файл (350+ МБ), на слабой линии
        // минуты. Раньше здесь был один GetStreamAsync без обратной связи:
        // после клика «Обновить» экран молчал минуты-две — «ничего не
        // происходит» (жалоба 2026-10-11). Теперь копируем поток вручную
        // и шлём push-событие updateProgress с процентами для полосы прогресса.
        private async Task InstallUpdate(string url, string callbackId) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !url.StartsWith(UpdateAssetPrefix, StringComparison.OrdinalIgnoreCase)) {
                SendCallback(callbackId, new { success = false, error = "Некорректная ссылка обновления" });
                return;
            }
            if (updateDownloading) {
                SendCallback(callbackId, new { success = false, error = "Установщик уже скачивается" });
                return;
            }
            // Отдельный клиент: шаринговый (15 с) таймаутится на медленных
            // заголовках, а здесь ждать можно долго.
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var installerPath = Path.Combine(Path.GetTempPath(), $"CatLuRadio-update-{Guid.NewGuid():N}.exe");
            try {
                updateDownloading = true;
                // ResponseHeadersRead: тело читаем сами, чтобы считать байты.
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using (var source = await response.Content.ReadAsStreamAsync())
                await using (var target = File.Create(installerPath)) {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastSent = DateTime.MinValue;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0) {
                        await target.WriteAsync(buffer.AsMemory(0, read));
                        received += read;
                        // Дроссель ~4 сообщения/с: без него WebView2 получал
                        // бы событие на каждый буфер (тычи за закачку).
                        var now = DateTime.UtcNow;
                        if ((now - lastSent).TotalMilliseconds >= 250) {
                            lastSent = now;
                            SendUpdateProgress(total > 0 ? (int)(received * 100 / total) : -1, received, total);
                        }
                    }
                    SendUpdateProgress(total > 0 ? 100 : -1, received, total > 0 ? total : received);
                }
                AppLog.Info($"Обновление скачано: {installerPath}");
                Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                SendCallback(callbackId, new { success = true });
                BeginInvoke(Close);
            } catch (Exception ex) {
                AppLog.Error("Обновление: не удалось скачать установщик", ex);
                // Частично скачанный .exe в Temp — мусор, убираем.
                try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { /* Недокачанный файл уберёт чистка Temp. */ }
                updateDownloading = false;
                SendCallback(callbackId, new { success = false, error = ex.Message });
            }
        }

        // Проценты скачивания обновления — в страницу: полоса прогресса идёт
        // на глазах, вместо «ничего не происходит» минуты.
        private void SendUpdateProgress(int percent, long received, long total) {
            try {
                // Поток копирования — фоновый: PostWebMessage на UI-поток,
                // как и в OnNativeError.
                if (InvokeRequired) { BeginInvoke(() => SendUpdateProgress(percent, received, total)); return; }
                if (!IsHandleCreated || webView?.CoreWebView2 is null) return;
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonConvert.SerializeObject(new { @event = "updateProgress", percent, received, total }));
            } catch (Exception ex) {
                AppLog.Error("updateProgress: не удалось отправить событие", ex);
            }
        }
        private async Task CheckStream(string url, string callbackId) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) {
                SendCallback(callbackId, new ApiResponse { Success = false, Error = "Некорректный URL" });
                return;
            }
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                SendCallback(callbackId, new ApiResponse { Success = response.IsSuccessStatusCode, Status = (int)response.StatusCode });
            } catch (Exception ex) { SendCallback(callbackId, new ApiResponse { Success = false, Error = ex.Message }); }
        }
        private async Task PlayNative(string url, double volume, string callbackId) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) {
                SendCallback(callbackId, new ApiResponse { Success = false, Error = "Некорректный URL" });
                return;
            }
            try {
                StopNative();
                nativePlayer.Volume = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);
                nativeMedia = new Media(nativeVlc, uri);
                nativeStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!nativePlayer.Play(nativeMedia)) throw new InvalidOperationException("LibVLC не смог открыть поток.");
                var completed = await Task.WhenAny(nativeStart.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                if (completed == nativeStart.Task && await nativeStart.Task) {
                    SendCallback(callbackId, new { success = true });
                    return;
                }
                StopNative();
                SendCallback(callbackId, new { success = false, error = "LibVLC не начал воспроизведение." });
            } catch (Exception ex) { StopNative(); SendCallback(callbackId, new { success = false, error = ex.Message }); }
        }
        private void StopNative() {
            nativeStart?.TrySetResult(false);
            nativeStart = null;
            nativePlayer.Stop();
            nativeMedia?.Dispose();
            nativeMedia = null;
        }

        // Поток умер посреди игры (обрыв сети): страница должна узнать об этом
        // событием — иначе кнопка Play зовёт resumeNative на мёртвом медиа и
        // ничего не делает. Обработчик вызывается из потока libvlc: только
        // BeginInvoke, без вызовов самого плеера из этого потока.
        private void OnNativeError() {
            // EncounteredError тоже приходит с потока libvlc: вне UI-потока
            // чтение CoreWebView2 бросает InvalidOperationException — поэтому
            // сначала BeginInvoke, и только на UI-потоке — отправка.
            AppLog.Warn("LibVLC: ошибка потока воспроизведения");
            try {
                if (InvokeRequired) { BeginInvoke(OnNativeError); return; }
                if (!IsHandleCreated || webView?.CoreWebView2 is null) return;
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonConvert.SerializeObject(new { @event = "nativeError" }));
            } catch (Exception ex) {
                AppLog.Error("nativeError: не удалось отправить событие", ex);
            }
        }

        // Эквалайзер LibVLC: values = null снимает полосы (чистый проход —
        // выключенный чекбокс), иначе навешиваем полосы.
        //
        // Усиление (Preamp) живёт отдельно от полос: раньше +6 дБ вшивались
        // в каждый экземпляр и применялись к любому источнику, т.е. и к
        // «горячим» радиопотокам, где давали перегрузку — «звук как из
        // бочки», искажённая громкость и нелинейный ползунок (0…100% шёл
        // усилением ×2) — жалоба 2026-10-09. Теперь это ползунок 0…+12 дБ
        // в настройках (по умолчанию +6): громкость даёт только
        // nativePlayer.Volume, усиление — только осознанный выбор юзера.
        private void SetNativeEqualizer(JArray? values, string callbackId) {
            nativeEqValues = values;
            bool success = ApplyEqualizerFilter();
            SendCallback(callbackId, new { success });
        }

        // Ползунок усиления (дБ): применяется сразу к уже играющему потоку —
        // Preamp пересобирается тем же ApplyEqualizerFilter().
        private void SetNativeGain(double db, string callbackId) {
            nativeGainDb = Math.Clamp(db, 0, 12);
            bool success = ApplyEqualizerFilter();
            SendCallback(callbackId, new { success });
        }

        // Общая сборка цепочки вывода LibVLC: полосы (если включены) + Preamp.
        // Усиление действует и при выключенном эквалайзере — тогда полосы
        // плоские («пустой» Equalizer), а при нуле усиления и без полос фильтр
        // снимается совсем (чистый проход как в 3.5.6).
        private bool ApplyEqualizerFilter() {
            nativeEqualizer?.Dispose();
            nativeEqualizer = null;
            bool haveBands = nativeEqValues is not null && nativeEqValues.Count > 0;
            if (!haveBands && nativeGainDb <= 0) {
                // Снятие фильтра — отдельный метод LibVLCSharp (libvlc
                // unset_equalizer): цепочка вывода звука становится чистой,
                // никаких полос и без Preamp.
                nativePlayer.UnsetEqualizer();
                return true;
            }
            nativeEqualizer = new Equalizer();
            if (haveBands) {
                for (uint index = 0; index < nativeEqualizer.BandCount && index < nativeEqValues!.Count; index++) {
                    // Индексатор JArray принимает только int/string: uint давал
                    // ArgumentException ("Int32 array index expected") — страница
                    // висела до таймаута моста на каждый запуск станции.
                    nativeEqualizer.SetAmp(Math.Clamp(nativeEqValues[(int)index]?.Value<float>() ?? 0, -12, 12), index);
                }
            }
            // Preamp — дБ, LibVLC сам клампит в ±20; наш ползунок 0…+12.
            nativeEqualizer.SetPreamp((float)nativeGainDb);
            bool success = nativePlayer.SetEqualizer(nativeEqualizer);
            if (!success) { nativeEqualizer.Dispose(); nativeEqualizer = null; }
            return success;
        }

        private void ShowSaveDialog(string defaultPath, string callbackId) {
            using var dialog = new SaveFileDialog { FileName = Path.GetFileName(defaultPath), Filter = "JSON files|*.json|All files|*.*", Title = "Save file" };
            if (dialog.ShowDialog() == DialogResult.OK) {
                ApprovePath(dialog.FileName);
                SendCallback(callbackId, new ApiResponse { Success = true, FilePath = dialog.FileName });
            }
            else SendCallback(callbackId, new ApiResponse { Success = false });
        }
        private void ShowOpenDialog(string callbackId) {
            using var dialog = new OpenFileDialog { Filter = "JSON files|*.json|All files|*.*", Title = "Select file" };
            if (dialog.ShowDialog() == DialogResult.OK) {
                ApprovePath(dialog.FileName);
                SendCallback(callbackId, new ApiResponse { Success = true, Data = new[] { dialog.FileName } });
            }
            else SendCallback(callbackId, new ApiResponse { Success = false });
        }

        /// <summary>
        /// Разрешает доступ к файлу, который пользователь явно выбрал в системном
        /// диалоге (импорт/экспорт станций). Путь запоминается на время сессии.
        /// </summary>
        private void ApprovePath(string path) {
            try { approvedPaths.Add(Path.GetFullPath(path)); }
            catch (Exception ex) { AppLog.Warn("Не удалось запомнить путь из диалога: " + path, ex); }
        }

        /// <summary>
        /// Каталог данных приложения и пути, выбранные пользователем в диалоге.
        /// Всё остальное — отказ.
        /// </summary>
        private bool IsPathAllowed(string path) {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }

            if (approvedPaths.Contains(full)) return true;

            string root = Path.GetFullPath(dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        private async Task WriteFile(string path, string content, string callbackId) {
            if (!IsPathAllowed(path)) {
                AppLog.Warn("Отказ в записи файла из страницы: " + path);
                SendCallback(callbackId, new ApiResponse { Success = false, Error = "Файл за пределами каталога приложения: " + path });
                return;
            }
            try {
                await File.WriteAllTextAsync(path, content, Encoding.UTF8);
                SendCallback(callbackId, new ApiResponse { Success = true });
            } catch (Exception ex) { SendCallback(callbackId, new ApiResponse { Success = false, Error = ex.Message }); }
        }
        private async Task ReadFile(string path, string callbackId) {
            if (!IsPathAllowed(path)) {
                AppLog.Warn("Отказ в чтении файла из страницы: " + path);
                SendCallback(callbackId, new ApiResponse { Success = false, Error = "Файл за пределами каталога приложения: " + path });
                return;
            }
            try {
                string content = await File.ReadAllTextAsync(path, Encoding.UTF8);
                SendCallback(callbackId, new ApiResponse { Success = true, Content = content });
            } catch (Exception ex) { SendCallback(callbackId, new ApiResponse { Success = false, Error = ex.Message }); }
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            base.OnFormClosing(e);
            // Ждём незавершённых записей (коротко: приложение закрывается),
            // иначе финальная запись могла бы конфликтовать с пишущейся задачей.
            bool locked = storeLock.Wait(TimeSpan.FromSeconds(2));
            try {
                string storePath = Path.Combine(dataPath, StoreFileName);
                File.WriteAllText(storePath + ".tmp", storeData.ToString(), Encoding.UTF8);
                File.Move(storePath + ".tmp", storePath, overwrite: true);
                // Все сохранения, выданные до этого момента, устарели.
                Volatile.Write(ref storeSavedSeq, Volatile.Read(ref storeSaveSeq));
            }
            catch (Exception ex) { AppLog.Error("Не удалось сохранить хранилище при выходе", ex); }
            finally { if (locked) storeLock.Release(); }
            httpClient.Dispose();
            StopNative();
            nativeEqualizer?.Dispose();
            nativePlayer.Dispose();
            nativeVlc.Dispose();
            webView.Dispose();
        }
    }
}
