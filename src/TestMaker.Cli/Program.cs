using System.Text;
using TestMaker.Core.Forms;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Templates;
using TestMaker.Core.Validation;
using TestMaker.Hwp;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    testmaker — 한글 시험지 만들기

      sample <시험.json>
          예시 시험 JSON을 만든다. 새 시험의 출발점.

      check <시험.json>
          문항 검사(정답 범위, 배점, 표기 오류 등).

      learn <학교양식.hwp> <양식폴더> [--also 기출시험지.hwp …] [--name 양식이름]
          학교 문서에서 양식을 학습한다. 첫 파일(보통 학교 양식 파일)에서 스타일·번호·상자 견본을,
          --also로 준 실제 시험지에서 문항 간격 같은 실제 사용 방식을 배운다. 원본 파일은 바꾸지 않는다.

      render <시험.json> [-t 양식폴더] [-o 출력.hwp|.hwpx] [--pdf] [--student-only] [--answers] [--show]
          시험지를 만든다. 학습한 양식이면 학생용과 원안지(정답 형광펜)를 함께 만든다.
          -t가 없으면 기본 양식.
          --pdf           같은 이름의 PDF도 저장
          --student-only  원안지는 만들지 않음
          --answers       정답 및 해설 파일(…_정답.hwp)도 저장
          --show          한글 창을 보이게 (확인용)
    """;

try
{
    return args switch
    {
        ["sample", var path] => Sample(path),
        ["check", var path] => Check(path),
        ["render", var path, .. var rest] => Render(path, rest),
        ["learn", var src, var dir, .. var rest] => Learn(src, dir, rest),
        _ => Help(),
    };
}
catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or FormatException
                              or ArgumentException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
{
    Console.Error.WriteLine($"실패: {e.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine(Usage);
    return 2;
}

static int Sample(string path)
{
    if (File.Exists(path)) { Console.Error.WriteLine($"이미 있는 파일입니다: {path}"); return 1; }
    JsonStore.Save(path, SampleExam.Create());
    Console.WriteLine($"예시 시험을 만들었습니다: {path}");
    return 0;
}

static int Check(string path)
{
    var exam = JsonStore.Load<Exam>(path);
    var issues = ExamValidator.Validate(exam);
    foreach (var i in issues) Console.WriteLine(i);
    Console.WriteLine($"{exam.Questions.Count}문항, 총 {Format.Points(exam.TotalPoints)}점 — " +
                      (issues.Count == 0 ? "문제 없음" : $"오류 {issues.Count(i => i.Severity == Severity.Error)}, 주의 {issues.Count(i => i.Severity == Severity.Warning)}"));
    return issues.Any(i => i.Severity == Severity.Error) ? 1 : 0;
}

static int Render(string path, string[] rest)
{
    var opts = Options.Parse(rest, valued: ["-t", "-o"], flags: ["--pdf", "--answers", "--show", "--student-only"]);
    var exam = JsonStore.Load<Exam>(path);
    var issues = ExamValidator.Validate(exam);
    foreach (var i in issues) Console.WriteLine(i);
    if (issues.Any(i => i.Severity == Severity.Error)) { Console.Error.WriteLine("오류가 있어 만들지 않았습니다."); return 1; }

    var output = Path.GetFullPath(opts.Value("-o") ?? Path.ChangeExtension(path, ".hwp"));
    var examDir = Path.GetDirectoryName(Path.GetFullPath(path));
    var t = opts.Value("-t");

    if (t is not null && FormRendering.IsFormDir(t))
    {
        var files = FormRendering.Render(exam, t, examDir, output, original: !opts.Has("--student-only"), pdf: opts.Has("--pdf"), visible: opts.Has("--show"));
        foreach (var f in files) Console.WriteLine($"{f.Path} ({f.Pages}쪽)");
    }
    else
    {
        string? templateDir = null;
        var template = t is not null ? ExamTemplate.Load(t, out templateDir) : ExamTemplate.Default();
        using var session = new HwpSession(visible: opts.Has("--show"));
        new ExamRenderer(session).Render(exam, template, templateDir, examDir);
        session.SaveAs(output, HwpSession.FormatOf(output));
        Console.WriteLine($"{output} ({session.PageCount}쪽)");
        if (opts.Has("--pdf")) session.SaveAs(Path.ChangeExtension(output, ".pdf"), HwpFormat.Pdf);
    }

    if (opts.Has("--answers"))
    {
        var answers = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_정답" + Path.GetExtension(output));
        using var session = new HwpSession();
        new AnswerSheetRenderer(session).Render(exam, new TextStyle());
        session.SaveAs(answers, HwpSession.FormatOf(answers));
        Console.WriteLine($"정답지: {answers}");
        if (opts.Has("--pdf")) session.SaveAs(Path.ChangeExtension(answers, ".pdf"), HwpFormat.Pdf);
    }
    return 0;
}

static int Learn(string source, string dir, string[] rest)
{
    var opts = Options.Parse(rest, valued: ["--name"], flags: [], multi: ["--also"]);
    var r = FormRendering.Learn(source, opts.Values("--also"), dir, opts.Value("--name"));
    var f = r.Form;
    Console.WriteLine($"""
        양식을 학습했습니다: {Path.GetFullPath(dir)}
          판정: {(f.Kind == FormKind.Structured ? "완성형(문항 번호가 스타일에서 나옴)" : "무질서형(문항 번호를 글자로 입력)")}
          찾은 문항: {r.QuestionCount}개, 단 폭 {f.ColumnWidthMm}mm
          발문: {f.Stem}   이어지는 발문: {f.StemContinuation}
          묶음 머리: {f.GroupHeader}   그림: {f.Figure}
          선택지 배치: {string.Join(", ", f.ChoiceLayouts)}
          <보기> 상자: {(f.ViewBox is { } v ? $"견본 있음, 줄 {v.Line}" : "없음")}
          자료 상자: {(f.PassageBox is { } p ? $"견본 있음, 줄 {p.Line}" : "없음")}
          배점 표기: "{f.PointsFormat}", 문항 사이 빈 줄: {f.SpacerLines}

        머리 부분에 남은 글:
        {string.Join("\n", r.PreambleText.Split('\n').Select(l => "    " + l))}

        머리 부분(base.hwpx)의 시험마다 바뀌는 글을 {"{{제목}}"}, {"{{과목}}"}, {"{{학년}}"}, {"{{일시}}"}, {"{{문항수}}"}, {"{{총점}}"}, {"{{쪽수}}"} 등으로
        바꿔 두면 자동으로 채워집니다(한글에서 base.hwpx를 열어 고치세요).
        """);
    if (r.ColoredFields.Count > 0)
    {
        Console.WriteLine("  양식에 색 글씨로 표시된 칸([ ] 부분) — 자리표시로 바꿔 두면 자동으로 채워집니다:");
        foreach (var c in r.ColoredFields) Console.WriteLine($"    {c}");
    }
    foreach (var n in f.Notes) Console.WriteLine($"  참고: {n}");
    return 0;
}

sealed class Options
{
    readonly Dictionary<string, string> values = [];
    readonly Dictionary<string, List<string>> lists = [];
    readonly HashSet<string> flags = [];

    public static Options Parse(string[] args, string[] valued, string[] flags, string[]? multi = null)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (valued.Contains(args[i]) && i + 1 < args.Length) o.values[args[i]] = args[++i];
            else if (multi?.Contains(args[i]) == true && i + 1 < args.Length)
            {
                if (!o.lists.TryGetValue(args[i], out var l)) o.lists[args[i]] = l = [];
                l.Add(args[++i]);
            }
            else if (flags.Contains(args[i])) o.flags.Add(args[i]);
            else throw new ArgumentException($"알 수 없는 옵션: {args[i]}");
        }
        return o;
    }

    public string? Value(string name) => values.GetValueOrDefault(name);
    public IReadOnlyList<string> Values(string name) => lists.GetValueOrDefault(name) ?? [];
    public bool Has(string flag) => flags.Contains(flag);
}
