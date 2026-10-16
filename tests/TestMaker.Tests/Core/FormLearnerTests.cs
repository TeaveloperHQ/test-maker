using System.IO.Compression;
using System.Xml.Linq;
using TestMaker.Core.Forms;
using TestMaker.Core.Models;

namespace TestMaker.Tests.Core;

/// <summary>
/// 한글 없이 도는 양식 학습·조립 테스트. Fixtures/school_exam.hwpx 는 SchoolExamFixture로 만든 무질서형 시험지
/// (번호 "1)"를 글자로 입력, 선택지 한 줄 5개, 유의사항에 "1. 2." 번호).
/// 다시 만들려면: TESTMAKER_WRITE_FIXTURE=1 dotnet test --filter Learn_and_render_unstructured
/// </summary>
public sealed class FormLearnerTests : IDisposable
{
    static readonly XNamespace Hp = Hx.Hp;
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "school_exam.hwpx");
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_form_").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // ── 무질서형 번호열 찾기 ──────────────────────────────────

    [Fact]
    public void Finds_typed_question_numbers_in_order()
    {
        Assert.Equal([1, 3, 5], FormLearner.FindQuestionSequence(["제목", "1. 첫 문항", "① 가", "2. 둘째", "① 가", "3) 셋째"]));
    }

    [Fact]
    public void Notice_list_is_not_mistaken_for_questions()
    {
        // 유의사항 1. 2. 와 문항 1. 2. 의 길이가 같으면 선택지가 따라오는 쪽(문항)
        Assert.Equal([2, 4], FormLearner.FindQuestionSequence(["1. 이름을 쓰시오.", "2. 시간은 45분.", "1. 문항", "① 가", "2. 문항", "① 가"]));
    }

    [Fact]
    public void Bare_number_paragraph_is_ignored()
    {
        Assert.Equal([1, 2], FormLearner.FindQuestionSequence(["1.", "1. 문항", "2. 문항"]));
    }

    // ── 선택지 줄 나누기 ────────────────────────────────────

    [Fact]
    public void Counts_typed_choices_in_a_line()
    {
        Assert.Equal(3, FormLearner.ChoiceCount("① 가    ② 나\t③ 다", autoFirst: false));
    }

    [Fact]
    public void Auto_numbered_first_choice_counts_as_an_item()
    {
        // 학교 양식: 첫 ①은 개요 번호, 나머지는 직접 입력
        Assert.Equal(3, FormLearner.ChoiceCount("ㄱ, ㄴ\t\t\t② ㄴ, ㄷ\t\t③ ㄷ, ㄹ", autoFirst: true));
    }

    // ── 학습 ───────────────────────────────────────────────

    [Fact]
    public void Learns_unstructured_school_exam()
    {
        var r = FormLearner.Learn(Fixture, [], "가상학교");
        var f = r.Form;

        Assert.Equal(FormKind.Unstructured, f.Kind);
        Assert.Equal(3, r.QuestionCount);
        Assert.Equal("{0}) ", f.NumberFormat);
        Assert.Equal("({0}점)", f.PointsFormat);
        Assert.Equal(2, f.SpacerLines);
        Assert.Equal(90, f.ColumnWidthMm, 0.5);

        var oneLine = Assert.Single(f.ChoiceLayouts, l => l.Lines == 1);
        Assert.Equal([5], oneLine.PerLine);
        // 자동번호 스타일이 없는 양식: 5행도 직접 입력
        Assert.Contains(f.ChoiceLayouts, l => l.Lines == 5 && !l.Auto);
        Assert.Contains(f.Notes, n => n.Contains("5행"));
    }

    [Fact]
    public void Saved_form_keeps_preamble_and_marker_and_tail()
    {
        FormLearner.Learn(Fixture, [], "가상학교").Save(dir);

        var text = AllText(Path.Combine(dir, ExamForm.BaseFile));
        Assert.Contains("2026학년도 1학기 기말고사", text);
        Assert.Contains("2. 시험 시간은 45분입니다.", text);
        Assert.Contains(ExamForm.Marker, text);
        Assert.Contains("- 수고하셨습니다 -", text);  // 꼬리(맺음말)도 남는다
        Assert.DoesNotContain("발문입니다", text);
        Assert.True(File.Exists(Path.Combine(dir, ExamForm.FileName)));
    }

    // ── 조립 ───────────────────────────────────────────────

    [Fact]
    public void Assembles_exam_with_school_numbering_and_marks_answers_in_original()
    {
        FormLearner.Learn(Fixture, [], "가상학교").Save(dir);
        var form = Serialization(dir);
        var exam = SampleExam.Create();

        var student = Path.Combine(dir, "student.hwpx");
        var original = Path.Combine(dir, "original.hwpx");
        var ph = FormAssembler.Assemble(exam, form, dir, null, student, original: false);
        FormAssembler.Assemble(exam, form, dir, null, original, original: true);

        var text = AllText(student);
        Assert.Contains("1) 다음 중 물질의 상태 변화", text);   // 무질서형: 번호를 학교 형식으로 직접 입력
        Assert.Contains("(4점)", text);                          // 학교 배점 형식
        Assert.Contains("③ 드라이아이스가", text);
        Assert.Contains("5) 여름철", text);
        Assert.Contains("- 수고하셨습니다 -", text);
        Assert.DoesNotContain(ExamForm.Marker, text);

        // 2번 문항 수식 8개(발문 3 + 선택지 5) + 해설은 시험지에 없음 → 자리표시 8개
        Assert.Equal(8, ph.Count(p => p.Kind == PlaceholderKind.Equation));
        Assert.All(ph, p => Assert.Contains(p.Key, text));

        // 원안지에만 형광펜, 선택형 문항 수만큼
        Assert.Equal(0, Count(student, "markpenBegin"));
        Assert.Equal(exam.Questions.Count(q => q.Type == QuestionType.MultipleChoice), Count(original, "markpenBegin"));
    }

    /// <summary>내장 표준 양식으로 서술형(조건·소문항) 조립: 서술형 번호는 스타일이, 소문항 번호는 "2-1." 형식으로.</summary>
    [Fact]
    public void Assembles_essay_section_with_standard_form()
    {
        var formDir = Path.Combine(AppContext.BaseDirectory, "forms", "default");
        var form = Serialization(formDir);
        var exam = new Exam
        {
            Info = new ExamInfo { Title = "시험", Subject = "과학" },
            Questions =
            [
                new Question { Stem = "옳은 것은?", Points = 4, Choices = ["가", "나", "다", "라", "마"], Answer = [1] },
                new Question { Type = QuestionType.Essay, Stem = "까닭을 서술하시오.", Points = 3,
                    Conditions = new BoxContent { Title = "<조 건>", Lines = ["∘ 한 문장으로 쓸 것"] } },
                new Question { Type = QuestionType.Essay, Stem = "물음에 답하시오.",
                    SubQuestions = [new SubQuestion { Text = "첫째 물음", Points = 2 }, new SubQuestion { Text = "둘째 물음", Points = 3 }] },
            ],
        };
        var output = Path.Combine(dir, "essay.hwpx");
        FormAssembler.Assemble(exam, form, formDir, null, output, original: false);

        var text = AllText(output);
        Assert.Contains("서술형 문항", text);              // 구역 머리
        Assert.Contains("까닭을 서술하시오. [3점]", text);
        Assert.DoesNotContain("서술형 1>", text);          // 번호는 스타일(문단번호)이 붙임
        Assert.Contains("∘ 한 문장으로 쓸 것", text);      // <조 건> 상자 줄
        Assert.Contains("물음에 답하시오. [총 5점]", text);
        Assert.Contains("2-1. 첫째 물음 [2점]", text);
        Assert.Contains("2-2. 둘째 물음 [3점]", text);
    }

    static ExamForm Serialization(string formDir) =>
        TestMaker.Core.Serialization.JsonStore.Load<ExamForm>(Path.Combine(formDir, ExamForm.FileName));

    static string AllText(string hwpx)
    {
        var section = Hx.Load(hwpx, Hx.SectionPart);
        return string.Join("\n", section.Descendants(Hp + "p").Select(Hx.Text));
    }

    static int Count(string hwpx, string element)
    {
        var section = Hx.Load(hwpx, Hx.SectionPart);
        return section.Descendants(Hp + element).Count();
    }
}
