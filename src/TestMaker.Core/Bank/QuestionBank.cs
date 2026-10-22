using System.Text.Json;
using System.Text.Json.Serialization;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;

namespace TestMaker.Core.Bank;

/// <summary>문항함의 칸. 실제 시험에 낼 문항과, 학생에게 나눠 줄 예상 문제를 나눠 모은다.</summary>
public enum BankCategory
{
    /// <summary>시험용</summary>
    Exam,
    /// <summary>예상문제용</summary>
    Practice,
}

/// <summary>문항함에 보관한 문항 하나. 폴더 하나 = 문항 하나(item.json + 그림).</summary>
public sealed class BankItem
{
    public const string FileName = "item.json";

    public string Id { get; set; } = "";
    public BankCategory Category { get; set; }
    public DateTime SavedAt { get; set; }
    /// <summary>목록에 보일 이름. 예: "07번 변형 1 · 열평형 온도 구하기"</summary>
    public string Title { get; set; } = "";
    /// <summary>만든 곳(작업 폴더 이름 등). 참고용.</summary>
    public string? Source { get; set; }
    /// <summary>문항. 그림 경로는 이 문항 폴더 기준.</summary>
    public Question Question { get; set; } = new();

    /// <summary>이 문항의 폴더(불러올 때 채움).</summary>
    [JsonIgnore]
    public string Dir { get; set; } = "";

    [JsonIgnore]
    public string CategoryText => QuestionBank.CategoryName(Category);
}

/// <summary>
/// 문항함: 만든 문항을 시험지로 만들기 전에 모아 두는 곳. 평소에 모아 두었다가 골라 조합해 시험지를 만든다.
///   &lt;root&gt;/시험용/&lt;id&gt;/item.json + 그림
///   &lt;root&gt;/예상문제/&lt;id&gt;/item.json + 그림
/// 목록 파일 없이 폴더를 그대로 읽는다(폴더를 손으로 옮기거나 지워도 어긋나지 않게).
/// </summary>
public sealed class QuestionBank(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public static string CategoryName(BankCategory c) => c == BankCategory.Exam ? "시험용" : "예상문제";

    string CategoryDir(BankCategory c) => Path.Combine(Root, CategoryName(c));

    /// <summary>문항을 보관한다. 그림은 문항 폴더로 복사하므로, 만든 작업 폴더를 지워도 남는다.</summary>
    /// <param name="baseDir">문항 안 그림 경로(상대 경로)의 기준 폴더.</param>
    public BankItem Add(Question question, string? baseDir, BankCategory category, string title, string? source = null)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(CategoryDir(category), id);
        Directory.CreateDirectory(dir);

        var q = Clone(question);
        if (q.Image is { Path.Length: > 0 } img) img.Path = CopyImage(img.Path, baseDir, dir, "그림");
        if (q.Group?.Image is { Path.Length: > 0 } gimg) gimg.Path = CopyImage(gimg.Path, baseDir, dir, "묶음그림");

        var item = new BankItem { Id = id, Category = category, SavedAt = DateTime.Now, Title = title, Source = source, Question = q, Dir = dir };
        JsonStore.Save(Path.Combine(dir, BankItem.FileName), item);
        return item;
    }

    /// <summary>보관한 문항 전부(최근 것이 앞). 읽지 못하는 폴더는 건너뛴다.</summary>
    public List<BankItem> Load()
    {
        var items = new List<BankItem>();
        foreach (var c in Enum.GetValues<BankCategory>())
        {
            var cd = CategoryDir(c);
            if (!Directory.Exists(cd)) continue;
            foreach (var d in Directory.EnumerateDirectories(cd))
            {
                var file = Path.Combine(d, BankItem.FileName);
                if (!File.Exists(file)) continue;
                try
                {
                    var item = JsonStore.Load<BankItem>(file);
                    item.Dir = d;
                    item.Category = c;     // 폴더가 기준(손으로 옮긴 경우)
                    items.Add(item);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { }
            }
        }
        return items.OrderByDescending(i => i.SavedAt).ToList();
    }

    /// <summary>문항 폴더 하나를 읽는다. 지워졌거나 읽지 못하면 null.</summary>
    public static BankItem? LoadItem(string dir)
    {
        var file = Path.Combine(dir, BankItem.FileName);
        if (!File.Exists(file)) return null;
        try
        {
            var item = JsonStore.Load<BankItem>(file);
            item.Dir = dir;
            return item;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { return null; }
    }

    /// <summary>시험용 ↔ 예상문제로 옮긴다.</summary>
    public void Move(BankItem item, BankCategory to)
    {
        if (item.Category == to) return;
        var target = Path.Combine(CategoryDir(to), Path.GetFileName(item.Dir));
        Directory.CreateDirectory(CategoryDir(to));
        Directory.Move(item.Dir, target);
        item.Dir = target;
        item.Category = to;
        JsonStore.Save(Path.Combine(target, BankItem.FileName), item);
    }

    public void Delete(BankItem item)
    {
        if (Directory.Exists(item.Dir)) Directory.Delete(item.Dir, recursive: true);
    }

    /// <summary>고른 문항을 고른 순서대로 시험으로 묶는다. 그림 경로는 절대 경로로 바꿔 어디서 출력해도 찾게 한다.</summary>
    public static Exam Compose(IEnumerable<BankItem> items, ExamInfo info)
    {
        var exam = new Exam { Info = info };
        foreach (var item in items) exam.Questions.Add(Absolutize(item.Question, item.Dir));
        return exam;
    }

    /// <summary>문항 사본을 만들고 그림 경로를 baseDir 기준 절대 경로로 바꾼다(여러 곳의 문항을 한 시험지로 모을 때).</summary>
    public static Question Absolutize(Question question, string? baseDir)
    {
        var q = Clone(question);
        if (baseDir is null) return q;
        if (q.Image is { Path.Length: > 0 } img) img.Path = Path.Combine(baseDir, img.Path);
        if (q.Group?.Image is { Path.Length: > 0 } gimg) gimg.Path = Path.Combine(baseDir, gimg.Path);
        return q;
    }

    static Question Clone(Question q) =>
        JsonSerializer.Deserialize<Question>(JsonSerializer.Serialize(q, JsonStore.Options), JsonStore.Options)!;

    static string CopyImage(string path, string? baseDir, string dir, string name)
    {
        var src = Path.IsPathRooted(path) || baseDir is null ? path : Path.Combine(baseDir, path);
        if (!File.Exists(src)) throw new FileNotFoundException($"그림 파일이 없습니다: {path}");
        var file = name + Path.GetExtension(src);
        File.Copy(src, Path.Combine(dir, file), overwrite: true);
        return file;
    }
}
