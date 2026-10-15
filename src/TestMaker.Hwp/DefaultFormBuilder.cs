using System.IO.Compression;
using TestMaker.Core.Forms;
using TestMaker.Core.Templates;

namespace TestMaker.Hwp;

/// <summary>
/// 내장 기본 양식(forms/default)을 만든다(개발용, 한글 필요).
/// 스타일·개요번호가 갖춰진 "표준 시험지"를 한글로 생성한 뒤, 학교 양식과 똑같이 <see cref="FormLearner"/>로 학습한다.
/// 그래서 기본 양식도 학교 양식과 같은 경로로 출력되고, 선생님이 스타일만 고치면 전체가 바뀐다.
///
/// 번호 체계: 문항 1. → 선택지 ① → 보기 ㄱ.  (5행 선택지만 자동번호, 나머지는 직접 입력 + 탭)
/// </summary>
public static class DefaultFormBuilder
{
    // A4, 좌우 여백 15mm, 2단 간격 8mm → 단 폭 86mm
    static readonly PageProfile Page = new() { MarginLeftMm = 15, MarginRightMm = 15, MarginTopMm = 15, MarginBottomMm = 15, HeaderMm = 10, FooterMm = 10 };
    static readonly ColumnProfile Columns = new() { Count = 2, GapMm = 8, Line = true };
    const int StemHang = 1500;    // 문항 번호 자리(15pt)
    const int ChoiceHang = 1300;  // ① 자리
    const int ViewHang = 1500;    // ㄱ. 자리

    public static FormLearnResult Build(string formDir)
    {
        var work = Directory.CreateTempSubdirectory("testmaker_defaultform_").FullName;
        try
        {
            using var session = new HwpSession();
            var blank = Path.Combine(work, "blank.hwpx");
            session.NewDocument();
            session.SaveAs(blank, HwpFormat.Hwpx);

            var styled = Path.Combine(work, "styled.hwpx");
            File.Copy(blank, styled);
            var ids = AddStyles(styled);

            var source = Path.Combine(work, "standard.hwpx");
            session.NewDocument();
            session.Open(styled);
            WriteSample(new HwpWriter(session), ids);
            session.SaveAs(source, HwpFormat.Hwpx);

            var result = FormLearner.Learn(source, [], "기본");
            result.Form.LearnedFrom = ["내장 표준 양식"];
            if (Directory.Exists(formDir)) Directory.Delete(formDir, recursive: true);
            result.Save(formDir);
            session.NewDocument();
            session.Open(Path.Combine(formDir, ExamForm.BaseFile)); // 열리는지 확인
            return result;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    sealed record StyleIds(int Stem, int Cont, int ChoiceAuto, int Choice5, int Choice3, int Choice2, int View, int ViewTitle, int Passage, int Figure, int Group);

    static StyleIds AddStyles(string hwpx)
    {
        using var zip = ZipFile.Open(hwpx, ZipArchiveMode.Update);
        var doc = Hx.Load(zip, Hx.HeaderPart);
        var h = new HeaderInfo(doc);

        int body = 0; // 함초롬바탕 10pt
        int bold = h.CharVariant(body, bold: true, underline: false);
        int num = h.AddNumbering((1, "DIGIT", "^1.", bold), (2, "CIRCLED_DIGIT", "^2", null), (3, "HANGUL_JAMO", "^3.", null));

        // 선택지 칸 탭: 번호 뒤 글자 폭을 n등분. 탭 위치는 문단 여백이 아니라 단 왼쪽 끝 기준이다(실측).
        double colMm = Columns.ColumnWidthMm(Page);
        int text = HwpUnit.FromMm(colMm) - StemHang;
        int Tabs(int n) => h.AddTabStops([.. Enumerable.Range(1, n - 1).Select(i => StemHang + text * i / n)]);

        int pStem = h.AddParaShape(new() { Heading = "NUMBER", NumberingId = num, Level = 0, Indent = -StemHang });
        int pCont = h.AddParaShape(new() { Left = StemHang });
        // 번호 문단: 한글은 번호를 왼쪽 여백 위치에 두고, 둘째 줄부터는 번호 뒤 글자 위치에 맞춘다(실측).
        int pChoiceAuto = h.AddParaShape(new() { Heading = "NUMBER", NumberingId = num, Level = 1, Left = StemHang, Indent = -ChoiceHang, Align = "LEFT" });
        int pChoice5 = h.AddParaShape(new() { Left = StemHang, Align = "LEFT", TabPr = Tabs(5) });
        int pChoice3 = h.AddParaShape(new() { Left = StemHang, Align = "LEFT", TabPr = Tabs(3) });
        int pChoice2 = h.AddParaShape(new() { Left = StemHang, Align = "LEFT", TabPr = Tabs(2) });
        int pView = h.AddParaShape(new() { Heading = "NUMBER", NumberingId = num, Level = 2, Left = 0, Indent = -ViewHang, LineSpacing = 150 });
        int pCenter = h.AddParaShape(new() { Align = "CENTER" });
        int pPassage = h.AddParaShape(new() { LineSpacing = 150 });
        int pGroup = h.AddParaShape(new() { });

        var ids = new StyleIds(
            Stem: h.AddStyle("문항", pStem, body),
            Cont: h.AddStyle("이어지는 발문", pCont, body),
            ChoiceAuto: h.AddStyle("선택지 자동번호", pChoiceAuto, body),
            Choice5: h.AddStyle("선택지 1행(5칸)", pChoice5, body),
            Choice3: h.AddStyle("선택지 2행(3칸)", pChoice3, body),
            Choice2: h.AddStyle("선택지 3행(2칸)", pChoice2, body),
            View: h.AddStyle("보기", pView, body),
            ViewTitle: h.AddStyle("보기 제목", pCenter, body),
            Passage: h.AddStyle("자료", pPassage, body),
            Figure: h.AddStyle("그림", pCenter, body),
            Group: h.AddStyle("묶음 문항", pGroup, bold));
        Hx.Replace(zip, Hx.HeaderPart, doc);
        return ids;
    }

    /// <summary>머리 부분과 견본 문항(학습기가 역할별 견본을 뽑을 수 있는 최소 구성).</summary>
    static void WriteSample(HwpWriter w, StyleIds s)
    {
        double boxMm = Columns.ColumnWidthMm(Page) - StemHang / 100.0 * 0.3528 - 2;
        w.AssumeEmptyParagraph();
        w.PageSetup(Page);

        // 머리 부분: 시험 정보 자리표시
        var title = new TextStyle { SizePt = 16, Bold = true };
        var normal = new TextStyle();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center });
        w.CharShape(title);
        w.Text("{{제목}}");
        w.BreakPara();
        w.CharShape(normal.With(bold: true));
        w.Text("{{과목}}   {{학년}}   {{일시}}");
        w.BreakPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Right });
        w.CharShape(normal);
        w.Text("(    )학년 (    )반 (    )번   이름: (                )");
        w.BreakPara();
        w.Columns(Columns);

        // 1번: 자료 상자 → 이어지는 발문 → <보기> → 2행(3+2) 선택지
        w.Style(s.Stem);
        w.Text("다음은 어떤 실험 과정을 나타낸 것이다.");
        w.BreakPara();
        w.Style(s.Cont);
        w.BeginTable(1, [boxMm]);
        w.Style(s.Passage);
        w.Text("(가) 첫째 과정");
        w.BreakPara();
        w.Text("(나) 둘째 과정");
        w.ExitTable();
        w.Style(s.Cont);
        w.Text("이에 대한 설명으로 옳은 것만을 <보기>에서 있는 대로 고른 것은? [3점]");
        w.BreakPara();
        w.Style(s.Cont);
        w.BeginTable(2, [boxMm]);
        w.Style(s.ViewTitle);
        w.Text("<보 기>");
        w.NextCell();
        w.Style(s.View);
        w.Text("첫째 진술");
        w.BreakPara();
        w.Text("둘째 진술");
        w.BreakPara();
        w.Text("셋째 진술");
        w.ExitTable();
        Choices(w, s.Choice3, ["① ㄱ\t② ㄴ\t③ ㄱ, ㄴ", "④ ㄴ, ㄷ\t⑤ ㄱ, ㄴ, ㄷ"]);
        Gap(w, s);

        // 2번: 5행(자동번호)
        w.Style(s.Stem);
        w.Text("다음 중 옳은 것은? [3점]");
        w.BreakPara();
        foreach (var c in new[] { "첫째 선택지", "둘째 선택지", "셋째 선택지", "넷째 선택지", "다섯째 선택지" })
        {
            w.Style(s.ChoiceAuto);
            w.Text(c);
            w.BreakPara();
        }
        Gap(w, s);

        // 3번: 3행(2+2+1)
        w.Style(s.Stem);
        w.Text("다음 중 옳은 것은? [3점]");
        w.BreakPara();
        Choices(w, s.Choice2, ["① 가\t② 나", "③ 다\t④ 라", "⑤ 마"]);
        Gap(w, s);

        // [4∼5] 묶음 + 4번: 1행(5칸)
        w.Style(s.Group);
        w.Text("[4∼5] 다음 글을 읽고 물음에 답하시오.");
        w.BreakPara();
        w.Style(s.Stem);
        w.Text("빈칸에 알맞은 것은? [3점]");
        w.BreakPara();
        Choices(w, s.Choice5, ["① 1\t② 2\t③ 3\t④ 4\t⑤ 5"]);
        Gap(w, s);

        // 5번: 5행
        w.Style(s.Stem);
        w.Text("다음 중 옳지 않은 것은? [3점]");
        w.BreakPara();
        foreach (var c in new[] { "가", "나", "다", "라", "마" })
        {
            w.Style(s.ChoiceAuto);
            w.Text(c);
            w.BreakPara();
        }

        // 꼬리
        w.Style(s.Cont);
        w.BreakPara();
        w.Style(s.Figure);
        w.Text("- 끝 -");
    }

    static void Choices(HwpWriter w, int style, string[] lines)
    {
        foreach (var line in lines)
        {
            w.Style(style);
            var parts = line.Split('\t');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) w.Run("InsertTab");
                w.Text(parts[i]);
            }
            w.BreakPara();
        }
    }

    /// <summary>문항 사이 빈 줄 2개(학습기가 간격으로 배움).</summary>
    static void Gap(HwpWriter w, StyleIds s)
    {
        w.Style(s.Cont);
        w.BreakPara();
        w.Style(s.Cont);
        w.BreakPara();
    }
}
