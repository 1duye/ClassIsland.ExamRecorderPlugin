using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums;
using ClassIsland.ExamRecorderPlugin.Models;

namespace ClassIsland.ExamRecorderPlugin.Views.AttachedSettings;

/// <summary>
/// 「科目」上的附加设置：把该科目标记为考试科目。
/// </summary>
/// <remarks>
/// 注册后该设置会出现在档案编辑器「科目 → 更多选项」中，每个科目（含之后新建的）各有一份，
/// 因此不再需要用「科目名以 _考试 结尾」来标记考试。
/// </remarks>
[AttachedSettingsUsage(AttachedSettingsTargets.Subject)]
[AttachedSettingsControlInfo(ExamSubjectAttachedSettings.SettingsGuidString, "考试科目", "\ue47a")]
public partial class ExamSubjectAttachedSettingsControl : AttachedSettingsControlBase<ExamSubjectAttachedSettings>
{
    public ExamSubjectAttachedSettingsControl()
    {
        InitializeComponent();
    }
}
