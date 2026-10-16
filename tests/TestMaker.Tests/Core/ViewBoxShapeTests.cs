using System.Xml.Linq;
using TestMaker.Core.Forms;

namespace TestMaker.Tests.Core;

/// <summary>&lt;보 기&gt; 상자를 "제목이 윗선에 걸린" 3×3 구조로 바꾸는 XML 변환.</summary>
public sealed class ViewBoxShapeTests
{
    static readonly XNamespace Hp = Hx.Hp;

    static XElement Cell(string text, int width) => new(Hp + "tc",
        new XElement(Hp + "subList", new XElement(Hp + "p", new XAttribute("paraPrIDRef", 0), new XAttribute("styleIDRef", 0),
            new XElement(Hp + "run", new XAttribute("charPrIDRef", 0), new XElement(Hp + "t", text)))),
        new XElement(Hp + "cellSz", new XAttribute("width", width), new XAttribute("height", 282)));

    /// <summary>한글 COM이 만든 것과 같은 모양: 표 폭 0, 칸 폭만 있음.</summary>
    static XElement TwoRowBox() => new(Hp + "tbl", new XAttribute("rowCnt", 2), new XAttribute("colCnt", 1),
        new XElement(Hp + "sz", new XAttribute("width", 0), new XAttribute("height", 0)),
        new XElement(Hp + "tr", Cell("<보 기>", 27000)),
        new XElement(Hp + "tr", Cell("첫째 진술", 27000)));

    [Fact]
    public void Builds_title_on_border_layout_with_widths_from_cells()
    {
        var header = new HeaderInfo(Hx.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "school_exam.hwpx"), Hx.HeaderPart));
        var tbl = TwoRowBox();
        ViewBoxShape.Apply(tbl, header);

        Assert.Equal("3", (string?)tbl.Attribute("rowCnt"));
        var cells = tbl.Descendants(Hp + "tc").ToList();
        Assert.Equal(6, cells.Count);
        Assert.All(cells, c => Assert.True(int.Parse((string)c.Element(Hp + "cellSz")!.Attribute("width")!) > 0));

        var title = cells.Single(c => Hx.DeepText(c) == "<보 기>");
        Assert.Equal("2", (string?)title.Element(Hp + "cellSpan")!.Attribute("rowSpan"));
        var content = cells.Single(c => Hx.DeepText(c) == "첫째 진술");
        Assert.Equal("3", (string?)content.Element(Hp + "cellSpan")!.Attribute("colSpan"));
        Assert.Equal("27000", (string?)content.Element(Hp + "cellSz")!.Attribute("width"));
        // 학습기가 여전히 <보기> 상자로 알아보고, 내용 칸을 찾을 수 있어야 한다.
        Assert.Same(content, FormLearner.ContentCell(tbl));
    }

    [Fact]
    public void Finds_view_box_by_title_ignoring_spaces()
    {
        var doc = new XDocument(new XElement(Hp + "sec", new XElement(Hp + "p", new XElement(Hp + "run", TwoRowBox()))));
        Assert.NotNull(ViewBoxShape.Find(doc));
    }
}
