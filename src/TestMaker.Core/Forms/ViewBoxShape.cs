using System.Text.RegularExpressions;
using System.Xml.Linq;
using static TestMaker.Core.Forms.Hx;

namespace TestMaker.Core.Forms;

/// <summary>
/// &lt;보 기&gt; 상자를 시험지 관례대로 "제목이 상자 윗선 가운데에 걸린" 모양으로 만든다(학교 양식에서 배운 구조).
///
///   ┌ 1행: [왼쪽: 아랫선] [제목: 선 없음, 1·2행 합침, 세로 가운데] [오른쪽: 아랫선]   ← 1행 아랫선 = 상자 윗선
///   │ 2행: [왼쪽: 윗선+왼선]                                   [오른쪽: 윗선+오른선]
///   └ 3행: [내용: 3칸 합침, 왼선+오른선+아랫선]
///
/// 제목 칸이 1·2행에 걸쳐 세로 가운데에 놓이므로, 1행 아랫선이 제목 글자 높이 가운데를 지나간다.
/// </summary>
public static class ViewBoxShape
{
    const int TitleWidth = 5000;   // "<보 기>" 칸 폭(HWPUNIT)
    const int Row0 = 740;          // 1행(선 위쪽) 높이
    const int Row1 = 440;          // 2행(선 아래쪽) 높이 — 제목 칸 높이 = Row0 + Row1
    const string Line = "0.12 mm";

    /// <summary>문서에서 첫 줄에 "보기"가 있는 표를 찾는다(띄어쓰기 무시).</summary>
    public static XElement? Find(XDocument section) => section.Descendants(Hp + "tbl").FirstOrDefault(t =>
        t.Elements(Hp + "tr").FirstOrDefault() is { } tr && Regex.Replace(DeepText(tr), @"\s+", "").Contains("보기"));

    /// <summary>
    /// "제목 줄 + 내용 줄" 2행 표를 윗선 제목 모양의 3×3 표로 바꾼다. 제목·내용 문단은 그대로 옮긴다.
    /// 이미 3행이면 아무것도 하지 않는다.
    /// </summary>
    public static void Apply(XElement tbl, HeaderInfo header)
    {
        var rows = tbl.Elements(Hp + "tr").ToList();
        if (rows.Count != 2) return;
        var titleParas = rows[0].Descendants(Hp + "subList").First().Elements(Hp + "p").ToList();
        var contentParas = rows[1].Descendants(Hp + "subList").First().Elements(Hp + "p").ToList();
        // 한글 COM으로 만든 표는 <hp:sz width>가 0으로 저장된다(실측) → 칸 폭(cellSz)에서 읽는다.
        int width = Math.Max(Attr(tbl.Element(Hp + "sz")!, "width"),
            rows.SelectMany(r => r.Descendants(Hp + "cellSz")).Select(c => Attr(c, "width")).DefaultIfEmpty(0).Max());
        if (width <= TitleWidth) throw new InvalidDataException("<보기> 상자 폭을 알 수 없습니다.");
        int side = (width - TitleWidth) / 2;
        tbl.Element(Hp + "sz")?.SetAttributeValue("width", width);

        int none = header.AddBorderFill(null, null, null, null);
        int lineBelow = header.AddBorderFill(null, null, null, Line);
        int topLeft = header.AddBorderFill(Line, null, Line, null);
        int topRight = header.AddBorderFill(null, Line, Line, null);
        int content = header.AddBorderFill(Line, Line, null, Line);

        XElement Empty() => new(titleParas[0].Name, titleParas[0].Attributes(),
            new XElement(Hp + "run", new XAttribute("charPrIDRef", Attr(titleParas[0].Elements(Hp + "run").First(), "charPrIDRef"))));

        foreach (var r in rows) r.Remove();
        tbl.Add(
            new XElement(Hp + "tr",
                Cell(0, 0, 1, 1, side, Row0, lineBelow, [Empty()]),
                Cell(1, 0, 1, 2, TitleWidth, Row0 + Row1, none, titleParas, noMargin: true),
                Cell(2, 0, 1, 1, side, Row0, lineBelow, [Empty()])),
            new XElement(Hp + "tr",
                Cell(0, 1, 1, 1, side, Row1, topLeft, [Empty()]),
                Cell(2, 1, 1, 1, side, Row1, topRight, [Empty()])),
            new XElement(Hp + "tr",
                Cell(0, 2, 3, 1, width, 1000, content, contentParas)));
        tbl.SetAttributeValue("rowCnt", 3);
        tbl.SetAttributeValue("colCnt", 3);
        tbl.SetAttributeValue("borderFillIDRef", none);
        StripLayout(tbl);
    }

    static XElement Cell(int col, int row, int colSpan, int rowSpan, int w, int h, int borderFill, IEnumerable<XElement> paras, bool noMargin = false) =>
        new(Hp + "tc",
            new XAttribute("name", ""), new XAttribute("header", 0), new XAttribute("hasMargin", noMargin ? 1 : 0),
            new XAttribute("protect", 0), new XAttribute("editable", 0), new XAttribute("dirty", 0),
            new XAttribute("borderFillIDRef", borderFill),
            new XElement(Hp + "subList",
                new XAttribute("id", ""), new XAttribute("textDirection", "HORIZONTAL"), new XAttribute("lineWrap", "BREAK"),
                new XAttribute("vertAlign", "CENTER"), new XAttribute("linkListIDRef", 0), new XAttribute("linkListNextIDRef", 0),
                new XAttribute("textWidth", 0), new XAttribute("textHeight", 0), new XAttribute("hasTextRef", 0), new XAttribute("hasNumRef", 0),
                paras),
            new XElement(Hp + "cellAddr", new XAttribute("colAddr", col), new XAttribute("rowAddr", row)),
            new XElement(Hp + "cellSpan", new XAttribute("colSpan", colSpan), new XAttribute("rowSpan", rowSpan)),
            new XElement(Hp + "cellSz", new XAttribute("width", w), new XAttribute("height", h)),
            noMargin
                ? new XElement(Hp + "cellMargin", new XAttribute("left", 0), new XAttribute("right", 0), new XAttribute("top", 0), new XAttribute("bottom", 0))
                : new XElement(Hp + "cellMargin", new XAttribute("left", 510), new XAttribute("right", 510), new XAttribute("top", 141), new XAttribute("bottom", 141)));
}
