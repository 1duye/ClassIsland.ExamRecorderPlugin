using ClassIsland.Shared.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.ExamRecorderPlugin.Models;

/// <summary>
/// 附加在「科目」上的考试科目标记。
/// </summary>
/// <remarks>
/// 该设置启用后，此科目排入的课程会被记录为考试科目。
/// 启用开关由档案编辑器「科目 → 更多选项 → 考试科目」提供，因此对之后新建的科目同样生效。
/// </remarks>
public class ExamSubjectAttachedSettings : ObservableRecipient, IAttachedSettings
{
    /// <summary>
    /// 此附加设置的 GUID 字符串。用于注册控件与读取设置，请勿修改。
    /// </summary>
    public const string SettingsGuidString = "794f6f94-be36-48b2-b6d2-dfee24efd7ea";

    /// <summary>
    /// 此附加设置的 GUID。
    /// </summary>
    public static readonly Guid SettingsGuid = new(SettingsGuidString);

    private bool _isAttachSettingsEnabled;
    private bool _useAlertTimeOverride;
    private int _alertTimeOverride = 15;

    /// <inheritdoc />
    /// <remarks>对应「更多选项 → 考试科目」处的开关：开启即把该科目视为考试科目。</remarks>
    public bool IsAttachSettingsEnabled
    {
        get => _isAttachSettingsEnabled;
        set => SetProperty(ref _isAttachSettingsEnabled, value);
    }

    /// <summary>
    /// 是否为该科目单独指定提醒时间。
    /// </summary>
    public bool UseAlertTimeOverride
    {
        get => _useAlertTimeOverride;
        set => SetProperty(ref _useAlertTimeOverride, value);
    }

    /// <summary>
    /// 该科目考试写入 EA2 档案时的提醒时间（分钟）。仅在 <see cref="UseAlertTimeOverride"/> 为 true 时生效。
    /// </summary>
    public int AlertTimeOverride
    {
        get => _alertTimeOverride;
        set => SetProperty(ref _alertTimeOverride, value);
    }
}
