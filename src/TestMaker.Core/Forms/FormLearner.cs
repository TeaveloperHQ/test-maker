using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestMaker.Core.Templates;
using static TestMaker.Core.Forms.Hx;

namespace TestMaker.Core.Forms;

/// <summary>학습 결과. <see cref="Save"/>로 양식 폴더를 쓴다.</summary>
public sealed class FormLearnResult
{
    public required ExamForm Form { get; init; }
    public required int QuestionCount { get; init; }
    public required string PreambleText { get; init; }
    /// <summary>머리 부분의 색 글씨(학교가 "채울 칸"으로 표시해 둔 곳) — 문맥과 함께.</summary>
    public IReadOnlyList<string> ColoredFields { get; init; } = [];

    internal string PrimaryPath { get; init; } = "";
    internal XDocument Header { get; init; } = new();
    internal XDocument BaseSection { get; init; } = new();
    internal XElement Boxes { get; init; } = new("boxes");

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, ExamForm.BaseFile);
        File.Copy(PrimaryPath, basePath, overwrite: true);
        using (var zip = ZipFile.Open(basePath, ZipArchiveMode.Update))
        {
            Replace(zip, HeaderPart, Header);
            Replace(zip, SectionPart, BaseSection);
            ReplaceText(zip, "Preview/PrvText.txt", PreambleText);
            // 문항이 이어지던 뒤쪽 구역이 있으면 버린다(첫 구역만 양식으로 쓴다).
            foreach (var extra in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^Contents/section[1-9]\d*\.xml$")).ToList())
                extra.Delete();
        }
        new XDocument(Boxes).Save(Path.Combine(dir, ExamForm.BoxesFile));
        Serialization.JsonStore.Save(Path.Combine(dir, ExamForm.FileName), Form);
    }
}

/// <summary>
/// 학교 시험지 문서에서 양식을 학습한다(XML만 읽음, 한글 불필요).
///
/// 문단마다 역할을 판별하고(발문/이어지는 발문/선택지/보기·자료 상자/그림/묶음 머리/빈 줄),
/// 주 문서(보통 학교 양식 파일)에서 역할별 스타일·문단 모양·상자 견본을 뽑는다.
/// 보조 문서(실제 시험지)는 문항 간격처럼 "실제로 어떻게 쓰는지" 통계에만 쓰고, 스타일은 이름으로 주 문서에 맞춘다.
/// </summary>
public static class FormLearner
{
    static readonly Regex Circled = new("[①-⑳]", RegexOptions.Compiled);
    static readonly Regex GroupRx = new(@"^\s*\[\s*\d+\s*[~～∼\-]\s*\d+\s*\]", RegexOptions.Compiled);
    static readonly Regex TypedNumberRx = new(@"^\s*(\d{1,3})(\s*[.)．]\s*)", RegexOptions.Compiled);
    static readonly Regex PointsRx = new(@"([\[\(（〔<])\s*(\d+(?:\.\d+)?)\s*점\s*([\]\)）〕>])", RegexOptions.Compiled);

    public static FormLearnResult Learn(string primaryHwpx, IReadOnlyList<string> extraHwpx, string name)
    {
        var header = Load(primaryHwpx, HeaderPart);
        var section = Load(primaryHwpx, SectionPart);
        var info = new HeaderInfo(header);
        var primary = Analyze(section, info);
        if (primary.Stems.Count == 0)
            throw new InvalidDataException(
                "문항을 찾지 못했습니다. 문항 번호가 개요/문단번호 스타일이거나 \"1.\" 형태의 글자로 문단 맨 앞에 있어야 합니다.");

        var extras = extraHwpx.Select(p => Analyze(Load(p, SectionPart), new HeaderInfo(Load(p, HeaderPart)))).ToList();
        var notes = new List<string>();
        var form = new ExamForm
        {
            Name = name,
            LearnedFrom = [Path.GetFileName(primaryHwpx), .. extraHwpx.Select(p => Path.GetFileName(p))],
            LearnedAt = DateTime.Now,
            Notes = notes,
        };

        // 발문
        form.Stem = Named(Mode(primary.Of(Role.Stem)) ?? new(), info);
        form.Kind = form.Stem.AutoNumber ? FormKind.Structured : FormKind.Unstructured;
        if (!form.Stem.AutoNumber)
        {
            var m = primary.Stems.Select(i => TypedNumberRx.Match(primary.Text[i])).FirstOrDefault(m => m.Success);
            if (m is not null) form.NumberFormat = "{0}" + Regex.Replace(m.Groups[2].Value, @"\s+", " ").TrimStart().TrimEnd() + " ";
        }
        form.PointsFormat = LearnPointsFormat([primary, .. extras]) ?? form.PointsFormat;

        // 이어지는 발문 / 묶음 머리 / 그림 / 빈 줄: 주 문서에 없으면 보조 문서에서 스타일 이름으로 가져온다.
        // 번호 붙는 스타일을 빌려 쓸 때는 번호를 떼고, 내어쓰기를 없애 발문 글자 위치에 맞춘다.
        RoleStyle Plain(RoleStyle r, string? align = null)
        {
            var c = Named(r.Clone(), info);
            // 실제 시험지의 모양(여백)을 가져오되, 첫 줄 내어쓰기는 없앤다. 선생님들이 그 자리를 공백으로
            // 메워 맞추던 것(예: "  이에 대한…")을 문단 모양으로 대신한다.
            c.ParaPr = r.ObservedParaShape is { } seen && align is null
                ? info.ParaVariant(info.ImportParaShape(r.ParaPr, seen, unnumber: true), unnumber: false, resetIndent: true, align: null)
                : info.ParaVariant(r.ParaPr, unnumber: true, resetIndent: r.AutoNumber, align: align);
            c.AutoNumber = false;
            return c;
        }
        form.StemContinuation = Plain(Pick(Role.StemCont, primary, extras, info) ?? form.Stem);
        if (form.Stem.AutoNumber)
            form.StemContinuation.ParaPr = info.AlignUnder(form.StemContinuation.ParaPr, form.Stem.ParaPr);
        form.GroupHeader = Pick(Role.GroupHeader, primary, extras, info) is { } gh ? Plain(gh) : Bold(Plain(form.Stem), info);
        form.Figure = Pick(Role.Figure, primary, extras, info) is { } fig ? Plain(fig) : Plain(form.Stem, align: "CENTER");
        form.Spacer = Plain(Pick(Role.Spacer, primary, extras, info) ?? form.Stem);

        // 문항 사이 빈 줄: 실제 시험지가 있으면 그쪽(양식은 빈칸이 과장되어 있다).
        var gaps = (extras.Count > 0 ? extras : [primary]).SelectMany(a => a.Gaps).ToList();
        if (gaps.Count > 0) form.SpacerLines = gaps.GroupBy(g => g).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

        // 선택지 배치
        form.ChoiceLayouts = LearnLayouts(primary, info, form.Stem, notes);

        // 상자
        var boxes = new XElement("boxes");
        form.ViewBox = LearnBox(primary, extras, Role.ViewBox, "view", boxes, info, allowAuto: true);
        form.PassageBox = LearnBox(primary, extras, Role.PassageBox, "passage", boxes, info, allowAuto: false);
        if (form.ViewBox is null) notes.Add("<보기> 상자 견본이 없어 <보기>는 글줄로만 넣습니다.");
        if (form.PassageBox is null) notes.Add("자료 상자 견본이 없어 자료는 글줄로만 넣습니다.");

        form.ColumnWidthMm = ColumnWidth(primary.Paras, primary.Stems[0]);

        var (baseSection, preambleText) = BuildBase(section, primary);
        var colored = ColoredFields(baseSection, info);
        foreach (var t in AutoTokens(baseSection, info)) notes.Add($"머리 부분 자동 채우기: {t}");
        return new FormLearnResult
        {
            Form = form,
            QuestionCount = primary.Stems.Count,
            PreambleText = preambleText,
            ColoredFields = colored,
            PrimaryPath = primaryHwpx,
            Header = header,
            BaseSection = baseSection,
            Boxes = boxes,
        };
    }

    // ── 문단 역할 판별 ────────────────────────────────────────

    internal enum Role { Stem, StemCont, GroupHeader, Figure, ViewBox, PassageBox, Choice, Spacer, Other }

    internal sealed record Cls(int Index, Role Role, int Style, int ParaPr, int CharPr, Heading Heading);

    internal sealed class Analysis
    {
        public required List<XElement> Paras { get; init; }
        public required List<string> Text { get; init; }
        public required HeaderInfo Header { get; init; }
        public List<int> Stems { get; } = [];
        public List<int> Groups { get; } = [];
        public List<Cls> Items { get; } = [];
        /// <summary>선택지 묶음(연속된 선택지 문단 번호들)</summary>
        public List<List<int>> ChoiceGroups { get; } = [];
        /// <summary>문항 끝 ~ 다음 문항 사이 빈 문단 수</summary>
        public List<int> Gaps { get; } = [];
        public int End { get; set; }

        public IEnumerable<Cls> Of(Role r) => Items.Where(i => i.Role == r);
    }

    internal static Analysis Analyze(XDocument section, HeaderInfo header)
    {
        var paras = section.Root!.Elements(Hp + "p").ToList();
        var a = new Analysis { Paras = paras, Text = paras.Select(Hx.Text).ToList(), Header = header };

        Cls C(int i, Role r)
        {
            var p = paras[i];
            int pp = Attr(p, "paraPrIDRef");
            var run = p.Elements(Hp + "run").FirstOrDefault(x => x.Elements(Hp + "t").Any(t => t.Value.Length > 0))
                      ?? p.Elements(Hp + "run").FirstOrDefault();
            int cp = run is null ? 0 : Attr(run, "charPrIDRef");
            return new Cls(i, r, Attr(p, "styleIDRef"), pp, cp, header.HeadingOf(pp));
        }

        // 발문: 번호(개요/문단번호 1수준)가 걸린, 글이 있는 문단. 원문자로 시작하면 선택지다.
        for (int i = 0; i < paras.Count; i++)
        {
            var h = header.HeadingOf(Attr(paras[i], "paraPrIDRef"));
            var t = a.Text[i].Trim();
            if (GroupRx.IsMatch(t)) { a.Groups.Add(i); continue; }
            if (h.IsNumbered && h.Level == 0 && t.Length > 0 && !Circled.IsMatch(t[..1])) a.Stems.Add(i);
        }
        if (a.Stems.Count < 2)
        {
            a.Stems.Clear();
            a.Stems.AddRange(FindQuestionSequence(a.Text));
        }
        if (a.Stems.Count == 0) return a;

        foreach (var g in a.Groups) a.Items.Add(C(g, Role.GroupHeader));
        var boundaries = a.Stems.Concat(a.Groups).Order().ToList();
        a.End = paras.Count;

        for (int k = 0; k < a.Stems.Count; k++)
        {
            int s = a.Stems[k];
            int e = boundaries.FirstOrDefault(b => b > s, paras.Count);
            a.Items.Add(C(s, Role.Stem));
            bool seenChoice = false;
            var group = new List<int>();
            int trailingBlank = 0;
            for (int i = s + 1; i < e; i++)
            {
                var t = a.Text[i].Trim();
                var objs = Objects(paras[i]).ToList();
                var h = header.HeadingOf(Attr(paras[i], "paraPrIDRef"));
                Role r;
                if (Table(paras[i]) is { } tbl) r = ClassifyTable(tbl);
                else if (t.Length == 0 && objs.Any(o => o is "pic" or "container" or "equation" or "ole" or "rect" or "connectLine")) r = Role.Figure;
                else if (t.Length > 0 && (Circled.IsMatch(t[..1]) || (h.IsNumbered && h.Level == 1))) r = Role.Choice;
                else if (t.Length == 0) r = Role.Spacer;
                else r = seenChoice ? Role.Other : Role.StemCont;

                if (r == Role.Choice) { seenChoice = true; group.Add(i); trailingBlank = 0; }
                else
                {
                    if (group.Count > 0) { a.ChoiceGroups.Add(group); group = []; }
                    trailingBlank = r == Role.Spacer && seenChoice ? trailingBlank + 1 : (r == Role.Spacer ? trailingBlank : 0);
                }
                a.Items.Add(C(i, r));
            }
            if (group.Count > 0) a.ChoiceGroups.Add(group);
            // 마지막 문항 뒤 빈 줄은 꼬리(서술형 칸, 확인란 앞 여백)라 간격으로 치지 않는다.
            if (k + 1 < a.Stems.Count && seenChoice) a.Gaps.Add(trailingBlank);
        }
        return a;
    }

    /// <summary>
    /// 무질서형: "1.", "2." … 로 시작하는 문단 중 1부터 이어지는 가장 긴 번호열을 고른다.
    /// 유의사항의 "1. 2. 3."과 길이가 같으면, 첫 항목 뒤에 선택지(①)가 따라오는 쪽, 그다음 뒤쪽을 고른다.
    /// </summary>
    internal static List<int> FindQuestionSequence(IReadOnlyList<string> texts)
    {
        var candidates = new List<(int Index, int Number)>();
        for (int i = 0; i < texts.Count; i++)
        {
            var m = TypedNumberRx.Match(texts[i]);
            if (m.Success && texts[i].Length > m.Length) candidates.Add((i, int.Parse(m.Groups[1].Value)));
        }

        List<int> best = [];
        int bestScore = -1;
        foreach (var start in candidates.Where(c => c.Number == 1))
        {
            var seq = new List<int>();
            int expected = 1;
            foreach (var c in candidates.Where(c => c.Index >= start.Index))
                if (c.Number == expected) { seq.Add(c.Index); expected++; }

            int firstEnd = seq.Count > 1 ? seq[1] : texts.Count;
            bool followedByChoice = Enumerable.Range(seq[0], firstEnd - seq[0]).Any(i => Circled.IsMatch(texts[i]));
            int score = seq.Count * 2 + (followedByChoice ? 1 : 0);
            if (score >= bestScore) { best = seq; bestScore = score; }
        }
        return best;
    }

    /// <summary>첫 줄에 "보 기"가 있으면 보기 상자, 1×1 표면 자료 상자, 나머지(자료표 등)는 그림처럼 취급.</summary>
    static Role ClassifyTable(XElement tbl)
    {
        var firstRow = tbl.Elements(Hp + "tr").FirstOrDefault();
        var head = firstRow is null ? "" : Regex.Replace(DeepText(firstRow), @"\s+", "");
        if (head.Contains("보기")) return Role.ViewBox;
        if (Attr(tbl, "rowCnt") == 1 && Attr(tbl, "colCnt") == 1) return Role.PassageBox;
        return Role.Figure;
    }

    // ── 역할별 스타일 고르기 ─────────────────────────────────────

    static RoleStyle? Mode(IEnumerable<Cls> items) => items
        .GroupBy(c => (c.Style, c.ParaPr, c.CharPr))
        .OrderByDescending(g => g.Count())
        .Select(g => g.First())
        .Select(c => new RoleStyle { StyleId = c.Style, ParaPr = c.ParaPr, CharPr = c.CharPr, AutoNumber = c.Heading.IsNumbered })
        .FirstOrDefault();

    /// <summary>주 문서에서 보이면 그대로, 아니면 보조 문서에서 가장 많이 쓴 스타일 이름을 주 문서 header에서 찾는다.</summary>
    static RoleStyle? Pick(Role role, Analysis primary, List<Analysis> extras, HeaderInfo info)
    {
        var own = Mode(primary.Of(role));
        if (own is not null) return Named(own, info);
        var used = extras.SelectMany(e => e.Of(role).Select(c => (Doc: e, Cls: c, Name: e.Header.StyleName(c.Style))))
            .Where(x => x.Name.Length > 0).GroupBy(x => x.Name).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (used is null || info.StyleByName(used.Key) is not { } st) return null;
        // 그 스타일로 쓴 문단들 중 가장 흔한 문단 모양(여백·들여쓰기)을 기억해 둔다.
        var seen = used.GroupBy(x => (x.Doc, x.Cls.ParaPr)).OrderByDescending(g => g.Count()).First().First();
        int pp = Attr(st, "paraPrIDRef");
        return new RoleStyle
        {
            StyleName = used.Key,
            StyleId = Attr(st, "id"),
            ParaPr = pp,
            CharPr = Attr(st, "charPrIDRef"),
            AutoNumber = info.HeadingOf(pp).IsNumbered,
            ObservedParaShape = seen.Doc.Header.ParaPr(seen.Cls.ParaPr),
        };
    }

    static RoleStyle Named(RoleStyle r, HeaderInfo info)
    {
        r.StyleName = info.StyleName(r.StyleId);
        return r;
    }

    static RoleStyle Unnumbered(RoleStyle r, HeaderInfo info, string? note, List<string> notes)
    {
        var c = Named(r.Clone(), info);
        c.ParaPr = info.Unnumbered(r.ParaPr);
        c.AutoNumber = false;
        if (note is not null) notes.Add(note);
        return c;
    }

    static RoleStyle Bold(RoleStyle r, HeaderInfo info)
    {
        var c = r.Clone();
        c.CharPr = info.CharVariant(r.CharPr, bold: true, underline: false);
        return c;
    }

    // ── 선택지 배치 ───────────────────────────────────────────

    /// <summary>
    /// 양식의 선택지 묶음마다 줄 수·줄당 개수·간격을 배운다. 줄 수별로 처음 나온 것을 견본으로 쓴다.
    /// 규칙(학교 관행): 5행은 개요 자동번호, 나머지(1·2·3행)는 원문자를 직접 입력한다.
    /// </summary>
    static List<ChoiceLayout> LearnLayouts(Analysis a, HeaderInfo info, RoleStyle stem, List<string> notes)
    {
        var layouts = new Dictionary<int, ChoiceLayout>();
        RoleStyle? autoStyle = null;

        foreach (var g in a.ChoiceGroups)
        {
            var lines = g.Select(i => a.Text[i]).ToList();
            var heads = g.Select(i => a.Header.HeadingOf(Attr(a.Paras[i], "paraPrIDRef"))).ToList();
            var perLine = new List<int>();
            var seps = new List<List<string>>();
            for (int l = 0; l < lines.Count; l++)
            {
                bool autoFirst = heads[l].IsNumbered && heads[l].Level == 1 && !Circled.IsMatch(lines[l].TrimStart()[..1]);
                var (count, sep) = SplitLine(lines[l], autoFirst);
                perLine.Add(count);
                seps.Add(sep);
            }

            bool onePerLine = perLine.All(n => n == 1);
            if (onePerLine && heads.All(h => h.IsNumbered && h.Level == 1))
            {
                var c = ItemAt(a, g[0]);
                autoStyle ??= new RoleStyle { StyleId = c.Style, ParaPr = c.ParaPr, CharPr = c.CharPr, AutoNumber = true, StyleName = info.StyleName(c.Style) };
                continue;
            }
            if (layouts.ContainsKey(lines.Count)) continue;

            // 직접 입력 줄: 번호 없는 줄(보통 둘째 줄)의 모양을 쓰고, 그래도 번호가 있으면 뗀다.
            var src = ItemAt(a, g.FirstOrDefault(i => !a.Header.HeadingOf(Attr(a.Paras[i], "paraPrIDRef")).IsNumbered, g[0]));
            var style = new RoleStyle { StyleId = src.Style, ParaPr = info.Unnumbered(src.ParaPr), CharPr = src.CharPr, StyleName = info.StyleName(src.Style) };
            layouts[lines.Count] = new ChoiceLayout { Lines = lines.Count, PerLine = perLine, Style = style, Separators = seps };
        }

        // 5행 자동번호: 견본이 없으면 발문과 같은 번호 체계의 2수준 스타일을 header에서 찾는다.
        if (autoStyle is null)
        {
            var stemHead = info.HeadingOf(stem.ParaPr);
            var st = info.AllStyles.FirstOrDefault(s =>
            {
                var h = info.HeadingOf(Attr(s, "paraPrIDRef"));
                return stemHead.IsNumbered && h.Type == stemHead.Type && h.NumberingId == stemHead.NumberingId && h.Level == 1;
            });
            if (st is not null)
                autoStyle = new RoleStyle { StyleId = Attr(st, "id"), ParaPr = Attr(st, "paraPrIDRef"), CharPr = Attr(st, "charPrIDRef"), AutoNumber = true, StyleName = (string?)st.Attribute("name") ?? "" };
        }
        var result = layouts.Values.OrderBy(l => l.Lines).ToList();
        if (autoStyle is not null)
            result.Add(new ChoiceLayout { Lines = 5, PerLine = [1, 1, 1, 1, 1], Style = autoStyle, Auto = true });
        else
        {
            var typed = result.FirstOrDefault()?.Style ?? stem;
            result.Add(new ChoiceLayout { Lines = 5, PerLine = [1, 1, 1, 1, 1], Style = typed, Separators = [[], [], [], [], []] });
            notes.Add("선택지 자동번호 스타일이 없어 5행 선택지도 원문자를 직접 입력합니다.");
        }
        return result;
    }

    static Cls ItemAt(Analysis a, int index) => a.Items.First(c => c.Index == index);

    /// <summary>선택지 줄 하나를 원문자 기준으로 나눠, 개수와 각 선택지 뒤 간격(탭·공백)을 돌려준다.</summary>
    internal static (int Count, List<string> Separators) SplitLine(string line, bool autoFirst)
    {
        var marks = Circled.Matches(line).Select(m => m.Index).ToList();
        var starts = new List<int>();
        if (autoFirst) starts.Add(0);
        starts.AddRange(marks);
        var seps = new List<string>();
        for (int j = 0; j + 1 < starts.Count; j++)
        {
            var seg = line[starts[j]..starts[j + 1]];
            var trimmed = seg.TrimEnd(' ', '\t');
            seps.Add(seg[trimmed.Length..]);
        }
        return (starts.Count, seps);
    }

    // ── 상자 ─────────────────────────────────────────────────

    /// <summary>
    /// 상자 표는 주 문서의 첫 견본을 통째로 쓴다. 상자 안 줄 스타일은 실제 시험지에서 가장 많이 쓴 스타일 이름을
    /// 우선한다(양식 파일과 실제 사용이 다를 수 있다. 예: 양식은 직접 친 ㄱ., 실제는 "박스 ㄱㄴㄷ" 자동).
    /// </summary>
    static BoxProto? LearnBox(Analysis a, List<Analysis> extras, Role role, string key, XElement boxes, HeaderInfo info, bool allowAuto)
    {
        var item = a.Of(role).FirstOrDefault();
        if (item is null) return null;
        var para = new XElement(a.Paras[item.Index]);
        StripLayout(para);
        var cell = ContentCell(Table(para)!);
        var line = cell.Descendants(Hp + "p").First();
        int styleId = Attr(line, "styleIDRef");
        int pp = Attr(line, "paraPrIDRef");
        var run = line.Elements(Hp + "run").FirstOrDefault(r => r.Elements(Hp + "t").Any()) ?? line.Elements(Hp + "run").First();
        int cp = Attr(run, "charPrIDRef");

        var usedName = extras.SelectMany(e => e.Of(role).Select(c => Table(e.Paras[c.Index]))
                .Where(t => t is not null)
                .Select(t => e.Header.StyleName(Attr(ContentCell(t!).Descendants(Hp + "p").First(), "styleIDRef"))))
            .Where(n => n.Length > 0).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (usedName is not null && info.StyleByName(usedName) is { } st)
        {
            styleId = Attr(st, "id");
            pp = Attr(st, "paraPrIDRef");
            cp = Attr(st, "charPrIDRef");
        }

        bool auto = info.HeadingOf(pp).IsNumbered && allowAuto;
        var proto = new BoxProto
        {
            AutoLabels = auto,
            Line = new RoleStyle
            {
                StyleId = styleId,
                StyleName = info.StyleName(styleId),
                ParaPr = auto ? pp : info.ParaVariant(pp, unnumber: true, resetIndent: false, align: null),
                CharPr = cp,
                AutoNumber = auto,
            },
        };
        boxes.Add(new XElement(key, para));
        return proto;
    }

    /// <summary>상자 표에서 내용이 들어가는 칸: 문단이 가장 많은(같으면 가장 높은) 칸.</summary>
    public static XElement ContentCell(XElement tbl) => tbl.Descendants(Hp + "tc")
        .OrderByDescending(tc => tc.Descendants(Hp + "p").Count())
        .ThenByDescending(tc => Attr(tc.Element(Hp + "cellSz") ?? tc, "height"))
        .First();

    // ── 배점, 단 폭 ──────────────────────────────────────────

    static string? LearnPointsFormat(IEnumerable<Analysis> docs) => docs
        .SelectMany(a => a.Stems.SelectMany(s => a.Items.Where(c => c.Index >= s && c.Role is Role.Stem or Role.StemCont)
            .Select(c => PointsRx.Match(a.Text[c.Index]))))
        .Where(m => m.Success)
        .Select(m => $"{m.Groups[1].Value}{{0}}점{m.Groups[3].Value}")
        .GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    /// <summary>1번 문항에 적용되는 단 폭(1번 문항까지 나온 마지막 단 설정 기준).</summary>
    static double ColumnWidth(List<XElement> paras, int firstStem)
    {
        var pagePr = paras.First().Descendants(Hp + "pagePr").FirstOrDefault();
        var margin = pagePr?.Element(Hp + "margin");
        double width = pagePr is null ? 59528 : Attr(pagePr, "width", 59528);
        if ((string?)pagePr?.Attribute("landscape") == "NARROWLY") width = Attr(pagePr!, "height", 84188);
        double body = width - (margin is null ? 8504 : Attr(margin, "left") + Attr(margin, "right"));
        var col = paras.Take(firstStem + 1).SelectMany(p => p.Descendants(Hp + "colPr")).LastOrDefault();
        int n = col is null ? 1 : Math.Max(1, Attr(col, "colCount", 1));
        double gap = col is null ? 0 : Attr(col, "sameGap");
        return HwpUnit.ToMm((body - gap * (n - 1)) / n);
    }

    /// <summary>머리·꼬리(머리말/꼬리말 포함)에서 검정이 아닌 색 글씨를 "문단 글 [색 글씨]" 형태로 모은다.</summary>
    static List<string> ColoredFields(XDocument baseSection, HeaderInfo info)
    {
        var result = new List<string>();
        foreach (var p in baseSection.Descendants(Hp + "p"))
        {
            var runs = p.Elements(Hp + "run").Where(r => r.Elements(Hp + "t").Any()).ToList();
            if (!runs.Any(Colored)) continue;
            var text = string.Concat(runs.Select(r => Colored(r) ? $"[{RunText(r)}]" : RunText(r))).Trim();
            if (text.Length > 0 && !result.Contains(text)) result.Add(text);
        }
        return result;

        bool Colored(XElement run)
        {
            var color = (string?)info.CharPr(Attr(run, "charPrIDRef"))?.Attribute("textColor") ?? "#000000";
            return color is not ("#000000" or "#FFFFFF") && RunText(run).Trim().Length > 0;
        }
        static string RunText(XElement run) => string.Concat(run.Elements(Hp + "t").Select(t => t.Value));
    }

    static readonly string[] Subjects =
        ["국어", "영어", "수학", "과학", "사회", "역사", "도덕", "기술", "가정", "기술가정", "정보", "음악", "미술", "체육", "한문", "일본어", "중국어", "생활과학"];
    static readonly (Regex Rx, string Token)[] TextTokens =
    [
        (new(@"\d{4}학년도\s*\d\s*학기\s*\d\s*차\s*지필평가"), "{{제목}}"),
        (new(@"\d{4}년\s*\d*\s*월\s*\d*\s*일\s*\d*\s*교시"), "{{일시}}"),
    ];

    /// <summary>
    /// 머리·꼬리의 시험마다 바뀌는 칸을 자리표시로 바꾼다.
    ///  - 색 글씨 칸: "시험지 [4]쪽" → {{쪽수}}, "선택형 [18]문항" → {{문항수}}, "[ 쪽" → 전체 쪽수, 과목명 → {{과목}}
    ///  - 글자 형식: "2025학년도 1학기 1차 지필평가" → {{제목}}, "2025년 4월 일 교시" → {{일시}}
    /// 바꾼 내용을 돌려준다(학습 결과에 알림).
    /// </summary>
    static List<string> AutoTokens(XDocument baseSection, HeaderInfo info)
    {
        var done = new List<string>();
        foreach (var p in baseSection.Descendants(Hp + "p"))
        {
            var runs = p.Elements(Hp + "run").Where(r => r.Elements(Hp + "t").Any()).ToList();
            var before = "";
            foreach (var run in runs)
            {
                var t = run.Elements(Hp + "t").First();
                var text = t.Value;
                var color = (string?)info.CharPr(Attr(run, "charPrIDRef"))?.Attribute("textColor") ?? "#000000";
                string? replaced = null;
                if (color is not ("#000000" or "#FFFFFF") && t.Elements().Count() == 0)
                {
                    var compact = Regex.Replace(text, @"\s+", "");
                    if (Regex.IsMatch(text, @"^\s*\d*\s*$") && before.TrimEnd().EndsWith("시험지")) replaced = "{{쪽수}}";
                    else if (Regex.IsMatch(text, @"^\s*\d*\s*$") && before.TrimEnd().EndsWith("선택형")) replaced = "{{문항수}}";
                    else if (Regex.IsMatch(text, @"^\s*\[\s*\d*\s*쪽\s*$")) replaced = "[{{쪽수}}쪽";
                    else if (Regex.IsMatch(text, @"^\s*\d*\s*쪽\s*$") && before.TrimEnd().EndsWith('[')) replaced = "{{쪽수}}쪽";
                    else if (Subjects.Contains(compact)) replaced = compact.Length < text.Trim().Length ? "{{과목띄움}}" : "{{과목}}";
                }
                if (replaced is not null)
                {
                    done.Add($"\"{text.Trim()}\" → {replaced}");
                    t.Value = replaced;
                }
                else if (t.Elements().Count() == 0)
                {
                    foreach (var (rx, token) in TextTokens)
                        if (rx.Match(text) is { Success: true } m)
                        {
                            done.Add($"\"{m.Value}\" → {token}");
                            t.Value = text = text.Replace(m.Value, token);
                        }
                }
                before += text;
            }
        }
        return done;
    }

    // ── 머리·꼬리 ───────────────────────────────────────────

    /// <summary>
    /// 첫 문항(또는 첫 묶음 머리) 앞 문단 + 자리표시 문단 + 꼬리(맨 끝 연속된 내용 문단, 예: 확인 사항 상자).
    /// 버려지는 문단에 있던 머리말/꼬리말/쪽 번호 같은 컨트롤은 자리표시 문단으로 옮겨 살린다.
    /// </summary>
    static (XDocument Section, string PreambleText) BuildBase(XDocument section, Analysis a)
    {
        int first = a.Stems.Concat(a.Groups).Min();
        int tailStart = a.Paras.Count;
        for (int i = a.Paras.Count - 1; i > a.Stems[^1]; i--)
        {
            bool content = a.Text[i].Trim().Length > 0 || Objects(a.Paras[i]).Any(o => o is not "t");
            if (!content) { if (tailStart < a.Paras.Count) break; continue; }
            tailStart = i;
        }
        // 마지막 문항의 선택지 등은 꼬리가 아니다.
        var lastChoice = a.ChoiceGroups.LastOrDefault()?.Last() ?? a.Stems[^1];
        if (tailStart <= lastChoice) tailStart = a.Paras.Count;

        var doc = new XDocument(section);
        var paras = doc.Root!.Elements(Hp + "p").ToList();
        var marker = new XElement(paras[first]);
        foreach (var run in marker.Elements(Hp + "run").ToList())
        {
            foreach (var child in run.Elements().ToList())
                if (child.Name != Hp + "secPr" && child.Name != Hp + "ctrl") child.Remove();
            if (!run.HasElements) run.Remove();
        }
        // 버려지는 문단의 머리말/꼬리말/쪽번호 등 컨트롤을 살린다(단 설정은 제외: 위치가 바뀌면 의미가 달라짐).
        foreach (var p in paras.Skip(first).Take(tailStart - first))
            foreach (var ctrl in p.Elements(Hp + "run").Elements(Hp + "ctrl").Where(c => !c.Elements(Hp + "colPr").Any()))
                if (!ReferenceEquals(p, paras[first]))
                    marker.Add(new XElement(Hp + "run", new XAttribute("charPrIDRef", 0), new XElement(ctrl)));
        StripLayout(marker);
        marker.Add(new XElement(Hp + "run", new XAttribute("charPrIDRef", Attr(marker.Elements(Hp + "run").FirstOrDefault() ?? paras[first].Elements(Hp + "run").First(), "charPrIDRef")),
            new XElement(Hp + "t", ExamForm.Marker)));

        foreach (var p in paras.Skip(first)) p.Remove();
        doc.Root.Add(marker);
        foreach (var p in a.Paras.Skip(tailStart)) doc.Root.Add(new XElement(p));

        var text = string.Join("\n", a.Text.Take(first).Where(t => t.Trim().Length > 0)) + "\n" + ExamForm.Marker;
        return (doc, text);
    }
}
