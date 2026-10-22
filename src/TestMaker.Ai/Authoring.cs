using TestMaker.Core.Bank;
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
        log?.Invoke($"자료 {targets.Count}개를 읽고 문항을 찾는 중…");
        var r = await claudeCode.RunAsync(Dir, Prompts.FindQuestions(targets), progress: log, ct: ct);
        if (r.IsError) throw new InvalidOperationException($"문항을 찾지 못했습니다: {r.Text}");
        if (!File.Exists(found)) throw new InvalidOperationException($"문항 목록 파일이 만들어지지 않았습니다. Claude 답: {r.Text}");

        var list = JsonStore.Load<FoundList>(found);
        int next = Job.Questions.Select(q => int.TryParse(q.Id.TrimStart('q'), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var added = list.Questions.Select((e, i) => new FoundQuestion
        {
            Id = $"q{next + i:00}",
            Source = targets.FirstOrDefault(s => s.Name == e.Source)?.Name ?? (targets.Count == 1 ? targets[0].Name : e.Source),
            Number = e.Number,
            Summary = e.Summary,
            Type = e.Type,
            HasFigure = e.HasFigure,
        }).ToList();
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

    // ── 행선지(③): 시험지 / 예상문제 / 버림 ──────────────────

    public static string Key(FoundQuestion q, int index) => $"{q.Id}/{index}";

    /// <summary>"원문항" 또는 "변형 n".</summary>
    public static string ResultLabel(FoundQuestion q, int index) =>
        q.KeepOriginal && index == 0 ? "원문항" : $"변형 {(q.KeepOriginal ? index : index + 1)}";

    /// <summary>문항함·④ 목록에 보일 이름. 예: "07번 변형 1 · 열평형 온도 구하기"</summary>
    public static string ResultTitle(FoundQuestion q, int index) => $"{q.Number}번 {ResultLabel(q, index)} · {q.Summary}";

    public string? DestinationOf(FoundQuestion q, int index) => Job.Destinations.GetValueOrDefault(Key(q, index));

    /// <summary>
    /// 결과 하나의 행선지를 정한다(null = 버림). 시험지·예상문제로 보내면 문항함(시험용·예상문제)에도 자동으로 들어가고,
    /// 바꾸면 칸을 옮기고, 버리면 문항함에서도 뺀다(다른 작업이 그 문항을 쓰고 있으면 남긴다).
    /// </summary>
    public void SetDestination(FoundQuestion q, int index, string? dest, QuestionBank? bank = null, string? jobsRoot = null)
    {
        var key = Key(q, index);
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
                else if (LoadItems(q) is var items && index < items.Count)
                {
                    Job.Archived[key] = bank.Add(items[index], Dir, cat, ResultTitle(q, index), source: Name).Dir;
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
