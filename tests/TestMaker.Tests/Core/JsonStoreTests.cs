using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Templates;

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
    public void Template_round_trips_through_folder_load()
    {
        var t = ExamTemplate.Default();
        t.Name = "우리학교";
        t.NumberFormat = "{0}) ";
        t.Choice.SizePt = 9.5;
        JsonStore.Save(Path.Combine(dir, ExamTemplate.FileName), t);

        var back = ExamTemplate.Load(dir, out var templateDir);
        Assert.Equal(Path.GetFullPath(dir), templateDir);
        Assert.Equal("우리학교", back.Name);
        Assert.Equal("{0}) ", back.NumberFormat);
        Assert.Equal(9.5, back.Choice.SizePt);
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
