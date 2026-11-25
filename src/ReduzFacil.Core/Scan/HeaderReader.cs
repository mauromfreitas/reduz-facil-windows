using System.Buffers.Binary;
using System.Text;

namespace ReduzFacil.Core.Scan;

/// <summary>Lê só o cabeçalho dos arquivos (rápido, sem abrir motor) para estimar a economia.</summary>
public static class HeaderReader
{
    public sealed record ImageDims(int Width, int Height, bool HasAlpha);

    public static ImageDims? ImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            var head = new byte[64];
            var n = fs.Read(head, 0, head.Length);
            if (n < 26) return null;

            // PNG: IHDR logo no início; tipo de cor 4 ou 6 = com transparência
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
            {
                var w = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16));
                var h = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20));
                var colorType = head[25];
                return new ImageDims(w, h, colorType is 4 or 6);
            }
            // BMP
            if (head[0] == 'B' && head[1] == 'M')
                return new ImageDims(BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(22))), false);
            // WebP
            if (Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WEBP")
            {
                var chunk = Encoding.ASCII.GetString(head, 12, 4);
                if (chunk == "VP8X")
                {
                    var w = 1 + (head[24] | head[25] << 8 | head[26] << 16);
                    var h = 1 + (head[27] | head[28] << 8 | head[29] << 16);
                    return new ImageDims(w, h, (head[20] & 0x10) != 0);
                }
                if (chunk == "VP8 ")
                    return new ImageDims(BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28)) & 0x3FFF, false);
                if (chunk == "VP8L")
                {
                    var b = head.AsSpan(21);
                    var w = 1 + (((b[1] & 0x3F) << 8) | b[0]);
                    var h = 1 + (((b[3] & 0xF) << 10) | (b[2] << 2) | ((b[1] & 0xC0) >> 6));
                    return new ImageDims(w, h, true);
                }
                return null;
            }
            // JPEG: procura o marcador SOF
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                fs.Position = 2;
                var buf = new byte[9];
                for (var guard = 0; guard < 500; guard++)
                {
                    if (fs.Read(buf, 0, 4) < 4) return null;
                    if (buf[0] != 0xFF) return null;
                    var marker = buf[1];
                    var len = buf[2] << 8 | buf[3];
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        if (fs.Read(buf, 0, 5) < 5) return null;
                        return new ImageDims(buf[3] << 8 | buf[4], buf[1] << 8 | buf[2], false);
                    }
                    if (len < 2) return null;
                    fs.Position += len - 2;
                }
            }
        }
        catch
        {
            // sem estimativa
        }
        return null;
    }

    /// <summary>Duração de WAV e FLAC pelo cabeçalho (segundos).</summary>
    public static double? AudioDuration(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[64 * 1024];
            var n = fs.Read(head, 0, head.Length);
            var ascii = Encoding.ASCII;
            if (n >= 44 && ascii.GetString(head, 0, 4) == "RIFF" && ascii.GetString(head, 8, 4) == "WAVE")
            {
                var pos = 12;
                long byteRate = 0;
                while (pos + 8 <= n)
                {
                    var id = ascii.GetString(head, pos, 4);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pos + 4));
                    if (id == "fmt " && pos + 16 <= n) byteRate = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pos + 16));
                    if (id == "data" && byteRate > 0)
                    {
                        var dataSize = size == 0xFFFFFFFF || size == 0 ? fs.Length - pos - 8 : size;
                        return dataSize / (double)byteRate;
                    }
                    pos += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - pos - 8);
                }
            }
            if (n >= 42 && ascii.GetString(head, 0, 4) == "fLaC")
            {
                // STREAMINFO: 18 bytes depois do cabeçalho do bloco (4) e do "fLaC" (4)
                var si = head.AsSpan(8);
                var rate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
                long samples = ((long)(si[13] & 0x0F) << 32) | ((long)si[14] << 24) | ((long)si[15] << 16) | ((long)si[16] << 8) | si[17];
                if (rate > 0 && samples > 0) return samples / (double)rate;
            }
        }
        catch
        {
            // sem estimativa
        }
        return null;
    }

    /// <summary>Número aproximado de páginas de um PDF (conta "/Type /Page"; PDFs compactados podem esconder).</summary>
    public static int? PdfPages(string path, long maxBytes = 300L * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > maxBytes) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            const int overlap = 32;
            var buf = new byte[(1 << 20) + overlap];
            var kept = 0; // bytes do fim da volta anterior, repetidos no começo desta
            var count = 0;
            while (true)
            {
                var read = fs.Read(buf, kept, buf.Length - kept);
                var len = kept + read;
                var last = read == 0;
                // Só conta ocorrências que COMEÇAM antes da faixa repetida (ela será vista de novo na próxima volta).
                var limit = last ? len : Math.Max(0, len - overlap);
                count += CountPages(buf.AsSpan(0, len), limit);
                if (last) break;
                kept = Math.Min(overlap, len);
                Buffer.BlockCopy(buf, len - kept, buf, 0, kept);
            }
            return count > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    internal static int CountPages(ReadOnlySpan<byte> data, int startLimit)
    {
        var count = 0;
        var i = 0;
        while (i < startLimit)
        {
            var k = data[i..].IndexOf("/Type"u8);
            if (k < 0) break;
            i += k;
            if (i >= startLimit) break;
            var j = i + 5;
            while (j < data.Length && (data[j] == ' ' || data[j] == '\r' || data[j] == '\n')) j++;
            if (j + 5 <= data.Length && data[j..].StartsWith("/Page"u8) && (j + 5 == data.Length || data[j + 5] != 's'))
                count++;
            i += 5;
        }
        return count;
    }
}
