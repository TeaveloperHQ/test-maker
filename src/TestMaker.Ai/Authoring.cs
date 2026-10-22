using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Validation;

namespace TestMaker.Ai;

/// <summary>한글 파일을 Claude가 읽을 수 있게 바꾸는 함수: (한글 파일 사본, 결과 폴더) → (PDF, 안에 든 그림들).</summary>
public delegate (string Pdf, IReadOnlyList<string> Figures) HwpConverter(string hwpPath, string outDir);

/// <summary>
/// 작업 하나를 진행한다: 자료 넣기 → 문항 찾기 → 문항별 변형 → 시험(Exam) 만들기.
/// Claude가 쓴 결과는 앱이 검사하고, 틀린 곳은 같은 대화로 고쳐 달라고 한다.
/// </summary>
public sealed class Authoring
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    public static readonly string[] HwpExtensions = [".hwp", ".hwpx"];
    const int MaxFixes = 2;

    readonly ClaudeCode? claude;
    readonly HwpConverter? convertHwp;

    public string Dir { get; }
    public AuthoringJob Job { get; }

    /// <summary>작업 이름(이름이 없던 예전 작업은 폴더 이름).</summary>
    public string Name => Job.Name.Length > 0 ? Job.Name : Path.GetFileName(Dir);

    Authoring(ClaudeCode? claude, string dir, AuthoringJob job, HwpConverter? convertHwp)
    {
        this.claude = claude;
        this.convertHwp = convertHwp;
        Dir = Path.GetFullPath(dir);
        Job = job;
    }

    /// <param name="claude">없으면(Claude Code 미설치) 문항 찾기·변형만 못 하고 나머지(자료, 시험지)는 된다.</param>
    public static Authoring Open(ClaudeCode? claude, string dir, HwpConverter? convertHwp = null)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, AuthoringJob.FileName);
        var job = File.Exists(file) ? JsonStore.Load<AuthoringJob>(file) : new AuthoringJob();
        return new Authoring(claude, dir, job, convertHwp);
    }

    /// <summary>이름 붙인 새 작업. 폴더 이름 = 작업 이름(같은 이름이 있으면 번호를 붙인다).</summary>
    public static Authoring Create(ClaudeCode? claude, string jobsRoot, string name, HwpConverter? convertHwp = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("작업 이름을 적으세요.");
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "작업";
        var dir = Path.Combine(jobsRoot, safe);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(jobsRoot, $"{safe} ({i})");
        var a = Open(claude, dir, convertHwp);
        a.Job.Name = name;
        a.Job.CreatedAt = DateTime.Now;
        a.Save();
        return a;
    }

    /// <summary>작업 목록(최근에 고친 것이 앞). 읽지 못하는 폴더는 건너뛴다.</summary>
    public static List<JobSummary> List(string jobsRoot)
    {
        var list = new List<JobSummary>();
        if (!Directory.Exists(jobsRoot)) return list;
        foreach (var d in Directory.EnumerateDirectories(jobsRoot))
        {
            var file = Path.Combine(d, AuthoringJob.FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var j = JsonStore.Load<AuthoringJob>(file);
                list.Add(new JobSummary(d, j.Name.Length > 0 ? j.Name : Path.GetFileName(d), File.GetLastWriteTime(file),
                                        j.Sources.Count, j.Questions.Count, j.ExamList.Count));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return list.OrderByDescending(s => s.Modified).ToList();
    }

    public void Save() => JsonStore.Save(Path.Combine(Dir, AuthoringJob.FileName), Job);

    ClaudeCode Claude => claude ?? throw new InvalidOperationException("Claude Code가 없어 문항 찾기·변형을 할 수 없습니다. 설치하고 로그인하세요.");

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || HwpExtensions.Contains(ext) || ext == ".pdf";
    }

    /// <summary>자료를 작업 폴더로 복사한다(원본은 건드리지 않음). 한글 파일은 PDF와 그림으로 바꿔 둔다.</summary>
    public SourceFile AddSource(string path)
    {
        if (!IsSupported(path)) throw new ArgumentException($"쓸 수 없는 파일 형식입니다: {Path.GetFileName(path)} (그림, PDF, 한글 파일)");
        var sources = Path.Combine(Dir, AuthoringJob.SourcesDir);
        Directory.CreateDirectory(sources);
        var name = UniqueName(sources, Path.GetFileName(path));
        var copy = Path.Combine(sources, name);
        File.Copy(path, copy);

        var src = new SourceFile { Name = name, Original = Path.GetFullPath(path) };
        if (HwpExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            if (convertHwp is null) throw new InvalidOperationException("한글 파일을 읽으려면 한글이 필요합니다.");
            var outDir = Path.Combine(sources, Path.GetFileNameWithoutExtension(name));
            Directory.CreateDirectory(outDir);
            var (pdf, figures) = convertHwp(copy, outDir);
            src.ReadFiles.Add(Relative(pdf));
            src.Figures.AddRange(figures.Select(Relative));
        }
        else src.ReadFiles.Add(Relative(copy));
        Job.Sources.Add(src);
        Save();
        return src;
    }

    /// <summary>붙여 넣은 그림(화면 캡처 등)을 자료로 넣는다.</summary>
    public SourceFile AddImage(byte[] png, string baseName = "붙여넣기")
    {
        var temp = Path.Combine(Path.GetTempPath(), $"{baseName}.png");
        File.WriteAllBytes(temp, png);
        try { return AddSource(temp); }
        finally { File.Delete(temp); }
    }

    public void RemoveSource(SourceFile src)
    {
        Job.Sources.Remove(src);
        Job.Questions.RemoveAll(q => q.Source == src.Name);
        Save();
    }

    // ── 문항 찾기 ──────────────────────────────────────────

    public async Task<IReadOnlyList<FoundQuestion>> FindQuestionsAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (Job.Sources.Count == 0) throw new InvalidOperationException("자료를 먼저 넣으세요.");
        var found = Path.Combine(Dir, Prompts.FoundFile);
        File.Delete(found);
        log?.Invoke("자료를 읽고 문항을 찾는 중…");
        var r = await Claude.RunAsync(Dir, Prompts.FindQuestions(Job), progress: log, ct: ct);
        if (r.IsError) throw new InvalidOperationException($"문항을 찾지 못했습니다: {r.Text}");
        if (!File.Exists(found)) throw new InvalidOperationException($"문항 목록 파일이 만들어지지 않았습니다. Claude 답: {r.Text}");

        var list = JsonStore.Load<FoundList>(found);
        Job.Questions = list.Questions.Select((e, i) => new FoundQuestion
        {
            Id = $"q{i + 1:00}",
            Source = Job.Sources.FirstOrDefault(s => s.Name == e.Source)?.Name ?? e.Source,
            Number = e.Number,
            Summary = e.Summary,
            Type = e.Type,
            HasFigure = e.HasFigure,
        }).ToList();
        Save();
        log?.Invoke($"문항 {Job.Questions.Count}개를 찾았습니다.");
        return Job.Questions;
    }

    sealed class FoundList
    {
        public List<FoundEntry> Questions { get; set; } = [];
    }

    sealed class FoundEntry
    {
        public string Source { get; set; } = "";
        public string Number { get; set; } = "";
        public string Summary { get; set; } = "";
        public QuestionType Type { get; set; }
        public bool HasFigure { get; set; }
    }

    // ── 변형 만들기 ────────────────────────────────────────

    /// <summary>한 문항의 변형(+원문항)을 만든다. 검사에 걸리면 같은 대화로 고쳐 달라고 한다.</summary>
    public async Task<IReadOnlyList<Question>> MakeVariantsAsync(FoundQuestion q, Action<string>? log = null, CancellationToken ct = default)
    {
        if (q.ExpectedCount == 0) return [];
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.ItemsDir));
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.FiguresDir));
        var item = Path.Combine(Dir, q.ItemFile);
        File.Delete(item);

        var r = await Claude.RunAsync(Dir, Prompts.MakeVariants(Job, q), progress: log, ct: ct);
        for (int attempt = 0; ; attempt++)
        {
            if (r.IsError) throw new InvalidOperationException($"{q.Number}번: 만들지 못했습니다 — {r.Text}");
            var problems = Check(q);
            if (problems.Count == 0) return LoadItems(q);
            if (attempt >= MaxFixes || r.SessionId is null)
                throw new InvalidOperationException($"{q.Number}번: 결과에 문제가 남았습니다 — {string.Join(" / ", problems)}");
            log?.Invoke($"고치는 중: {string.Join(" / ", problems)}");
            r = await Claude.RunAsync(Dir, Prompts.Fix(q, problems), resume: r.SessionId, progress: log, ct: ct);
        }
    }

    /// <summary>결과 파일 검사: 형식, 문항 수, 정답 범위·표기(ExamValidator), 그림 파일.</summary>
    public List<string> Check(FoundQuestion q)
    {
        var path = Path.Combine(Dir, q.ItemFile);
        if (!File.Exists(path)) return [$"결과 파일({q.ItemFile})이 없습니다."];
        Exam exam;
        try { exam = JsonStore.Load<Exam>(path); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { return [$"JSON 형식 오류: {e.Message}"]; }

        var problems = new List<string>();
        if (exam.Questions.Count != q.ExpectedCount)
            problems.Add($"문항이 {exam.Questions.Count}개입니다({q.ExpectedCount}개여야 함).");
        problems.AddRange(ExamValidator.Validate(exam, expectedTotal: null).Select(i => i.ToString()));
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var img = exam.Questions[i].Image;
            if (img is { Path.Length: > 0 } && !File.Exists(Path.Combine(Dir, img.Path)))
                problems.Add($"{i + 1}번: 그림 파일이 없습니다({img.Path}).");
        }
        return problems;
    }

    public List<Question> LoadItems(FoundQuestion q)
    {
        var path = Path.Combine(Dir, q.ItemFile);
        return File.Exists(path) ? JsonStore.Load<Exam>(path).Questions : [];
    }

    // ── 시험 만들기 ────────────────────────────────────────

    /// <summary>고른 문항의 결과를 자료 순서대로 모아 시험을 만든다. exclude: 결과 중 빼기로 한 것(문항 Id, 순번).</summary>
    public Exam BuildExam(ISet<(string Id, int Index)>? exclude = null)
    {
        var exam = new Exam { Info = Job.Info };
        foreach (var q in Job.Questions.Where(q => q.Include))
        {
            var items = LoadItems(q);
            for (int i = 0; i < items.Count; i++)
                if (exclude?.Contains((q.Id, i)) != true) exam.Questions.Add(items[i]);
        }
        return exam;
    }

    string Relative(string full) => Path.GetRelativePath(Dir, full);

    static string UniqueName(string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (int i = 2; File.Exists(Path.Combine(dir, candidate)); i++) candidate = $"{stem} ({i}){ext}";
        return candidate;
    }
}
