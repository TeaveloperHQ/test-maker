using TestMaker.Core.Models;
using TestMaker.Core.Templates;

namespace TestMaker.Hwp;

/// <summary>정답표 + 해설. 학교 양식과 무관하게 A4 한 단으로 그린다(교사용).</summary>
public sealed class AnswerSheetRenderer(HwpSession session)
{
    readonly HwpWriter w = new(session);

    public void Render(Exam exam, TextStyle baseStyle)
    {
        session.NewDocument();
        w.PageSetup(new PageProfile());
        var body = new TextStyle { FontHangul = baseStyle.FontHangul, FontLatin = baseStyle.FontLatin, SizePt = 10 };

        w.ParaShape(new ParaStyle { Align = ParaAlign.Center });
        w.CharShape(body.With(bold: true).Sized(16));
        w.Text($"{exam.Info.Title} {exam.Info.Subject} 정답 및 해설".Trim());
        w.BreakPara();
        w.CharShape(body);
        w.Text(string.Join("  ", new[] { exam.Info.Grade, $"{exam.Questions.Count}문항", $"총 {Format.Points(exam.TotalPoints)}점" }.Where(s => s.Length > 0)));
        w.BreakPara();
        w.BreakPara();

        // 본문 폭 180mm에서 표 바깥 여백을 뺀 만큼
        double[] widths = [14, 20, 120, 20];
        w.BeginTable(exam.Questions.Count + 2, widths);
        Row(["번호", "유형", "정답", "배점"], bold: true, first: true);
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var q = exam.Questions[i];
            Row([(i + 1).ToString(), TypeName(q.Type), AnswerOf(q), Format.Points(q.Points)], bold: false, first: false);
        }
        Row(["", "", "합계", Format.Points(exam.TotalPoints)], bold: true, first: false);
        w.ExitTable();

        var explained = exam.Questions.Select((q, i) => (q, n: i + 1))
            .Where(x => !string.IsNullOrWhiteSpace(x.q.Explanation)).ToList();
        if (explained.Count == 0) return;

        w.BreakPara(); w.BreakPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left });
        w.CharShape(body.With(bold: true).Sized(12));
        w.Text("해설");
        foreach (var (q, n) in explained)
        {
            w.BreakPara();
            w.ParaShape(new ParaStyle { Align = ParaAlign.Justify, LeftMarginPt = 0, IndentPt = -14, SpaceBeforePt = 4 });
            w.CharShape(body.With(bold: true));
            w.Text($"{n}. ");
            w.Rich(q.Explanation!, body);
        }

        void Row(string[] cells, bool bold, bool first)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                if (!(first && c == 0)) w.NextCell();
                w.ParaShape(new ParaStyle { Align = c == 2 && !bold ? ParaAlign.Left : ParaAlign.Center, LineSpacingPercent = 130 });
                if (c == 2 && !bold) w.Rich(cells[c], body);
                else { w.CharShape(body.With(bold: bold)); w.Text(cells[c]); }
            }
        }
    }

    static string TypeName(QuestionType t) => t switch
    {
        QuestionType.MultipleChoice => "선택형",
        QuestionType.ShortAnswer => "단답형",
        _ => "서술형",
    };

    static string AnswerOf(Question q) => q.Type == QuestionType.MultipleChoice
        ? string.Join(", ", q.Answer.Select(Format.ChoiceMark))
        : q.AnswerText ?? "";
}

static class TextStyleExtensions
{
    public static TextStyle Sized(this TextStyle s, double pt)
    {
        var c = s.With();
        c.SizePt = pt;
        return c;
    }
}
