using TestMaker.Core.Models;
using TestMaker.Core.Text;

namespace TestMaker.Core.Validation;

public enum Severity { Warning, Error }

public sealed record ValidationIssue(Severity Severity, int? QuestionNumber, string Message)
{
    public override string ToString() =>
        $"[{(Severity == Severity.Error ? "오류" : "주의")}] {(QuestionNumber is int n ? $"{n}번: " : "")}{Message}";
}

/// <summary>출력 전에 시험지의 흔한 실수를 잡는다. 오류가 있으면 출력하지 않는다.</summary>
public static class ExamValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(Exam exam, double? expectedTotal = 100)
    {
        var issues = new List<ValidationIssue>();
        if (exam.Questions.Count == 0)
            issues.Add(new(Severity.Error, null, "문항이 없습니다."));

        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var q = exam.Questions[i];
            int n = i + 1;
            void Err(string m) => issues.Add(new(Severity.Error, n, m));
            void Warn(string m) => issues.Add(new(Severity.Warning, n, m));

            if (string.IsNullOrWhiteSpace(q.Stem)) Err("발문이 비어 있습니다.");
            if (q.Points <= 0) Err("배점이 0 이하입니다.");

            foreach (var text in AllText(q))
            {
                try { InlineMarkup.Parse(text); }
                catch (FormatException e) { Err(e.Message); }
            }

            if (q.Type == QuestionType.MultipleChoice)
            {
                if (q.Choices.Count < 2) Err("선택지가 2개 미만입니다.");
                if (q.Answer.Count == 0) Err("정답이 지정되지 않았습니다.");
                foreach (var a in q.Answer)
                    if (a < 1 || a > q.Choices.Count) Err($"정답 {a}번은 선택지 범위(1~{q.Choices.Count}) 밖입니다.");
                if (q.Choices.Distinct().Count() != q.Choices.Count) Warn("같은 내용의 선택지가 있습니다.");
                if (q.Choices.Any(c => c.TrimStart().Length > 0 && c.TrimStart()[0] is >= '①' and <= '⑳'))
                    Warn("선택지에 ①② 기호가 들어 있습니다. 기호는 자동으로 붙습니다.");
            }
            else
            {
                if (q.Choices.Count > 0) Warn("선택형이 아닌데 선택지가 있습니다. 출력되지 않습니다.");
                if (string.IsNullOrWhiteSpace(q.AnswerText)) Warn("모범 답안이 없습니다.");
            }

            if (q.Image is { } img && string.IsNullOrWhiteSpace(img.Path)) Err("그림 경로가 비어 있습니다.");
        }

        if (expectedTotal is double total && exam.Questions.Count > 0 && Math.Abs(exam.TotalPoints - total) > 1e-9)
            issues.Add(new(Severity.Warning, null, $"총점이 {Format.Points(exam.TotalPoints)}점입니다({Format.Points(total)}점이 아님)."));

        return issues;
    }

    static IEnumerable<string> AllText(Question q)
    {
        yield return q.Stem;
        if (q.StemAfter is not null) yield return q.StemAfter;
        if (q.Group is not null) yield return q.Group.Text;
        foreach (var c in q.Choices) yield return c;
        if (q.Box is not null) foreach (var l in q.Box.Lines) yield return l;
        if (q.Passage is not null) foreach (var l in q.Passage.Lines) yield return l;
    }
}
