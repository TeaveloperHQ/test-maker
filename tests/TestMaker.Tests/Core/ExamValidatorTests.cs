using TestMaker.Core.Models;
using TestMaker.Core.Validation;

namespace TestMaker.Tests.Core;

public sealed class ExamValidatorTests
{
    static Question Mc(params int[] answer) => new()
    {
        Stem = "옳은 것은?",
        Points = 100,
        Choices = ["가", "나", "다", "라", "마"],
        Answer = [.. answer],
    };

    [Fact]
    public void Sample_exam_has_no_errors()
    {
        var issues = ExamValidator.Validate(SampleExam.Create(), expectedTotal: null);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Answer_outside_choices_is_error()
    {
        var issues = ExamValidator.Validate(new Exam { Questions = [Mc(6)] });
        var e = Assert.Single(issues, i => i.Severity == Severity.Error);
        Assert.Equal(1, e.QuestionNumber);
        Assert.Contains("범위", e.Message);
    }

    [Fact]
    public void Missing_answer_and_too_few_choices_are_errors()
    {
        var q = new Question { Stem = "?", Points = 100, Choices = ["가"] };
        var issues = ExamValidator.Validate(new Exam { Questions = [q] });
        Assert.Equal(2, issues.Count(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Broken_markup_is_reported_with_question_number()
    {
        var q = Mc(1);
        q.Choices[2] = "$x^2";
        var issues = ExamValidator.Validate(new Exam { Questions = [Mc(1), q] });
        var e = Assert.Single(issues, i => i.Severity == Severity.Error);
        Assert.Equal(2, e.QuestionNumber);
    }

    [Fact]
    public void Total_not_100_is_only_a_warning()
    {
        var q = Mc(1);
        q.Points = 3;
        var issues = ExamValidator.Validate(new Exam { Questions = [q] });
        var w = Assert.Single(issues);
        Assert.Equal(Severity.Warning, w.Severity);
    }

    [Fact]
    public void Choice_mark_in_text_is_warned()
    {
        var q = Mc(1);
        q.Choices[0] = "① 가";
        var issues = ExamValidator.Validate(new Exam { Questions = [q] });
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("기호"));
    }
}
