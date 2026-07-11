namespace Clipora.Scrolling;

/// <summary>
/// 一次滑动的参数（时长 + 每格滚轮像素）。常量逐字沿用原 HistoryWindow code-behind。
/// </summary>
/// <param name="DurationMs">缓动总时长（毫秒）。</param>
/// <param name="PixelsPerNotch">每格滚轮（120 单位）对应的像素位移，仅 <c>GlideBy</c> 使用。</param>
public sealed record ScrollGlide(double DurationMs, double PixelsPerNotch)
{
    /// <summary>纵向滚轮缺省手感：165ms、每格 88px（原 <c>WheelScrollDurationMs</c> / <c>WheelScrollPixelsPerNotch</c>）。</summary>
    public static readonly ScrollGlide Wheel = new(165, 88);

    /// <summary>沿用其余参数，仅替换时长（"回到顶部"按计算时长滑动时使用）。</summary>
    public ScrollGlide WithDuration(double durationMs) => this with { DurationMs = durationMs };
}
