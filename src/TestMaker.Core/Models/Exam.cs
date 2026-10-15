namespace TestMaker.Core.Models;

/// <summary>시험지 한 부. 문항 순서가 곧 문항 번호다.</summary>
public sealed class Exam
{
    public ExamInfo Info { get; set; } = new();
    public List<Question> Questions { get; set; } = [];

    public double TotalPoints => Questions.Sum(q => q.Points);
}

/// <summary>시험지 머리 정보. 양식(preamble)의 {{키}} 자리표시를 채우는 값이기도 하다.</summary>
public sealed class ExamInfo
{
    /// <summary>예: "2026학년도 2학기 중간고사"</summary>
    public string Title { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>예: "1학년"</summary>
    public string Grade { get; set; } = "";
    public string School { get; set; } = "";
    /// <summary>예: "2026. 10. 20.(화) 2교시"</summary>
    public string Date { get; set; } = "";
    public int? DurationMinutes { get; set; }

    /// <summary>양식에 넣을 추가 자리표시 값. 키는 {{ }} 없이 적는다.</summary>
    public Dictionary<string, string> Extra { get; set; } = [];

    /// <summary>양식의 {{키}} → 값. 문항 수/총점처럼 시험에서 계산되는 값도 포함한다.</summary>
    public IReadOnlyDictionary<string, string> ToTokens(Exam exam)
    {
        var tokens = new Dictionary<string, string>
        {
            ["제목"] = Title,
            ["과목"] = Subject,
            // 제목 상자처럼 글자 사이를 띄운 과목명(예: "과    학")
            ["과목띄움"] = string.Join("    ", Subject.Where(c => !char.IsWhiteSpace(c))),
            ["학년"] = Grade,
            ["학교"] = School,
            ["일시"] = Date,
            ["시간"] = DurationMinutes is int m ? $"{m}분" : "",
            ["문항수"] = exam.Questions.Count.ToString(),
            ["총점"] = Format.Points(exam.TotalPoints),
        };
        foreach (var (k, v) in Extra) tokens[k] = v;
        return tokens;
    }
}

public enum QuestionType
{
    /// <summary>선택형(객관식)</summary>
    MultipleChoice,
    /// <summary>단답형</summary>
    ShortAnswer,
    /// <summary>서술형</summary>
    Essay,
}

public sealed class Question
{
    /// <summary>문제은행에서 문항을 구분하는 값(선택).</summary>
    public string? Id { get; set; }
    public QuestionType Type { get; set; } = QuestionType.MultipleChoice;

    /// <summary>발문. 인라인 표기: $수식$, **굵게**, __밑줄__ (<see cref="Text.InlineMarkup"/>).</summary>
    public string Stem { get; set; } = "";
    public double Points { get; set; }

    /// <summary>이 문항부터 시작하는 묶음 문항 머리. 예: [15∼16] 공통 지문.</summary>
    public GroupIntro? Group { get; set; }

    /// <summary>발문 아래 그림.</summary>
    public QuestionImage? Image { get; set; }
    /// <summary>발문 아래 자료 상자(실험 과정, (가)(나) 등). 줄 머리는 직접 적는다.</summary>
    public BoxContent? Passage { get; set; }
    /// <summary>그림·자료 뒤에 이어지는 발문(번호 없음). 예: "이에 대한 설명으로 옳은 것은?"</summary>
    public string? StemAfter { get; set; }
    /// <summary>&lt;보기&gt; 상자. ㄱ.ㄴ.ㄷ. 머리는 양식이 붙이므로 적지 않아도 된다(적어도 알아서 처리).</summary>
    public BoxContent? Box { get; set; }

    /// <summary>선택지. ①②… 기호는 렌더러가 붙이므로 넣지 않는다.</summary>
    public List<string> Choices { get; set; } = [];

    /// <summary>선택형 정답 번호(1부터). 복수 정답 허용.</summary>
    public List<int> Answer { get; set; } = [];
    /// <summary>단답형/서술형 모범 답안.</summary>
    public string? AnswerText { get; set; }
    public string? Explanation { get; set; }

    /// <summary>서술형 답란의 빈 줄 수.</summary>
    public int AnswerLines { get; set; } = 4;

    public QuestionMeta Meta { get; set; } = new();
}

public sealed class BoxContent
{
    /// <summary>상자 위 제목. 빈 문자열이면 생략.</summary>
    public string Title { get; set; } = "<보기>";
    /// <summary>상자 안 줄들. 예: "ㄱ. …", "ㄴ. …"</summary>
    public List<string> Lines { get; set; } = [];
}

public sealed class GroupIntro
{
    /// <summary>묶음 문항 수(이 문항 포함).</summary>
    public int Count { get; set; } = 2;
    /// <summary>공통 지문. 번호 범위 [n∼m]는 자동으로 앞에 붙는다.</summary>
    public string Text { get; set; } = "";
    public QuestionImage? Image { get; set; }
}

public sealed class QuestionImage
{
    /// <summary>그림 파일 경로. 상대 경로는 시험 JSON 파일 위치 기준.</summary>
    public string Path { get; set; } = "";
    /// <summary>표시 폭(mm). null이면 단 폭에 맞춰 줄인다(원본보다 크게 늘리진 않음).</summary>
    public double? WidthMm { get; set; }
}

public sealed class QuestionMeta
{
    /// <summary>단원</summary>
    public string? Unit { get; set; }
    /// <summary>성취기준 코드. 예: [9과01-01]</summary>
    public string? Standard { get; set; }
    /// <summary>난이도: 상/중/하</summary>
    public string? Difficulty { get; set; }
    public List<string> Tags { get; set; } = [];
}

public static class Format
{
    public static string Points(double p) => p == Math.Floor(p) ? ((int)p).ToString() : p.ToString("0.#");

    /// <summary>1 → ①. 20까지 지원.</summary>
    public static string ChoiceMark(int number) =>
        number is >= 1 and <= 20 ? ((char)(0x2460 + number - 1)).ToString() : $"({number})";
}
