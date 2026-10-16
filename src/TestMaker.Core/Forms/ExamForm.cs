namespace TestMaker.Core.Forms;

/// <summary>
/// 학교 시험지 양식(form.json). 양식 폴더 = form.json + base.hwpx(학교 문서의 header·머리·꼬리) + boxes.xml(상자 견본).
/// 모든 스타일/문단 모양/글자 모양 id는 base.hwpx의 header를 가리킨다.
/// </summary>
public sealed class ExamForm
{
    public const string FileName = "form.json";
    public const string BaseFile = "base.hwpx";
    public const string BoxesFile = "boxes.xml";
    public const string Marker = "{{문항}}";

    public string Name { get; set; } = "";
    public List<string> LearnedFrom { get; set; } = [];
    public DateTime? LearnedAt { get; set; }

    /// <summary>양식 판정: 완성형(번호가 스타일에서 나옴) / 무질서형(번호를 글자로 입력).</summary>
    public FormKind Kind { get; set; }

    public double ColumnWidthMm { get; set; } = 85;

    /// <summary>발문(번호 붙는 문단)</summary>
    public RoleStyle Stem { get; set; } = new();
    /// <summary>번호가 스타일에서 나오지 않을 때 직접 쓰는 번호 형식. {0} = 번호.</summary>
    public string NumberFormat { get; set; } = "{0}. ";
    /// <summary>그림·자료 뒤에 이어지는 발문(번호 없음)</summary>
    public RoleStyle StemContinuation { get; set; } = new();
    public RoleStyle GroupHeader { get; set; } = new();
    public RoleStyle Figure { get; set; } = new();
    public RoleStyle Spacer { get; set; } = new();
    /// <summary>문항 사이 빈 문단 수(실제 시험지 기준).</summary>
    public int SpacerLines { get; set; } = 3;

    /// <summary>발문 끝 배점 형식. {0} = 배점.</summary>
    public string PointsFormat { get; set; } = "[{0}점]";

    /// <summary>선택지 배치 견본. 줄 수(1행/2행/3행/5행)별.</summary>
    public List<ChoiceLayout> ChoiceLayouts { get; set; } = [];

    /// <summary>&lt;보기&gt; 상자 견본이 있는지(boxes.xml의 view).</summary>
    public BoxProto? ViewBox { get; set; }
    /// <summary>자료 상자 견본(boxes.xml의 passage).</summary>
    public BoxProto? PassageBox { get; set; }

    /// <summary>서술형·논술형 견본. 양식에 없으면 null(발문 모양으로 대신 쓴다).</summary>
    public EssayProto? Essay { get; set; }
    /// <summary>&lt;조 건&gt; 상자 견본(boxes.xml의 condition). 없으면 &lt;보기&gt; 상자 견본에 제목만 바꿔 쓴다.</summary>
    public BoxProto? ConditionBox { get; set; }

    public List<string> Notes { get; set; } = [];
}

/// <summary>서술형·논술형 문항 견본(학교는 보통 선택형과 다른 번호 체계를 쓴다. 예: "논술형 1>").</summary>
public sealed class EssayProto
{
    /// <summary>서술형 발문. AutoNumber면 번호("논술형 1>")가 스타일에서 나온다.</summary>
    public RoleStyle Stem { get; set; } = new();
    /// <summary>번호가 스타일에서 나오지 않을 때 직접 쓰는 번호 형식. {0} = 서술형 안에서의 번호.</summary>
    public string NumberFormat { get; set; } = "서술형 {0}> ";
    /// <summary>소문항 줄. {0} = 서술형 번호, {1} = 소문항 번호. 예: "2-1. "</summary>
    public RoleStyle Sub { get; set; } = new();
    public string SubNumberFormat { get; set; } = "{0}-{1}. ";
    /// <summary>소문항이 있을 때 발문 끝 배점 형식. 예: "[총 {0}점]"</summary>
    public string TotalPointsFormat { get; set; } = "[총 {0}점]";
    /// <summary>답 쓰는 줄 견본(boxes.xml의 answerLine)이 있는지.</summary>
    public bool HasAnswerLine { get; set; }
    /// <summary>서술형 구역 머리 상자 견본(boxes.xml의 essayHeader. 예: "논술형 문항")이 있는지.</summary>
    public bool HasHeader { get; set; }
}

public enum FormKind { Structured, Unstructured }

/// <summary>한 역할이 쓰는 스타일과, 그 학교가 실제로 쓰는 문단/글자 모양.</summary>
public sealed class RoleStyle
{
    public string StyleName { get; set; } = "바탕글";
    public int StyleId { get; set; }
    public int ParaPr { get; set; }
    public int CharPr { get; set; }
    /// <summary>문단 모양에 개요/문단번호가 걸려 있어 번호가 자동으로 붙는지.</summary>
    public bool AutoNumber { get; set; }

    /// <summary>학습 중에만 쓰는 값: 보조 문서에서 실제로 쓰인 문단 모양(여백 등을 옮겨 오기 위해).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal System.Xml.Linq.XElement? ObservedParaShape { get; set; }

    public RoleStyle Clone() => (RoleStyle)MemberwiseClone();
    public override string ToString() => $"{StyleName}{(AutoNumber ? "(자동번호)" : "")}";
}

/// <summary>
/// 선택지 배치 한 가지. 예: 2행 = 첫 줄 3개, 둘째 줄 2개.
/// 선택지 사이 탭 개수는 저장하지 않고 출력할 때 문단의 탭 위치로 계산한다(견본의 탭 개수는 짧은 선택지 기준).
/// </summary>
public sealed class ChoiceLayout
{
    public int Lines { get; set; }
    public List<int> PerLine { get; set; } = [];
    public RoleStyle Style { get; set; } = new();
    /// <summary>true면 한 줄에 하나, 번호(①)는 스타일이 붙인다(5행).</summary>
    public bool Auto { get; set; }

    public int Count => PerLine.Sum();
    public int MaxPerLine => PerLine.Count == 0 ? 1 : PerLine.Max();
    public override string ToString() => Auto ? $"{Lines}행(자동번호)" : $"{Lines}행({string.Join("+", PerLine)})";
}

/// <summary>상자 견본 정보. 표 자체는 boxes.xml에 있고, 내용 줄 모양만 여기에.</summary>
public sealed class BoxProto
{
    /// <summary>내용 줄 모양(상자 안 문단)</summary>
    public RoleStyle Line { get; set; } = new();
    /// <summary>ㄱ.ㄴ.ㄷ. 같은 줄 머리를 스타일이 붙이는지.</summary>
    public bool AutoLabels { get; set; }
}
