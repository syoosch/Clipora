using System;

namespace Clipora.Common;

/// <summary>纯函数：字节数 → 人话带单位字符串，供超限提示与设置显示。</summary>
public static class ByteSizeFormatter
{
    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024L * 1024 * 1024;

    public static string Format(long bytes)
    {
        if (bytes < KB)
            return $"{bytes} B";

        if (bytes < MB)
            return $"{bytes / (double)KB:0.0} KB";

        if (bytes < GB)
            return $"{bytes / (double)MB:0.0} MB";

        return $"{bytes / (double)GB:0.0} GB";
    }
}
