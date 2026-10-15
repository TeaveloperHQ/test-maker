namespace TestMaker.Core.Templates;

/// <summary>
/// 학교 양식을 학습하지 않았을 때 쓰는 기본 서식(한글 COM으로 직접 서식을 그리는 방식).
/// 학교 양식은 <see cref="Forms.ExamForm"/>(학교 스타일 견본 복제 방식)을 쓴다.
/// TODO: 기본 양식도 스타일이 갖춰진 내장 양식(form)으로 옮기면 이 클래스와 ExamRenderer는 없앤다.
/// </summary>
public sealed class ExamTemplate
{
    public const string FileName = "template.json";
    public const string DefaultMarker = "{{문항}}";

    public string Name { get; set; } = "기본";
    /// <summary>학습에 쓴 원본 파일 이름(기록용).</summary>
    public string? LearnedFrom { get; set; }
    public DateTime? LearnedAt { get; set; }

    /// <summary>
    /// 제목·학년반번호이름 칸 등 문항 앞부분을 원본 그대로 담은 HWPX(템플릿 폴더 기준 상대 경로).
    /// 안의 {{키}}는 시험 정보로 바뀌고, <see cref="Marker"/> 자리부터 문항이 들어간다.
    /// null이면 기본 머리말을 그린다.
    /// </summary>
    public string? Preamble { get; set; }
    public string Marker { get; set; } = DefaultMarker;

    public PageProfile Page { get; set; } = new();
    public ColumnProfile Columns { get; set; } = new();

    public TextStyle QuestionNumber { get; set; } = new() { Bold = true };
    public TextStyle QuestionStem { get; set; } = new();
    public ParaStyle QuestionPara { get; set; } = new() { LeftMarginPt = 0, IndentPt = -13 };
    public TextStyle Choice { get; set; } = new();
    public ParaStyle ChoicePara { get; set; } = new() { LeftMarginPt = 10 };
    public TextStyle BoxText { get; set; } = new();
    public ParaStyle BoxPara { get; set; } = new() { LineSpacingPercent = 150 };

    /// <summary>문항 번호 형식. {0} = 번호. 예: "{0}. ", "{0}) "</summary>
    public string NumberFormat { get; set; } = "{0}. ";
    /// <summary>발문 끝 배점 형식. {0} = 배점. 빈 문자열이면 배점을 쓰지 않는다.</summary>
    public string PointsFormat { get; set; } = "[{0}점]";
    /// <summary>
    /// 한 줄에 놓을 선택지 수. 0이면 길이를 보고 자동(한 줄 전부 → 두 줄 → 한 개씩).
    /// 2 이상이면 테두리 없는 표로 칸을 맞춘다.
    /// </summary>
    public int ChoicesPerLine { get; set; }
    public int BlankLinesBetweenQuestions { get; set; } = 1;
    /// <summary>마지막 문항 뒤에 가운데 정렬로 넣을 문구. 예: "- 수고하셨습니다 -"</summary>
    public string ClosingText { get; set; } = "";

    /// <summary>학습 때 남긴 참고 사항(확인이 필요한 점 등).</summary>
    public List<string> Notes { get; set; } = [];

    public static ExamTemplate Default() => new();

    /// <summary>템플릿 폴더 또는 template.json 경로에서 읽는다.</summary>
    public static ExamTemplate Load(string pathOrDir, out string templateDir)
    {
        var file = Directory.Exists(pathOrDir) ? Path.Combine(pathOrDir, FileName) : pathOrDir;
        templateDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return Serialization.JsonStore.Load<ExamTemplate>(file);
    }
}

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
