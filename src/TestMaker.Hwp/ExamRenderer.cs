using TestMaker.Core.Models;
using TestMaker.Core.Templates;
using TestMaker.Core.Text;

namespace TestMaker.Hwp;

/// <summary>시험 + 양식 → 한글 문서. 결과는 세션의 현재 문서에 그려지고, 저장은 호출자가 한다.</summary>
public sealed class ExamRenderer(HwpSession session)
{
    const double MmPerPt = 0.3528;

    readonly HwpWriter w = new(session);

    /// <param name="templateDir">양식 폴더(preamble.hwpx 위치). 기본 양식이면 null.</param>
    /// <param name="examDir">시험 JSON 폴더(그림 상대 경로 기준).</param>
    public void Render(Exam exam, ExamTemplate t, string? templateDir, string? examDir)
    {
        var preamble = t.Preamble is not null && templateDir is not null ? Path.Combine(templateDir, t.Preamble) : null;
        if (preamble is not null)
        {
            if (!File.Exists(preamble)) throw new FileNotFoundException($"양식의 머리 부분 파일이 없습니다: {preamble}");
            StartFromPreamble(preamble, exam, t);
        }
        else
        {
            StartDefault(exam, t);
        }

        double colWidth = t.Columns.ColumnWidthMm(t.Page);
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            w.NewPara();
            if (i > 0) for (int b = 0; b < t.BlankLinesBetweenQuestions; b++) w.BreakPara();
            Question(i + 1, exam.Questions[i], t, colWidth, examDir);
        }

        if (t.ClosingText.Length > 0)
        {
            w.NewPara();
            w.BreakPara();
            w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = t.QuestionPara.LineSpacingPercent });
            w.CharShape(t.QuestionStem.With(bold: true));
            w.Text(t.ClosingText);
        }
    }

    /// <summary>학습한 머리 부분을 열고 {{키}}를 채운 뒤, 문서 끝의 문항 자리표시를 지우고 그 자리부터 쓴다.</summary>
    void StartFromPreamble(string preamblePath, Exam exam, ExamTemplate t)
    {
        // 양식 원본을 직접 열면 실수로 덮어쓸 수 있으니 임시 복사본을 연다.
        var temp = Path.Combine(Path.GetTempPath(), $"testmaker_{Guid.NewGuid():N}.hwpx");
        File.Copy(preamblePath, temp);
        try
        {
            session.Open(temp);
        }
        finally
        {
            // 한글이 파일을 잡고 있을 수 있으므로 지우기는 최선만 다한다.
            try { File.Delete(temp); } catch (IOException) { }
        }

        foreach (var (key, value) in exam.Info.ToTokens(exam))
            w.ReplaceAll("{{" + key + "}}", value);
        w.ReplaceAll(t.Marker, "");
        w.MoveDocEnd();
        w.AssumeEmptyParagraph();
    }

    void StartDefault(Exam exam, ExamTemplate t)
    {
        session.NewDocument();
        w.AssumeEmptyParagraph();
        w.PageSetup(t.Page);
        var title = t.QuestionStem;

        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 160 });
        w.CharShape(new TextStyle { FontHangul = title.FontHangul, FontLatin = title.FontLatin, SizePt = 16, Bold = true });
        w.Text(exam.Info.Title.Length > 0 ? exam.Info.Title : "시험지");

        var sub = string.Join("  ", new[] { exam.Info.Subject, exam.Info.Grade, exam.Info.Date }.Where(s => s.Length > 0));
        if (sub.Length > 0)
        {
            w.BreakPara();
            w.CharShape(title.With(bold: true));
            w.Text(sub);
        }

        w.BreakPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Right, LineSpacingPercent = 160 });
        w.CharShape(title);
        w.Text("(    )학년 (    )반 (    )번   이름: (                )");
        w.BreakPara();

        if (t.Columns.Count > 1) w.Columns(t.Columns);
    }

    void Question(int number, Question q, ExamTemplate t, double colWidthMm, string? examDir)
    {
        w.ParaShape(t.QuestionPara);
        w.CharShape(t.QuestionNumber);
        w.Text(string.Format(t.NumberFormat, number));
        w.Rich(q.Stem, t.QuestionStem);
        if (t.PointsFormat.Length > 0)
        {
            w.CharShape(t.QuestionStem);
            w.Text(" " + string.Format(t.PointsFormat, Format.Points(q.Points)));
        }

        if (q.Image is { } img) ImageBlock(img, colWidthMm, examDir);
        if (q.Box is { } box) BoxBlock(box, t, colWidthMm);

        switch (q.Type)
        {
            case QuestionType.MultipleChoice:
                Choices(q.Choices, t, colWidthMm);
                break;
            case QuestionType.Essay:
                AnswerArea(q.AnswerLines, t, colWidthMm);
                break;
        }
    }

    void ImageBlock(QuestionImage img, double colWidthMm, string? examDir)
    {
        var path = Path.IsPathRooted(img.Path) || examDir is null ? img.Path : Path.Combine(examDir, img.Path);
        if (!File.Exists(path)) throw new FileNotFoundException($"그림 파일이 없습니다: {path}");

        double maxWidth = colWidthMm - 4;
        var px = ImageSize.Read(path) ?? (400, 300);
        // 96dpi 기준 원래 크기보다 키우지 않는다.
        double natural = px.Width * 25.4 / 96;
        double width = Math.Min(img.WidthMm ?? Math.Min(natural, maxWidth), maxWidth);
        double height = width * px.Height / px.Width;

        w.NewPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = 100 });
        w.Picture(path, width, height);
    }

    void BoxBlock(BoxContent box, ExamTemplate t, double colWidthMm)
    {
        w.NewPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = t.QuestionPara.LineSpacingPercent });
        w.BeginTable(1, [colWidthMm - 6]);

        if (box.Title.Length > 0)
        {
            w.ParaShape(t.BoxPara.WithAlign(ParaAlign.Center));
            w.CharShape(t.BoxText);
            w.Text(box.Title);
            w.BreakPara();
        }
        w.ParaShape(t.BoxPara);
        for (int i = 0; i < box.Lines.Count; i++)
        {
            if (i > 0) w.BreakPara();
            w.Rich(box.Lines[i], t.BoxText);
        }
        w.ExitTable();
    }

    /// <summary>
    /// 한 줄에 하나면 문단으로, 여러 개면 테두리 없는 표로 놓는다.
    /// (탭 정렬은 탭 위치 단위가 상황마다 달라 쓰지 않는다 — 실측)
    /// </summary>
    void Choices(IReadOnlyList<string> choices, ExamTemplate t, double colWidthMm)
    {
        double available = colWidthMm - t.ChoicePara.LeftMarginPt * MmPerPt - 2;
        // 양식에서 배운 값은 "최대" 개수로만 쓴다. 긴 선택지까지 억지로 한 줄에 넣지 않는다.
        int max = t.ChoicesPerLine > 0 ? t.ChoicesPerLine : choices.Count;
        int perLine = AutoPerLine(choices, t, available, max);

        if (perLine <= 1)
        {
            for (int i = 0; i < choices.Count; i++)
            {
                w.NewPara();
                w.ParaShape(t.ChoicePara);
                Choice(i, choices[i], t);
            }
            return;
        }

        int rows = (choices.Count + perLine - 1) / perLine;
        w.NewPara();
        w.ParaShape(t.ChoicePara.WithIndent(0));
        w.BeginTable(rows, Enumerable.Repeat(available / perLine, perLine).ToArray());
        var cellPara = t.ChoicePara.WithAlign(ParaAlign.Left);
        for (int cell = 0; cell < rows * perLine; cell++)
        {
            if (cell > 0) w.NextCell();
            w.ClearCellBorder();
            w.ParaShape(cellPara);
            if (cell < choices.Count) Choice(cell, choices[cell], t);
        }
        w.ExitTable();
    }

    void Choice(int index, string text, ExamTemplate t)
    {
        w.CharShape(t.Choice);
        w.Text(Format.ChoiceMark(index + 1) + " ");
        w.Rich(text, t.Choice);
    }

    /// <summary>
    /// 한 줄 전부 → 두 줄(5개면 3+2) → 한 개씩 순서로, 가장 긴 선택지가 칸에 들어가는 배치를 고른다(최대 max개).
    /// </summary>
    static int AutoPerLine(IReadOnlyList<string> choices, ExamTemplate t, double availableMm, int max)
    {
        double emMm = t.Choice.SizePt * MmPerPt * t.Choice.RatioPercent / 100.0;
        double longest = choices.Max(c => InlineMarkup.EstimateWidthEm(c) + 1.5) * emMm + HwpWriter.CellPaddingMm; // "① " 포함
        foreach (int k in new[] { choices.Count, (choices.Count + 1) / 2 }.Distinct())
            if (k > 1 && k <= max && longest <= availableMm / k) return k;
        return 1;
    }

    void AnswerArea(int lines, ExamTemplate t, double colWidthMm)
    {
        w.NewPara();
        w.ParaShape(new ParaStyle { Align = ParaAlign.Center, LineSpacingPercent = t.QuestionPara.LineSpacingPercent });
        w.BeginTable(1, [colWidthMm - 6]);
        w.ParaShape(t.BoxPara);
        w.CharShape(t.BoxText);
        for (int i = 1; i < Math.Max(1, lines); i++) w.BreakPara();
        w.ExitTable();
    }
}

static class ParaStyleExtensions
{
    /// <summary>여백·들여쓰기 없이 정렬만 바꾼 모양(표 칸 안 등).</summary>
    public static ParaStyle WithAlign(this ParaStyle s, ParaAlign align) => new()
    {
        Align = align,
        LineSpacingPercent = s.LineSpacingPercent,
        SpaceBeforePt = s.SpaceBeforePt,
        SpaceAfterPt = s.SpaceAfterPt,
    };

    public static ParaStyle WithIndent(this ParaStyle s, double indentPt) => new()
    {
        Align = s.Align,
        LeftMarginPt = s.LeftMarginPt,
        IndentPt = indentPt,
        LineSpacingPercent = s.LineSpacingPercent,
        SpaceBeforePt = s.SpaceBeforePt,
        SpaceAfterPt = s.SpaceAfterPt,
    };
}
