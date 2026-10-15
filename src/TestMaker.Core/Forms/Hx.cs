using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TestMaker.Core.Forms;

/// <summary>HWPX XML 도우미: 네임스페이스, 문단 글 읽기, 문단/런 만들기, 패키지 읽고 쓰기.</summary>
public static class Hx
{
    public static readonly XNamespace Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    public static readonly XNamespace Hh = "http://www.hancom.co.kr/hwpml/2011/head";
    public static readonly XNamespace Hc = "http://www.hancom.co.kr/hwpml/2011/core";

    public const string HeaderPart = "Contents/header.xml";
    public const string SectionPart = "Contents/section0.xml";

    /// <summary>문단의 본문 글(표·그림 안 글 제외). 탭은 \t.</summary>
    public static string Text(XElement p) => string.Concat(p.Elements(Hp + "run").Elements(Hp + "t").Select(TText));

    /// <summary>표 안까지 포함한 모든 글.</summary>
    public static string DeepText(XElement e) => string.Concat(e.Descendants(Hp + "t").Select(TText));

    static string TText(XElement t) => string.Concat(t.Nodes().Select(n => n switch
    {
        XText x => x.Value,
        XElement e when e.Name.LocalName == "tab" => "\t",
        XElement e when e.Name.LocalName == "lineBreak" => "\n",
        _ => "",
    }));

    /// <summary>문단의 run 바로 아래에 있는 개체 이름들(tbl, pic, container, equation, secPr, ctrl …).</summary>
    public static IEnumerable<string> Objects(XElement p) =>
        p.Elements(Hp + "run").Elements().Where(e => e.Name != Hp + "t").Select(e => e.Name.LocalName);

    public static XElement? Table(XElement p) => p.Elements(Hp + "run").Elements(Hp + "tbl").FirstOrDefault();

    public static int Attr(XElement e, string name, int fallback = 0) =>
        int.TryParse((string?)e.Attribute(name), out var v) ? v : fallback;

    public static XElement Para(int styleId, int paraPr, IEnumerable<XElement> runs) =>
        new(Hp + "p",
            new XAttribute("id", "0"),
            new XAttribute("paraPrIDRef", paraPr),
            new XAttribute("styleIDRef", styleId),
            new XAttribute("pageBreak", "0"),
            new XAttribute("columnBreak", "0"),
            new XAttribute("merged", "0"),
            runs);

    public static XElement EmptyRun(int charPr) => new(Hp + "run", new XAttribute("charPrIDRef", charPr));

    /// <summary>글 조각(문자열, 탭, 형광펜 시작/끝)으로 run 하나를 만든다.</summary>
    public static XElement Run(int charPr, IEnumerable<object> pieces)
    {
        var t = new XElement(Hp + "t");
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case string s: t.Add(new XText(s)); break;
                case XElement e: t.Add(e); break;
            }
        }
        return new XElement(Hp + "run", new XAttribute("charPrIDRef", charPr), t);
    }

    public static XElement Tab() =>
        new(Hp + "tab", new XAttribute("width", "4000"), new XAttribute("leader", "0"), new XAttribute("type", "1"));

    public static XElement MarkpenBegin(string color = "#FFFF00") => new(Hp + "markpenBegin", new XAttribute("color", color));
    public static XElement MarkpenEnd() => new(Hp + "markpenEnd");

    /// <summary>
    /// 줄 배치 캐시(linesegarray)를 지운다. 한글이 열 때 다시 계산한다.
    /// 복제·수정한 문단에 옛 캐시가 남으면 글자가 겹쳐 보일 수 있다.
    /// </summary>
    public static void StripLayout(XElement e)
    {
        foreach (var l in e.DescendantsAndSelf(Hp + "linesegarray").ToList()) l.Remove();
    }

    public static XDocument Load(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part) ?? throw new InvalidDataException($"HWPX 안에 {part}이(가) 없습니다.");
        using var s = entry.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    public static XDocument Load(string hwpx, string part)
    {
        using var zip = ZipFile.OpenRead(hwpx);
        return Load(zip, part);
    }

    /// <summary>패키지의 XML 파트를 바꿔 쓴다. mimetype이 첫 항목인 규칙은 기존 항목을 건드리지 않아 유지된다.</summary>
    public static void Replace(ZipArchive zip, string part, XDocument doc)
    {
        zip.GetEntry(part)?.Delete();
        using var s = zip.CreateEntry(part, CompressionLevel.Optimal).Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        doc.Save(w);
    }

    public static void ReplaceText(ZipArchive zip, string part, string text)
    {
        zip.GetEntry(part)?.Delete();
        using var s = zip.CreateEntry(part, CompressionLevel.Optimal).Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }
}
