using TestMaker.Core.Templates;
using TestMaker.Hwp;

namespace TestMaker.Tests.Hwp;

/// <summary>
/// 기본 양식과 일부러 다르게 만든 "어느 학교 시험지". 양식 학습이 무엇을 배워야 하는지의 정답지 역할.
/// 유의사항에 "1. 2." 번호를 넣어 문항 번호와 헷갈리게 했다.
/// </summary>
public static class SchoolExamFixture
{
    public static readonly TextStyle Number = new() { FontHangul = "맑은 고딕", FontLatin = "맑은 고딕", SizePt = 11, Bold = true };
    public static readonly TextStyle Stem = new() { FontHangul = "맑은 고딕", FontLatin = "맑은 고딕", SizePt = 11 };
    public static readonly TextStyle ChoiceText = new() { FontHangul = "맑은 고딕", FontLatin = "맑은 고딕", SizePt = 10 };
    public static readonly ParaStyle QuestionPara = new() { Align = ParaAlign.Justify, IndentPt = -15, LineSpacingPercent = 170 };
    public static readonly ParaStyle ChoicePara = new() { Align = ParaAlign.Left, LeftMarginPt = 12, LineSpacingPercent = 170 };
    public static readonly PageProfile Page = new() { MarginLeftMm = 12, MarginRightMm = 12, MarginTopMm = 15, MarginBottomMm = 15, HeaderMm = 10, FooterMm = 10 };
    public static readonly ColumnProfile Columns = new() { Count = 2, GapMm = 6, Line = false };
    public const string Title = "2026학년도 1학기 기말고사";
    public const string Closing = "- 수고하셨습니다 -";

    public static void Create(HwpSession session, string path)
    {
        var w = new HwpWriter(session);
        session.NewDocument();
        w.AssumeEmptyParagraph();
        w.PageSetup(Page);

        var titleStyle = new TextStyle { FontHangul = "맑은 고딕", FontLatin = "맑은 고딕", SizePt = 18, Bold = true };
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center });
        w.CharShape(titleStyle);
        w.Text(Title);
        w.BreakPara();
        w.CharShape(Stem.With(bold: true));
        w.Text("과학 (1학년)");
        w.BreakPara();

        string[] info = ["학년", "", "반", "", "번호", "", "이름", ""];
        w.BeginTable(1, [14, 14, 14, 14, 14, 14, 14, 40]);
        for (int i = 0; i < info.Length; i++)
        {
            if (i > 0) w.NextCell();
            w.CharShape(ChoiceText);
            w.Text(info[i]);
        }
        w.ExitTable();

        var note = new TextStyle { FontHangul = "맑은 고딕", FontLatin = "맑은 고딕", SizePt = 9 };
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left });
        w.CharShape(note);
        w.Text("1. 답안지에 학년, 반, 번호, 이름을 정확히 쓰시오.");
        w.BreakPara();
        w.Text("2. 시험 시간은 45분입니다.");
        w.BreakPara();

        w.Columns(Columns);

        for (int q = 1; q <= 3; q++)
        {
            if (q > 1) { w.BreakPara(); w.BreakPara(); w.BreakPara(); }
            w.ParaShape(QuestionPara);
            w.CharShape(Number);
            w.Text($"{q}) ");
            w.CharShape(Stem);
            w.Text($"{q}번 문항의 발문입니다. 옳은 것을 고르시오. ({q + 2}점)");

            if (q == 2)
            {
                w.BreakPara();
                w.ParaShape(new ParaStyle { Align = ParaAlign.Center });
                w.BeginTable(1, [80]);
                w.ParaShape(new ParaStyle { Align = ParaAlign.Left, LineSpacingPercent = 140 });
                w.CharShape(ChoiceText);
                w.Text("ㄱ. 첫째 진술");
                w.BreakPara();
                w.Text("ㄴ. 둘째 진술");
                w.ExitTable();
            }
            else w.BreakPara();

            w.ParaShape(ChoicePara);
            w.CharShape(ChoiceText);
            w.Text("① 가    ② 나    ③ 다    ④ 라    ⑤ 마");
        }

        w.BreakPara();
        w.BreakPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center });
        w.CharShape(Stem);
        w.Text(Closing);

        session.SaveAs(path, HwpSession.FormatOf(path));
    }
}
