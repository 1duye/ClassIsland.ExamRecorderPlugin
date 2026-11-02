// SPDX-License-Identifier: MIT
// Copyright (c) 2026 α-氯氰菊酯 <https://github.com/1duye>
// Watermark: 1duye/ClassIsland.ExamRecorderPlugin#WM-2026​​‌‌​​​‌​‌‌​​‌​​​‌‌‌​‌​‌​‌‌‌‌​​‌​‌‌​​‌​‌​‌‌‌‌‌​​​‌​​​​‌‌​‌‌​‌‌​​​‌‌​​​​‌​‌‌‌​​‌‌​‌‌‌​​‌‌​‌​​‌​​‌​‌‌‌​​‌‌​‌‌​‌‌​​​‌‌​​​​‌​‌‌​‌‌‌​​‌‌​​‌​​​​‌​‌‌‌​​‌​​​‌​‌​‌‌‌‌​​​​‌‌​​​​‌​‌‌​‌‌​‌​‌​‌​​‌​​‌‌​​‌​‌​‌‌​​​‌‌​‌‌​‌‌‌‌​‌‌‌​​‌​​‌‌​​‌​​​‌‌​​‌​‌​‌‌‌​​‌​​‌​‌​​​​​‌‌​‌‌​​​‌‌‌​‌​‌​‌‌​​‌‌‌​‌‌​‌​​‌​‌‌​‌‌‌​​‌‌‌‌‌​​​​‌‌​​‌​​​‌‌​​​​​​‌‌​​‌​​​‌‌​‌‌​

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.ExamRecorderPlugin.Models;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ExamRecorderPlugin;

public class ExamRecorderService : BackgroundService
{
    private readonly ILessonsService _lessons;
    private readonly IProfileService _profile;
    private readonly ILogger<ExamRecorderService> _logger;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static string CfgPath => Path.Combine(CommonDirectories.AppRootFolderPath, "Config", "ExamRecorderPlugin.json");
    private static string LogDir => Path.Combine(Path.GetDirectoryName(CommonDirectories.AppRootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? ".", "data", "logs", "exam2");
    private static string LogPath => Path.Combine(LogDir, "ExamRecorderPlugin.log");
    /// <summary>单个日志文件的大小上限，超过后轮转成 .1.log。</summary>
    private const long LogMaxBytes = 1024 * 1024;
    private static readonly object _lock = new();
    private static readonly object _uiLock = new();

    /// <summary>连堂合并允许的最大课间（超过则视为不同场次，例如跨午休）。</summary>
    private static readonly TimeSpan ConsecutiveGap = TimeSpan.FromMinutes(15);

    private static string CleanHost(string h) { if (h.StartsWith("http://")) return h[7..]; if (h.StartsWith("https://")) return h[8..]; return h; }
    private static string BaseUrl() { var s = Load(); return $"http://{CleanHost(s.HttpApiHost)}:{s.HttpApiPort}"; }
    private const string AutoPlayerApiPath = "/api/v1/examaware-auto-player";

    /// <summary>旧版用于标记考试科目的科目名后缀。</summary>
    public const string LegacyExamSuffix = "_考试";

    /// <summary>代码水印：同时写入启动日志与程序集元数据，用于溯源。</summary>
    public const string Watermark = "1duye/ClassIsland.ExamRecorderPlugin#WM-2026";

    /// <summary>最近一次生成考试档案的结果摘要，供设置页展示。</summary>
    public static string LastRecordSummary { get; private set; } = "尚未生成考试档案";

    /// <summary>最近一次推送的结果摘要，供设置页展示。</summary>
    public static string LastPushSummary { get; private set; } = "尚未推送";

    /// <summary>最近一次生成是否真的产出了档案。设置页据此如实反馈，避免「没生成却说已推送」。</summary>
    public static bool LastRecordProduced { get; private set; }

    private static Action? _recordRequested;

    /// <summary>
    /// 请求立即重新生成考试档案并推送。供设置页在标记考试科目后手动触发，
    /// 免去等到下一次课表变更或下课。
    /// </summary>
    public static void RequestRecord()
    {
        try { _recordRequested?.Invoke(); }
        catch (Exception ex) { Log("WARN", $"manual record err: {ex.Message}"); }
    }

    /// <summary>ExamAware「自动放映」插件上报端口的文件（由 EA 端插件每 5 秒刷新）。</summary>
    private static string EndpointFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "@dsz-examaware", "desktop", "auto-player-endpoint.json");

    /// <summary>读取 EA 上报的端口；文件缺失、字段非法或解析失败时返回 null。</summary>
    public static (int Port, string BaseUrl, DateTime UpdatedAt)? DiscoverEndpoint()
    {
        try
        {
            if (!File.Exists(EndpointFilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(EndpointFilePath, Encoding.UTF8));
            var root = doc.RootElement;
            if (!root.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port)) return null;
            if (port is < 1 or > 65535) return null;
            var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" : "";
            var updatedAt = DateTime.MinValue;
            if (root.TryGetProperty("updatedAt", out var u) && u.GetString() is { } us &&
                DateTime.TryParse(us, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                updatedAt = dt;
            }
            return (port, baseUrl, updatedAt);
        }
        catch { return null; }
    }

    /// <summary>
    /// 实际使用的推送地址：开启「自动发现端口」且发现文件新鲜（10 分钟内）时用发现到的端口，
    /// 否则回退到设置里的固定端口。
    /// </summary>
    public static (string Url, string Source) ResolveSignalUrl()
    {
        var cfg = Load();
        if (cfg.AutoDiscoverPort)
        {
            var found = DiscoverEndpoint();
            if (found is { } ep && (DateTime.UtcNow - ep.UpdatedAt).TotalMinutes <= 10)
            {
                var host = string.IsNullOrWhiteSpace(cfg.HttpApiHost) ? "127.0.0.1" : CleanHost(cfg.HttpApiHost);
                return ($"http://{host}:{ep.Port}{AutoPlayerApiPath}/signal", $"自动发现 · 端口 {ep.Port}");
            }
        }
        return ($"{BaseUrl()}{AutoPlayerApiPath}/signal", $"设置端口 {cfg.HttpApiPort}");
    }

    public static string SignalUrl() => ResolveSignalUrl().Url;

    /// <summary>
    /// 读取某个科目上的「考试科目」附加设置。仅当该科目被标记为考试科目时返回非 null。
    /// </summary>
    public static ExamSubjectAttachedSettings? GetExamSubjectSettings(Subject? subject)
    {
        if (subject == null) return null;
        try
        {
            return IAttachedSettingsHostService.GetAttachedSettingsByPriority<ExamSubjectAttachedSettings>(
                ExamSubjectAttachedSettings.SettingsGuid, subject: subject);
        }
        catch (Exception ex) { Log("WARN", $"read subject settings err: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 枚举当前档案中已标记为考试科目的科目名，供设置页展示。
    /// </summary>
    public static List<string> GetMarkedSubjectNames()
    {
        try
        {
            var subjects = IAppHost.TryGetService<IProfileService>()?.Profile.Subjects;
            if (subjects == null) return [];
            return subjects.Values
                .Where(s => s != null && GetExamSubjectSettings(s) != null)
                .Select(s => s!.Name)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();
        }
        catch { return []; }
    }
    
    public static void Log(string lvl, string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                RotateLogIfNeeded();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{lvl}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>
    /// 当前日志超过 1 MiB 时改名成 .1.log（覆盖旧的），只保留两份。
    /// 调用方已经持有 _lock，这里不能再加锁，否则会死锁。
    /// </summary>
    private static void RotateLogIfNeeded()
    {
        try
        {
            var current = new FileInfo(LogPath);
            if (!current.Exists || current.Length < LogMaxBytes) return;
            var backup = Path.Combine(LogDir, "ExamRecorderPlugin.1.log");
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(LogPath, backup);
        }
        catch
        {
            // 轮转失败（文件被占用之类）不能反过来影响写日志本身，下次再试
        }
    }

    /// <summary>
    /// 先写临时文件再原子替换。EA 端可能正好在这时读档案，
    /// 直接覆盖写会让它读到半截 JSON，然后判「档案坏了」。
    /// </summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static ExamRecorderSettings Load() { try { if (File.Exists(CfgPath)) return JsonSerializer.Deserialize<ExamRecorderSettings>(File.ReadAllText(CfgPath, Encoding.UTF8)) ?? new(); } catch {} return new(); }
    public static void Save(ExamRecorderSettings s) { try { s.HttpApiHost = CleanHost(s.HttpApiHost); var d = Path.GetDirectoryName(CfgPath); if (d != null) Directory.CreateDirectory(d); WriteAllTextAtomic(CfgPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); } catch {} }

    public ExamRecorderService(ILessonsService lessons, IProfileService profile, ILogger<ExamRecorderService> logger)
    { _lessons = lessons; _profile = profile; _logger = logger; }

    private ClassPlan? _plan;
    private DateTime _lastBreakPush = DateTime.MinValue;
    private PropertyChangedEventHandler? _lessonsPropertyChanged;

    // 推送状态：Push() 不阻塞调用方，推送途中到达的请求只合并保留最后一次。
    private static readonly object _pushLock = new();
    private static bool _pushInFlight;
    private static string? _pushActiveFile;
    private static string? _pushPendingFile;
    private static bool _pushPendingFutureOnly;
    private static Task? _pushTask;
    /// <summary>单次推送的最大重试次数（网络异常 / 5xx 才重试，4xx 直接放弃）。</summary>
    private const int PushMaxRetries = 2;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        Log("INFO", $"started, profile={_profile.CurrentProfilePath}, log={LogPath}");
        Log("INFO", $"WM {Watermark}");
        // 保存成字段才能反订阅，避免匿名 lambda 把服务一直挂在事件上。
        _lessonsPropertyChanged = (_, e) => { if (e.PropertyName == nameof(ILessonsService.CurrentClassPlan)) Bind(_lessons.CurrentClassPlan); };
        _lessons.PropertyChanged += _lessonsPropertyChanged;
        _lessons.OnBreakingTime += OnBreakingTime;
        Bind(_lessons.CurrentClassPlan);
        _recordRequested = () => Record();
        Clean(); Record();
        return Task.CompletedTask;
    }

    private void Bind(ClassPlan? p) { if (_plan != null) _plan.ClassesChanged -= OnChange; _plan = p; if (_plan != null) _plan.ClassesChanged += OnChange; }
    private void OnChange(object? _, EventArgs __) => Record();

    private void OnBreakingTime(object? _, EventArgs __)
    {
        // Debounce repeated break events within a short interval.
        var now = DateTime.Now;
        if ((now - _lastBreakPush).TotalSeconds < 10) return;
        _lastBreakPush = now;
        Log("INFO", "breaking time reached, pushing exam record again");
        Record(futureOnly: true);
    }

    private void Clean()
    {
        try
        {
            var dir = Ea2Dir(); if (!Directory.Exists(dir)) return;
            var today = DateTime.Now.ToString("yyyyMMdd"); var n = 0;
            foreach (var f in Directory.GetFiles(dir, "考试记录_*.ea2")) { var name = Path.GetFileNameWithoutExtension(f).Replace("考试记录_", ""); if (name != today) { File.Delete(f); n++; } }
            if (n > 0) Log("INFO", $"cleaned {n} expired");
        } catch (Exception ex) { Log("WARN", $"clean err: {ex.Message}"); }
    }

    private static readonly object _recordLock = new();
    private static bool _recordRunning;
    private static bool _recordPending;
    private static bool _recordPendingFutureOnly;

    /// <summary>
    /// 生成并推送考试档案。课表变更（UI 线程）、设置页保存、下课事件都可能同时打进来，
    /// 这里串行化：正在跑时只记下「还要再跑一次」，避免两轮交错写同一份档案。
    /// </summary>
    private void Record(bool futureOnly = false)
    {
        lock (_recordLock)
        {
            if (_recordRunning)
            {
                _recordPending = true;
                _recordPendingFutureOnly = futureOnly;
                return;
            }
            _recordRunning = true;
        }
        try { RecordCore(futureOnly); }
        finally
        {
            bool again;
            bool againFutureOnly;
            lock (_recordLock)
            {
                _recordRunning = false;
                again = _recordPending;
                againFutureOnly = _recordPendingFutureOnly;
                _recordPending = false;
            }
            if (again)
            {
                // 本轮生成期间课表又变了：补跑一轮，别让最新状态丢在路上
                _ = Task.Run(() => Record(againFutureOnly));
            }
        }
    }

    private void RecordCore(bool futureOnly)
    {
        try
        {
            LastRecordProduced = false;
            var now = DateTime.Now; var today = now.Date; var plan = _lessons.CurrentClassPlan;
            if (plan == null) { LastRecordSummary = "尚未排课（无课表）"; return; }
            var subs = _profile.Profile.Subjects; var cfg = Load();
            // ① 按课表顺序收集「考试科目」的课次，并记下它在启用课次里的序号（判断连堂用）
            var raw = new List<ExamWindow>();
            var seq = 0;
            foreach (var c in plan.Classes)
            {
                if (!c.IsEnabled) continue;
                seq++;
                if (!subs.TryGetValue(c.SubjectId, out var sub)) continue;
                // 识别方式：科目上的「考试科目」附加设置；可选兼容旧的 _考试 后缀。
                var subjectSettings = GetExamSubjectSettings(sub);
                var legacyMatch = cfg.AlsoMatchLegacySuffix && sub.Name.EndsWith(LegacyExamSuffix, StringComparison.Ordinal);
                if (subjectSettings == null && !legacyMatch) continue;
                var t = c.CurrentTimeLayoutItem;
                var examName = sub.Name.EndsWith(LegacyExamSuffix, StringComparison.Ordinal)
                    ? sub.Name[..^LegacyExamSuffix.Length]
                    : sub.Name;
                var alertTime = subjectSettings is { UseAlertTimeOverride: true }
                    ? subjectSettings.AlertTimeOverride
                    : cfg.DefaultAlertTime;
                var start = today.Add(t.StartTime);
                var end = today.Add(t.EndTime);
                if (end <= start) continue;
                raw.Add(new ExamWindow(examName, c.SubjectId, start, end, alertTime, seq));
            }
            if (raw.Count == 0) { LastRecordSummary = "今天没有考试课次"; return; }

            // ② 连堂合并：同一科目 + 课表上相邻两节 + 课间不超过 ConsecutiveGap → 合成一场
            var merged = ExamMerge.Merge(raw, cfg.MergeConsecutivePeriods, ConsecutiveGap);
            var mergedPeriods = raw.Count - merged.Count;
            var exams = merged
                .Select(m => new ExamInfo
                {
                    Name = m.Name,
                    Start = m.Start.ToString("yyyy-MM-dd HH:mm:ss"),
                    End = m.End.ToString("yyyy-MM-dd HH:mm:ss"),
                    AlertTime = m.AlertTime,
                    Materials = []
                })
                .ToList();
            if (exams.Count == 0) return;
            var ea2 = new ExamRecord { ExamName = $"{today:yyyy-MM-dd}考试", Message = $"共{exams.Count}项", ExamInfos = exams };
            var dir = Ea2Dir(); Directory.CreateDirectory(dir); var fp = Path.Combine(dir, $"考试记录_{today:yyyyMMdd}.ea2");
            WriteAllTextAtomic(fp, JsonSerializer.Serialize(ea2, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            LastRecordProduced = true;
            Log("INFO", $"recorded {exams.Count} exams: {fp}" + (mergedPeriods > 0 ? $" (merged {mergedPeriods} consecutive period(s))" : ""));
            LastRecordSummary = $"{exams.Count} 场考试" + (mergedPeriods > 0 ? $"（合并 {mergedPeriods} 节课）" : "") + $" · {now:HH:mm:ss} 更新";
            Push(fp, futureOnly);
            ScheduleUiActions(exams);
        } catch (Exception ex) { Log("ERROR", $"record err: {ex.Message}"); }
    }

    /// <summary>
    /// 推送考试档案。不阻塞调用方：Record() 可能由 UI 线程（课表变更、设置页保存）同步触发，
    /// 之前用 .Result 同步等 HTTP，EA 没开时会把界面卡住数秒。
    /// </summary>
    private static void Push(string fp, bool futureOnly)
    {
        lock (_pushLock)
        {
            if (_pushInFlight)
            {
                // 同一份档案正在推就直接合并掉；推送途中课表又变了，只记住最新的一份。
                if (fp == _pushActiveFile || fp == _pushPendingFile) return;
                _pushPendingFile = fp;
                _pushPendingFutureOnly = futureOnly;
                return;
            }
            _pushInFlight = true;
            _pushActiveFile = fp;
        }
        var task = Task.Run(() => PushLoopAsync(fp, futureOnly));
        lock (_pushLock) { _pushTask = task; }
    }

    /// <summary>消费合并后的推送请求。异常在这里兜底，不会逃逸成未观察异常。</summary>
    private static async Task PushLoopAsync(string fp, bool futureOnly)
    {
        try
        {
            while (true)
            {
                await PushOnceAsync(fp, futureOnly).ConfigureAwait(false);
                lock (_pushLock)
                {
                    if (_pushPendingFile is null)
                    {
                        _pushInFlight = false;
                        _pushActiveFile = null;
                        _pushTask = null;
                        return;
                    }
                    fp = _pushPendingFile;
                    futureOnly = _pushPendingFutureOnly;
                    _pushActiveFile = fp;
                    _pushPendingFile = null;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_pushLock) { _pushInFlight = false; _pushActiveFile = null; _pushPendingFile = null; _pushTask = null; }
            try { LastPushSummary = $"推送失败 · {ex.Message}"; Log("WARN", $"push fail: {ex.Message}"); } catch { }
        }
    }

    private static async Task PushOnceAsync(string fp, bool futureOnly)
    {
        var mode = futureOnly ? "future-only" : "active-or-future";
        var resolved = ResolveSignalUrl();
        var url = resolved.Url;
        var payload = JsonSerializer.Serialize(new { ea2Path = fp, futureOnly });
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Log("INFO", $"push -> {url} mode={mode} source={resolved.Source} attempt={attempt + 1}");
                using var body = new StringContent(payload, Encoding.UTF8, "application/json");
                using var r = await _http.PostAsync(url, body).ConfigureAwait(false);
                var ok = r.IsSuccessStatusCode;
                LastPushSummary = ok
                    ? $"推送成功 · {DateTime.Now:HH:mm:ss}（{mode}）"
                    : $"推送失败 · HTTP {(int)r.StatusCode}";
                Log(ok ? "INFO" : "WARN", ok ? $"push OK: {fp}" : $"push HTTP {(int)r.StatusCode}");
                // 4xx 是地址/参数写错了，重试没意义；5xx 才值得再来一次
                if (ok || (int)r.StatusCode < 500 || attempt >= PushMaxRetries) return;
            }
            catch (Exception ex)
            {
                if (attempt >= PushMaxRetries)
                {
                    LastPushSummary = $"推送失败 · {ex.Message}";
                    Log("WARN", $"push fail after {attempt + 1} tries: {ex.Message}");
                    return;
                }
                Log("WARN", $"push attempt {attempt + 1} failed, retrying: {ex.Message}");
            }
            // 400ms → 1200ms 退避：EA 可能正在重启 HTTP 服务，几秒后就能连上
            await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(3, attempt))).ConfigureAwait(false);
        }
    }

    // ---- 主界面显隐联动 ----
    private sealed record UiAction(DateTime At, bool Hide);
    private static List<ExamInfo>? _lastExams;
    private static Timer? _uiTimer;
    private static List<UiAction> _uiActions = [];
    private static bool _uiHidden;

    /// <summary>设置保存后重新调度（开关变化立即生效，关闭开关时恢复已隐藏的主界面）。</summary>
    public static void RefreshUiSchedule()
    {
        if (_lastExams == null) return;
        ScheduleUiActions(_lastExams);
    }

    private static void ScheduleUiActions(List<ExamInfo> exams)
    {
        try
        {
            var cfg = Load();
            lock (_uiLock)
            {
                _lastExams = exams;
                _uiTimer?.Dispose();
                _uiTimer = null;
                _uiActions = [];
                if (!cfg.HideMainWindowOnExamStart && !cfg.ShowMainWindowOnExamEnd)
                {
                    RestoreMainWindowIfNeeded();
                    return;
                }
                var now = DateTime.Now;
                foreach (var e in exams)
                {
                    if (!DateTime.TryParseExact(e.Start, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
                    if (!DateTime.TryParseExact(e.End, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) continue;
                    if (cfg.HideMainWindowOnExamStart && start > now) _uiActions.Add(new UiAction(start, true));
                    if (cfg.ShowMainWindowOnExamEnd && end > now) _uiActions.Add(new UiAction(end, false));
                }
                ArmNextUiAction();
            }
        }
        catch (Exception ex) { Log("WARN", $"ui schedule err: {ex.Message}"); }
    }

    private static void ArmNextUiAction()
    {
        if (_uiActions.Count == 0) return;
        var next = _uiActions.Min(x => x.At);
        var item = _uiActions.First(x => x.At == next);
        var delay = (item.At - DateTime.Now).TotalMilliseconds;
        if (delay <= 0)
        {
            ApplyUiAction(item.Hide);
            _uiActions.Remove(item);
            ArmNextUiAction();
            return;
        }
        _uiTimer = new Timer(_ =>
        {
            lock (_uiLock)
            {
                var current = _uiActions.FirstOrDefault(x => x.At == item.At);
                if (current != null) { ApplyUiAction(current.Hide); _uiActions.Remove(current); }
                ArmNextUiAction();
            }
        }, null, (int)Math.Ceiling(delay), Timeout.Infinite);
    }

    private static void RestoreMainWindowIfNeeded()
    {
        if (!_uiHidden) return;
        _uiHidden = false;
        ApplyUiAction(false);
    }

    private static void ApplyUiAction(bool hide)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (main == null) { Log("WARN", hide ? "ui hide skipped: main window unavailable" : "ui show skipped: main window unavailable"); return; }
                if (hide) { main.Hide(); _uiHidden = true; }
                else { main.Show(); main.Activate(); _uiHidden = false; }
                Log("INFO", hide ? "main window hidden" : "main window shown");
            }
            catch (Exception ex) { Log("WARN", $"ui action err: {ex.Message}"); }
        });
    }

    private static string Ea2Dir() => Path.Combine(Path.GetDirectoryName(CommonDirectories.AppRootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? ".", "data", "EA2");
    public override void Dispose()
    {
        Log("INFO", "disposing");
        _recordRequested = null;
        // 退出时给在途推送最多 2 秒，别让最后一份档案的通知悄无声息地丢掉
        try
        {
            Task? inflight;
            lock (_pushLock) inflight = _pushTask;
            inflight?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        lock (_uiLock) { _uiTimer?.Dispose(); _uiTimer = null; _uiActions = []; RestoreMainWindowIfNeeded(); }
        _lessons.OnBreakingTime -= OnBreakingTime;
        if (_lessonsPropertyChanged != null) { _lessons.PropertyChanged -= _lessonsPropertyChanged; _lessonsPropertyChanged = null; }
        Bind(null);
        base.Dispose();
    }

    private record ExamRecord { [JsonPropertyName("examName")] public string ExamName { get; init; } = ""; [JsonPropertyName("message")] public string Message { get; init; } = ""; [JsonPropertyName("examInfos")] public List<ExamInfo> ExamInfos { get; init; } = []; }
    private record ExamInfo { [JsonPropertyName("name")] public string Name { get; init; } = ""; [JsonPropertyName("start")] public string Start { get; init; } = ""; [JsonPropertyName("end")] public string End { get; init; } = ""; [JsonPropertyName("alertTime")] public int AlertTime { get; init; } [JsonPropertyName("materials")] public List<string> Materials { get; init; } = []; }
}