using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TestMaker.Hwp;

public enum HwpFormat { Hwp, Hwpx, Pdf }

/// <summary>
/// 설치된 한글을 숨김 상태로 띄워 문서 하나를 다루는 세션.
/// 사용자가 이미 한글을 열어 두었으면 COM이 그 창에 붙을 수 있으므로, 그때는 아무것도 건드리지 않고 중단한다.
/// </summary>
public sealed class HwpSession : IDisposable
{
    const string ProgId = "HWPFrame.HwpObject";
    readonly dynamic hwp;
    bool disposed;

    public static bool IsInstalled => Type.GetTypeFromProgID(ProgId) is not null;

    public HwpSession(bool visible = false)
    {
        var type = Type.GetTypeFromProgID(ProgId)
            ?? throw new InvalidOperationException("한글(한컴오피스)이 설치되어 있지 않습니다.");
        var before = Process.GetProcessesByName("Hwp").Select(p => p.Id).ToHashSet();
        hwp = Activator.CreateInstance(type)!;

        bool fresh = Process.GetProcessesByName("Hwp").Any(p => !before.Contains(p.Id));
        if (!fresh || (int)hwp.XHwpDocuments.Count != 1 || !string.IsNullOrEmpty((string)hwp.Path))
        {
            // 남의 창이므로 Clear/Quit 하지 않고 참조만 놓는다.
            Marshal.FinalReleaseComObject(hwp);
            disposed = true;
            throw new InvalidOperationException(
                "열려 있는 한글 창에 연결되었습니다. 작업 중인 문서를 보호하려고 중단합니다. 한글 창을 모두 닫고 다시 실행하세요.");
        }
        hwp.XHwpWindows.Item(0).Visible = visible;
    }

    /// <summary>한글 자동화 객체(HwpObject). 래퍼에 없는 기능이 필요할 때만 쓴다.</summary>
    public dynamic Raw => hwp;

    public void NewDocument() => hwp.Clear(1);

    public void Open(string path)
    {
        bool ok = hwp.Open(Path.GetFullPath(path), "", "forceopen:true");
        if (!ok) throw new IOException($"한글에서 파일을 열지 못했습니다: {path}");
    }

    public void SaveAs(string path, HwpFormat format)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string fmt = format switch { HwpFormat.Hwp => "HWP", HwpFormat.Hwpx => "HWPX", _ => "PDF" };
        bool ok = hwp.SaveAs(full, fmt, "");
        if (!ok) throw new IOException($"저장하지 못했습니다({fmt}): {path} — 파일이 다른 프로그램에서 열려 있는지 확인하세요.");
    }

    public static HwpFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hwp" => HwpFormat.Hwp,
        ".hwpx" => HwpFormat.Hwpx,
        ".pdf" => HwpFormat.Pdf,
        var e => throw new ArgumentException($"지원하지 않는 확장자: {e} (.hwp, .hwpx, .pdf)"),
    };

    public int PageCount => (int)hwp.PageCount;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { hwp.Clear(1); } catch (COMException) { }
        try { hwp.Quit(); } catch (COMException) { }
        Marshal.FinalReleaseComObject(hwp);
    }
}
