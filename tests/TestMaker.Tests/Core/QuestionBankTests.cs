using TestMaker.Core.Bank;
using TestMaker.Core.Models;

namespace TestMaker.Tests.Core;

public sealed class QuestionBankTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("testmaker_bank_").FullName;
    readonly QuestionBank bank;

    public QuestionBankTests() => bank = new QuestionBank(Path.Combine(dir, "문항함"));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    Question WithFigure()
    {
        var job = Path.Combine(dir, "job");
        Directory.CreateDirectory(Path.Combine(job, "figures"));
        File.WriteAllBytes(Path.Combine(job, "figures", "q01_변형1.png"), [1, 2, 3]);
        return new Question { Stem = "열평형", Points = 4, Choices = ["가", "나"], Answer = [1],
                              Image = new QuestionImage { Path = "figures/q01_변형1.png", WidthMm = 70 } };
    }

    [Fact]
    public void Stored_question_keeps_its_own_copy_of_the_figure()
    {
        var item = bank.Add(WithFigure(), Path.Combine(dir, "job"), BankCategory.Exam, "07번 변형 1");
        Directory.Delete(Path.Combine(dir, "job"), recursive: true);          // 작업 폴더를 지워도

        var loaded = Assert.Single(bank.Load());
        Assert.Equal("07번 변형 1", loaded.Title);
        Assert.Equal(BankCategory.Exam, loaded.Category);
        Assert.Equal("그림.png", loaded.Question.Image!.Path);
        Assert.True(File.Exists(Path.Combine(loaded.Dir, "그림.png")));       // 그림은 남는다
        Assert.EndsWith(Path.Combine("시험용", item.Id), loaded.Dir);
    }

    [Fact]
    public void Moves_between_exam_and_practice()
    {
        var item = bank.Add(WithFigure(), Path.Combine(dir, "job"), BankCategory.Exam, "가");
        bank.Move(item, BankCategory.Practice);

        var loaded = Assert.Single(bank.Load());
        Assert.Equal(BankCategory.Practice, loaded.Category);
        Assert.Contains("예상문제", loaded.Dir);
        Assert.True(File.Exists(Path.Combine(loaded.Dir, "그림.png")));
    }

    [Fact]
    public void Compose_keeps_order_and_makes_figure_paths_absolute()
    {
        var a = bank.Add(WithFigure(), Path.Combine(dir, "job"), BankCategory.Exam, "가");
        var b = bank.Add(new Question { Stem = "둘째", Points = 3, Choices = ["1", "2"], Answer = [2] }, null, BankCategory.Practice, "나");

        var exam = QuestionBank.Compose([b, a], new ExamInfo { Title = "기말" });
        Assert.Equal(["둘째", "열평형"], exam.Questions.Select(q => q.Stem));
        Assert.Equal(Path.Combine(a.Dir, "그림.png"), exam.Questions[1].Image!.Path);
        Assert.Equal("그림.png", a.Question.Image!.Path);                     // 보관본은 그대로
    }

    [Fact]
    public void Delete_removes_the_folder()
    {
        var item = bank.Add(WithFigure(), Path.Combine(dir, "job"), BankCategory.Exam, "가");
        bank.Delete(item);
        Assert.Empty(bank.Load());
    }
}
