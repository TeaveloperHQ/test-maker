using TestMaker.Core.Forms;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;

namespace TestMaker.Tests.Core;

public sealed class JsonStoreTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_json_").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Exam_round_trips_and_stays_human_readable()
    {
        var path = Path.Combine(dir, "exam.json");
        var exam = SampleExam.Create();
        JsonStore.Save(path, exam);

        var json = File.ReadAllText(path);
        Assert.Contains("물질의 상태 변화", json);                       // 한글이 \uXXXX로 바뀌지 않음
        Assert.Contains("\"type\": \"Essay\"", json);                  // enum은 이름으로

        var back = JsonStore.Load<Exam>(path);
        Assert.Equal(exam.Questions.Count, back.Questions.Count);
        Assert.Equal(exam.Questions[2].Box!.Lines, back.Questions[2].Box!.Lines);
        Assert.Equal(exam.TotalPoints, back.TotalPoints);
    }

    [Fact]
    public void Form_round_trips_and_keeps_choice_layouts()
    {
        var f = new ExamForm
        {
            Name = "우리학교",
            NumberFormat = "{0}) ",
            ChoiceLayouts = [new ChoiceLayout { Lines = 2, PerLine = [3, 2] }],
        };
        var path = Path.Combine(dir, ExamForm.FileName);
        JsonStore.Save(path, f);

        var back = JsonStore.Load<ExamForm>(path);
        Assert.Equal("우리학교", back.Name);
        Assert.Equal("{0}) ", back.NumberFormat);
        Assert.Equal([3, 2], back.ChoiceLayouts[0].PerLine);
    }

    /// <summary>저장소의 내장 기본 양식(forms/default)이 프로그램 옆에 복사되고, 기대한 구조를 가진다.</summary>
    [Fact]
    public void Built_in_default_form_is_shipped_and_structured()
    {
        var formDir = Path.Combine(AppContext.BaseDirectory, "forms", "default");
        var f = JsonStore.Load<ExamForm>(Path.Combine(formDir, ExamForm.FileName));
        Assert.True(File.Exists(Path.Combine(formDir, ExamForm.BaseFile)));
        Assert.Equal(FormKind.Structured, f.Kind);
        Assert.Equal([1, 2, 3, 5], f.ChoiceLayouts.Select(l => l.Lines));
        Assert.True(f.ChoiceLayouts.Single(l => l.Lines == 5).Auto);
        Assert.True(f.ViewBox?.AutoLabels);
        Assert.NotNull(f.PassageBox);
    }

    [Fact]
    public void Tokens_include_computed_values()
    {
        var exam = SampleExam.Create();
        exam.Info.Extra["담당"] = "김교사";
        var tokens = exam.Info.ToTokens(exam);
        Assert.Equal("과학", tokens["과목"]);
        Assert.Equal("5", tokens["문항수"]);
        Assert.Equal("22", tokens["총점"]);
        Assert.Equal("45분", tokens["시간"]);
        Assert.Equal("김교사", tokens["담당"]);
    }
}
