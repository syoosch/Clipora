using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Clipora.Services;

/// <summary>从外部拖入 IDataObject 读取显式位图格式，不接触系统剪贴板。</summary>
internal static class ExternalImageDataReader
{
    private const string PngFormat = "PNG";

    public static bool HasSupportedFormat(IDataObject dataObject)
    {
        try
        {
            return dataObject.GetDataPresent(PngFormat, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.Bitmap, autoConvert: false)
                || dataObject.GetDataPresent(DataFormats.Dib, autoConvert: false);
        }
        catch
        {
            return false;
        }
    }

    public static BitmapSource? TryRead(IDataObject dataObject)
    {
        if (!HasSupportedFormat(dataObject))
            return null;

        BitmapSource? image = TryReadFormat(dataObject, PngFormat, isDib: false)
            ?? TryReadFormat(dataObject, DataFormats.Bitmap, isDib: false)
            ?? TryReadFormat(dataObject, DataFormats.Dib, isDib: true);
        if (image is null)
            return null;

        if (image.IsFrozen)
            return image;

        BitmapSource clone = image.Clone();
        clone.Freeze();
        return clone;
    }

    private static BitmapSource? TryReadFormat(IDataObject dataObject, string format, bool isDib)
    {
        try
        {
            if (!dataObject.GetDataPresent(format, autoConvert: false))
                return null;

            object? raw = dataObject.GetData(format, autoConvert: false);
            if (raw is BitmapSource bitmapSource)
                return bitmapSource;

            byte[]? bytes = raw switch
            {
                byte[] array => array,
                Stream stream => ReadAllBytes(stream),
                _ => null,
            };
            if (bytes is null || bytes.Length == 0)
                return null;

            return Decode(isDib ? WrapDibAsBmp(bytes) : bytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        long originalPosition = 0;
        bool restorePosition = stream.CanSeek;
        if (restorePosition)
        {
            originalPosition = stream.Position;
            stream.Position = 0;
        }

        try
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        finally
        {
            if (restorePosition)
                stream.Position = originalPosition;
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            return null;

        BitmapFrame frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 12)
            throw new InvalidDataException("DIB header is incomplete.");

        int pixelOffset = CalculatePixelOffset(dib);
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), checked((uint)bmp.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), checked((uint)pixelOffset));
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static int CalculatePixelOffset(byte[] dib)
    {
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(0, 4));
        if (headerSize < 12 || headerSize > dib.Length)
            throw new InvalidDataException("DIB header size is invalid.");

        int paletteEntries = 0;
        int paletteEntrySize = headerSize == 12 ? 3 : 4;
        int maskBytes = 0;

        if (headerSize == 12)
        {
            ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10, 2));
            if (bitCount <= 8)
                paletteEntries = 1 << bitCount;
        }
        else if (dib.Length >= 40)
        {
            ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));
            uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
            paletteEntries = colorsUsed > 0 ? checked((int)colorsUsed) : bitCount <= 8 ? 1 << bitCount : 0;
            if (headerSize == 40)
                maskBytes = compression == 3 ? 12 : compression == 6 ? 16 : 0;
        }

        return checked(14 + (int)headerSize + maskBytes + (paletteEntries * paletteEntrySize));
    }
}
