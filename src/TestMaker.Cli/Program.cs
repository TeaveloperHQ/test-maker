using System.Text;
using TestMaker.Ai;
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
          시험지를 만든다. 학생용과 원안지(정답 형광펜)를 함께 만든다.
          -t가 없으면 내장 기본 양식(forms/default).
          --pdf           같은 이름의 PDF도 저장
          --student-only  원안지는 만들지 않음
          --answers       정답 및 해설 파일(…_정답.hwp)도 저장
          --show          한글 창을 보이게 (확인용)

      author <작업폴더> <자료 …> [-n 변형수] [--keep] [--style 방식] [--request 요청] [-t 양식폴더]
          자료(그림·PDF·한글 파일)에서 문항을 찾아 변형 문항을 만들고 시험지를 출력한다(Claude Code 필요).
          -n        문항마다 만들 변형 수(기본 1)    --keep   원래 문항도 넣기
          --style   알아서 | 숫자만 바꾸기 | 상황·소재 바꾸기 | 더 어렵게 | 더 쉽게

      build-default-form <폴더>
          (개발용) 내장 기본 양식을 한글로 다시 만든다. 저장소의 forms/default를 갱신할 때 쓴다.
    """;

try
{
    return args switch
    {
        ["sample", var path] => Sample(path),
        ["check", var path] => Check(path),
        ["render", var path, .. var rest] => Render(path, rest),
        ["learn", var src, var dir, .. var rest] => Learn(src, dir, rest),
        ["build-default-form", var dir] => BuildDefaultForm(dir),
        ["author", var dir, .. var rest] when rest.Length > 0 => await Author(dir, rest),
        ["originals", var dir, .. var names] => await Originals(dir, names),
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
    var formDir = opts.Value("-t") ?? FormRendering.DefaultFormDir;
    if (!FormRendering.IsFormDir(formDir))
    {
        Console.Error.WriteLine($"양식 폴더가 아닙니다(form.json 없음): {formDir}  — learn으로 학교 양식을 먼저 학습하세요.");
        return 1;
    }

    var files = FormRendering.Render(exam, formDir, examDir, output, original: !opts.Has("--student-only"), pdf: opts.Has("--pdf"), visible: opts.Has("--show"));
    foreach (var f in files) Console.WriteLine($"{f.Path} ({f.Pages}쪽)");

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

static async Task<int> Author(string dir, string[] rest)
{
    var files = rest.TakeWhile(a => !a.StartsWith('-')).ToList();
    var opts = Options.Parse(rest[files.Count..], valued: ["-n", "--style", "--request", "-t"], flags: ["--keep"]);
    var exe = ClaudeCode.Find() ?? throw new InvalidOperationException("Claude Code(claude.exe)를 찾지 못했습니다. 설치하고 로그인하세요.");
    var a = Authoring.Open(new ClaudeCode(exe), dir, HwpSource.Convert);
    a.RenderPages = PdfPages.Save;
    void Log(string m) => Console.WriteLine($"  … {m}");

    foreach (var f in files) Console.WriteLine($"자료: {a.AddSource(f).Name}");
    a.Job.Request = opts.Value("--request") ?? "";
    a.Job.FormDir = opts.Value("-t");
    var found = await a.FindQuestionsAsync(Log);
    foreach (var q in found)
    {
        q.Variants = int.Parse(opts.Value("-n") ?? "1");
        q.KeepOriginal = opts.Has("--keep");
        q.Style = opts.Value("--style") ?? VariantStyle.Auto;
        Console.WriteLine($"{q.Id}  {q.Number}번 [{q.TypeText}{(q.HasFigure ? ", 그림" : "")}] {q.Summary}");
    }
    a.Save();
    foreach (var q in found)
    {
        var items = await a.MakeVariantsAsync(q, Log);
        Console.WriteLine($"{q.Number}번: {items.Count}문항 완료");
    }

    var exam = a.BuildExam();
    var examPath = Path.Combine(a.Dir, "시험.json");
    JsonStore.Save(examPath, exam);
    var output = Path.Combine(a.Dir, "시험지.hwp");
    var rendered = FormRendering.Render(exam, a.Job.FormDir ?? FormRendering.DefaultFormDir, a.Dir, output, original: true, pdf: true);
    foreach (var r in rendered) Console.WriteLine($"{r.Path} ({r.Pages}쪽)");
    return 0;
}

/// <summary>작업의 예전 문항(원문을 옮기지 않은 것)에 원문을 옮긴다. 자료 이름을 주면 그 자료만.</summary>
static async Task<int> Originals(string dir, string[] names)
{
    var exe = ClaudeCode.Find() ?? throw new InvalidOperationException("Claude Code(claude.exe)를 찾지 못했습니다.");
    var a = Authoring.Open(new ClaudeCode(exe), dir, HwpSource.Convert);
    a.RenderPages = PdfPages.Save;
    foreach (var s in a.Job.Sources.Where(s => names.Length == 0 || names.Contains(s.Name)))
        Console.WriteLine($"{s.Name}: 원문 {await a.TranscribeOriginalsAsync(s, m => Console.WriteLine($"  … {m}"))}개");
    return 0;
}

static int BuildDefaultForm(string dir)
{
    var r = DefaultFormBuilder.Build(dir, m => Console.WriteLine($"  … {m}"));
    Console.WriteLine($"기본 양식을 만들었습니다: {Path.GetFullPath(dir)}");
    Console.WriteLine($"  선택지 배치: {string.Join(", ", r.Form.ChoiceLayouts)}, <보기>: {r.Form.ViewBox?.Line}, 자료: {r.Form.PassageBox?.Line}");
    foreach (var n in r.Form.Notes) Console.WriteLine($"  참고: {n}");
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
