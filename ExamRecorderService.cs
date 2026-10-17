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
    private static readonly object _lock = new();
    private static readonly object _uiLock = new();

    /// <summary>连堂合并允许的最大课间（超过则视为不同场次，例如跨午休）。</summary>
    private static readonly TimeSpan ConsecutiveGap = TimeSpan.FromMinutes(15);

    private static string CleanHost(string h) { if (h.StartsWith("http://")) return h[7..]; if (h.StartsWith("https://")) return h[8..]; return h; }
    private static string BaseUrl() { var s = Load(); return $"http://{CleanHost(s.HttpApiHost)}:{s.HttpApiPort}"; }
    private const string AutoPlayerApiPath = "/api/v1/examaware-auto-player";

    /// <summary>旧版用于标记考试科目的科目名后缀。</summary>
    public const string LegacyExamSuffix = "_考试";

    /// <summary>最近一次生成考试档案的结果摘要，供设置页展示。</summary>
    public static string LastRecordSummary { get; private set; } = "尚未生成考试档案";

    /// <summary>最近一次推送的结果摘要，供设置页展示。</summary>
    public static string LastPushSummary { get; private set; } = "尚未推送";

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
    { try { lock (_lock) { Directory.CreateDirectory(LogDir); File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{lvl}] {msg}{Environment.NewLine}", Encoding.UTF8); } } catch {} }

    public static ExamRecorderSettings Load() { try { if (File.Exists(CfgPath)) return JsonSerializer.Deserialize<ExamRecorderSettings>(File.ReadAllText(CfgPath, Encoding.UTF8)) ?? new(); } catch {} return new(); }
    public static void Save(ExamRecorderSettings s) { try { s.HttpApiHost = CleanHost(s.HttpApiHost); var d = Path.GetDirectoryName(CfgPath); if (d != null) Directory.CreateDirectory(d); File.WriteAllText(CfgPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8); } catch {} }

    public ExamRecorderService(ILessonsService lessons, IProfileService profile, ILogger<ExamRecorderService> logger)
    { _lessons = lessons; _profile = profile; _logger = logger; }

    private ClassPlan? _plan;
    private DateTime _lastBreakPush = DateTime.MinValue;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        Log("INFO", $"started, profile={_profile.CurrentProfilePath}, log={LogPath}");
        _lessons.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ILessonsService.CurrentClassPlan)) Bind(_lessons.CurrentClassPlan); };
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

    private void Record(bool futureOnly = false)
    {
        try
        {
            var now = DateTime.Now; var today = now.Date; var plan = _lessons.CurrentClassPlan; if (plan == null) return;
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
            if (raw.Count == 0) return;

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
            File.WriteAllText(fp, JsonSerializer.Serialize(ea2, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8);
            Log("INFO", $"recorded {exams.Count} exams: {fp}" + (mergedPeriods > 0 ? $" (merged {mergedPeriods} consecutive period(s))" : ""));
            LastRecordSummary = $"{exams.Count} 场考试" + (mergedPeriods > 0 ? $"（合并 {mergedPeriods} 节课）" : "") + $" · {now:HH:mm:ss} 更新";
            Push(fp, futureOnly);
            ScheduleUiActions(exams);
        } catch (Exception ex) { Log("ERROR", $"record err: {ex.Message}"); }
    }

    private void Push(string fp, bool futureOnly)
    {
        var mode = futureOnly ? "future-only" : "active-or-future";
        try
        {
            var resolved = ResolveSignalUrl();
            var url = resolved.Url;
            Log("INFO", $"push -> {url} mode={mode} source={resolved.Source}");
            var r = _http.PostAsync(url, new StringContent(JsonSerializer.Serialize(new { ea2Path = fp, futureOnly }), Encoding.UTF8, "application/json")).Result;
            LastPushSummary = r.IsSuccessStatusCode
                ? $"推送成功 · {DateTime.Now:HH:mm:ss}（{mode}）"
                : $"推送失败 · HTTP {(int)r.StatusCode}";
            Log(r.IsSuccessStatusCode ? "INFO" : "WARN", r.IsSuccessStatusCode ? $"push OK: {fp}" : $"push HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex)
        {
            LastPushSummary = $"推送失败 · {ex.Message}";
            Log("WARN", $"push fail: {ex.Message}");
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
        lock (_uiLock) { _uiTimer?.Dispose(); _uiTimer = null; _uiActions = []; RestoreMainWindowIfNeeded(); }
        _lessons.OnBreakingTime -= OnBreakingTime;
        Bind(null);
        base.Dispose();
    }

    private record ExamRecord { [JsonPropertyName("examName")] public string ExamName { get; init; } = ""; [JsonPropertyName("message")] public string Message { get; init; } = ""; [JsonPropertyName("examInfos")] public List<ExamInfo> ExamInfos { get; init; } = []; }
    private record ExamInfo { [JsonPropertyName("name")] public string Name { get; init; } = ""; [JsonPropertyName("start")] public string Start { get; init; } = ""; [JsonPropertyName("end")] public string End { get; init; } = ""; [JsonPropertyName("alertTime")] public int AlertTime { get; init; } [JsonPropertyName("materials")] public List<string> Materials { get; init; } = []; }
}