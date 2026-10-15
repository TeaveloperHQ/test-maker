using TestMaker.Core.Forms;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;

namespace TestMaker.Hwp;

public sealed record RenderedFile(string Path, int Pages);

/// <summary>
/// 학습한 양식(form.json)으로 시험지를 만든다.
/// 1) XML 조립(학교 스타일 그대로) → 2) 한글로 열어 수식·그림 자리표시를 개체로 바꿈 → 3) 저장.
/// 학생용과 원안지(정답 형광펜)를 같은 방식으로 각각 만든다.
/// </summary>
public static class FormRendering
{
    public static ExamForm LoadForm(string formDir) => JsonStore.Load<ExamForm>(Path.Combine(formDir, ExamForm.FileName));

    public static bool IsFormDir(string dir) => File.Exists(Path.Combine(dir, ExamForm.FileName));

    /// <param name="output">학생용 출력(.hwp/.hwpx). 원안지는 같은 폴더에 "_원안지"를 붙인다.</param>
    public static List<RenderedFile> Render(Exam exam, string formDir, string? examDir, string output, bool original, bool pdf, bool visible = false)
    {
        var form = LoadForm(formDir);
        var results = new List<RenderedFile>();
        var work = Directory.CreateTempSubdirectory("testmaker_render_").FullName;
        try
        {
            using var session = new HwpSession(visible);
            var variants = new List<(string Output, bool Original)> { (output, false) };
            if (original)
                variants.Add((Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_원안지" + Path.GetExtension(output)), true));

            // 전체 쪽수 칸이 있으면 한 번 그려 쪽수를 알아낸 뒤 채운다(쪽수는 다 그려야 안다).
            var extra = new Dictionary<string, string>();
            if (FormAssembler.HasToken(formDir, "쪽수"))
            {
                var probe = Path.Combine(work, "pages.hwpx");
                var ph = FormAssembler.Assemble(exam, form, formDir, examDir, probe, original: false, new Dictionary<string, string> { ["쪽수"] = "0" });
                session.NewDocument();
                session.Open(probe);
                FillPlaceholders(session, ph);
                extra["쪽수"] = session.PageCount.ToString();
            }

            foreach (var (target, isOriginal) in variants)
            {
                var assembled = Path.Combine(work, isOriginal ? "original.hwpx" : "student.hwpx");
                var placeholders = FormAssembler.Assemble(exam, form, formDir, examDir, assembled, isOriginal, extra);

                session.NewDocument();
                session.Open(assembled);
                FillPlaceholders(session, placeholders);

                session.SaveAs(target, HwpSession.FormatOf(target));
                results.Add(new RenderedFile(target, session.PageCount));
                if (pdf)
                {
                    var p = Path.ChangeExtension(target, ".pdf");
                    session.SaveAs(p, HwpFormat.Pdf);
                    results.Add(new RenderedFile(p, session.PageCount));
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return results;
    }

    static void FillPlaceholders(HwpSession session, IReadOnlyList<Placeholder> placeholders)
    {
        var w = new HwpWriter(session);
        foreach (var ph in placeholders)
        {
            if (!w.FindAndSelect(ph.Key))
                throw new InvalidOperationException($"자리표시 {ph.Key}를 문서에서 찾지 못했습니다.");
            w.DeleteSelection();
            switch (ph.Kind)
            {
                case PlaceholderKind.Equation:
                    w.Equation(ph.Value, ph.SizePt);
                    break;
                case PlaceholderKind.Image:
                    if (!File.Exists(ph.Value)) throw new FileNotFoundException($"그림 파일이 없습니다: {ph.Value}");
                    var px = ImageSize.Read(ph.Value) ?? (400, 300);
                    double width = ph.WidthMm ?? 60;
                    w.Picture(ph.Value, width, width * px.Height / px.Width);
                    break;
            }
        }
    }

    /// <summary>학교 문서(.hwp/.hwpx)에서 양식을 학습한다. 원본은 읽기만 하고, 복사본을 한글로 HWPX로 바꿔 분석한다.</summary>
    /// <param name="primary">견본을 뽑을 주 문서(보통 학교 양식 파일).</param>
    /// <param name="extras">실제 시험지들(문항 간격 등 실제 사용 통계만).</param>
    public static FormLearnResult Learn(string primary, IReadOnlyList<string> extras, string formDir, string? name = null)
    {
        var work = Directory.CreateTempSubdirectory("testmaker_learn_").FullName;
        try
        {
            using var session = new HwpSession();
            string Normalize(string src, int i)
            {
                if (!File.Exists(src)) throw new FileNotFoundException($"파일이 없습니다: {src}");
                var copy = Path.Combine(work, $"src{i}{Path.GetExtension(src)}");
                File.Copy(src, copy);
                var hwpx = Path.Combine(work, $"src{i}_n.hwpx");
                session.NewDocument();
                session.Open(copy);
                session.SaveAs(hwpx, HwpFormat.Hwpx);
                return hwpx;
            }
            var p = Normalize(primary, 0);
            var e = extras.Select((x, i) => Normalize(x, i + 1)).ToList();

            var result = FormLearner.Learn(p, e, name ?? new DirectoryInfo(formDir).Name);
            result.Form.LearnedFrom = [Path.GetFileName(primary), .. extras.Select(x => Path.GetFileName(x))];
            result.Save(formDir);

            // 한글에서 열리는지만 확인한다. 다시 저장하면 header id가 바뀌어 form.json과 어긋날 수 있어 저장하지 않는다.
            session.NewDocument();
            session.Open(Path.Combine(formDir, ExamForm.BaseFile));
            return result;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
