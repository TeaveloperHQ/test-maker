using System.Xml.Linq;
using TestMaker.Core.Forms;

namespace TestMaker.Tests.Core;

public sealed class StyleTagsTests
{
    static readonly XNamespace Hp = Hx.Hp;

    [Fact]
    public void Applies_style_from_tag_and_removes_tag()
    {
        var header = new HeaderInfo(Hx.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "school_exam.hwpx"), Hx.HeaderPart));
        var style = header.AllStyles.First(s => (string?)s.Attribute("name") == "본문");
        int id = int.Parse((string)style.Attribute("id")!);

        var p = new XElement(Hp + "p", new XAttribute("paraPrIDRef", 0), new XAttribute("styleIDRef", 0),
            new XElement(Hp + "run", new XAttribute("charPrIDRef", 5), new XElement(Hp + "t", StyleTags.Tag(id) + "발문입니다")));
        var untouched = new XElement(Hp + "p", new XAttribute("styleIDRef", 0),
            new XElement(Hp + "run", new XElement(Hp + "t", "§ 표시 아님")));
        var doc = new XDocument(new XElement(Hp + "sec", p, untouched));

        Assert.Equal(1, StyleTags.Apply(doc, header));
        Assert.Equal(id.ToString(), (string?)p.Attribute("styleIDRef"));
        Assert.Equal((string?)style.Attribute("paraPrIDRef"), (string?)p.Attribute("paraPrIDRef"));
        Assert.Equal((string?)style.Attribute("charPrIDRef"), (string?)p.Element(Hp + "run")!.Attribute("charPrIDRef"));
        Assert.Equal("발문입니다", Hx.Text(p));
        Assert.Equal("0", (string?)untouched.Attribute("styleIDRef"));
    }
}
