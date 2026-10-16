using System.IO.Compression;
using TestMaker.Core.Forms;
using TestMaker.Core.Templates;

namespace TestMaker.Hwp;

/// <summary>
/// 표준 시험지 양식을 만든다(개발용, 한글 필요).
///  1) 스타일·개요번호가 갖춰진 "표준 시험지 양식(B4).hwp"를 한글로 생성 — 선생님들이 앱 없이 바로 쓰는 배포용
///  2) 그 문서를 학교 양식과 똑같이 <see cref="FormLearner"/>로 학습 → 앱의 내장 기본 양식(forms/default)
///
/// 판형: B4 2단(학교 지필평가·학력평가와 같은 판형). 본문 한양신명조 11pt.
/// 모든 글은 스타일을 거친다 — 학교 사정에 맞게 스타일에서 글꼴·크기만 바꾸면 전체가 바뀐다.
/// 번호 체계: 문항 1. → 선택지 ① → 보기 ㄱ.  (5행 선택지만 자동번호, 2·3행은 원문자 직접 입력 + 칸에 맞춘 탭)
/// 머리 부분: 학교 지필평가형(결재란, 시험명·과목 상자, 유의사항). 학교마다 다르므로 학교 양식은 학습해서 쓴다.
/// </summary>
public static class DefaultFormBuilder
{
    public const string DistributionFile = "표준 시험지 양식(B4).hwp";
    const string Font = "한양신명조";
    const double BodyPt = 11;

    // B4 257×364mm, 좌우 19mm·위아래 10mm, 2단 간격 8mm → 단 폭 105.5mm (학교 지필평가 시험지와 같은 값)
    static readonly PageProfile Page = new()
    {
        WidthMm = 257, HeightMm = 364,
        MarginLeftMm = 19, MarginRightMm = 19, MarginTopMm = 10, MarginBottomMm = 10, HeaderMm = 11, FooterMm = 10,
    };
    static readonly ColumnProfile Columns = new() { Count = 2, GapMm = 8, Line = true };
    const int StemHang = 1650;    // 문항 번호 자리(11pt 글자 "10." 너비 정도)
    const int ChoiceHang = 1400;  // ① 자리
    const int ViewHang = 1650;    // ㄱ. 자리

    static readonly TextStyle Body = new() { FontHangul = Font, FontLatin = Font, SizePt = BodyPt };

    /// <summary>배포용 파일에서 자리표시 대신 넣을 예시 글.</summary>
    static readonly (string Token, string Text)[] DistributionText =
    [
        ("{{제목}}", "○○○○학년도 ○학기 ○차 지필평가"),
        ("{{과목띄움}}", "과    목"),
        ("{{학년}}", "○학년"),
        ("{{일시}}", "○○○○년 ○월 ○일 ○교시"),
        ("{{학교}}", "○○중학교"),
        ("{{쪽수}}", "○"),
        ("{{문항수}}", "○○"),
        ("{{총점}}", "100"),
    ];

    /// <param name="formDir">내장 양식 폴더(forms/default). 배포용 .hwp는 그 상위 폴더에 쓴다.</param>
    public static FormLearnResult Build(string formDir, Action<string>? log = null)
    {
        var work = Directory.CreateTempSubdirectory("testmaker_defaultform_").FullName;
        try
        {
            using var session = new HwpSession();
            var blank = Path.Combine(work, "blank.hwpx");
            session.NewDocument();
            session.SaveAs(blank, HwpFormat.Hwpx);
            session.NewDocument(); // 파일 잠금 해제

            var styled = Path.Combine(work, "styled.hwpx");
            File.Copy(blank, styled);
            var ids = AddStyles(styled);

            var source = Path.Combine(work, "standard.hwpx");
            session.Open(styled);
            WriteSample(new HwpWriter(session), ids, log ?? (_ => { }));
            session.SaveAs(source, HwpFormat.Hwpx); // 앱용: {{자리표시}} 그대로
            session.NewDocument();

            // 스타일 지정과 <보 기> 상자 모양은 XML에서 한다(COM 스타일 적용은 가끔 대화상자를 띄워 멈춘다,
            // 칸 합치기·칸별 테두리도 XML이 확실하다).
            FinishInXml(source, ids);
            log?.Invoke("스타일·<보기> 상자");

            // 배포용: 앱 없이 쓰는 선생님이 바로 고쳐 쓰도록 자리표시를 예시 글로 바꿔 저장
            session.Open(source);
            var w = new HwpWriter(session);
            foreach (var (token, text) in DistributionText) w.ReplaceAll(token, text);
            var dist = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(formDir))!, DistributionFile);
            session.SaveAs(dist, HwpFormat.Hwp);
            session.NewDocument();

            var result = FormLearner.Learn(source, [], "표준(B4)");
            result.Form.LearnedFrom = [DistributionFile];
            if (Directory.Exists(formDir)) Directory.Delete(formDir, recursive: true);
            result.Save(formDir);
            session.Open(Path.Combine(formDir, ExamForm.BaseFile)); // 열리는지 확인
            return result;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void FinishInXml(string hwpx, StyleIds ids)
    {
        using var zip = ZipFile.Open(hwpx, ZipArchiveMode.Update);
        var header = Hx.Load(zip, Hx.HeaderPart);
        var section = Hx.Load(zip, Hx.SectionPart);
        var info = new HeaderInfo(header);
        if (StyleTags.Apply(section, info) == 0) throw new InvalidOperationException("스타일 표시를 찾지 못했습니다.");
        StyleBoxParagraphs(section, info, ids);
        var tbl = ViewBoxShape.Find(section) ?? throw new InvalidOperationException("견본의 <보기> 상자를 찾지 못했습니다.");
        ViewBoxShape.Apply(tbl, info);
        Hx.Replace(zip, Hx.HeaderPart, header);
        Hx.Replace(zip, Hx.SectionPart, section);
    }

    /// <summary>
    /// 견본 문항 안의 상자(자료·보기) 문단: "이어지는 발문" 스타일을 주고, 표 위치 기준을 단이 아닌 문단으로 바꿔
    /// 발문 글자 위치에 맞춘다. (표 앞에 스타일 표시를 넣으면 한글이 표를 다른 문단으로 떼어 빈 줄이 생긴다 — 실측)
    /// 머리 부분(결재란·유의사항)과 꼬리(확인 사항) 상자는 그대로 둔다.
    /// </summary>
    static void StyleBoxParagraphs(System.Xml.Linq.XDocument section, HeaderInfo info, StyleIds ids)
    {
        var paras = section.Root!.Elements(Hx.Hp + "p").ToList();
        string Id(int v) => v.ToString();
        int first = paras.FindIndex(p => (string?)p.Attribute("styleIDRef") == Id(ids.Stem));
        var choiceStyles = new[] { ids.ChoiceAuto, ids.Choice2, ids.Choice3, ids.Choice5 }.Select(Id).ToHashSet();
        int lastChoice = paras.FindLastIndex(p => choiceStyles.Contains((string?)p.Attribute("styleIDRef") ?? ""));
        var cont = info.Style(ids.Cont)!;
        for (int i = first + 1; i < lastChoice; i++)
        {
            var tbl = Hx.Table(paras[i]);
            if (tbl is null) continue;
            paras[i].SetAttributeValue("styleIDRef", ids.Cont);
            paras[i].SetAttributeValue("paraPrIDRef", Hx.Attr(cont, "paraPrIDRef"));
            tbl.Element(Hx.Hp + "pos")?.SetAttributeValue("horzRelTo", "PARA");
        }
    }

    sealed record StyleIds(int Stem, int Cont, int ChoiceAuto, int Choice5, int Choice3, int Choice2, int View, int ViewTitle, int Passage, int Figure, int Group);

    static StyleIds AddStyles(string hwpx)
    {
        using var zip = ZipFile.Open(hwpx, ZipArchiveMode.Update);
        var doc = Hx.Load(zip, Hx.HeaderPart);
        var h = new HeaderInfo(doc);

        var font = h.AddFont(Font);
        int body = h.AddCharShape(font, BodyPt);
        int bold = h.AddCharShape(font, BodyPt, bold: true);
        h.SetStyleCharShape(0, body); // 바탕글도 본문 글꼴로
        int num = h.AddNumbering((1, "DIGIT", "^1.", bold), (2, "CIRCLED_DIGIT", "^2", null), (3, "HANGUL_JAMO", "^3.", null));

        // 선택지 칸 탭: 번호 뒤 글자 폭을 n등분. 탭 위치는 문단 여백이 아니라 단 왼쪽 끝 기준이다(실측).
        int colHu = HwpUnit.FromMm(Columns.ColumnWidthMm(Page));
        int text = colHu - StemHang;
        int Tabs(int n) => h.AddTabStops([.. Enumerable.Range(1, n - 1).Select(i => StemHang + text * i / n)]);

        // 번호 문단: 번호는 왼쪽 여백 위치, 둘째 줄부터는 번호 뒤 글자 위치(내어쓰기) — 실측
        int pStem = h.AddParaShape(new() { Heading = "NUMBER", NumberingId = num, Level = 0, Indent = -StemHang });
        int pCont = h.AddParaShape(new() { Left = StemHang });
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

    /// <summary>머리 부분(학교 지필평가형)과 견본 문항(학습기가 역할별 견본을 뽑을 수 있는 구성), 꼬리(확인 사항).</summary>
    static void WriteSample(HwpWriter w, StyleIds s, Action<string> log)
    {
        double bodyMm = Page.BodyWidthMm - 2;
        double colMm = Columns.ColumnWidthMm(Page);
        double boxMm = colMm - StemHang / 100.0 * 0.3528 - 2;
        w.AssumeEmptyParagraph();
        w.PageSetup(Page);
        log("쪽 설정"); w.PageNumber(); log("쪽 번호");

        // ── 머리 부분: 결재란 ──
        var small = Body.Sized(10);
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 130 });
        // 첫 줄: 칸 이름, 둘째 줄: 서명 칸(출제교사 칸에는 "(인)" 자리)
        w.BeginTable(2, [24, 80, 28, 28, 28, 28]);
        string[] head = ["출제교사", "", "계", "부 장", "교 감", "교 장"];
        string[] sign = ["", "(인)                    (인)", "", "", "", ""];
        for (int i = 0; i < head.Length * 2; i++)
        {
            if (i > 0) w.NextCell();
            w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 130 });
            w.CharShape(small);
            var text = i < head.Length ? head[i] : sign[i - head.Length];
            if (i >= head.Length) w.BreakPara(); // 서명 칸 높이
            w.Text(text);
        }
        w.ExitTable();

        log("결재란");
        // ── 시험명·과목 상자 ──
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 150 });
        w.BeginTable(1, [bodyMm]);
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 150 });
        w.CharShape(Body.With(bold: true).Sized(13));
        w.Text("{{제목}}");
        w.BreakPara();
        w.CharShape(Body.With(bold: true).Sized(22));
        w.Text("{{학년}}  {{과목띄움}}");
        w.BreakPara();
        w.CharShape(Body.With(bold: true));
        w.Text("{{일시}}        {{학교}}   {{학년}} (    )반 (    )번   성명 (                    )");
        w.ExitTable();

        log("제목 상자"); w.Columns(Columns); log("다단");

        // ── 유의사항 상자(1단 위) ──
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left });
        w.BeginTable(1, [colMm - 2]);
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left, LeftMarginPt = 12, IndentPt = -12, LineSpacingPercent = 140 });
        w.CharShape(small.With(bold: true));
        w.Text("※ 답안지에 학년, 반, 번호, 성명을 정확히 표기하시오.");
        w.BreakPara();
        w.Text("※ 선택형 문제의 답을 골라 답안지의 해당란에 표기하시오.");
        w.BreakPara();
        w.Text("※ 시험지 {{쪽수}}쪽, {{문항수}}문항, {{총점}}점 만점입니다. 문항에 따라 배점이 다릅니다.");
        w.ExitTable();
        Tag(w, s.Cont);
        w.BreakPara();

        log("유의사항");
        // ── 견본 문항 ──
        // 1번: 자료 상자 → 이어지는 발문 → <보기> → 2행(3+2) 선택지
        Tag(w, s.Stem);
        w.Text("다음은 어떤 실험 과정을 나타낸 것이다.");
        w.BreakPara();
        w.BeginTable(1, [boxMm]);
        Tag(w, s.Passage);
        w.Text("(가) 첫째 과정");
        w.BreakPara();
        Tag(w, s.Passage);
        w.Text("(나) 둘째 과정");
        w.ExitTable();
        Tag(w, s.Cont);
        w.Text("이에 대한 설명으로 옳은 것만을 <보기>에서 있는 대로 고른 것은? [3점]");
        w.BreakPara();
        w.BeginTable(2, [boxMm]);
        Tag(w, s.ViewTitle);
        w.Text("<보 기>");
        w.NextCell();
        Tag(w, s.View);
        w.Text("첫째 진술");
        w.BreakPara();
        Tag(w, s.View);
        w.Text("둘째 진술");
        w.BreakPara();
        Tag(w, s.View);
        w.Text("셋째 진술");
        w.ExitTable();
        Choices(w, s.Choice3, ["① ㄱ\t② ㄴ\t③ ㄱ, ㄴ", "④ ㄴ, ㄷ\t⑤ ㄱ, ㄴ, ㄷ"]);
        Gap(w, s);

        // 2번: 5행(자동번호)
        Tag(w, s.Stem);
        w.Text("다음 중 옳은 것은? [3점]");
        w.BreakPara();
        foreach (var c in new[] { "첫째 선택지", "둘째 선택지", "셋째 선택지", "넷째 선택지", "다섯째 선택지" })
        {
            Tag(w, s.ChoiceAuto);
            w.Text(c);
            w.BreakPara();
        }
        Gap(w, s);

        // 3번: 3행(2+2+1)
        Tag(w, s.Stem);
        w.Text("다음 중 옳은 것은? [3점]");
        w.BreakPara();
        Choices(w, s.Choice2, ["① 가\t② 나", "③ 다\t④ 라", "⑤ 마"]);
        Gap(w, s);

        // [4∼5] 묶음 + 4번: 1행(5칸)
        Tag(w, s.Group);
        w.Text("[4∼5] 다음 글을 읽고 물음에 답하시오.");
        w.BreakPara();
        Tag(w, s.Stem);
        w.Text("빈칸에 알맞은 것은? [3점]");
        w.BreakPara();
        Choices(w, s.Choice5, ["① 1\t② 2\t③ 3\t④ 4\t⑤ 5"]);
        Gap(w, s);

        // 5번: 5행
        Tag(w, s.Stem);
        w.Text("다음 중 옳지 않은 것은? [3점]");
        w.BreakPara();
        foreach (var c in new[] { "가", "나", "다", "라", "마" })
        {
            Tag(w, s.ChoiceAuto);
            w.Text(c);
            w.BreakPara();
        }

        log("견본 문항");
        // ── 꼬리: 확인 사항 ──
        Tag(w, s.Cont);
        w.BreakPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left });
        w.BeginTable(1, [colMm - 2]);
        w.ParaShape(new ParaStyle { Align = ParaAlign.Left, LineSpacingPercent = 150 });
        w.CharShape(Body.With(bold: true));
        w.Text("* 확인 사항");
        w.BreakPara();
        w.CharShape(Body);
        w.Text("답안지의 해당란에 필요한 내용을 정확히 기입(표기)했는지 확인하시오.");
        w.ExitTable();
    }

    /// <summary>문단 맨 앞에 스타일 표시를 남긴다. 스타일은 저장 뒤 XML에서 지정한다(<see cref="StyleTags"/>).</summary>
    static void Tag(HwpWriter w, int styleId) => w.Text(StyleTags.Tag(styleId));

    static void Choices(HwpWriter w, int style, string[] lines)
    {
        foreach (var line in lines)
        {
            Tag(w, style);
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
        Tag(w, s.Cont);
        w.BreakPara();
        Tag(w, s.Cont);
        w.BreakPara();
    }
}
