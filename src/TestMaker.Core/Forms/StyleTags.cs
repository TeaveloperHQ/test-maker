using System.Text.RegularExpressions;
using System.Xml.Linq;
using static TestMaker.Core.Forms.Hx;

namespace TestMaker.Core.Forms;

/// <summary>
/// 문단 맨 앞의 스타일 표시 "§{스타일 id}§"를 읽어 그 문단에 스타일을 지정하고 표시를 지운다.
///
/// 한글 COM의 스타일 적용(HAction "Style")은 가끔 스타일 대화상자를 띄워, 숨김 한글에서는 그대로 멈춘다(실측).
/// 그래서 COM으로는 글과 표만 쓰고 이 표시를 남긴 뒤, 스타일은 XML에서 지정한다.
/// </summary>
public static class StyleTags
{
    static readonly Regex TagRx = new(@"^§(\d+)§", RegexOptions.Compiled);

    public static string Tag(int styleId) => $"§{styleId}§";

    /// <returns>스타일을 지정한 문단 수</returns>
    public static int Apply(XDocument section, HeaderInfo header)
    {
        int count = 0;
        foreach (var p in section.Descendants(Hp + "p"))
        {
            var firstT = p.Elements(Hp + "run").Elements(Hp + "t").FirstOrDefault();
            var textNode = firstT?.Nodes().OfType<XText>().FirstOrDefault();
            if (textNode is null) continue;
            var m = TagRx.Match(textNode.Value);
            if (!m.Success) continue;

            int id = int.Parse(m.Groups[1].Value);
            var style = header.Style(id) ?? throw new InvalidDataException($"스타일 {id}이(가) 문서에 없습니다.");
            textNode.Value = textNode.Value[m.Length..];
            // 표 앞에 넣었던 표시를 지우고 남은 빈 글 조각은 없앤다(남겨 두면 표 위에 빈 줄이 생긴다 — 실측).
            if (textNode.Value.Length == 0 && firstT!.Nodes().Count() == 1 && firstT.Parent!.Elements().Count() > 1)
                firstT.Remove();
            p.SetAttributeValue("styleIDRef", id);
            p.SetAttributeValue("paraPrIDRef", Attr(style, "paraPrIDRef"));
            foreach (var run in p.Elements(Hp + "run")) run.SetAttributeValue("charPrIDRef", Attr(style, "charPrIDRef"));
            p.Element(Hp + "linesegarray")?.Remove();
            count++;
        }
        return count;
    }
}
