using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TestMaker.Ai;

public sealed record ClaudeResult(string? SessionId, string Text, bool IsError, double CostUsd);

/// <summary>
/// 설치된 Claude Code를 비대화형(-p)으로 실행한다. 작업 폴더(cwd) 안에서만 파일을 읽고 쓰게 하고(--restricted),
/// 그림을 그리는 스크립트를 돌릴 수 있도록 PowerShell만 허용한다. 진행 상황은 stream-json으로 받아 알린다.
/// </summary>
public sealed class ClaudeCode(string exe)
{
    /// <summary>파일 읽기·쓰기 + 그림 스크립트 실행.</summary>
    const string Tools = "Read,Write,Edit,Glob,Grep,PowerShell";

    public string Exe { get; } = exe;

    /// <summary>claude.exe를 찾는다(PATH, 기본 설치 위치). 없으면 null.</summary>
    public static string? Find()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                var p = Path.Combine(d.Trim('"'), n);
                if (File.Exists(p)) return p;
            }
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var d in Directory.EnumerateDirectories(winget, "Anthropic.ClaudeCode*"))
            {
                var p = Path.Combine(d, "claude.exe");
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <param name="workDir">Claude가 다룰 수 있는 폴더(자료·결과가 모두 여기에).</param>
    /// <param name="resume">이전 대화를 이어서(고쳐 달라고 할 때).</param>
    /// <param name="progress">진행 상황 한 줄씩(예: "그림 그리는 중").</param>
    public async Task<ClaudeResult> RunAsync(string workDir, string prompt, string? resume = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--restricted",
                                  "--tools", Tools, "--allowedTools", Tools, "--permission-mode", "acceptEdits" })
            psi.ArgumentList.Add(a);
        if (resume is not null) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(resume); }
        // Claude Code 안에서 이 프로그램을 실행했을 때(개발 중) 중첩 실행으로 막히지 않도록.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Claude Code를 실행하지 못했습니다.");
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();

        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        ClaudeResult? result = null;
        string? session = null;
        while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String) session = sid.GetString();
                switch (root.GetProperty("type").GetString())
                {
                    case "assistant":
                        foreach (var c in root.GetProperty("message").GetProperty("content").EnumerateArray())
                        {
                            if (c.GetProperty("type").GetString() == "tool_use")
                                progress?.Invoke(Describe(c.GetProperty("name").GetString() ?? "", c.GetProperty("input")));
                        }
                        break;
                    case "result":
                        result = new ClaudeResult(session,
                            root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "",
                            root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
                            root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : 0);
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var err = await errTask;
        return result ?? throw new InvalidOperationException(
            "Claude Code가 결과 없이 끝났습니다. " + (err.Length > 0 ? err.Trim() : "로그인이 되어 있는지 확인하세요(명령 창에서 claude 실행)."));
    }

    /// <summary>도구 호출을 선생님이 알아볼 말로.</summary>
    static string Describe(string tool, JsonElement input)
    {
        string? Str(string name) => input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var file = Path.GetFileName(Str("file_path") ?? "");
        return tool switch
        {
            "Read" => $"읽는 중: {file}",
            "Write" or "Edit" => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? $"문항 쓰는 중: {file}" : $"파일 쓰는 중: {file}",
            "PowerShell" => $"그림 작업 중: {Str("description") ?? "스크립트 실행"}",
            "Glob" or "Grep" => "파일 찾는 중",
            _ => tool,
        };
    }
}
