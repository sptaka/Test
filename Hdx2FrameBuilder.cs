using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hdx2;

/// <summary>Optional settings for <see cref="Hdx2FrameBuilder.CreateFrames"/>.</summary>
public sealed class Hdx2Options
{
    /// <summary>Session id written to every frame. null = random non-zero value.</summary>
    public uint? SessionId { get; init; }

    /// <summary>
    /// false (default): Gray8 bitmaps (1 byte/pixel).
    /// true: Bgr32 bitmaps with R=G=B (4x memory; use if the display path mishandles Gray8).
    /// </summary>
    public bool UseBgr32 { get; init; }
}

/// <summary>
/// Builds the HDX2 frames (docs/sender-spec.md, 16-level version) as WPF bitmaps.
///
///   y = 0..39   header (8x8 px binary cells, copy A at the left, copy B at the right)
///   y = 40..59  calibration strip (16 patches)
///   y = 60..    data rows: each byte = 6 px (high nibble 3 px, low nibble 3 px),
///               last 4 bytes of each row = CRC-32 over (session, frame, row, payload)
/// </summary>
public static class Hdx2FrameBuilder
{
    public const int DataTop = 60;
    public const int BytePixels = 6;
    const int CalibrationTop = 40;
    const int HeaderBytes = 56;
    const int HeaderBits = HeaderBytes * 8;
    const int HeaderCell = 8;
    const int HeaderCellsPerRow = 96;
    const int HeaderCopyWidth = HeaderCellsPerRow * HeaderCell;   // 768
    const int RowCrcPrefix = 10;
    const byte HeaderZero = 40;
    const byte HeaderOne = 200;

    /// <summary>Sent value for each 4-bit level: V(L) = 10 + (L*219 + 7) / 15.</summary>
    public static readonly byte[] Level =
        [10, 25, 39, 54, 68, 83, 98, 112, 127, 141, 156, 171, 185, 200, 214, 229];

    /// <summary>
    /// Splits <paramref name="data"/> into frames and renders each one.
    /// Show the returned bitmaps in order, in a loop, each for about 250 ms, at 1:1 scale.
    /// </summary>
    /// <param name="data">Bytes to send.</param>
    /// <param name="width">Output width in pixels (at least 1536; 1920 = 320 bytes per row).</param>
    /// <param name="height">Output height in pixels (rows below y=60 carry data).</param>
    /// <param name="options">Optional session id and pixel format.</param>
    public static List<BitmapSource> CreateFrames(byte[] data, int width = 1920, int height = 1080, Hdx2Options? options = null)
    {
        options ??= new Hdx2Options();
        if (width < 2 * HeaderCopyWidth)
            throw new ArgumentOutOfRangeException(nameof(width), $"width must be at least {2 * HeaderCopyWidth}.");
        if (height <= DataTop)
            throw new ArgumentOutOfRangeException(nameof(height), $"height must be greater than {DataTop}.");

        int rowBytes = width / BytePixels;      // 320 at 1920
        int rowPayload = rowBytes - 4;          // 316
        int dataRows = height - DataTop;        // 1020
        long framePayload = (long)rowPayload * dataRows;
        int totalFrames = (int)Math.Max(1, (data.LongLength + framePayload - 1) / framePayload);
        uint sessionId = options.SessionId ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        uint fileCrc = Crc32(data);

        var layout = new Layout(width, height, rowBytes, rowPayload, dataRows, framePayload);
        var gray = new byte[width * height];
        var frames = new List<BitmapSource>(totalFrames);
        for (int f = 0; f < totalFrames; f++)
        {
            DrawFrame(gray, layout, data, sessionId, f, totalFrames, fileCrc);
            frames.Add(ToBitmap(gray, width, height, options.UseBgr32));
        }
        return frames;
    }

    readonly record struct Layout(int Width, int Height, int RowBytes, int RowPayload, int DataRows, long FramePayload);

    static void DrawFrame(byte[] gray, Layout l, byte[] data, uint sessionId, int frameIndex, int totalFrames, uint fileCrc)
    {
        Array.Clear(gray);

        // Header (56 bytes), drawn twice.
        var header = new byte[HeaderBytes];
        "HDX2"u8.CopyTo(header);
        header[4] = 2;  // version
        header[5] = 3;  // symbol width
        header[6] = 4;  // bits per symbol
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), sessionId);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), frameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), totalFrames);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), data.LongLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), fileCrc);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)l.RowPayload);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), (ushort)l.DataRows);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), Crc32(header.AsSpan(0, 52)));

        FillRect(gray, l.Width, 0, 0, l.Width, CalibrationTop, HeaderZero);
        foreach (int x0 in new[] { 0, l.Width - HeaderCopyWidth })
        {
            for (int n = 0; n < HeaderBits; n++)
            {
                bool one = ((header[n / 8] >> (7 - n % 8)) & 1) != 0;
                FillRect(gray, l.Width, x0 + n % HeaderCellsPerRow * HeaderCell, n / HeaderCellsPerRow * HeaderCell,
                    HeaderCell, HeaderCell, one ? HeaderOne : HeaderZero);
            }
        }

        // Calibration strip: 16 patches across the full width.
        for (int i = 0; i < 16; i++)
        {
            int x0 = i * l.Width / 16, x1 = (i + 1) * l.Width / 16;
            FillRect(gray, l.Width, x0, CalibrationTop, x1 - x0, DataTop - CalibrationTop, Level[i]);
        }

        // Data rows: payload + CRC-32(session | frame | row | payload).
        var row = new byte[l.RowBytes];
        var prefix = new byte[RowCrcPrefix];
        for (int r = 0; r < l.DataRows; r++)
        {
            Array.Clear(row);
            long offset = frameIndex * l.FramePayload + (long)r * l.RowPayload;
            if (offset < data.LongLength)
                data.AsSpan((int)offset, (int)Math.Min(l.RowPayload, data.LongLength - offset)).CopyTo(row);

            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(0), sessionId);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), frameIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(8), (ushort)r);
            uint crc = Crc32(row.AsSpan(0, l.RowPayload), Crc32(prefix));
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(l.RowPayload), crc);

            DrawDataRow(row, gray.AsSpan((DataTop + r) * l.Width, l.Width));
        }
    }

    /// <summary>Each byte becomes 6 px: Level[high nibble] x3, then Level[low nibble] x3.</summary>
    static void DrawDataRow(ReadOnlySpan<byte> rowBytes, Span<byte> pixelRow)
    {
        for (int i = 0; i < rowBytes.Length; i++)
        {
            byte b = rowBytes[i];
            var px = pixelRow.Slice(i * BytePixels, BytePixels);
            px[..3].Fill(Level[b >> 4]);
            px[3..].Fill(Level[b & 0x0F]);
        }
    }

    static void FillRect(byte[] gray, int stride, int x, int y, int w, int h, byte value)
    {
        for (int yy = y; yy < y + h; yy++)
            gray.AsSpan(yy * stride + x, w).Fill(value);
    }

    static BitmapSource ToBitmap(byte[] gray, int width, int height, bool bgr32)
    {
        BitmapSource bitmap;
        if (!bgr32)
        {
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
        }
        else
        {
            var bgra = new byte[gray.Length * 4];
            for (int i = 0; i < gray.Length; i++)
            {
                byte g = gray[i];
                bgra[i * 4] = g;
                bgra[i * 4 + 1] = g;
                bgra[i * 4 + 2] = g;
                bgra[i * 4 + 3] = 255;
            }
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        }
        bitmap.Freeze();   // BitmapSource.Create copies the buffer, so gray can be reused
        return bitmap;
    }

    static readonly uint[] CrcTable = CreateCrcTable();

    static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (same as zlib crc32). Pass a previous result as <paramref name="crc"/> to continue it.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes, uint crc = 0)
    {
        crc = ~crc;
        foreach (byte b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
