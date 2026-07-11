using System;

namespace Clipora.Services;

/// <summary>时间显示：近期相对（"x 分钟前"），久远绝对（"6月10日"）；以及按天分组键。</summary>
public static class TimeFormat
{
    /// <summary>条目时间显示（绝对）：今天→"今天 HH:mm"；昨天→"昨天 HH:mm"；更早→"MM-dd"/"yyyy-MM-dd"。</summary>
    public static string Display(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        DateTime today = DateTime.Now.Date;

        if (local.Date == today) return "今天 " + local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "昨天 " + local.ToString("HH:mm");
        if (local.Year == today.Year) return local.ToString("MM-dd");
        return local.ToString("yyyy-MM-dd");
    }

    public static string DayGroup(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        DateTime today = DateTime.Now.Date;

        if (local.Date == today) return "今天";
        if (local.Date == today.AddDays(-1)) return "昨天";
        if (local.Year == today.Year) return local.ToString("M月d日");
        return local.ToString("yyyy年M月d日");
    }

    /// <summary>8 小时段索引：0=凌晨(00–08)，1=日间(08–16)，2=晚间(16–24)。</summary>
    public static int SegmentIndex(DateTime utc)
    {
        int hour = utc.ToLocalTime().Hour;
        return hour < 8 ? 0 : hour < 16 ? 1 : 2;
    }

    /// <summary>段标签（绝对，不随日期变化）。</summary>
    public static string SegmentLabel(int index) => index switch
    {
        0 => "凌晨 00:00–08:00",
        1 => "日间 08:00–16:00",
        _ => "晚间 16:00–24:00",
    };

    /// <summary>
    /// 分组键（绝对、稳定）：本地日期 + 段索引，如 <c>2026-06-23#0</c>。
    /// 不随"今天/昨天"漂移，跨零点时次日同段与前一天同段键不同，故新内容不会并入前一天的段。
    /// </summary>
    public static string SegmentKey(DateTime utc) =>
        utc.ToLocalTime().Date.ToString("yyyy-MM-dd") + "#" + SegmentIndex(utc);

    /// <summary>分组标题（相对、随当前日期变化）：今天/昨天/具体日期 + 段标签，用于 header 显示。</summary>
    public static string SegmentTitle(DateTime utc) =>
        DayGroup(utc) + " " + SegmentLabel(SegmentIndex(utc));
}
