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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>
/// 자료 → 문항 찾기 → 변형 → 시험지 한 번의 작업. 작업 폴더(job.json이 있는 곳)에 모든 것이 남는다.
///   sources/  선생님이 준 자료의 사본(+ 한글 파일은 PDF와 그 안의 그림)
///   items/    문항별 결과(q01.json …)   figures/  그린 그림
/// </summary>
public sealed class AuthoringJob
{
    public const string FileName = "job.json";
    public const string SourcesDir = "sources";
    public const string ItemsDir = "items";
    public const string FiguresDir = "figures";

    public List<SourceFile> Sources { get; set; } = [];
    public List<FoundQuestion> Questions { get; set; } = [];
    public string Style { get; set; } = VariantStyle.Auto;
    /// <summary>선생님의 추가 요청(예: "난이도를 조금 높여 줘").</summary>
    public string Request { get; set; } = "";
    public ExamInfo Info { get; set; } = new();
    /// <summary>시험지 양식 폴더. null이면 내장 기본 양식.</summary>
    public string? FormDir { get; set; }
}

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

    bool keepOriginal;
    /// <summary>원래 문항도 그대로 옮겨 넣을지.</summary>
    public bool KeepOriginal { get => keepOriginal; set => Set(ref keepOriginal, value); }

    string status = "";
    [JsonIgnore]
    public string Status { get => status; set => Set(ref status, value); }

    [JsonIgnore]
    public string TypeText => Type switch { QuestionType.Essay => "서술형", QuestionType.ShortAnswer => "단답형", _ => "선택형" };

    public int ExpectedCount => Variants + (KeepOriginal ? 1 : 0);
    public string ItemFile => Path.Combine(AuthoringJob.ItemsDir, Id + ".json");
}
