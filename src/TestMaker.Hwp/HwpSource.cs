using System.IO.Compression;

namespace TestMaker.Hwp;

/// <summary>문항 자료로 받은 한글 파일을 Claude가 읽을 수 있는 형태(PDF + 안에 든 그림)로 바꾼다.</summary>
public static class HwpSource
{
    static readonly string[] Viewable = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    /// <param name="hwpPath">한글 파일(사본). 고치지 않는다.</param>
    /// <param name="outDir">PDF와 그림을 둘 폴더.</param>
    public static (string Pdf, IReadOnlyList<string> Figures) Convert(string hwpPath, string outDir)
    {
        if (HwpFileInfo.IsPasswordProtected(hwpPath))
            throw new IOException($"암호가 걸린 한글 파일입니다: {Path.GetFileName(hwpPath)} — 한글에서 암호를 풀고 다른 이름으로 저장한 사본을 주세요.");
        Directory.CreateDirectory(outDir);
        var name = Path.GetFileNameWithoutExtension(hwpPath);
        var pdf = Path.Combine(outDir, name + ".pdf");
        var hwpx = Path.Combine(Path.GetTempPath(), $"testmaker_src_{Guid.NewGuid():N}.hwpx");
        try
        {
            using (var session = new HwpSession())
            {
                session.Open(hwpPath);
                session.SaveAs(pdf, HwpFormat.Pdf);
                session.SaveAs(hwpx, HwpFormat.Hwpx);
            }
            return (pdf, ExtractImages(hwpx, outDir));
        }
        finally
        {
            File.Delete(hwpx);
        }
    }

    /// <summary>HWPX 안의 그림(BinData)을 꺼낸다. 볼 수 있는 형식(PNG/JPG/GIF/BMP)만.</summary>
    public static List<string> ExtractImages(string hwpx, string outDir)
    {
        var result = new List<string>();
        Directory.CreateDirectory(outDir);
        using var zip = ZipFile.OpenRead(hwpx);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName))
        {
            var ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (!Viewable.Contains(ext) || e.Length < 1024) continue;   // 아주 작은 것은 글머리표·장식
            var target = Path.Combine(outDir, $"그림{result.Count + 1}{ext}");
            e.ExtractToFile(target, overwrite: true);
            result.Add(target);
        }
        return result;
    }
}
