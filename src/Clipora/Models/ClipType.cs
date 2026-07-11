namespace Clipora.Models;

/// <summary>剪贴板内容的类型分类（用于筛选与卡片渲染）。</summary>
public enum ClipType
{
    Text,
    RichText,
    Url,
    Code,
    Image,
    File,
    Color,
}
