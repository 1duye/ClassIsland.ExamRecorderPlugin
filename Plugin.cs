using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.ExamRecorderPlugin.Views.AttachedSettings;
using ClassIsland.ExamRecorderPlugin.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ExamRecorderPlugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSettingsPage<ExamRecorderSettingsPage>();
        // 在档案编辑器「科目 → 更多选项」中提供「考试科目」开关。
        services.AddAttachedSettingsControl<ExamSubjectAttachedSettingsControl>();
        services.AddHostedService<ExamRecorderService>();
    }
}
