using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using TestMaker.Core.Models;

namespace TestMaker.Ai;

/// <summary>화면에 묶이는 값의 변경 알림.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 작업 = 시험 하나의 프로필(예: "2026 2학기 1차고사"). 작업 폴더(job.json이 있는 곳)에 모든 것이 남아
/// 언제든 다시 열면 그대로 이어서 할 수 있다.
///   sources/  선생님이 준 자료의 사본(+ 한글 파일은 PDF와 그 안의 그림)
///   items/    문항별 결과(q01.json …)   figures/  그린 그림   시험지/  만든 시험지(날짜별)
/// </summary>
public sealed class AuthoringJob
{
    public const string FileName = "job.json";
    public const string SourcesDir = "sources";
    public const string ItemsDir = "items";
    public const string FiguresDir = "figures";
    public const string ExamsDir = "시험지";
    public const string OriginalsDir = "originals";

    /// <summary>작업 이름(예: "2026 2학기 1차고사"). 비어 있으면 폴더 이름.</summary>
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<SourceFile> Sources { get; set; } = [];
    public List<FoundQuestion> Questions { get; set; } = [];
    /// <summary>선생님의 추가 요청(예: "난이도를 조금 높여 줘"). 모든 문항에 적용.</summary>
    public string Request { get; set; } = "";
    public ExamInfo Info { get; set; } = new();
    /// <summary>시험지 양식 폴더. null이면 내장 기본 양식.</summary>
    public string? FormDir { get; set; }
    /// <summary>③에서 정한 결과의 행선지: "q01/0" → <see cref="Destination.Exam"/> 또는 <see cref="Destination.Practice"/>. 없으면 버림.</summary>
    public Dictionary<string, string> Destinations { get; set; } = [];
    /// <summary>행선지를 정한 결과가 자동으로 들어간 문항함 폴더: "q01/0" → 폴더.</summary>
    public Dictionary<string, string> Archived { get; set; } = [];
    /// <summary>④ 시험지 탭의 문항과 순서.</summary>
    public List<ExamListEntry> ExamList { get; set; } = [];
    /// <summary>④ 예상문제 탭의 문항과 순서.</summary>
    public List<ExamListEntry> PracticeList { get; set; } = [];
    /// <summary>예상문제지 제목(비어 있으면 "작업 이름 예상문제").</summary>
    public string PracticeTitle { get; set; } = "";
    public bool Pdf { get; set; } = true;
    public bool Answers { get; set; } = true;
}

/// <summary>③에서 결과를 보낼 곳. ④의 두 탭.</summary>
public static class Destination
{
    public const string Exam = "시험지";
    public const string Practice = "예상문제";
}

/// <summary>④ 시험지 목록의 한 줄(저장용). 이번 작업의 결과("job:q01/0")이거나 문항함의 문항("bank:id").</summary>
public sealed class ExamListEntry
{
    public string Key { get; set; } = "";
    /// <summary>문항함 문항이면 그 폴더.</summary>
    public string? BankDir { get; set; }
}

/// <summary>③에 보일 결과 하나: 열쇠(행선지·문항함에 쓰는 이름), "원문항"/"변형 n", 문항.</summary>
public sealed record JobResult(string Key, string Label, Question Item);

/// <summary>작업 목록에 보일 요약.</summary>
public sealed record JobSummary(string Dir, string Name, DateTime Modified, int Sources, int Questions, int ExamQuestions, int PracticeQuestions = 0);

public sealed class SourceFile
{
    /// <summary>sources/ 안의 사본 이름.</summary>
    public string Name { get; set; } = "";
    /// <summary>선생님이 고른 원래 파일(참고용, 고치지 않는다).</summary>
    public string? Original { get; set; }
    /// <summary>Claude가 읽을 파일들(작업 폴더 기준). 그림·PDF는 자기 자신, 한글 파일은 바꾼 PDF.</summary>
    public List<string> ReadFiles { get; set; } = [];
    /// <summary>한글 파일 안에 들어 있던 그림(작업 폴더 기준). 그림을 그대로 따라 그릴 때 원본으로 쓴다.</summary>
    public List<string> Figures { get; set; } = [];
    /// <summary>PDF 자료의 쪽 그림(작업 폴더 기준). Claude가 여기서 문항 영역(원문 캡처)과 그림을 잘라 낸다.</summary>
    public List<string> Pages { get; set; } = [];
    /// <summary>이 자료에서 문항을 찾았는지. [문항 찾기]는 아직 안 찾은 자료만 읽는다.</summary>
    public bool Analyzed { get; set; }
}

public static class VariantStyle
{
    public const string Auto = "알아서";
    public const string Numbers = "숫자만 바꾸기";
    public const string Situation = "상황·소재 바꾸기";
    public const string Harder = "더 어렵게";
    public const string Easier = "더 쉽게";
    public static readonly string[] All = [Auto, Numbers, Situation, Harder, Easier];
}

/// <summary>자료에서 찾은 문항 하나와, 이 문항으로 만들 것.</summary>
public sealed class FoundQuestion : Observable
{
    /// <summary>작업 안에서의 이름(q01 …). 결과 파일 이름이 된다.</summary>
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>자료에 적힌 번호(예: "07").</summary>
    public string Number { get; set; } = "";
    public string Summary { get; set; } = "";
    public QuestionType Type { get; set; }
    public bool HasFigure { get; set; }

    bool include = true;
    public bool Include { get => include; set => Set(ref include, value); }

    int variants = 1;
    /// <summary>만들 변형 문항 수.</summary>
    public int Variants { get => variants; set => Set(ref variants, Math.Clamp(value, 0, 10)); }

    string style = VariantStyle.Auto;
    /// <summary>이 문항의 변형 방식(<see cref="VariantStyle"/>).</summary>
    public string Style { get => style; set => Set(ref style, value); }

    bool keepOriginal = true;
    /// <summary>원래 문항도 결과에 넣을지(기본 켬 — 필요 없으면 ③에서 버림).</summary>
    public bool KeepOriginal { get => keepOriginal; set => Set(ref keepOriginal, value); }

    string status = "";
    [JsonIgnore]
    public string Status { get => status; set => Set(ref status, value); }

    [JsonIgnore]
    public string TypeText => Type switch { QuestionType.Essay => "서술형", QuestionType.ShortAnswer => "단답형", _ => "선택형" };

    [JsonIgnore]
    public string FigureText => HasFigure ? " · 그림 있음" : "";

    public int ExpectedCount => Variants + (KeepOriginal ? 1 : 0);
    public string ItemFile => Path.Combine(AuthoringJob.ItemsDir, Id + ".json");
    /// <summary>[문항 찾기] 때 옮긴 원문항.</summary>
    public string OriginalFile => Path.Combine(AuthoringJob.OriginalsDir, Id + ".json");
    /// <summary>자료에서 이 문항 영역만 잘라 낸 그림(원문 그대로 보기).</summary>
    public string SnapshotFile => Path.Combine(AuthoringJob.OriginalsDir, Id + ".png");
}
