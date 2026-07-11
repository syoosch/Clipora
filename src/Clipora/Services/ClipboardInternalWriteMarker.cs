using System.Windows;

namespace Clipora.Services;

/// <summary>
/// 标记 Clipora 主动写入的剪贴板数据。监听器据此跳过任意数量的更新消息，
/// 避免卡片重用、纯文本粘贴和顺序粘贴被当成新的外部复制。
/// </summary>
public static class ClipboardInternalWriteMarker
{
    public const string Format = "Clipora.InternalWrite.v1";

    public static void SetClipboard(DataObject dataObject)
    {
        dataObject.SetData(Format, true, autoConvert: false);
        Clipboard.SetDataObject(dataObject, copy: true);
    }

    public static bool IsPresent(IDataObject dataObject) =>
        dataObject.GetDataPresent(Format, autoConvert: false);

    public static bool IsPresentOnClipboard()
    {
        try { return Clipboard.ContainsData(Format); }
        catch { return false; }
    }
}
