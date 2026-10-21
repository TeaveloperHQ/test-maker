using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestMaker.Core.Models;
using TestMaker.Core.Text;

namespace TestMaker.App;

/// <summary>화면 미리보기용 글·그림(시험지 서식이 아니라 알아보기 쉬운 글).</summary>
public static class Preview
{
    public static string Plain(string s) => InlineMarkup.ToPlain(s);

    /// <summary>발문 아래 부분: 자료, 이어지는 발문, &lt;보기&gt;, &lt;조건&gt;, 소문항, 선택지.</summary>
    public static string Body(Question q)
    {
        var sb = new StringBuilder();
        if (q.Passage is { } p) foreach (var l in p.Lines) sb.AppendLine("  " + Plain(l));
        if (q.StemAfter is { Length: > 0 } after) sb.AppendLine(Plain(after));
        if (q.Box is { } box)
        {
            sb.AppendLine(box.Title.Length > 0 ? box.Title : "<보기>");
            var labels = "ㄱㄴㄷㄹㅁㅂㅅ";
            for (int i = 0; i < box.Lines.Count; i++)
            {
                var line = Plain(box.Lines[i]);
                bool labelled = line.Length > 1 && labels.Contains(line[0]) && line[1] == '.';
                sb.AppendLine($"  {(labelled || i >= labels.Length ? "" : labels[i] + ". ")}{line}");
            }
        }
        if (q.Conditions is { } c)
        {
            sb.AppendLine(c.Title);
            foreach (var l in c.Lines) sb.AppendLine("  " + Plain(l));
        }
        for (int i = 0; i < q.SubQuestions.Count; i++) sb.AppendLine($"({i + 1}) {Plain(q.SubQuestions[i].Text)} [{Format.Points(q.SubQuestions[i].Points)}점]");
        if (q.Choices.Count > 0)
            sb.AppendLine(string.Join("   ", q.Choices.Select((ch, i) => $"{Format.ChoiceMark(i + 1)} {Plain(ch)}")));
        return sb.ToString().TrimEnd();
    }

    public static string Answer(Question q)
    {
        var sb = new StringBuilder();
        if (q.Answer.Count > 0) sb.Append("정답 " + string.Join(", ", q.Answer.Select(Format.ChoiceMark)));
        else if (q.AnswerText is { Length: > 0 } a) sb.Append("모범 답안: " + Plain(a));
        else if (q.SubQuestions.Count > 0)
            sb.Append(string.Join("  ", q.SubQuestions.Select((s, i) => $"({i + 1}) {Plain(s.AnswerText ?? "")}")));
        if (q.Explanation is { Length: > 0 } ex) sb.Append("\n해설: " + Plain(ex));
        return sb.ToString();
    }

    /// <summary>단원 · 난이도 (있는 것만).</summary>
    public static string Meta(Question q) =>
        string.Join(" · ", new[] { q.Meta.Unit, q.Meta.Difficulty is { Length: > 0 } d ? $"난이도 {d}" : null }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>파일을 잠그지 않게 읽는다(다시 그리면 덮어쓰므로).</summary>
    public static ImageSource? LoadImage(string? path, int decodeWidth = 0)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException) { return null; }
    }
}
