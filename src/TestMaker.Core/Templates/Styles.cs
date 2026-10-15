namespace TestMaker.Core.Templates;

// 한글 COM으로 직접 서식을 줄 때 쓰는 글자·문단·용지 모양. 내장 기본 양식 생성기(DefaultFormBuilder)와
// 정답지(AnswerSheetRenderer)가 쓴다. 시험지 본문은 학교/기본 양식의 스타일을 쓰므로 여기 값을 쓰지 않는다.

public sealed class TextStyle
{
    public string FontHangul { get; set; } = "함초롬바탕";
    public string FontLatin { get; set; } = "함초롬바탕";
    public double SizePt { get; set; } = 10;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    /// <summary>#RRGGBB</summary>
    public string Color { get; set; } = "#000000";
    /// <summary>장평(%)</summary>
    public int RatioPercent { get; set; } = 100;
    /// <summary>자간(%)</summary>
    public int SpacingPercent { get; set; }

    public TextStyle With(bool? bold = null, bool? underline = null)
    {
        var c = (TextStyle)MemberwiseClone();
        if (bold is bool b) c.Bold = b;
        if (underline is bool u) c.Underline = u;
        return c;
    }
}

public enum ParaAlign { Justify, Left, Right, Center, Distribute }

public sealed class ParaStyle
{
    public ParaAlign Align { get; set; } = ParaAlign.Justify;
    public double LeftMarginPt { get; set; }
    /// <summary>첫 줄 들여쓰기(pt). 음수면 내어쓰기.</summary>
    public double IndentPt { get; set; }
    public int LineSpacingPercent { get; set; } = 160;
    public double SpaceBeforePt { get; set; }
    public double SpaceAfterPt { get; set; }
}

public sealed class PageProfile
{
    public double WidthMm { get; set; } = 210;
    public double HeightMm { get; set; } = 297;
    public bool Landscape { get; set; }
    public double MarginLeftMm { get; set; } = 15;
    public double MarginRightMm { get; set; } = 15;
    public double MarginTopMm { get; set; } = 15;
    public double MarginBottomMm { get; set; } = 15;
    public double HeaderMm { get; set; } = 10;
    public double FooterMm { get; set; } = 10;

    public double BodyWidthMm => (Landscape ? HeightMm : WidthMm) - MarginLeftMm - MarginRightMm;
}

public sealed class ColumnProfile
{
    public int Count { get; set; } = 2;
    public double GapMm { get; set; } = 8;
    /// <summary>단 사이 구분선</summary>
    public bool Line { get; set; } = true;

    public double ColumnWidthMm(PageProfile page) => (page.BodyWidthMm - GapMm * (Count - 1)) / Count;
}

/// <summary>한글 내부 단위 변환. HWPUNIT = 1/7200 inch.</summary>
public static class HwpUnit
{
    public static double ToMm(double hwpUnit) => Math.Round(hwpUnit * 25.4 / 7200, 1);
    public static int FromMm(double mm) => (int)Math.Round(mm * 7200 / 25.4);
    public static double ToPt(double hwpUnit) => hwpUnit / 100.0;
    public static int FromPt(double pt) => (int)Math.Round(pt * 100);

    // 문단 여백/간격: COM(HParaShape)과 HWPX의 <hp:default> 값은 HWPUNIT의 2배 스케일이다
    // (<hp:case>의 HwpUnitChar 값이 절반). 2026-10 한컴오피스 2024에서 실측.
    public static double ParaToPt(double comValue) => Math.Round(comValue / 200.0, 1);
    public static int ParaFromPt(double pt) => (int)Math.Round(pt * 200);
}
