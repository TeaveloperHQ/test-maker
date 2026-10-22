using System.IO.Compression;
using TestMaker.Ai;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Hwp;

namespace TestMaker.Tests.Ai;

/// <summary>Claude 없이 도는 부분: 자료 넣기, 결과 검사, 시험 모으기, 지시문.</summary>
public sealed class AuthoringTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_job_").FullName;
    readonly Authoring job;

    public AuthoringTests() => job = Authoring.Open(new ClaudeCode("claude-not-used.exe"), Path.Combine(dir, "job"), HwpSource.Convert);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string MakeFile(string name, int bytes = 10)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    [Fact]
    public void Adds_copies_of_sources_without_touching_originals()
    {
        var src = MakeFile("문항.png");
        var a = job.AddSource(src);
        var b = job.AddSource(src);

        Assert.Equal("문항.png", a.Name);
        Assert.Equal("문항 (2).png", b.Name);                       // 같은 이름은 번호를 붙여 따로
        Assert.Equal([Path.Combine("sources", "문항.png")], a.ReadFiles);
        Assert.True(File.Exists(Path.Combine(job.Dir, "sources", "문항.png")));
        Assert.True(File.Exists(Path.Combine(job.Dir, AuthoringJob.FileName)));
        Assert.Throws<ArgumentException>(() => job.AddSource(MakeFile("메모.txt")));
    }

    [Fact]
    public void Named_jobs_are_listed_and_reopen_with_everything()
    {
        var root = Path.Combine(dir, "작업");
        var a = Authoring.Create(null, root, "2026 2학기 1차고사");
        var b = Authoring.Create(null, root, "2026 2학기 1차고사");          // 같은 이름 → 폴더에 번호
        Assert.EndsWith("2026 2학기 1차고사", a.Dir);
        Assert.EndsWith("2026 2학기 1차고사 (2)", b.Dir);

        a.Job.Info.Title = "2026학년도 2학기 1차 지필평가";
        a.Job.Destinations["q01/0"] = Destination.Exam;
        a.Job.Destinations["q01/1"] = Destination.Practice;
        a.Job.ExamList.Add(new ExamListEntry { Key = "bank:abc", BankDir = @"C:\문항함\시험용\abc" });
        a.Job.ExamList.Add(new ExamListEntry { Key = "job:q01/0" });
        a.Job.PracticeList.Add(new ExamListEntry { Key = "job:q01/1" });
        a.Save();
        File.SetLastWriteTime(Path.Combine(b.Dir, AuthoringJob.FileName), DateTime.Now.AddDays(-1));

        var list = Authoring.List(root);
        Assert.Equal(["2026 2학기 1차고사", "2026 2학기 1차고사"], list.Select(s => s.Name));
        Assert.Equal(a.Dir, list[0].Dir);                                     // 최근에 고친 것이 앞
        Assert.Equal(2, list[0].ExamQuestions);

        var back = Authoring.Open(null, a.Dir);
        Assert.Equal("2026 2학기 1차고사", back.Name);
        Assert.Equal("2026학년도 2학기 1차 지필평가", back.Job.Info.Title);
        Assert.Equal(Destination.Practice, back.Job.Destinations["q01/1"]);
        Assert.Equal(["bank:abc", "job:q01/0"], back.Job.ExamList.Select(e => e.Key));
        Assert.Equal(["job:q01/1"], back.Job.PracticeList.Select(e => e.Key));
        Assert.Equal(1, list[0].PracticeQuestions);
    }

    [Fact]
    public void Edit_renames_job_and_folder_and_duplicate_skips_made_exams()
    {
        var root = Path.Combine(dir, "작업");
        var a = Authoring.Create(null, root, "1차고사");
        a.AddSource(MakeFile("문항.png"));
        Directory.CreateDirectory(Path.Combine(a.Dir, AuthoringJob.ExamsDir, "2026-10-22 0900"));
        File.WriteAllText(Path.Combine(a.Dir, AuthoringJob.ExamsDir, "2026-10-22 0900", "시험.hwp"), "x");

        var renamed = Authoring.Edit(a.Dir, "2026 2학기 1차고사", grade: "3학년", subject: "과학");
        Assert.EndsWith("2026 2학기 1차고사", renamed);
        Assert.False(Directory.Exists(a.Dir));
        var back = Authoring.Open(null, renamed);
        Assert.Equal("2026 2학기 1차고사", back.Name);
        Assert.Equal("3학년", back.Job.Info.Grade);
        Assert.Equal(renamed, Authoring.Edit(renamed, "2026 2학기 1차고사"));      // 같은 이름이면 그대로

        var copy = Authoring.Duplicate(renamed, "2027 2학기 1차고사");
        Assert.Equal("2027 2학기 1차고사", Authoring.Open(null, copy).Name);
        Assert.True(File.Exists(Path.Combine(copy, "sources", "문항.png")));
        Assert.False(Directory.Exists(Path.Combine(copy, AuthoringJob.ExamsDir)));   // 만든 시험지는 복사하지 않음
    }

    /// <summary>③ 행선지: 시험지·예상문제로 보내면 문항함에 들어가고, 바꾸면 칸을 옮기고, 버리면 문항함에서도 빠진다.</summary>
    [Fact]
    public void Destination_files_into_bank_moves_and_discards()
    {
        var bank = new TestMaker.Core.Bank.QuestionBank(Path.Combine(dir, "문항함"));
        var root = Path.Combine(dir, "작업");
        var a = Authoring.Create(null, root, "1차고사");
        var q = new FoundQuestion { Id = "q01", Number = "7", Summary = "열평형", Variants = 2 };
        a.Job.Questions.Add(q);
        WriteItems(a, q, [Mc("가"), Mc("나")]);

        a.SetDestination(q, 0, Destination.Exam, bank, root);
        a.SetDestination(q, 1, Destination.Practice, bank, root);
        Assert.Equal(Destination.Exam, a.DestinationOf(q, 0));
        var items = bank.Load();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Category == TestMaker.Core.Bank.BankCategory.Exam && i.Title == "7번 변형 1 · 열평형");

        a.SetDestination(q, 0, Destination.Practice, bank, root);              // 바꾸면 칸을 옮긴다(새로 넣지 않음)
        Assert.Equal(2, bank.Load().Count(i => i.Category == TestMaker.Core.Bank.BankCategory.Practice));

        a.SetDestination(q, 1, null, bank, root);                               // 버리면 문항함에서도 빠진다
        Assert.Null(a.DestinationOf(q, 1));
        Assert.Single(bank.Load());

        // 다른 작업이 쓰고 있는 문항함 문항은 버려도 남긴다.
        var other = Authoring.Create(null, root, "예상문제 모음");
        other.Job.PracticeList.Add(new ExamListEntry { Key = "bank:x", BankDir = a.Job.Archived["q01/0"] });
        other.Save();
        a.SetDestination(q, 0, null, bank, root);
        Assert.Single(bank.Load());
        Assert.Empty(a.Job.Archived);
    }

    [Fact]
    public void Old_jobs_are_migrated_and_finding_skips_analyzed_sources()
    {
        var a = Authoring.Create(null, Path.Combine(dir, "작업"), "예전 작업");
        a.AddSource(MakeFile("문항.png"));
        a.Job.Questions.Add(new FoundQuestion { Id = "q01", Source = "문항.png" });
        a.Job.ExamList.Add(new ExamListEntry { Key = "job:q01/0" });
        a.Job.Archived["q01/0"] = "시험용";                                     // 예전에는 칸 이름을 적었다
        a.Save();

        var back = Authoring.Open(null, a.Dir);
        Assert.True(back.Job.Sources[0].Analyzed);                              // 이미 찾은 자료
        Assert.Equal(Destination.Exam, back.Job.Destinations["q01/0"]);         // ④에 있던 것 → 시험지
        Assert.Empty(back.Job.Archived);
        Assert.Empty(back.FindQuestionsAsync().Result);                         // 새 자료가 없으면 Claude 없이 끝
    }

    void WriteItems(Authoring a, FoundQuestion q, List<Question> items) =>
        JsonStore.Save(Path.Combine(a.Dir, q.ItemFile), new Exam { Questions = items });

    [Fact]
    public void Without_claude_only_finding_and_making_fail()
    {
        var a = Authoring.Create(null, Path.Combine(dir, "작업"), "클로드 없음");
        a.AddSource(MakeFile("문항.png"));                                   // 자료 넣기는 된다
        var e = Assert.Throws<AggregateException>(() => a.FindQuestionsAsync().Wait());
        Assert.Contains("Claude Code", e.InnerException!.Message);
    }

    [Fact]
    public void Check_reports_count_answer_and_missing_figure()
    {
        var q = new FoundQuestion { Id = "q01", Number = "7", Variants = 2 };
        Assert.Contains(job.Check(q), p => p.Contains("없습니다"));     // 결과 파일 없음

        WriteItems(q, [
            new Question { Stem = "가", Points = 4, Choices = ["1", "2", "3", "4", "5"], Answer = [6] },
        ]);
        var problems = job.Check(q);
        Assert.Contains(problems, p => p.Contains("2개여야"));
        Assert.Contains(problems, p => p.Contains("범위"));

        WriteItems(q, [
            new Question { Stem = "가", Points = 4, Choices = ["1", "2"], Answer = [1], Image = new QuestionImage { Path = "figures/없음.png" } },
            new Question { Stem = "나", Points = 4, Choices = ["1", "2"], Answer = [2] },
        ]);
        Assert.Equal(["1번: 그림 파일이 없습니다(figures/없음.png)."], job.Check(q));
    }

    [Fact]
    public void Exam_follows_question_order_and_skips_excluded()
    {
        var q1 = new FoundQuestion { Id = "q01", Variants = 2 };
        var q2 = new FoundQuestion { Id = "q02", Variants = 1 };
        var q3 = new FoundQuestion { Id = "q03", Variants = 1, Include = false };
        job.Job.Questions.AddRange([q1, q2, q3]);
        WriteItems(q1, [Mc("1-가"), Mc("1-나")]);
        WriteItems(q2, [Mc("2-가")]);
        WriteItems(q3, [Mc("3-가")]);

        var exam = job.BuildExam(new HashSet<(string, int)> { ("q01", 0) });
        Assert.Equal(["1-나", "2-가"], exam.Questions.Select(q => q.Stem));
    }

    [Fact]
    public void Variant_prompt_carries_the_teachers_figure_rule()
    {
        job.Job.Sources.Add(new SourceFile { Name = "a.hwp", ReadFiles = ["sources/a/a.pdf"], Figures = ["sources/a/그림1.png"] });
        var q = new FoundQuestion { Id = "q03", Source = "a.hwp", Number = "5", Summary = "열평형", Variants = 2, KeepOriginal = true };
        var prompt = Prompts.MakeVariants(job.Job, q);

        Assert.Contains("원본 그림과 최대한 똑같이", prompt);
        Assert.Contains("BOM", prompt);
        Assert.Contains("items/q03.json", prompt);
        Assert.Contains("문항 3개", prompt);                // 원문항 + 변형 2
        Assert.Contains("sources/a/그림1.png", prompt);
    }

    [Fact]
    public void Extracts_viewable_images_from_hwpx()
    {
        var hwpx = Path.Combine(dir, "a.hwpx");
        using (var zip = ZipFile.Open(hwpx, ZipArchiveMode.Create))
        {
            void Add(string name, int size) { using var s = zip.CreateEntry(name).Open(); s.Write(new byte[size]); }
            Add("BinData/image1.png", 4000);
            Add("BinData/image2.emf", 4000);   // 볼 수 없는 형식
            Add("BinData/image3.jpg", 100);    // 장식용 작은 그림
            Add("Contents/section0.xml", 10);
        }
        var images = HwpSource.ExtractImages(hwpx, Path.Combine(dir, "out"));
        Assert.Equal(["그림1.png"], images.Select(Path.GetFileName));
    }

    /// <summary>한글 파일 자료: 한글로 PDF를 만들어 Claude가 읽을 파일로 둔다.</summary>
    [TestMaker.Tests.Hwp.HwpFact]
    public void Hwp_source_becomes_pdf()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "school_exam.hwpx");
        var src = job.AddSource(fixture);
        var pdf = Assert.Single(src.ReadFiles);
        Assert.EndsWith(".pdf", pdf);
        Assert.True(new FileInfo(Path.Combine(job.Dir, pdf)).Length > 1000);
    }

    void WriteItems(FoundQuestion q, List<Question> items) =>
        JsonStore.Save(Path.Combine(job.Dir, q.ItemFile), new Exam { Questions = items });

    static Question Mc(string stem) => new() { Stem = stem, Points = 4, Choices = ["1", "2"], Answer = [1] };
}
