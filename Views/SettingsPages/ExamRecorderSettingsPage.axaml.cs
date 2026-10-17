using System;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Timer = System.Timers.Timer;

namespace ClassIsland.ExamRecorderPlugin.Views.SettingsPages;

[SettingsPageInfo("classisland.exam-recorder.settings", "ExamAware2联系人")]
public partial class ExamRecorderSettingsPage : SettingsPageBase
{
    private Timer? _t;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public ExamRecorderSettingsPage()
    {
        InitializeComponent();
        Load();
        RefreshSubjects();
        RefreshRuntimeStatus();
        _ = Chk();
    }

    private void Load()
    {
        var s = ExamRecorderService.Load();
        AlertBox.Value = s.DefaultAlertTime;
        HostBox.Text = s.HttpApiHost;
        PortBox.Value = s.HttpApiPort;
        LegacySwitch.IsChecked = s.AlsoMatchLegacySuffix;
        MergeSwitch.IsChecked = s.MergeConsecutivePeriods;
        AutoDiscoverSwitch.IsChecked = s.AutoDiscoverPort;
        RefreshEndpoint();
        HideSwitch.IsChecked = s.HideMainWindowOnExamStart;
        ShowSwitch.IsChecked = s.ShowMainWindowOnExamEnd;
        CurCfg.Text = $"当前：提醒 {s.DefaultAlertTime} 分钟 · {(s.MergeConsecutivePeriods ? "合并连堂" : "不合并连堂")} · {s.HttpApiHost}:{s.HttpApiPort}";
    }

    /// <summary>刷新「当前推送地址」，让用户看清端口是自动发现的还是手填的。</summary>
    private void RefreshEndpoint()
    {
        try
        {
            var resolved = ExamRecorderService.ResolveSignalUrl();
            EndpointText.Text = $"{resolved.Url}\n（{resolved.Source}）";
        }
        catch { EndpointText.Text = "—"; }
    }

    /// <summary>刷新「已标记的考试科目」列表。</summary>
    private void RefreshSubjects()
    {
        var names = ExamRecorderService.GetMarkedSubjectNames();
        MarkedList.ItemsSource = names;
        MarkedEmpty.IsVisible = names.Count == 0;
    }

    private void RefreshRuntimeStatus()
    {
        RecordText.Text = ExamRecorderService.LastRecordSummary;
        PushText.Text = ExamRecorderService.LastPushSummary;
    }

    private void OnRefreshSubjects(object? _, RoutedEventArgs __)
    {
        RefreshSubjects();
        RefreshRuntimeStatus();
    }

    /// <summary>标记科目后立即重新生成档案并推送，无需等到下一次下课。</summary>
    private void OnRegenerate(object? _, RoutedEventArgs __)
    {
        ExamRecorderService.RequestRecord();
        RefreshSubjects();
        RefreshRuntimeStatus();
        Toast("已重新生成并推送", true);
    }

    private void OnCheck(object? _, RoutedEventArgs __) => _ = Chk();

    private void OnSave(object? _, RoutedEventArgs __)
    {
        try
        {
            var s = new ExamRecorderSettings
            {
                DefaultAlertTime = (int)(AlertBox.Value ?? 15),
                HttpApiHost = HostBox.Text ?? "127.0.0.1",
                HttpApiPort = (int)(PortBox.Value ?? 31235),
                HideMainWindowOnExamStart = HideSwitch.IsChecked ?? false,
                ShowMainWindowOnExamEnd = ShowSwitch.IsChecked ?? false,
                AlsoMatchLegacySuffix = LegacySwitch.IsChecked ?? false,
                MergeConsecutivePeriods = MergeSwitch.IsChecked ?? true,
                AutoDiscoverPort = AutoDiscoverSwitch.IsChecked ?? true
            };
            ExamRecorderService.Save(s);
            Load();
            RefreshSubjects();
            // 连堂开关会影响考试档案的生成结果，保存后立即重算并推送一次
            ExamRecorderService.RequestRecord();
            ExamRecorderService.RefreshUiSchedule();
            RefreshEndpoint();
            ExamRecorderService.Log("INFO", $"saved alert={s.DefaultAlertTime} host={s.HttpApiHost}:{s.HttpApiPort} legacy={s.AlsoMatchLegacySuffix} merge={s.MergeConsecutivePeriods} autodiscover={s.AutoDiscoverPort}");
            Toast("设置已保存", true);
        }
        catch (Exception ex)
        {
            ExamRecorderService.Log("ERROR", $"save err: {ex.Message}");
            Toast("保存失败: " + ex.Message, false);
        }
    }

    private async Task Chk()
    {
        try
        {
            var url = ExamRecorderService.SignalUrl();
            var r = await _http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
            var ok = (int)r.StatusCode == 400 || r.IsSuccessStatusCode;
            ExamRecorderService.Log("INFO", $"conn: {(ok ? "OK" : $"HTTP {(int)r.StatusCode}")} url={url}");
            SetConn(ok);
        }
        catch (Exception ex)
        {
            ExamRecorderService.Log("WARN", $"conn fail: {ex.Message}");
            SetConn(false);
        }
    }

    private void SetConn(bool ok)
    {
        SetClass(ConnDot, "conn-ok", ok);
        SetClass(ConnDot, "conn-bad", !ok);
        var label = ok ? "已连接" : "未连接";
        if (ConnText.Text != label)
        {
            ConnText.Text = label;
            ConnHint.Text = ok
                ? "ExamAware 自动放映插件服务已连接，考试档案生成后会自动推送。"
                : "无法连接到自动放映插件服务，请检查 EA 端是否已开启 HTTP API，以及地址与端口是否一致。";
        }
        RefreshRuntimeStatus();
    }

    private void Toast(string msg, bool ok)
    {
        _t?.Stop();
        _t?.Dispose();
        ToastText.Text = msg;
        SetClass(ToastBorder, "toast-ok", ok);
        SetClass(ToastBorder, "toast-bad", !ok);
        ToastBorder.Opacity = 1;
        _t = new Timer(3000) { AutoReset = false };
        _t.Elapsed += (_, __) => Dispatcher.UIThread.Post(() => ToastBorder.Opacity = 0);
        _t.Start();
    }

    /// <summary>切换样式类，避免重复添加。</summary>
    private static void SetClass(StyledElement target, string name, bool enabled)
    {
        if (enabled)
        {
            if (!target.Classes.Contains(name)) target.Classes.Add(name);
        }
        else
        {
            target.Classes.Remove(name);
        }
    }
}
