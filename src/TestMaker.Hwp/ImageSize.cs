using System.Buffers.Binary;

namespace TestMaker.Hwp;

/// <summary>그림 비율 계산용으로 PNG/JPEG/GIF/BMP 헤더에서 픽셀 크기만 읽는다.</summary>
public static class ImageSize
{
    public static (int Width, int Height)? Read(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> h = stackalloc byte[26];
        int n = f.Read(h);
        if (n >= 24 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G')
            return (BinaryPrimitives.ReadInt32BigEndian(h[16..]), BinaryPrimitives.ReadInt32BigEndian(h[20..]));
        if (n >= 10 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F')
            return (BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]));
        if (n >= 26 && h[0] == 'B' && h[1] == 'M')
            return (BinaryPrimitives.ReadInt32LittleEndian(h[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h[22..])));
        if (n >= 2 && h[0] == 0xFF && h[1] == 0xD8) return ReadJpeg(f);
        return null;
    }

    static (int, int)? ReadJpeg(FileStream f)
    {
        f.Position = 2;
        Span<byte> b = stackalloc byte[9];
        while (f.Read(b[..4]) == 4)
        {
            if (b[0] != 0xFF) return null;
            byte marker = b[1];
            int len = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
            // SOF0~SOF15 (DHT C4, JPG C8, DAC CC 제외)
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (f.Read(b[..5]) != 5) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(b[3..]), BinaryPrimitives.ReadUInt16BigEndian(b[1..]));
            }
            f.Position += len - 2;
        }
        return null;
    }
}
