using System.Xml.Linq;
using TestMaker.Core.Forms;
using TestMaker.Core.Models;
using TestMaker.Hwp;

namespace TestMaker.Tests.Hwp;

[Collection("Hwp")]
public sealed class HwpIntegrationTests : IDisposable
{
    static readonly XNamespace Hp = Hx.Hp;
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_it_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>내장 기본 양식: 번호·ㄱ.ㄴ.ㄷ.은 스타일(개요 번호)이 붙이므로 본문 글자에는 없다.</summary>
    [HwpFact]
    public void Default_form_renders_all_questions_with_styles()
    {
        var exam = SampleExam.Create();
        var output = Path.Combine(dir, "exam.hwpx");
        var files = FormRendering.Render(exam, FormRendering.DefaultFormDir, null, output, original: false, pdf: false);
        Assert.Single(files);

        var text = AllText(output);
        Assert.Contains(exam.Info.Title, text);              // 머리 부분 {{제목}}
        Assert.Contains("다음 중 물질의 상태 변화", text);
        Assert.DoesNotContain("1. 다음 중", text);          // 번호는 스타일이 붙임
        Assert.Contains("기체의 압력은", text);
        Assert.DoesNotContain("ㄱ. 기체의 압력은", text);   // <보기> ㄱ.도 스타일이 붙임
        Assert.DoesNotContain("#EQ", text);
        Assert.Equal(8, Equations(output).Count);
    }

    /// <summary>무질서형 학교 시험지(.hwp)에서 학습 → 학생용·원안지 출력까지, 한글로 끝까지.</summary>
    [HwpFact]
    public void Learn_and_render_unstructured_school_exam()
    {
        var source = Path.Combine(dir, "school.hwp"); // .hwp → .hwpx 변환까지 시험
        using (var session = new HwpSession()) SchoolExamFixture.Create(session, source);
        MaybeUpdateCommittedFixture(source);

        var formDir = Path.Combine(dir, "form");
        var learned = FormRendering.Learn(source, [], formDir, "가상학교");
        Assert.Equal(FormKind.Unstructured, learned.Form.Kind);

        var output = Path.Combine(dir, "exam.hwpx");
        var files = FormRendering.Render(SampleExam.Create(), formDir, null, output, original: true, pdf: false);
        Assert.Equal(2, files.Count);

        var text = AllText(output);
        Assert.Contains(SchoolExamFixture.Title, text);
        Assert.Contains("1) 다음 중 물질의 상태 변화", text);
        Assert.Contains("(4점)", text);
        Assert.DoesNotContain("#EQ", text);   // 수식 자리표시가 모두 수식으로 바뀜
        Assert.Equal(8, Equations(output).Count);
        Assert.True(File.Exists(Path.Combine(dir, "exam_원안지.hwpx")));
    }

    /// <summary>
    /// 실제 학교 양식(완성형)으로 학습·출력. 학교 파일은 저장소에 넣지 않으므로 경로를 환경 변수로 준다.
    /// TESTMAKER_SCHOOL_FORM=양식.hwp [TESTMAKER_SCHOOL_EXAM=기출.hwp]
    /// </summary>
    [HwpFact]
    public void Learn_and_render_structured_school_form()
    {
        var formFile = Environment.GetEnvironmentVariable("TESTMAKER_SCHOOL_FORM");
        if (formFile is null || !File.Exists(formFile)) return; // 학교 파일이 없는 환경에서는 할 일 없음
        var examFile = Environment.GetEnvironmentVariable("TESTMAKER_SCHOOL_EXAM");
        var extras = examFile is not null && File.Exists(examFile) ? new[] { examFile } : [];

        var formDir = Path.Combine(dir, "school");
        var learned = FormRendering.Learn(formFile, extras, formDir);
        Assert.Equal(FormKind.Structured, learned.Form.Kind);
        Assert.Contains(learned.Form.ChoiceLayouts, l => l.Auto);

        var output = Path.Combine(dir, "school_exam.hwpx");
        FormRendering.Render(SampleExam.Create(), formDir, null, output, original: false, pdf: false);
        var text = AllText(output);
        Assert.Contains("다음 중 물질의 상태 변화", text);
        Assert.DoesNotContain("1. 다음 중", text);   // 번호는 스타일(개요 번호)이 붙이므로 글자로 넣지 않음
        Assert.DoesNotContain("#EQ", text);
    }

    static string AllText(string hwpx)
    {
        var section = Hx.Load(hwpx, Hx.SectionPart);
        return string.Join("\n", section.Descendants(Hp + "p").Select(Hx.Text));
    }

    static List<string> Equations(string hwpx) =>
        Hx.Load(hwpx, Hx.SectionPart).Descendants(Hp + "script").Select(e => e.Value).ToList();

    /// <summary>TESTMAKER_WRITE_FIXTURE=1 이면 단위 테스트용 고정 파일(Fixtures/school_exam.hwpx)을 새로 만든다.</summary>
    static void MaybeUpdateCommittedFixture(string sourceHwp)
    {
        if (Environment.GetEnvironmentVariable("TESTMAKER_WRITE_FIXTURE") != "1") return;
        var target = Path.Combine(FindRepoRoot(), "tests", "TestMaker.Tests", "Fixtures", "school_exam.hwpx");
        using var session = new HwpSession();
        session.Open(sourceHwp);
        session.SaveAs(target, HwpFormat.Hwpx);
    }

    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TestMaker.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다.");
    }
}
