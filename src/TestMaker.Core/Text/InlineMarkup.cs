using System.Text;

namespace TestMaker.Core.Text;

public enum SegmentKind { Text, Equation }

/// <summary>한 덩어리의 글 또는 수식. 수식이면 Text가 한글 수식 스크립트다.</summary>
public sealed record Segment(SegmentKind Kind, string Text, bool Bold = false, bool Underline = false);

/// <summary>
/// 문항 글에 쓰는 가벼운 표기법.
///   $x^2 over 2$  → 한글 수식 (한글 수식 편집기 스크립트 문법)
///   **굵게**      → 굵게
///   __밑줄__      → 밑줄 (예: 밑줄 친 __㉠__)
///   \$ \* \_ \\   → 글자 그대로
/// </summary>
public static class InlineMarkup
{
    public static IReadOnlyList<Segment> Parse(string source)
    {
        var result = new List<Segment>();
        var sb = new StringBuilder();
        bool bold = false, underline = false;

        void Flush()
        {
            if (sb.Length == 0) return;
            result.Add(new Segment(SegmentKind.Text, sb.ToString(), bold, underline));
            sb.Clear();
        }

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\\' && i + 1 < source.Length && source[i + 1] is '$' or '*' or '_' or '\\')
            {
                sb.Append(source[++i]);
            }
            else if (c == '$')
            {
                int end = FindUnescaped(source, '$', i + 1);
                if (end < 0) throw new FormatException($"닫히지 않은 수식 표기($): \"{source}\"");
                Flush();
                result.Add(new Segment(SegmentKind.Equation, source[(i + 1)..end].Trim(), bold, underline));
                i = end;
            }
            else if (c == '*' && At(source, i + 1, '*'))
            {
                Flush(); bold = !bold; i++;
            }
            else if (c == '_' && At(source, i + 1, '_'))
            {
                Flush(); underline = !underline; i++;
            }
            else sb.Append(c);
        }
        if (bold) throw new FormatException($"닫히지 않은 굵게 표기(**): \"{source}\"");
        if (underline) throw new FormatException($"닫히지 않은 밑줄 표기(__): \"{source}\"");
        Flush();
        return result;
    }

    /// <summary>
    /// 표기를 걷어낸 글의 대략적인 폭(전각 글자 수 단위). 한글·기호는 1, 영문·숫자·공백·문장부호는 0.5.
    /// 수식은 스크립트의 영문자·숫자 수 × 0.5로 어림한다(over, sqrt 같은 명령어도 세므로 넉넉하게 나온다).
    /// </summary>
    public static double EstimateWidthEm(string source) => Parse(source).Sum(s => s.Kind == SegmentKind.Text
        ? s.Text.Sum(c => c < 'ᄀ' ? 0.5 : 1.0)
        : Math.Max(1.0, s.Text.Count(char.IsLetterOrDigit) * 0.5));

    static bool At(string s, int i, char c) => i < s.Length && s[i] == c;

    static int FindUnescaped(string s, char c, int from)
    {
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == c) return i;
        }
        return -1;
    }
}
