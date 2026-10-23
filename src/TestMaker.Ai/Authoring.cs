using TestMaker.Core.Bank;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Validation;

namespace TestMaker.Ai;

/// <summary>한글 파일을 Claude가 읽을 수 있게 바꾸는 함수: (한글 파일 사본, 결과 폴더) → (PDF, 안에 든 그림들).</summary>
public delegate (string Pdf, IReadOnlyList<string> Figures) HwpConverter(string hwpPath, string outDir);

/// <summary>PDF 쪽을 그림으로 저장하는 함수: (PDF, 결과 폴더) → 쪽 그림들.</summary>
public delegate IReadOnlyList<string> PageRenderer(string pdfPath, string outDir);

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

    /// <summary>PDF 자료의 쪽을 그림으로 저장하는 함수(있으면 원문 캡처·그림 자르기에 쓴다).</summary>
    public PageRenderer? RenderPages { get; set; }

    /// <summary>PDF 자료(한글에서 바꾼 것 포함)의 쪽 그림을 만들어 둔다(이미 있으면 그대로). 예전 자료도 처음 필요할 때 만든다.</summary>
    public void EnsurePages(SourceFile src)
    {
        if (src.Pages.Count > 0 && src.Pages.All(p => File.Exists(Path.Combine(Dir, p)))) return;
        if (RenderPages is null || src.ReadFiles.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) is not { } pdf) return;
        var full = Path.Combine(Dir, pdf);
        if (!File.Exists(full)) return;
        var outDir = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "_쪽");
        try
        {
            src.Pages = RenderPages(full, outDir).Select(Relative).ToList();
            Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>문항 영역만 잘라 낸 원문 그림(없으면 null).</summary>
    public string? SnapshotOf(FoundQuestion q) => File.Exists(Path.Combine(Dir, q.SnapshotFile)) ? Path.Combine(Dir, q.SnapshotFile) : null;

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
        Migrate(job);
        return new Authoring(claude, dir, job, convertHwp);
    }

    /// <summary>예전 형식의 작업을 지금 형식으로(문항을 찾은 자료 표시, ④에 들어 있던 결과 → 시험지 행선지).</summary>
    static void Migrate(AuthoringJob job)
    {
        foreach (var s in job.Sources)
            if (!s.Analyzed && job.Questions.Any(q => q.Source == s.Name)) s.Analyzed = true;
        if (job.Destinations.Count == 0)
            foreach (var e in job.ExamList.Where(e => e.Key.StartsWith("job:")))
                job.Destinations[e.Key[4..]] = Destination.Exam;
        // 예전에는 칸 이름("시험용")을 적었다 — 폴더가 아닌 것은 버린다.
        foreach (var k in job.Archived.Where(kv => !Path.IsPathRooted(kv.Value)).Select(kv => kv.Key).ToList()) job.Archived.Remove(k);
    }

    /// <summary>이름 붙인 새 작업. 폴더 이름 = 작업 이름(같은 이름이 있으면 번호를 붙인다).</summary>
    public static Authoring Create(ClaudeCode? claude, string jobsRoot, string name, HwpConverter? convertHwp = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("작업 이름을 적으세요.");
        var a = Open(claude, FreeDir(jobsRoot, name), convertHwp);
        a.Job.Name = name;
        a.Job.CreatedAt = DateTime.Now;
        a.Save();
        return a;
    }

    static string FolderName(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        return safe.Length == 0 ? "작업" : safe;
    }

    static string FreeDir(string jobsRoot, string name, string? except = null)
    {
        var safe = FolderName(name);
        var dir = Path.Combine(jobsRoot, safe);
        for (int i = 2; Directory.Exists(dir) && !SameDir(dir, except); i++) dir = Path.Combine(jobsRoot, $"{safe} ({i})");
        return dir;
    }

    static bool SameDir(string a, string? b) =>
        b is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 작업 고치기: 이름(+학년·과목)을 바꾸고 폴더 이름도 맞춘다. 바뀐 폴더를 돌려준다.
    /// 폴더 안 파일이 다른 프로그램에서 열려 있으면 폴더 이름은 그대로 두고 이름만 바꾼다.
    /// </summary>
    public static string Edit(string dir, string name, string? grade = null, string? subject = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("작업 이름을 적으세요.");
        var file = Path.Combine(dir, AuthoringJob.FileName);
        var job = JsonStore.Load<AuthoringJob>(file);
        job.Name = name;
        if (grade is not null) job.Info.Grade = grade;
        if (subject is not null) job.Info.Subject = subject;
        JsonStore.Save(file, job);

        var root = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\'))!;
        var target = FreeDir(root, name, except: dir);
        if (SameDir(target, dir)) return dir;
        try
        {
            Directory.Move(dir, target);
            return target;
        }
        catch (IOException) { return dir; }          // 열려 있는 파일이 있으면 폴더 이름만 못 바꾼다
    }

    /// <summary>작업 복제: 자료·문항 표·결과·시험지 목록을 새 이름의 작업으로 복사한다(이미 만든 시험지 파일은 빼고).</summary>
    public static string Duplicate(string dir, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("작업 이름을 적으세요.");
        var root = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\'))!;
        var target = FreeDir(root, name);
        CopyDir(dir, target, skipTop: AuthoringJob.ExamsDir);
        var file = Path.Combine(target, AuthoringJob.FileName);
        var job = JsonStore.Load<AuthoringJob>(file);
        job.Name = name;
        job.CreatedAt = DateTime.Now;
        JsonStore.Save(file, job);
        return target;
    }

    static void CopyDir(string from, string to, string? skipTop)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            if (skipTop is not null && Path.GetFileName(d) == skipTop) continue;
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)), null);
        }
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
                                        j.Sources.Count, j.Questions.Count, j.ExamList.Count, j.PracticeList.Count));
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
        EnsurePages(src);
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

    /// <summary>자료를 뺀다: 그 자료에서 찾은 문항과 결과도 함께(문항함에 자동으로 들어간 것도 정리).</summary>
    public void RemoveSource(SourceFile src, QuestionBank? bank = null, string? jobsRoot = null)
    {
        ForgetQuestionsOf(src.Name, bank, jobsRoot);
        Job.Sources.Remove(src);
        Save();
    }

    /// <summary>한 자료에서 찾은 문항과 그 결과를 지운다(다시 찾기·자료 빼기).</summary>
    void ForgetQuestionsOf(string sourceName, QuestionBank? bank, string? jobsRoot)
    {
        foreach (var q in Job.Questions.Where(q => q.Source == sourceName).ToList())
        {
            ForgetResults(q, bank, jobsRoot);
            File.Delete(Path.Combine(Dir, q.ItemFile));
            File.Delete(Path.Combine(Dir, q.OriginalFile));
            File.Delete(Path.Combine(Dir, ViewOriginalFile(q)));
            File.Delete(Path.Combine(Dir, q.SnapshotFile));
            Job.Questions.Remove(q);
        }
    }

    // ── 문항 찾기 ──────────────────────────────────────────

    /// <summary>
    /// 아직 문항을 찾지 않은 자료에서 문항을 찾아 목록 끝에 더한다(이미 찾은 문항과 결과는 그대로).
    /// <paramref name="again"/>을 주면 그 자료의 문항을 지우고 다시 찾는다.
    /// </summary>
    public async Task<IReadOnlyList<FoundQuestion>> FindQuestionsAsync(Action<string>? log = null, CancellationToken ct = default,
        SourceFile? again = null, QuestionBank? bank = null, string? jobsRoot = null)
    {
        if (Job.Sources.Count == 0) throw new InvalidOperationException("자료를 먼저 넣으세요.");
        var targets = again is not null ? [again] : Job.Sources.Where(s => !s.Analyzed).ToList();
        if (targets.Count == 0) return [];
        var claudeCode = Claude;
        if (again is not null) ForgetQuestionsOf(again.Name, bank, jobsRoot);

        var found = Path.Combine(Dir, Prompts.FoundFile);
        File.Delete(found);
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.FiguresDir));
        int next = Job.Questions.Select(q => int.TryParse(q.Id.TrimStart('q'), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        log?.Invoke($"자료 {targets.Count}개를 읽고 문항을 찾는 중(원문항도 옮김)…");
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.OriginalsDir));
        foreach (var s in targets) EnsurePages(s);
        var r = await claudeCode.RunAsync(Dir, Prompts.FindQuestions(targets, next), progress: log, ct: ct);
        if (r.IsError) throw new InvalidOperationException($"문항을 찾지 못했습니다: {r.Text}");
        if (!File.Exists(found)) throw new InvalidOperationException($"문항 목록 파일이 만들어지지 않았습니다. Claude 답: {r.Text}");

        var list = JsonStore.Load<FoundList>(found);
        var added = list.Questions.Select((e, i) => new FoundQuestion
        {
            Id = $"q{next + i:00}",
            Source = targets.FirstOrDefault(s => s.Name == e.Source)?.Name ?? (targets.Count == 1 ? targets[0].Name : e.Source),
            Number = e.Number,
            Summary = e.Summary,
            Type = e.Type,
            HasFigure = e.HasFigure,
        }).ToList();
        // 옮긴 원문항: 검사를 통과한 것만 남긴다(못 옮긴 문항은 [변형 만들기] 때 예전처럼 같이 옮긴다).
        int saved = 0;
        for (int i = 0; i < added.Count; i++)
            if (list.Questions[i].Original is { } original && OriginalProblems(original).Count == 0)
            {
                JsonStore.Save(Path.Combine(Dir, added[i].OriginalFile), new Exam { Questions = [original] });
                saved++;
            }
        if (saved < added.Count) log?.Invoke($"원문항 {added.Count - saved}개는 옮기지 못했습니다([변형 만들기] 때 함께 옮깁니다).");
        // 자료 순서대로 끼워 넣는다(목록이 자료별로 모여 보이게).
        foreach (var q in added) Job.Questions.Add(q);
        var order = Job.Sources.Select(s => s.Name).ToList();
        Job.Questions = Job.Questions.OrderBy(q => order.IndexOf(q.Source) is var i && i >= 0 ? i : int.MaxValue).ToList();
        foreach (var s in targets) s.Analyzed = true;
        Save();
        log?.Invoke($"문항 {added.Count}개를 찾았습니다.");
        return added;
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
        /// <summary>[문항 찾기] 때 옮긴 원문항(앱의 문항 형식).</summary>
        public Question? Original { get; set; }
    }

    /// <summary>옮긴 원문항 검사(형식 오류, 그림 파일).</summary>
    List<string> OriginalProblems(Question q)
    {
        var problems = ExamValidator.Validate(new Exam { Questions = [q] }, expectedTotal: null)
            .Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
        if (q.Image is { Path.Length: > 0 } img && !File.Exists(Path.Combine(Dir, img.Path))) problems.Add($"그림 파일이 없습니다({img.Path}).");
        return problems;
    }

    // ── 원문항 ────────────────────────────────────────────

    /// <summary>[문항 찾기] 때 원문항을 옮겨 두었는지. 옮겨 두었으면 결과 파일(items)에는 변형만 들어 있다.</summary>
    public bool HasOriginal(FoundQuestion q) => File.Exists(Path.Combine(Dir, q.OriginalFile));

    public Question? LoadOriginal(FoundQuestion q)
    {
        var path = Path.Combine(Dir, q.OriginalFile);
        try { return File.Exists(path) ? JsonStore.Load<Exam>(path).Questions.FirstOrDefault() : null; }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// 원문 보기용 원문항. 이미 변형을 만든 문항에 나중에 옮긴 원문은 보기 전용 파일(<c>qNN.view.json</c>)에 둔다 —
    /// 결과의 열쇠·행선지가 바뀌지 않게.
    /// </summary>
    public Question? LoadOriginalForView(FoundQuestion q)
    {
        if (LoadOriginal(q) is { } o) return o;
        var path = Path.Combine(Dir, ViewOriginalFile(q));
        try { return File.Exists(path) ? JsonStore.Load<Exam>(path).Questions.FirstOrDefault() : null; }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    static string ViewOriginalFile(FoundQuestion q) => Path.Combine(AuthoringJob.OriginalsDir, q.Id + ".view.json");

    /// <summary>
    /// 이미 찾은 문항(원문을 옮기지 않은 예전 문항)의 원문을 옮긴다. 문항 목록과 결과는 그대로 둔다.
    /// 아직 변형을 만들지 않은 문항은 보통 원문항이 되고(원문항 칸이 바로 쓰임), 이미 만든 문항은 보기 전용으로 둔다.
    /// </summary>
    /// <returns>옮긴 문항 수.</returns>
    public async Task<int> TranscribeOriginalsAsync(SourceFile src, Action<string>? log = null, CancellationToken ct = default)
    {
        // 원문(글)이나 원문 그림(문항 영역 캡처) 중 하나라도 없는 문항.
        var todo = Job.Questions.Where(q => q.Source == src.Name && (LoadOriginalForView(q) is null || SnapshotOf(q) is null)).ToList();
        if (todo.Count == 0) return 0;
        var claudeCode = Claude;
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.FiguresDir));
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.OriginalsDir));
        EnsurePages(src);
        var outFile = Path.Combine(Dir, Prompts.OriginalsFile);
        File.Delete(outFile);
        log?.Invoke($"'{src.Name}'에서 문항 {todo.Count}개의 원문을 옮기는 중…");
        var r = await claudeCode.RunAsync(Dir, Prompts.TranscribeOriginals(src, todo), progress: log, ct: ct);
        if (r.IsError) throw new InvalidOperationException($"원문을 옮기지 못했습니다: {r.Text}");
        if (!File.Exists(outFile)) throw new InvalidOperationException($"원문 파일이 만들어지지 않았습니다. Claude 답: {r.Text}");

        var list = JsonStore.Load<OriginalList>(outFile);
        foreach (var e in list.Originals)
        {
            var q = todo.FirstOrDefault(x => x.Id == e.Id);
            // 이미 옮긴 원문(글)은 그대로 둔다(결과 열쇠가 바뀌지 않게) — 원문 그림만 새로 생겼을 수 있다.
            if (q is null || LoadOriginalForView(q) is not null || e.Original is not { } original || OriginalProblems(original).Count > 0) continue;
            var target = File.Exists(Path.Combine(Dir, q.ItemFile)) ? ViewOriginalFile(q) : q.OriginalFile;
            JsonStore.Save(Path.Combine(Dir, target), new Exam { Questions = [original] });
        }
        var done = todo.Count(q => LoadOriginalForView(q) is not null || SnapshotOf(q) is not null);
        log?.Invoke($"원문 {done}개를 옮겼습니다.");
        return done;
    }

    sealed class OriginalList
    {
        public List<OriginalEntry> Originals { get; set; } = [];
    }

    sealed class OriginalEntry
    {
        public string Id { get; set; } = "";
        public Question? Original { get; set; }
    }

    /// <summary>Claude가 결과 파일에 써야 할 문항 수(원문항을 옮겨 두었으면 변형만).</summary>
    public int ClaudeCount(FoundQuestion q) => HasOriginal(q) ? q.Variants : q.ExpectedCount;

    // ── 변형 만들기 ────────────────────────────────────────

    /// <summary>한 문항의 변형(+원문항)을 만든다. 검사에 걸리면 같은 대화로 고쳐 달라고 한다.</summary>
    public async Task<IReadOnlyList<Question>> MakeVariantsAsync(FoundQuestion q, Action<string>? log = null, CancellationToken ct = default)
    {
        if (q.ExpectedCount == 0) return [];
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.ItemsDir));
        Directory.CreateDirectory(Path.Combine(Dir, AuthoringJob.FiguresDir));
        var item = Path.Combine(Dir, q.ItemFile);
        File.Delete(item);
        if (ClaudeCount(q) == 0)
        {
            // 원문항만: 이미 옮겨 두었으니 Claude 없이 끝난다.
            JsonStore.Save(item, new Exam());
            return Results(q).Select(x => x.Item).ToList();
        }

        var r = await Claude.RunAsync(Dir, Prompts.MakeVariants(Job, q, HasOriginal(q)), progress: log, ct: ct);
        for (int attempt = 0; ; attempt++)
        {
            if (r.IsError) throw new InvalidOperationException($"{q.Number}번: 만들지 못했습니다 — {r.Text}");
            var problems = Check(q);
            if (problems.Count == 0) return Results(q).Select(x => x.Item).ToList();
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
        if (exam.Questions.Count != ClaudeCount(q))
            problems.Add($"문항이 {exam.Questions.Count}개입니다({ClaudeCount(q)}개여야 함).");
        problems.AddRange(ExamValidator.Validate(exam, expectedTotal: null).Select(i => i.ToString()));
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var img = exam.Questions[i].Image;
            if (img is { Path.Length: > 0 } && !File.Exists(Path.Combine(Dir, img.Path)))
                problems.Add($"{i + 1}번: 그림 파일이 없습니다({img.Path}).");
        }
        return problems;
    }

    /// <summary>결과 파일(items)에 든 문항 그대로.</summary>
    public List<Question> LoadItems(FoundQuestion q)
    {
        var path = Path.Combine(Dir, q.ItemFile);
        return File.Exists(path) ? JsonStore.Load<Exam>(path).Questions : [];
    }

    /// <summary>
    /// ③에 보일 결과: 원문항(켜져 있으면) + 변형. 원문항을 [문항 찾기] 때 옮겨 두었으면 원문항 칸을 끄고 켜는 대로 바로 바뀐다.
    /// 열쇠: 원문항 "q01/원", 변형 "q01/1" … (예전 방식으로 만든 문항은 결과 파일 순번 "q01/0" …).
    /// </summary>
    public List<JobResult> Results(FoundQuestion q)
    {
        var items = LoadItems(q);
        var list = new List<JobResult>();
        if (HasOriginal(q))
        {
            if (q.KeepOriginal && LoadOriginal(q) is { } original) list.Add(new JobResult($"{q.Id}/원", "원문항", original));
            for (int i = 0; i < items.Count; i++) list.Add(new JobResult($"{q.Id}/{i + 1}", $"변형 {i + 1}", items[i]));
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
                list.Add(new JobResult($"{q.Id}/{i}", q.KeepOriginal && i == 0 ? "원문항" : $"변형 {(q.KeepOriginal ? i : i + 1)}", items[i]));
        }
        return list;
    }

    // ── 행선지(③): 시험지 / 예상문제 / 버림 ──────────────────

    /// <summary>문항함·④ 목록에 보일 이름. 예: "07번 변형 1 · 열평형 온도 구하기"</summary>
    public static string ResultTitle(FoundQuestion q, string label) => $"{q.Number}번 {label} · {q.Summary}";

    public string? DestinationOf(string key) => Job.Destinations.GetValueOrDefault(key);

    /// <summary>
    /// 결과 하나의 행선지를 정한다(null = 버림). 시험지·예상문제로 보내면 문항함(시험용·예상문제)에도 자동으로 들어가고,
    /// 바꾸면 칸을 옮기고, 버리면 문항함에서도 뺀다(다른 작업이 그 문항을 쓰고 있으면 남긴다).
    /// </summary>
    public void SetDestination(FoundQuestion q, string key, string? dest, QuestionBank? bank = null, string? jobsRoot = null)
    {
        if (Job.Destinations.GetValueOrDefault(key) == dest) return;
        if (dest is null)
        {
            Job.Destinations.Remove(key);
            Unarchive(key, bank, jobsRoot);
        }
        else
        {
            Job.Destinations[key] = dest;
            if (bank is not null)
            {
                var cat = dest == Destination.Exam ? BankCategory.Exam : BankCategory.Practice;
                if (Job.Archived.TryGetValue(key, out var bankDir) && QuestionBank.LoadItem(bankDir) is { } item)
                {
                    bank.Move(item, cat);
                    Job.Archived[key] = item.Dir;
                }
                else if (Results(q).FirstOrDefault(x => x.Key == key) is { } result)
                {
                    Job.Archived[key] = bank.Add(result.Item, Dir, cat, ResultTitle(q, result.Label), source: Name).Dir;
                }
            }
        }
        Save();
    }

    /// <summary>한 문항의 결과 행선지를 모두 지운다(다시 만들기 전, 자료를 뺄 때).</summary>
    public void ForgetResults(FoundQuestion q, QuestionBank? bank = null, string? jobsRoot = null)
    {
        var prefix = q.Id + "/";
        foreach (var key in Job.Destinations.Keys.Concat(Job.Archived.Keys).Where(k => k.StartsWith(prefix)).Distinct().ToList())
        {
            Job.Destinations.Remove(key);
            Unarchive(key, bank, jobsRoot);
        }
        Save();
    }

    void Unarchive(string key, QuestionBank? bank, string? jobsRoot)
    {
        if (!Job.Archived.Remove(key, out var bankDir) || bank is null) return;
        if (jobsRoot is not null && IsBankItemUsedElsewhere(jobsRoot, bankDir, Dir)) return;
        if (QuestionBank.LoadItem(bankDir) is { } item) bank.Delete(item);
    }

    /// <summary>다른 작업의 ④ 목록이 이 문항함 문항을 쓰고 있는지.</summary>
    public static bool IsBankItemUsedElsewhere(string jobsRoot, string bankDir, string exceptJobDir)
    {
        if (!Directory.Exists(jobsRoot)) return false;
        var target = Path.GetFullPath(bankDir).TrimEnd('\\');
        foreach (var d in Directory.EnumerateDirectories(jobsRoot))
        {
            if (string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(exceptJobDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(d, AuthoringJob.FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var j = JsonStore.Load<AuthoringJob>(file);
                if (j.ExamList.Concat(j.PracticeList).Any(e => e.BankDir is { } b &&
                        string.Equals(Path.GetFullPath(b).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return false;
    }

    // ── 시험 만들기 ────────────────────────────────────────

    /// <summary>쓰는 문항의 결과를 자료 순서대로 모두 모아 시험을 만든다(명령 창 author용).</summary>
    public Exam BuildExam()
    {
        var exam = new Exam { Info = Job.Info };
        foreach (var q in Job.Questions.Where(q => q.Include))
            exam.Questions.AddRange(Results(q).Select(r => r.Item));
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
