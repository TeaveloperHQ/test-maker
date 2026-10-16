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
    /// <summary>이 세션이 띄운 한글 프로세스. 닫을 때 완전히 끝날 때까지 기다린다.</summary>
    readonly Process? ownProcess;
    bool disposed;

    public static bool IsInstalled => Type.GetTypeFromProgID(ProgId) is not null;

    public HwpSession(bool visible = false)
    {
        var type = Type.GetTypeFromProgID(ProgId)
            ?? throw new InvalidOperationException("한글(한컴오피스)이 설치되어 있지 않습니다.");
        var before = Process.GetProcessesByName("Hwp").Select(p => p.Id).ToHashSet();
        hwp = Activator.CreateInstance(type)!;

        var started = Process.GetProcessesByName("Hwp").Where(p => !before.Contains(p.Id)).ToList();
        bool fresh = started.Count > 0;
        if (fresh) ownProcess = started[0];
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

    // 한글은 자동화 프로그램이 임시 폴더 밖의 파일을 열거나 저장하면 보안 확인 창
    // ("한글을 이용하여 위 파일에 접근하려는 시도가 있습니다… 접근 허용?")을 띄워, 숨김 상태에서는 그대로 멈춘다(실측).
    // 그래서 한글이 직접 읽고 쓰는 파일은 항상 이 세션의 임시 폴더에 두고, 사용자 위치와는 .NET이 복사한다.
    string? workDir;
    int workSeq;

    string WorkPath(string extension)
    {
        workDir ??= Directory.CreateTempSubdirectory("testmaker_hwp_").FullName;
        return Path.Combine(workDir, $"f{++workSeq}{extension}");
    }

    static bool InTemp(string fullPath) =>
        fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);

    /// <param name="displayName">오류 메시지에 보일 원래 파일 이름(임시 사본을 열 때).</param>
    public void Open(string path, string? displayName = null)
    {
        var full = Path.GetFullPath(path);
        var name = displayName ?? Path.GetFileName(path);
        if (HwpFileInfo.IsPasswordProtected(full))
            throw new IOException($"암호가 걸린 한글 파일이라 열 수 없습니다: {name} — 한글에서 암호를 풀고 다른 이름으로 저장한 사본을 주세요.");
        if (!InTemp(full))
        {
            var copy = WorkPath(Path.GetExtension(full));
            File.Copy(full, copy);
            full = copy;
        }
        // suspendpassword: 암호 입력창을 띄우지 않는다(숨김 창에서 입력창이 뜨면 멈춘다).
        bool ok = hwp.Open(full, "", "forceopen:true;suspendpassword:true");
        if (!ok) throw new IOException($"한글에서 파일을 열지 못했습니다: {name}");
    }

    public void SaveAs(string path, HwpFormat format)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string fmt = format switch { HwpFormat.Hwp => "HWP", HwpFormat.Hwpx => "HWPX", _ => "PDF" };

        // 한글은 항상 임시 폴더의 새 파일에 저장한다(보안 확인 창·덮어쓰기 확인 창을 피함).
        var temp = InTemp(full) && !File.Exists(full) ? full : WorkPath(Path.GetExtension(full));
        bool ok = hwp.SaveAs(temp, fmt, "");
        if (!ok) throw new IOException($"저장하지 못했습니다({fmt}): {path}");
        if (temp == full) return;
        try
        {
            File.Copy(temp, full, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{Path.GetFileName(full)} 파일이 다른 프로그램(한글 등)에서 열려 있어 저장할 수 없습니다. 닫고 다시 실행하세요.", e);
        }
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
        // 한글은 Quit 뒤에도 잠시 살아 있다. 바로 다음 세션이 그 인스턴스에 붙어
        // "열려 있는 한글에 연결됨"으로 멈추지 않도록, 우리가 띄운 프로세스가 끝날 때까지 기다린다.
        try { ownProcess?.WaitForExit(15_000); } catch (InvalidOperationException) { }
        ownProcess?.Dispose();
        if (workDir is not null)
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
