using System.Xml.Linq;
using static TestMaker.Core.Forms.Hx;

namespace TestMaker.Core.Forms;

/// <summary>번호 머리(개요/문단번호) 정보. Type이 NONE이면 번호 없음.</summary>
public sealed record Heading(string Type, int NumberingId, int Level)
{
    public bool IsNumbered => Type is "OUTLINE" or "NUMBER";
}

/// <summary>
/// header.xml의 스타일·문단 모양·글자 모양 표. 읽기와, 조립에 필요한 최소한의 추가(굵게 변형, 번호 뗀 문단 모양)를 한다.
/// </summary>
public sealed class HeaderInfo(XDocument doc)
{
    public XDocument Doc { get; } = doc;

    XElement Styles => Doc.Descendants(Hh + "styles").First();
    XElement ParaProps => Doc.Descendants(Hh + "paraProperties").First();
    XElement CharProps => Doc.Descendants(Hh + "charProperties").First();

    readonly Dictionary<(int, bool, bool), int> charVariants = [];

    public XElement? Style(int id) => Styles.Elements(Hh + "style").FirstOrDefault(s => Attr(s, "id", -1) == id);
    public XElement? StyleByName(string name) => Styles.Elements(Hh + "style").FirstOrDefault(s => (string?)s.Attribute("name") == name);
    public string StyleName(int id) => (string?)Style(id)?.Attribute("name") ?? "";
    public XElement? ParaPr(int id) => ParaProps.Elements(Hh + "paraPr").FirstOrDefault(p => Attr(p, "id", -1) == id);
    public XElement? CharPr(int id) => CharProps.Elements(Hh + "charPr").FirstOrDefault(p => Attr(p, "id", -1) == id);

    public Heading HeadingOf(int paraPrId)
    {
        var h = ParaPr(paraPrId)?.Element(Hh + "heading");
        return h is null ? new("NONE", 0, 0) : new((string?)h.Attribute("type") ?? "NONE", Attr(h, "idRef"), Attr(h, "level"));
    }

    public IEnumerable<XElement> AllStyles => Styles.Elements(Hh + "style");

    public bool IsBold(int charPrId) => CharPr(charPrId)?.Element(Hh + "bold") is not null;

    /// <summary>같은 글자 모양에 굵게/밑줄만 더한 글자 모양 id. 필요하면 새로 만든다.</summary>
    public int CharVariant(int charPrId, bool bold, bool underline)
    {
        var src = CharPr(charPrId);
        if (src is null) return charPrId;
        bool hasBold = src.Element(Hh + "bold") is not null;
        bool hasUnderline = (string?)src.Element(Hh + "underline")?.Attribute("type") is { } u && u != "NONE";
        bold |= hasBold;
        underline |= hasUnderline;
        if (bold == hasBold && underline == hasUnderline) return charPrId;
        if (charVariants.TryGetValue((charPrId, bold, underline), out var cached)) return cached;

        var c = new XElement(src);
        int id = NextId(CharProps, "charPr");
        c.SetAttributeValue("id", id);
        if (bold && c.Element(Hh + "bold") is null)
        {
            // 스키마 순서: … offset, italic, bold, underline …
            var anchor = c.Element(Hh + "underline");
            var b = new XElement(Hh + "bold");
            if (anchor is not null) anchor.AddBeforeSelf(b); else c.Add(b);
        }
        if (underline)
        {
            var ul = c.Element(Hh + "underline");
            if (ul is null) c.Add(ul = new XElement(Hh + "underline", new XAttribute("shape", "SOLID"), new XAttribute("color", "#000000")));
            ul.SetAttributeValue("type", "BOTTOM");
        }
        CharProps.Add(c);
        CharProps.SetAttributeValue("itemCnt", CharProps.Elements(Hh + "charPr").Count());
        charVariants[(charPrId, bold, underline)] = id;
        return id;
    }

    /// <summary>번호(개요/문단번호)만 뗀 문단 모양 id. 번호가 없으면 그대로.</summary>
    public int Unnumbered(int paraPrId) => ParaVariant(paraPrId, unnumber: true, resetIndent: false, align: null);

    /// <summary>
    /// 문단 모양 변형: 번호 떼기, 첫 줄 들여쓰기/내어쓰기 없애기(번호 뒤 글자 위치에 맞춤), 정렬 바꾸기.
    /// 바뀌는 것이 없으면 원래 id.
    /// </summary>
    public int ParaVariant(int paraPrId, bool unnumber, bool resetIndent, string? align)
    {
        var src = ParaPr(paraPrId);
        if (src is null) return paraPrId;
        bool numbered = HeadingOf(paraPrId).IsNumbered;
        bool hasIndent = src.Descendants(Hc + "intent").Any(e => Attr(e, "value") != 0);
        bool alignDiffers = align is not null && (string?)src.Element(Hh + "align")?.Attribute("horizontal") != align;
        if (!(unnumber && numbered) && !(resetIndent && hasIndent) && !alignDiffers) return paraPrId;

        var key = (paraPrId, unnumber && numbered, resetIndent && hasIndent, alignDiffers ? align : null);
        if (paraVariants.TryGetValue(key, out var cached)) return cached;

        var p = new XElement(src);
        int id = NextId(ParaProps, "paraPr");
        p.SetAttributeValue("id", id);
        if (unnumber && numbered)
        {
            var h = p.Element(Hh + "heading")!;
            h.SetAttributeValue("type", "NONE");
            h.SetAttributeValue("idRef", 0);
            h.SetAttributeValue("level", 0);
        }
        if (resetIndent) foreach (var e in p.Descendants(Hc + "intent")) e.SetAttributeValue("value", 0);
        if (alignDiffers) p.Element(Hh + "align")?.SetAttributeValue("horizontal", align);
        ParaProps.Add(p);
        ParaProps.SetAttributeValue("itemCnt", ParaProps.Elements(Hh + "paraPr").Count());
        paraVariants[key] = id;
        return id;
    }

    readonly Dictionary<(int, bool, bool, string?), int> paraVariants = [];

    /// <summary>
    /// 번호 붙는 문단(내어쓰기로 번호 자리를 만든 문단) 아래에 이어 쓰는 문단 모양: 첫 줄 들여쓰기를 없애고,
    /// 왼쪽 여백을 그 문단의 내어쓰기만큼 주어 번호 뒤 글자 위치에 맞춘다.
    /// </summary>
    public int AlignUnder(int paraPrId, int numberedParaPrId)
    {
        var src = ParaPr(paraPrId);
        var anchor = ParaPr(numberedParaPrId);
        if (src is null || anchor is null) return paraPrId;
        var p = new XElement(src);
        int id = NextId(ParaProps, "paraPr");
        p.SetAttributeValue("id", id);
        foreach (var margin in p.Descendants(Hh + "margin"))
        {
            // <hp:case>/<hp:default>/바로 아래 중 같은 자리의 기준 문단 값
            var container = margin.Parent!.Name.LocalName;
            var aMargin = anchor.Descendants(Hh + "margin").FirstOrDefault(m => m.Parent!.Name.LocalName == container)
                          ?? anchor.Descendants(Hh + "margin").FirstOrDefault();
            int hang = -Attr(aMargin?.Element(Hc + "intent") ?? anchor, "value");
            int aLeft = Attr(aMargin?.Element(Hc + "left") ?? anchor, "value");
            margin.Element(Hc + "intent")?.SetAttributeValue("value", 0);
            if (hang > 0) margin.Element(Hc + "left")?.SetAttributeValue("value", aLeft + hang);
        }
        ParaProps.Add(p);
        ParaProps.SetAttributeValue("itemCnt", ParaProps.Elements(Hh + "paraPr").Count());
        return id;
    }

    /// <summary>
    /// 다른 문서의 문단 모양에서 정렬·여백·들여쓰기·줄 간격·번호를 가져와, 이 문서 문단 모양의 변형으로 만든다.
    /// (탭·테두리 id는 문서마다 달라 이 문서 것을 그대로 둔다.)
    /// </summary>
    public int ImportParaShape(int baseParaPrId, XElement foreign, bool unnumber)
    {
        var src = ParaPr(baseParaPrId);
        if (src is null) return baseParaPrId;
        var p = new XElement(src);
        int id = NextId(ParaProps, "paraPr");
        p.SetAttributeValue("id", id);
        foreach (var name in new[] { "align", "heading" })
            if (foreign.Element(Hh + name) is { } f) p.Element(Hh + name)?.ReplaceWith(new XElement(f));
        // 여백/줄 간격은 <hp:switch> 안에 있거나 바로 아래에 있다.
        var fSwitch = foreign.Element(Hp + "switch");
        var pSwitch = p.Element(Hp + "switch");
        if (fSwitch is not null && pSwitch is not null) pSwitch.ReplaceWith(new XElement(fSwitch));
        else
            foreach (var name in new[] { "margin", "lineSpacing" })
                if (foreign.Element(Hh + name) is { } f) p.Element(Hh + name)?.ReplaceWith(new XElement(f));
        if (unnumber && p.Element(Hh + "heading") is { } h)
        {
            h.SetAttributeValue("type", "NONE");
            h.SetAttributeValue("idRef", 0);
            h.SetAttributeValue("level", 0);
        }
        ParaProps.Add(p);
        ParaProps.SetAttributeValue("itemCnt", ParaProps.Elements(Hh + "paraPr").Count());
        return id;
    }

    static int NextId(XElement parent, string local) =>
        parent.Elements(Hh + local).Select(e => Attr(e, "id", -1)).DefaultIfEmpty(-1).Max() + 1;
}
