using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestMaker.Core.Models;
using TestMaker.Core.Text;
using static TestMaker.Core.Forms.Hx;

namespace TestMaker.Core.Forms;

public enum PlaceholderKind { Equation, Image }

/// <summary>XML로는 넣기 어려운 개체(수식·그림) 자리. 한글에서 이 글자를 찾아 개체로 바꾼다.</summary>
public sealed record Placeholder(string Key, PlaceholderKind Kind, string Value, double SizePt, double? WidthMm = null);

/// <summary>
/// 시험 + 학습한 양식 → HWPX. 학교 문서(base.hwpx)의 자리표시 문단을 문항 문단으로 바꾼다.
/// 모든 문단은 학교 스타일을 쓰므로 번호(개요/문단번호)·탭·상자 모양이 학교 것 그대로 따라온다.
/// 직접 서식은 문장 안 굵게/밑줄과 원안지 형광펜뿐이다.
/// </summary>
public sealed class FormAssembler
{
    static readonly Regex BoxLabelRx = new(@"^\s*[ㄱ-ㅎ]\s*\.\s*", RegexOptions.Compiled);
    const string Labels = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎ";

    readonly ExamForm form;
    readonly HeaderInfo header;
    readonly XElement boxes;
    readonly bool original;
    readonly List<Placeholder> placeholders = [];
    readonly string? examDir;
    readonly double defaultTabHu;

    FormAssembler(ExamForm form, HeaderInfo header, XElement boxes, bool original, string? examDir, double defaultTabHu)
    {
        this.form = form;
        this.header = header;
        this.boxes = boxes;
        this.original = original;
        this.examDir = examDir;
        this.defaultTabHu = defaultTabHu;
    }

    /// <summary>문서 기본 탭 간격(실제 HWPUNIT). secPr의 tabStopVal(실제 값), 없으면 tabStop의 절반, 그것도 없으면 4000.</summary>
    static double DefaultTab(XDocument section)
    {
        var sec = section.Descendants(Hp + "secPr").FirstOrDefault();
        if (sec is null) return 4000;
        int val = Attr(sec, "tabStopVal");
        if (val > 0) return val;
        int stop = Attr(sec, "tabStop");
        return stop > 0 ? stop / 2.0 : 4000;
    }

    /// <param name="original">원안지: 정답 선택지에 형광펜.</param>
    /// <returns>한글에서 채워야 할 수식·그림 자리표시.</returns>
    /// <param name="extraTokens">시험 정보 외 자리표시 값(예: 다 그린 뒤 알게 되는 쪽수).</param>
    public static IReadOnlyList<Placeholder> Assemble(Exam exam, ExamForm form, string formDir, string? examDir, string outputHwpx, bool original,
        IReadOnlyDictionary<string, string>? extraTokens = null)
    {
        File.Copy(Path.Combine(formDir, ExamForm.BaseFile), outputHwpx, overwrite: true);
        var boxesPath = Path.Combine(formDir, ExamForm.BoxesFile);
        var boxes = File.Exists(boxesPath) ? XElement.Load(boxesPath) : new XElement("boxes");

        using var zip = ZipFile.Open(outputHwpx, ZipArchiveMode.Update);
        var headerDoc = Load(zip, HeaderPart);
        var section = Load(zip, SectionPart);
        var a = new FormAssembler(form, new HeaderInfo(headerDoc), boxes, original, examDir, DefaultTab(section));

        var tokens = exam.Info.ToTokens(exam).ToDictionary();
        foreach (var (k, v) in extraTokens ?? new Dictionary<string, string>()) tokens[k] = v;
        FillTokens(section, tokens);
        var marker = section.Root!.Elements(Hp + "p").FirstOrDefault(p => Hx.Text(p).Contains(ExamForm.Marker))
            ?? throw new InvalidDataException($"양식의 base.hwpx에 문항 자리표시 {ExamForm.Marker}가 없습니다.");

        var paras = a.Questions(exam);
        if (paras.Count == 0) paras.Add(Hx.Para(form.Spacer.StyleId, form.Spacer.ParaPr, [EmptyRun(form.Spacer.CharPr)]));
        // 자리표시 문단에 붙어 있던 머리말/쪽번호/단 설정 컨트롤은 첫 문단 맨 앞으로 옮긴다.
        var ctrlRuns = marker.Elements(Hp + "run").Where(r => r.Elements().Any(e => e.Name == Hp + "secPr" || e.Name == Hp + "ctrl")).ToList();
        foreach (var r in ctrlRuns.AsEnumerable().Reverse()) paras[0].Elements(Hp + "run").First().AddBeforeSelf(new XElement(r));
        marker.ReplaceWith(paras);

        Replace(zip, HeaderPart, headerDoc);
        Replace(zip, SectionPart, section);
        ReplaceText(zip, "Preview/PrvText.txt", exam.Info.Title);
        return a.placeholders;
    }

    /// <summary>양식 머리·꼬리에 {{키}} 자리표시가 있는지.</summary>
    public static bool HasToken(string formDir, string key)
    {
        var section = Load(Path.Combine(formDir, ExamForm.BaseFile), SectionPart);
        return section.Descendants(Hp + "t").Any(t => t.Value.Contains("{{" + key + "}}"));
    }

    /// <summary>문서 전체(머리말·꼬리말 포함)의 {{키}}를 값으로 바꾼다. 한 글 조각 안에 있는 것만.</summary>
    static void FillTokens(XDocument section, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var x in section.Descendants(Hp + "t").SelectMany(t => t.Nodes().OfType<XText>()))
            foreach (var (k, v) in tokens)
                if (x.Value.Contains("{{" + k + "}}")) x.Value = x.Value.Replace("{{" + k + "}}", v);
    }

    // ── 문항 ─────────────────────────────────────────────────

    List<XElement> Questions(Exam exam)
    {
        var list = new List<XElement>();
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            int n = i + 1;
            var q = exam.Questions[i];
            if (i > 0) for (int s = 0; s < form.SpacerLines; s++) list.Add(Empty(form.Spacer));

            if (q.Group is { } g)
            {
                var prefix = $"[{n}∼{n + Math.Max(1, g.Count) - 1}] ";
                list.Add(Para(form.GroupHeader, [Run(form.GroupHeader.CharPr, [prefix]), .. Rich(g.Text, form.GroupHeader.CharPr)]));
                if (g.Image is not null) list.Add(Figure(g.Image));
            }

            bool pointsOnFirst = string.IsNullOrWhiteSpace(q.StemAfter);
            var stemRuns = new List<XElement>();
            if (!form.Stem.AutoNumber) stemRuns.Add(Run(header.CharVariant(form.Stem.CharPr, true, false), [string.Format(form.NumberFormat, n)]));
            stemRuns.AddRange(Rich(q.Stem, form.Stem.CharPr));
            if (pointsOnFirst) stemRuns.AddRange(Points(q, form.Stem.CharPr));
            list.Add(Para(form.Stem, stemRuns));

            if (q.Image is not null) list.Add(Figure(q.Image));
            if (q.Passage is not null) list.Add(BoxPara("passage", form.PassageBox, q.Passage, labels: false));
            if (!pointsOnFirst)
                list.Add(Para(form.StemContinuation, [.. Rich(q.StemAfter!, form.StemContinuation.CharPr), .. Points(q, form.StemContinuation.CharPr)]));
            if (q.Box is not null) list.Add(BoxPara("view", form.ViewBox, q.Box, labels: true));

            switch (q.Type)
            {
                case QuestionType.MultipleChoice: list.AddRange(Choices(q)); break;
                case QuestionType.Essay: for (int l = 0; l < q.AnswerLines; l++) list.Add(Empty(form.Spacer)); break;
            }
        }
        // 꼬리(확인 사항 상자 등)와 마지막 문항 사이도 문항 간격만큼 띄운다.
        for (int s = 0; s < form.SpacerLines && list.Count > 0; s++) list.Add(Empty(form.Spacer));
        return list;
    }

    IEnumerable<XElement> Points(Question q, int charPr) =>
        form.PointsFormat.Length == 0 ? [] : [Run(charPr, [" " + string.Format(form.PointsFormat, Format.Points(q.Points))])];

    // ── 선택지 ───────────────────────────────────────────────

    IEnumerable<XElement> Choices(Question q)
    {
        var layout = ChooseLayout(q.Choices);
        if (layout.Auto)
        {
            for (int i = 0; i < q.Choices.Count; i++)
                yield return Para(layout.Style, Highlight(q, i, Rich(q.Choices[i], layout.Style.CharPr)));
            yield break;
        }

        // 칸 맞춤: 줄을 줄당 개수만큼 같은 폭의 칸으로 나누고, 다음 칸 시작까지 필요한 탭 수를 계산한다.
        // (학교 견본의 탭 개수는 짧은 선택지 기준이라 그대로 쓰면 긴 선택지에서 넘친다.)
        int textStart = header.MarginHu(layout.Style.ParaPr, "left");
        int colHu = Templates.HwpUnit.FromMm(form.ColumnWidthMm);
        double emHu = SizePt(layout.Style.CharPr) * 100;
        var stops = header.TabStops(layout.Style.ParaPr);

        int idx = 0;
        for (int l = 0; l < layout.PerLine.Count; l++)
        {
            var runs = new List<XElement>();
            double slot = (colHu - textStart) / (double)layout.MaxPerLine;
            double x = textStart;
            for (int j = 0; j < layout.PerLine[l] && idx < q.Choices.Count; j++, idx++)
            {
                var choiceRuns = new List<XElement> { Run(layout.Style.CharPr, [Format.ChoiceMark(idx + 1) + " "]) };
                choiceRuns.AddRange(Rich(q.Choices[idx], layout.Style.CharPr));
                runs.AddRange(Highlight(q, idx, choiceRuns));
                x += (InlineMarkup.EstimateWidthEm(q.Choices[idx]) + 1.5) * emHu;

                bool lastInLine = j == layout.PerLine[l] - 1 || idx == q.Choices.Count - 1;
                if (lastInLine) continue;
                double target = textStart + slot * (j + 1);
                int tabs = 0;
                do { x = NextTabStop(x, stops); tabs++; } while (x < target - emHu / 2 && tabs < 20);
                runs.Add(Run(layout.Style.CharPr, Enumerable.Range(0, tabs).Select(_ => (object)Tab())));
            }
            yield return Para(layout.Style, runs);
        }
    }

    /// <summary>x 다음 탭 위치: 문단의 탭 정의, 그 뒤로는 문서 기본 탭 간격(단 왼쪽 끝 기준).</summary>
    double NextTabStop(double x, IReadOnlyList<int> stops)
    {
        foreach (var s in stops) if (s > x + 1) return s;
        return (Math.Floor(x / defaultTabHu) + 1) * defaultTabHu;
    }

    /// <summary>
    /// 줄 수가 적은 배치부터, 가장 긴 선택지("① " 포함)와 칸 사이 여유 한 글자가 칸에 들어가는 것을 고른다.
    /// 칸 = (단 폭 − 선택지 문단 왼쪽 여백) / 줄당 개수. 안 맞거나 선택지 수가 견본과 다르면 5행(자동번호).
    /// </summary>
    ChoiceLayout ChooseLayout(IReadOnlyList<string> choices)
    {
        var auto = form.ChoiceLayouts.FirstOrDefault(l => l.Auto) ?? form.ChoiceLayouts.Last();
        foreach (var l in form.ChoiceLayouts.Where(l => !l.Auto && l.Count == choices.Count).OrderBy(l => l.Lines))
        {
            double emMm = SizePt(l.Style.CharPr) * 0.3528;
            double needed = (choices.Max(c => InlineMarkup.EstimateWidthEm(c)) + 1.5 + 1) * emMm;
            double slot = (form.ColumnWidthMm - LeftMarginMm(l.Style.ParaPr)) / l.MaxPerLine;
            if (needed <= slot) return l;
        }
        if (auto.Auto) return auto;
        // 자동번호 스타일이 없는 양식: 한 줄에 하나씩 직접 입력
        return new ChoiceLayout { Lines = choices.Count, PerLine = [.. choices.Select(_ => 1)], Style = auto.Style };
    }

    IEnumerable<XElement> Highlight(Question q, int index, List<XElement> runs)
    {
        if (!original || !q.Answer.Contains(index + 1) || runs.Count == 0) return runs;
        var first = runs[0].Element(Hp + "t");
        var last = runs[^1].Element(Hp + "t");
        first?.AddFirst(MarkpenBegin());
        last?.Add(MarkpenEnd());
        return runs;
    }

    // ── 상자 ─────────────────────────────────────────────────

    XElement BoxPara(string key, BoxProto? proto, BoxContent content, bool labels)
    {
        var lines = content.Lines.Select((l, i) => (Text: l, Index: i)).ToList();
        string Line(string text, int i, bool auto) => !labels ? text
            : auto ? BoxLabelRx.Replace(text, "")
            : BoxLabelRx.IsMatch(text) ? text : $"{Labels[Math.Min(i, Labels.Length - 1)]}. {text}";

        var src = boxes.Element(key)?.Element(Hp + "p");
        if (proto is null || src is null)
        {
            // 견본이 없는 양식: 제목 + 줄을 이어지는 발문 모양으로.
            var p = new List<XElement>();
            var style = form.StemContinuation;
            var runs = new List<XElement>();
            if (content.Title.Length > 0) runs.Add(Run(style.CharPr, [content.Title]));
            foreach (var (t, i) in lines) { runs.Add(Run(style.CharPr, [new XElement(Hp + "lineBreak")])); runs.AddRange(Rich(Line(t, i, false), style.CharPr)); }
            return Para(style, runs);
        }

        var para = new XElement(src);
        var tbl = Table(para)!;
        tbl.SetAttributeValue("id", Random.Shared.Next(100_000_000, int.MaxValue));
        var cell = FormLearner.ContentCell(tbl);
        var sub = cell.Element(Hp + "subList")!;
        foreach (var p in sub.Elements(Hp + "p").ToList()) p.Remove();
        foreach (var (t, i) in lines)
            sub.Add(Para(proto.Line, Rich(Line(t, i, proto.AutoLabels), proto.Line.CharPr)));
        if (lines.Count == 0) sub.Add(Para(proto.Line, [EmptyRun(proto.Line.CharPr)]));
        ShrinkRow(tbl, cell);
        return para;
    }

    /// <summary>
    /// 내용 칸이 있는 행의 높이를 최소로 줄여 내용만큼만 커지게 한다.
    /// (실제 시험지에서 뽑은 상자는 그림이 들어 있던 높이로 고정되어 있을 수 있다.)
    /// </summary>
    static void ShrinkRow(XElement tbl, XElement cell)
    {
        const int MinRow = 1000;
        int row = Attr(cell.Element(Hp + "cellAddr") ?? cell, "rowAddr");
        foreach (var tc in tbl.Descendants(Hp + "tc").Where(tc => Attr(tc.Element(Hp + "cellAddr") ?? tc, "rowAddr") == row))
            tc.Element(Hp + "cellSz")?.SetAttributeValue("height", MinRow);
        // 표 전체 높이 = 행마다 가장 높은 칸의 합
        int total = tbl.Elements(Hp + "tr").Sum(tr => tr.Elements(Hp + "tc").Select(tc => Attr(tc.Element(Hp + "cellSz") ?? tc, "height")).DefaultIfEmpty(0).Max());
        tbl.Element(Hp + "sz")?.SetAttributeValue("height", total);
    }

    // ── 그림 ─────────────────────────────────────────────────

    XElement Figure(QuestionImage img)
    {
        var path = Path.IsPathRooted(img.Path) || examDir is null ? img.Path : Path.Combine(examDir, img.Path);
        var key = $"#IMG{placeholders.Count + 1:D3}#";
        placeholders.Add(new Placeholder(key, PlaceholderKind.Image, path, 0, img.WidthMm ?? Math.Min(form.ColumnWidthMm - 6, 70)));
        return Para(form.Figure, [Run(form.Figure.CharPr, [key])]);
    }

    // ── 글 ──────────────────────────────────────────────────

    /// <summary>인라인 표기 → run 목록. 수식은 자리표시 글자로.</summary>
    List<XElement> Rich(string markup, int charPr)
    {
        var runs = new List<XElement>();
        foreach (var seg in InlineMarkup.Parse(markup))
        {
            int cp = header.CharVariant(charPr, seg.Bold, seg.Underline);
            if (seg.Kind == SegmentKind.Equation)
            {
                var key = $"#EQ{placeholders.Count + 1:D3}#";
                placeholders.Add(new Placeholder(key, PlaceholderKind.Equation, seg.Text, SizePt(charPr)));
                runs.Add(Run(cp, [key]));
                continue;
            }
            var pieces = new List<object>();
            var parts = seg.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) pieces.Add(new XElement(Hp + "lineBreak"));
                if (parts[i].Length > 0) pieces.Add(parts[i]);
            }
            runs.Add(Run(cp, pieces));
        }
        return runs.Count > 0 ? runs : [EmptyRun(charPr)];
    }

    double LeftMarginMm(int paraPr) => Templates.HwpUnit.ToMm(header.MarginHu(paraPr, "left"));

    double SizePt(int charPr) => (header.CharPr(charPr) is { } c ? Attr(c, "height", 1000) : 1000) / 100.0;

    XElement Para(RoleStyle s, IEnumerable<XElement> runs) => Hx.Para(s.StyleId, s.ParaPr, runs);
    XElement Empty(RoleStyle s) => Hx.Para(s.StyleId, s.ParaPr, [EmptyRun(s.CharPr)]);
}
