using System.Windows;

namespace Clipora.Services;

/// <summary>判定外部拖入的 <see cref="IDataObject"/> 是否含 Clipora 可接受的内容格式。
/// 由主面板拖入浮层与悬浮球拖入方块共用，保证两处接受口径一字不差地一致。</summary>
internal static class ExternalDropSupport
{
    public static bool HasAcceptableFormat(IDataObject dataObject)
    {
        try
        {
            return dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                || ExternalImageDataReader.HasSupportedFormat(dataObject)
                || dataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.Text, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.StringFormat, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.Rtf, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.Html, autoConvert: false);
        }
        catch
        {
            return false;
        }
    }
}
