using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TestMaker.Hwp;

/// <summary>한글을 띄우기 전에 파일 상태(암호 등)를 확인한다.</summary>
public static class HwpFileInfo
{
    static readonly byte[] Signature = Encoding.ASCII.GetBytes("HWP Document File");

    /// <summary>
    /// 암호가 걸린 문서인지. .hwp는 FileHeader의 속성(서명 32바이트 + 버전 4바이트 뒤 4바이트) 1번 비트,
    /// .hwpx는 매니페스트의 암호화 정보로 판단한다. 판단할 수 없으면 false.
    /// </summary>
    public static bool IsPasswordProtected(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".hwpx")
            {
                using var zip = ZipFile.OpenRead(path);
                var manifest = zip.GetEntry("META-INF/manifest.xml");
                if (manifest is null) return false;
                using var r = new StreamReader(manifest.Open());
                return r.ReadToEnd().Contains("encryption-data", StringComparison.OrdinalIgnoreCase);
            }
            if (ext == ".hwp")
            {
                // FileHeader 스트림은 작아서 OLE 파일 앞부분(미니 스트림)에 그대로 들어 있다.
                var bytes = File.ReadAllBytes(path);
                int i = bytes.AsSpan().IndexOf(Signature);
                if (i < 0 || i + 40 > bytes.Length) return false;
                uint props = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 36));
                return (props & 0x2) != 0; // bit0 압축, bit1 암호, bit2 배포용
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return false;
    }
}
