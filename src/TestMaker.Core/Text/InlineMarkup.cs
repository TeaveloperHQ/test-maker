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

    /// <summary>
    /// 수식 바로 앞뒤에 영문자·숫자가 붙은 곳(예: "$3 over 4$F", "W$-F$"). 한 식이 글자와 수식으로 쪼개져
    /// 글꼴이 섞이고, 분수 뒤에 붙은 글자는 분모인지 아닌지 헷갈린다 → 식 전체를 하나의 수식으로 써야 한다.
    /// </summary>
    public static IEnumerable<string> GluedEquations(string source)
    {
        var segs = Parse(source);
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].Kind != SegmentKind.Equation) continue;
            bool before = i > 0 && segs[i - 1].Kind == SegmentKind.Text && IsGlue(segs[i - 1].Text[^1]);
            bool after = i + 1 < segs.Count && segs[i + 1].Kind == SegmentKind.Text && IsGlue(segs[i + 1].Text[0]);
            if (before || after) yield return segs[i].Text;
        }

        static bool IsGlue(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
    }

    /// <summary>표기를 걷어 내고 수식은 읽을 수 있는 글로 바꾼 글(미리보기용). 예: "부피의 ${1} over {4}$" → "부피의 1/4".</summary>
    public static string ToPlain(string source)
    {
        try
        {
            return string.Concat(Parse(source).Select(s => s.Kind == SegmentKind.Text ? s.Text : EquationToPlain(s.Text)));
        }
        catch (FormatException) { return source; }
    }

    /// <summary>한글 수식 스크립트를 대강 읽을 수 있는 글로(완벽한 변환이 아니라 미리보기용).</summary>
    public static string EquationToPlain(string script)
    {
        var s = " " + script + " ";
        // 분수: {a} over {b} 또는 a over b → a/b (괄호가 필요하면 둔다)
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(\{[^{}]*\}|[^\s{}]+)\s+over\s+(\{[^{}]*\}|[^\s{}]+)", m =>
        {
            string Part(string p) { var t = p.Trim('{', '}').Trim(); return t.Contains(' ') || t.Contains('+') || t.Contains('-') ? $"({t})" : t; }
            return Part(m.Groups[1].Value) + "/" + Part(m.Groups[2].Value);
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var (k, v) in new[] { ("TIMES", "×"), ("DIV", "÷"), ("CDOT", "·"), ("LEQ", "≤"), ("GEQ", "≥"), ("NEQ", "≠"), ("APPROX", "≈"), ("ALPHA", "α"), ("BETA", "β"), ("theta", "θ"), ("DELTA", "Δ"), ("rightarrow", "→"), ("->", "→"), ("deg", "°") })
            s = System.Text.RegularExpressions.Regex.Replace(s, $@"(?<![A-Za-z]){System.Text.RegularExpressions.Regex.Escape(k)}(?![A-Za-z])", v, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"sqrt\s*\{([^{}]*)\}", "√($1)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![A-Za-z])(rm|it|bold|LEFT|RIGHT)(?![A-Za-z])", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = s.Replace("^2", "²").Replace("^{2}", "²").Replace("^3", "³").Replace("^{3}", "³")
             .Replace("{", "").Replace("}", "").Replace("~", " ").Replace("`", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

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
