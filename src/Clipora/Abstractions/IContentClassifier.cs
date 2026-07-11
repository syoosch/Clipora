using System.Windows;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>读取当前剪贴板内容并分类生成一条 <see cref="ClipItem"/>；无可用内容返回 null。</summary>
public interface IContentClassifier
{
    ClipItem? Classify();

    /// <summary>分类外部拖入的数据；支持显式 FileDrop、位图、纯文本与富文本格式。</summary>
    ClipItem? Classify(IDataObject dataObject);
}
