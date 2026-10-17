namespace ClassIsland.ExamRecorderPlugin;

public class ExamRecorderSettings
{
    /// <summary>写入 EA2 档案的默认提醒时间（分钟）。</summary>
    public int DefaultAlertTime { get; set; } = 15;

    /// <summary>
    /// 自动发现 EA 端端口：读取 ExamAware「自动放映」插件上报的发现文件，
    /// 避免 EA 重启后端口变化导致推送失败。默认开启。
    /// </summary>
    public bool AutoDiscoverPort { get; set; } = true;

    public string HttpApiHost { get; set; } = "127.0.0.1";

    public int HttpApiPort { get; set; } = 31235;

    /// <summary>进入放映（考试开始）时隐藏 ClassIsland 主界面。</summary>
    public bool HideMainWindowOnExamStart { get; set; }

    /// <summary>退出放映（考试结束）后恢复显示 ClassIsland 主界面。</summary>
    public bool ShowMainWindowOnExamEnd { get; set; }

    /// <summary>
    /// 连堂合并：同一天里同一考试科目的相邻两节课（连堂）合并为一场考试，
    /// 时间跨度覆盖两节课与中间课间。默认开启。
    /// </summary>
    public bool MergeConsecutivePeriods { get; set; } = true;

    /// <summary>
    /// 兼容旧版命名：额外把科目名以 <c>_考试</c> 结尾的科目视为考试科目。
    /// 默认关闭；识别考试科目请使用「科目 → 更多选项 → 考试科目」开关。
    /// </summary>
    public bool AlsoMatchLegacySuffix { get; set; }
}
