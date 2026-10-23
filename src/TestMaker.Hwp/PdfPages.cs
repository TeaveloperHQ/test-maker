using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TestMaker.Hwp;

/// <summary>
/// 자료 PDF의 쪽을 PNG로 저장한다(Windows에 들어 있는 PDF 엔진). Claude가 이 그림에서 문항 영역(원문 캡처)과
/// 문항 그림을 잘라 낸다.
/// </summary>
public static class PdfPages
{
    /// <returns>저장한 쪽 그림(쪽 순서대로).</returns>
    public static IReadOnlyList<string> Save(string pdfPath, string outDir) => Save(pdfPath, outDir, 1600, 40);

    public static IReadOnlyList<string> Save(string pdfPath, string outDir, uint width, int maxPages) =>
        // WinRT 비동기를 화면 스레드에서 기다리면 멈출 수 있어 스레드 풀에서 돈다.
        Task.Run(() => SaveAsync(pdfPath, outDir, width, maxPages)).GetAwaiter().GetResult();

    static async Task<IReadOnlyList<string>> SaveAsync(string pdfPath, string outDir, uint width, int maxPages)
    {
        Directory.CreateDirectory(outDir);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        var saved = new List<string>();
        for (uint i = 0; i < Math.Min(doc.PageCount, (uint)maxPages); i++)
        {
            using var page = doc.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = width,
                BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            });
            stream.Seek(0);
            var target = Path.Combine(outDir, $"쪽{i + 1}.png");
            using (var output = File.Create(target))
            using (var input = stream.AsStreamForRead())
                await input.CopyToAsync(output);
            saved.Add(target);
        }
        return saved;
    }
}
