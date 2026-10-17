using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.ExamRecorderPlugin;

/// <summary>一节课的考试候选窗口。<paramref name="Seq"/> 是它在当天启用课次里的序号，用于判断是否连堂。</summary>
public readonly record struct ExamWindow(
    string Name,
    Guid SubjectId,
    DateTime Start,
    DateTime End,
    int AlertTime,
    int Seq);

/// <summary>
/// 连堂合并：同一个考试科目、课表上紧挨着的两节课，合成一场（时间跨度覆盖两节与中间课间）。
/// 不依赖 ClassIsland 类型，方便单测。
/// </summary>
public static class ExamMerge
{
    /// <summary>
    /// <paramref name="raw"/> 需按课表顺序排列。<paramref name="enabled"/> 为 false 时原样返回。
    /// </summary>
    public static List<ExamWindow> Merge(IEnumerable<ExamWindow> raw, bool enabled, TimeSpan maxGap)
    {
        var items = raw?.ToList() ?? [];
        if (!enabled || items.Count < 2) return items;

        var result = new List<ExamWindow>(items.Count);
        foreach (var item in items)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                var gap = item.Start - last.End;
                var sameSubject = item.SubjectId == last.SubjectId;
                var adjacent = item.Seq == last.Seq + 1;
                if (sameSubject && adjacent && gap >= TimeSpan.Zero && gap <= maxGap)
                {
                    // 延长到后一节的结束时间，保留第一场的名字与提醒时间
                    result[^1] = last with { End = item.End, Seq = item.Seq };
                    continue;
                }
            }
            result.Add(item);
        }
        return result;
    }
}
