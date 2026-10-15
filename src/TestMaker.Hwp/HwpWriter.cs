using System.Globalization;
using System.Reflection;
using TestMaker.Core.Templates;
using TestMaker.Core.Text;

namespace TestMaker.Hwp;

/// <summary>
/// 커서 위치에 글·서식·표·수식을 넣는 기본 동작 모음. 모든 동작은 문서 끝에 이어 쓰는 것을 전제로 한다.
/// 한글 액션/파라미터셋 이름은 한컴 "HwpAutomation" 문서 기준.
/// </summary>
public sealed class HwpWriter(HwpSession session)
{
    readonly dynamic hwp = session.Raw;

    /// <summary>커서가 있는 문단이 비어 있는지. <see cref="NewPara"/>가 빈 줄을 덧붙이지 않게 하는 데 쓴다.</summary>
    public bool ParagraphEmpty { get; private set; } = true;

    public void Run(string action) => hwp.HAction.Run(action);
    public void BreakPara() { Run("BreakPara"); ParagraphEmpty = true; }
    public void BreakLine() => Run("BreakLine");
    public void MoveDocEnd() => hwp.MovePos(3, 0, 0);

    /// <summary>새 문단에서 시작한다. 이미 빈 문단이면 그대로 쓴다.</summary>
    public void NewPara()
    {
        if (!ParagraphEmpty) BreakPara();
    }

    /// <summary>양식을 연 직후처럼 커서 위치 문단 상태를 외부에서 알려 줄 때.</summary>
    public void AssumeEmptyParagraph() => ParagraphEmpty = true;

    public void Text(string s)
    {
        if (s.Length == 0) return;
        ParagraphEmpty = false;
        var p = hwp.HParameterSet.HInsertText;
        hwp.HAction.GetDefault("InsertText", p.HSet);
        p.Text = s;
        hwp.HAction.Execute("InsertText", p.HSet);
    }

    /// <summary>현재 문단에 문단 스타일을 적용한다. id = header.xml의 스타일 id.</summary>
    public void Style(int id)
    {
        var p = hwp.HParameterSet.HStyle;
        hwp.HAction.GetDefault("Style", p.HSet);
        p.Apply = id;
        hwp.HAction.Execute("Style", p.HSet);
    }

    /// <summary>이후 입력할 글자의 모양.</summary>
    public void CharShape(TextStyle s)
    {
        var p = hwp.HParameterSet.HCharShape;
        hwp.HAction.GetDefault("CharShape", p.HSet);
        // 한글·영문 외 언어(한자·기호 등)도 한글 글꼴로 맞춘다. ①② 같은 기호가 다른 글꼴로 튀지 않게.
        p.FaceNameHangul = s.FontHangul; p.FontTypeHangul = 1;
        p.FaceNameLatin = s.FontLatin; p.FontTypeLatin = 1;
        p.FaceNameHanja = s.FontHangul; p.FontTypeHanja = 1;
        p.FaceNameJapanese = s.FontHangul; p.FontTypeJapanese = 1;
        p.FaceNameOther = s.FontHangul; p.FontTypeOther = 1;
        p.FaceNameSymbol = s.FontHangul; p.FontTypeSymbol = 1;
        p.FaceNameUser = s.FontHangul; p.FontTypeUser = 1;
        p.Height = HwpUnit.FromPt(s.SizePt);
        p.Bold = s.Bold ? 1 : 0;
        p.Italic = s.Italic ? 1 : 0;
        p.UnderlineType = s.Underline ? 1 : 0;
        p.RatioHangul = s.RatioPercent; p.RatioLatin = s.RatioPercent;
        p.SpacingHangul = s.SpacingPercent; p.SpacingLatin = s.SpacingPercent;
        var (r, g, b) = ParseColor(s.Color);
        p.TextColor = hwp.RGBColor(r, g, b);
        hwp.HAction.Execute("CharShape", p.HSet);
    }

    /// <summary>현재 문단의 모양(이후 새 문단에도 이어짐).</summary>
    public void ParaShape(ParaStyle s)
    {
        var p = hwp.HParameterSet.HParaShape;
        hwp.HAction.GetDefault("ParagraphShape", p.HSet);
        p.AlignType = (int)s.Align;
        p.LeftMargin = HwpUnit.ParaFromPt(s.LeftMarginPt);
        p.Indentation = HwpUnit.ParaFromPt(s.IndentPt);
        p.PrevSpacing = HwpUnit.ParaFromPt(s.SpaceBeforePt);
        p.NextSpacing = HwpUnit.ParaFromPt(s.SpaceAfterPt);
        p.LineSpacingType = 0; // 비율(%)
        p.LineSpacing = s.LineSpacingPercent;
        hwp.HAction.Execute("ParagraphShape", p.HSet);
    }

    /// <summary>인라인 표기($수식$, **굵게**, __밑줄__)를 해석해 쓴다. 글 안의 \n은 강제 줄 나눔.</summary>
    public void Rich(string markup, TextStyle style)
    {
        foreach (var seg in InlineMarkup.Parse(markup))
        {
            if (seg.Kind == SegmentKind.Equation)
            {
                Equation(seg.Text, style.SizePt);
                continue;
            }
            CharShape(style.With(bold: style.Bold || seg.Bold, underline: style.Underline || seg.Underline));
            var lines = seg.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) BreakLine();
                Text(lines[i]);
            }
        }
    }

    /// <summary>글자처럼 취급되는 수식. script는 한글 수식 편집기 문법.</summary>
    public void Equation(string script, double sizePt)
    {
        ParagraphEmpty = false;
        var p = hwp.HParameterSet.HEqEdit;
        hwp.HAction.GetDefault("EquationCreate", p.HSet);
        p.@string = script;
        p.BaseUnit = hwp.PointToHwpUnit(sizePt);
        bool ok = hwp.HAction.Execute("EquationCreate", p.HSet);
        if (!ok) throw new InvalidOperationException($"수식을 넣지 못했습니다: {script}");
    }

    public void PageSetup(PageProfile page)
    {
        var p = hwp.HParameterSet.HSecDef;
        hwp.HAction.GetDefault("PageSetup", p.HSet);
        p.PageDef.PaperWidth = HwpUnit.FromMm(page.WidthMm);
        p.PageDef.PaperHeight = HwpUnit.FromMm(page.HeightMm);
        p.PageDef.Landscape = page.Landscape ? 1 : 0;
        p.PageDef.LeftMargin = HwpUnit.FromMm(page.MarginLeftMm);
        p.PageDef.RightMargin = HwpUnit.FromMm(page.MarginRightMm);
        p.PageDef.TopMargin = HwpUnit.FromMm(page.MarginTopMm);
        p.PageDef.BottomMargin = HwpUnit.FromMm(page.MarginBottomMm);
        p.PageDef.HeaderLen = HwpUnit.FromMm(page.HeaderMm);
        p.PageDef.FooterLen = HwpUnit.FromMm(page.FooterMm);
        p.HSet.SetItem("ApplyClass", 24);
        p.HSet.SetItem("ApplyTo", 3); // 문서 전체
        hwp.HAction.Execute("PageSetup", p.HSet);
    }

    /// <summary>
    /// 커서 위치부터 다단. "다단 설정 나누기" 뒤에 ApplyTo=2로 적용해야 앞부분(제목)은 한 단으로 남는다.
    /// (ApplyTo 5/6은 문서 전체에 적용됨 — 실측)
    /// </summary>
    public void Columns(ColumnProfile cols)
    {
        Run("BreakColDef");
        var p = hwp.HParameterSet.HColDef;
        hwp.HAction.GetDefault("MultiColumn", p.HSet);
        p.Count = cols.Count;
        p.SameSize = 1;
        p.SameGap = HwpUnit.FromMm(cols.GapMm);
        p.LineType = cols.Line ? 1 : 0;
        p.HSet.SetItem("ApplyClass", 832);
        p.HSet.SetItem("ApplyTo", 2);
        hwp.HAction.Execute("MultiColumn", p.HSet);
    }

    /// <summary>표 칸의 기본 안쪽 여백(좌우 합). 한글은 ColWidth에 이 값을 더해 칸을 그린다(실측).</summary>
    public const double CellPaddingMm = 3.6;

    /// <summary>
    /// 글자처럼 취급되는 표를 만들고 커서를 첫 칸에 둔다. 칸 이동은 <see cref="NextCell"/>, 끝나면 <see cref="ExitTable"/>.
    /// </summary>
    /// <param name="colWidthsMm">칸의 바깥 폭(안쪽 여백 포함). 표 전체 폭 = 합계.</param>
    public void BeginTable(int rows, IReadOnlyList<double> colWidthsMm)
    {
        ParagraphEmpty = false;
        int cols = colWidthsMm.Count;
        var p = hwp.HParameterSet.HTableCreation;
        hwp.HAction.GetDefault("TableCreate", p.HSet);
        p.Rows = rows;
        p.Cols = cols;
        p.WidthType = 2; // 지정 폭
        p.HeightType = 0;
        p.WidthValue = HwpUnit.FromMm(colWidthsMm.Sum());
        // C#에서 CreateItemArray는 null을 돌려주고 배열의 SetItem도 바인딩되지 않는다(실측).
        // 만든 뒤 ColWidth 속성으로 다시 받아 Item 속성을 리플렉션으로 설정한다.
        p.CreateItemArray("ColWidth", cols);
        object arr = p.ColWidth;
        for (int i = 0; i < cols; i++)
            arr.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, arr,
                [i, HwpUnit.FromMm(Math.Max(1, colWidthsMm[i] - CellPaddingMm))]);
        p.TableProperties.TreatAsChar = 1;
        bool ok = hwp.HAction.Execute("TableCreate", p.HSet);
        if (!ok) throw new InvalidOperationException("표를 만들지 못했습니다.");
    }

    public void NextCell() => Run("TableRightCell");

    /// <summary>커서가 있는 칸의 네 테두리를 없앤다. 칸마다 따로 해야 안쪽 선까지 지워진다(블록 전체 적용은 바깥선만 지움 — 실측).</summary>
    public void ClearCellBorder()
    {
        Run("TableCellBlock");
        var p = hwp.HParameterSet.HCellBorderFill;
        hwp.HAction.GetDefault("CellBorderFill", p.HSet);
        p.BorderTypeLeft = 0;
        p.BorderTypeRight = 0;
        p.BorderTypeTop = 0;
        p.BorderTypeBottom = 0;
        hwp.HAction.Execute("CellBorderFill", p.HSet);
        Run("Cancel");
    }

    /// <summary>
    /// 표 밖으로 나간다. 한글은 문서 끝에 표를 만들면 표 뒤에 빈 문단을 자동으로 붙이므로(실측),
    /// 커서는 그 빈 문단에 놓인다.
    /// </summary>
    public void ExitTable()
    {
        MoveDocEnd();
        ParagraphEmpty = true;
    }

    /// <summary>현재 위치에 그림을 글자처럼 넣는다.</summary>
    public void Picture(string path, double widthMm, double heightMm)
    {
        ParagraphEmpty = false;
        var ctrl = hwp.InsertPicture(Path.GetFullPath(path), true, 1, false, false, 0, widthMm, heightMm);
        if (ctrl is null) throw new IOException($"그림을 넣지 못했습니다: {path}");
        var props = ctrl.Properties;
        props.SetItem("TreatAsChar", true);
        ctrl.Properties = props;
    }

    /// <summary>문서 처음부터 글을 찾아 선택한다. 못 찾으면 false.</summary>
    public bool FindAndSelect(string text)
    {
        hwp.MovePos(2, 0, 0);
        var p = hwp.HParameterSet.HFindReplace;
        hwp.HAction.GetDefault("RepeatFind", p.HSet);
        p.FindString = text;
        p.Direction = 0;
        p.IgnoreMessage = 1;
        p.FindRegExp = 0;
        p.MatchCase = 1;
        p.WholeWordOnly = 0;
        return hwp.HAction.Execute("RepeatFind", p.HSet);
    }

    /// <summary>선택한 글을 지운다.</summary>
    public void DeleteSelection() => Run("Delete");

    /// <summary>문서 전체(표 안 포함)에서 바꾸기.</summary>
    public void ReplaceAll(string find, string replace)
    {
        var p = hwp.HParameterSet.HFindReplace;
        hwp.HAction.GetDefault("AllReplace", p.HSet);
        p.FindString = find;
        p.ReplaceString = replace;
        p.IgnoreMessage = 1;
        p.Direction = 0;
        p.FindRegExp = 0;
        p.MatchCase = 0;
        p.WholeWordOnly = 0;
        p.ReplaceMode = 1;
        hwp.HAction.Execute("AllReplace", p.HSet);
    }

    static (int R, int G, int B) ParseColor(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return (0, 0, 0);
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }
}
