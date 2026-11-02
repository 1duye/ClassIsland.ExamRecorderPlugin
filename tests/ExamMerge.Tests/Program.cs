using ClassIsland.ExamRecorderPlugin;

namespace ExamMergeTests;

/// <summary>
/// ExamMerge 单测。不引 xUnit，纯控制台断言：离线也能跑，ci 里 dotnet run 即可。
/// 跑法：dotnet run --project tests/ExamMerge.Tests
/// </summary>
internal static class Program
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);
    private static readonly Guid Math = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Chinese = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static int _passed;
    private static int _failed;

    private static ExamWindow Win(string name, Guid subject, string start, string end, int seq, int alert = 10)
        => new(name, subject, DateTime.Parse(start), DateTime.Parse(end), alert, seq);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
            return;
        }
        _failed++;
        Console.Error.WriteLine($"✗ {name}{(string.IsNullOrEmpty(detail) ? "" : "：" + detail)}");
    }

    public static int Main()
    {
        var a = Win("数学", Math, "2026-11-02 08:00", "2026-11-02 09:00", seq: 1, alert: 10);
        var b = Win("数学", Math, "2026-11-02 09:10", "2026-11-02 10:10", seq: 2, alert: 99);

        // 1. 关闭合并：原样返回
        var off = ExamMerge.Merge([a, b], enabled: false, MaxGap);
        Check("关闭合并后保持两场", off.Count == 2);
        Check("关闭合并后不动结束时间", off[0].End == a.End);

        // 2. 空 / 单场
        Check("空列表返回空", ExamMerge.Merge([], true, MaxGap).Count == 0);
        Check("单场返回单场", ExamMerge.Merge([a], true, MaxGap).Count == 1);

        // 3. 相邻同科目、课间 10 分钟 → 合并
        var merged = ExamMerge.Merge([a, b], enabled: true, MaxGap);
        Check("相邻同科目合并成一场", merged.Count == 1);
        Check("合并后结束时间取后一节", merged.Count == 1 && merged[0].End == b.End);
        Check("合并后保留第一节的名字", merged.Count == 1 && merged[0].Name == a.Name);
        Check("合并后保留第一节的提醒时间", merged.Count == 1 && merged[0].AlertTime == a.AlertTime);
        Check("合并后 Seq 推进到后一节", merged.Count == 1 && merged[0].Seq == b.Seq);

        // 4. 三节连堂 → 一场
        var c = Win("数学", Math, "2026-11-02 10:20", "2026-11-02 11:20", seq: 3);
        var chain = ExamMerge.Merge([a, b, c], true, MaxGap);
        Check("三节连堂合并成一场", chain.Count == 1 && chain[0].End == c.End);

        // 5. 课间超过上限（20 分钟）→ 不合并
        var far = Win("数学", Math, "2026-11-02 09:20", "2026-11-02 10:20", seq: 2);
        Check("课间 20 分钟不合并", ExamMerge.Merge([a, far], true, MaxGap).Count == 2);

        // 6. 课间正好等于上限（15 分钟）→ 合并
        var edge = Win("数学", Math, "2026-11-02 09:15", "2026-11-02 10:15", seq: 2);
        Check("课间正好 15 分钟仍合并", ExamMerge.Merge([a, edge], true, MaxGap).Count == 1);

        // 7. 同课间但不同科目 → 不合并
        var chinese = Win("语文", Chinese, "2026-11-02 09:10", "2026-11-02 10:10", seq: 2);
        Check("不同科目不合并", ExamMerge.Merge([a, chinese], true, MaxGap).Count == 2);

        // 8. Seq 不相邻（中间夹了别的课）→ 不合并
        var gapSeq = Win("数学", Math, "2026-11-02 09:10", "2026-11-02 10:10", seq: 3);
        Check("Seq 不相邻不合并", ExamMerge.Merge([a, gapSeq], true, MaxGap).Count == 2);

        // 9. 时间重叠（负课间）→ 不合并
        var overlap = Win("数学", Math, "2026-11-02 08:30", "2026-11-02 09:30", seq: 2);
        Check("时间重叠不合并", ExamMerge.Merge([a, overlap], true, MaxGap).Count == 2);

        // 10. 普通课夹在中间不参与合并，顺序与数量不变
        var phys = Win("物理", Chinese, "2026-11-02 11:30", "2026-11-02 12:10", seq: 4);
        var mixed = ExamMerge.Merge([a, b, chinese, phys], true, MaxGap);
        Check("混合课表只合并该合并的", mixed.Count == 3);

        Console.WriteLine($"{_passed} 通过，{_failed} 失败");
        return _failed == 0 ? 0 : 1;
    }
}
