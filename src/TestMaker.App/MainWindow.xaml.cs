using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TestMaker.Ai;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Templates;
using TestMaker.Core.Validation;
using TestMaker.Hwp;

namespace TestMaker.App;

public static class Choices
{
    public static readonly int[] Counts = Enumerable.Range(0, 11).ToArray();
}

public sealed record FormChoice(string Name, string Dir);

/// <summary>결과 미리보기 한 칸(만든 문항 하나).</summary>
public sealed class ResultItem(string id, int index, Action<ResultItem> changed) : Observable
{
    public string Id { get; } = id;
    public int Index { get; } = index;
    public string Title { get; init; } = "";
    public string Top { get; init; } = "";
    public string Bottom { get; init; } = "";
    public string Answer { get; init; } = "";
    public ImageSource? Image { get; init; }

    bool include = true;
    public bool Include { get => include; set { if (Set(ref include, value)) changed(this); } }
}

public partial class MainWindow : Window
{
    static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TestMaker");
    static readonly string JobsDir = Path.Combine(Home, "작업");
    static readonly string FormsDir = Path.Combine(Home, "양식");

    readonly ObservableCollection<SourceFile> sources = [];
    readonly ObservableCollection<FoundQuestion> questions = [];
    readonly ObservableCollection<ResultItem> results = [];
    readonly HashSet<(string Id, int Index)> excluded = [];
    readonly ClaudeCode? claude;
    Authoring? job;
    CancellationTokenSource? cts;

    public MainWindow()
    {
        InitializeComponent();
        SourceList.ItemsSource = sources;
        QuestionGrid.ItemsSource = questions;
        ResultList.ItemsSource = results;
        StyleBox.ItemsSource = VariantStyle.All;
        StyleBox.SelectedIndex = 0;
        AllVariants.ItemsSource = Choices.Counts;
        AllVariants.SelectedItem = 1;
        sources.CollectionChanged += (_, _) => DropHint.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadForms();

        if (ClaudeCode.Find() is { } exe) claude = new ClaudeCode(exe);
        else Status("Claude Code를 찾지 못했습니다. 설치하고 로그인한 뒤 다시 실행하세요(문항 찾기·변형에 필요).");
        // TestMaker.exe <작업 폴더> 로 실행하면 그 작업을 연다.
        var args = Environment.GetCommandLineArgs();
        if (claude is not null && args.Length > 1 && File.Exists(Path.Combine(args[1], AuthoringJob.FileName)))
            job = Authoring.Open(claude, args[1], HwpSource.Convert);
        ShowJob();

        // (개발용) TESTMAKER_SNAPSHOT=파일.png: 화면을 그 파일로 저장하고 끝낸다(화면 캡처 없이 확인).
        if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT") is { Length: > 0 } snap)
            ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render((Visual)Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var f = File.Create(snap)) enc.Save(f);
                Close();
            };
    }

    // ── 작업 ──────────────────────────────────────────────

    Authoring EnsureJob()
    {
        if (job is not null) return job;
        if (claude is null) throw new InvalidOperationException("Claude Code가 없어 작업을 만들 수 없습니다.");
        job = Authoring.Open(claude, Path.Combine(JobsDir, DateTime.Now.ToString("yyyy-MM-dd HHmmss")), HwpSource.Convert);
        ShowJob();
        return job;
    }

    void ShowJob()
    {
        sources.Clear(); questions.Clear(); results.Clear(); excluded.Clear();
        JobText.Text = job is null ? "새 작업" : $"작업 폴더: {job.Dir}";
        if (job is null) return;
        foreach (var s in job.Job.Sources) sources.Add(s);
        foreach (var q in job.Job.Questions)
        {
            q.Status = File.Exists(Path.Combine(job.Dir, q.ItemFile)) ? "완료" : "";
            questions.Add(q);
        }
        StyleBox.SelectedItem = job.Job.Style;
        RequestBox.Text = job.Job.Request;
        var info = job.Job.Info;
        TitleBox.Text = info.Title; SubjectBox.Text = info.Subject.Length > 0 ? info.Subject : "과학";
        GradeBox.Text = info.Grade; DateBox.Text = info.Date;
        if (job.Job.FormDir is { } fd) FormBox.SelectedItem = FormBox.Items.Cast<FormChoice>().FirstOrDefault(f => f.Dir == fd) ?? FormBox.SelectedItem;
        RefreshResults();
    }

    void NewJob_Click(object sender, RoutedEventArgs e)
    {
        job = null;
        ShowJob();
        Status("새 작업을 시작합니다. 자료를 넣으세요.");
    }

    void OpenJob_Click(object sender, RoutedEventArgs e)
    {
        if (claude is null) return;
        Directory.CreateDirectory(JobsDir);
        var d = new OpenFolderDialog { Title = "작업 폴더 고르기", InitialDirectory = JobsDir };
        if (d.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(d.FolderName, AuthoringJob.FileName)))
        {
            MessageBox.Show(this, "작업 폴더가 아닙니다(job.json 없음).", "작업 열기");
            return;
        }
        job = Authoring.Open(claude, d.FolderName, HwpSource.Convert);
        ShowJob();
        Status("작업을 열었습니다.");
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = job?.Dir ?? JobsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // ── ① 자료 ────────────────────────────────────────────

    void Sources_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Sources_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFiles(files);
    }

    async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "문항 자료 고르기",
            Multiselect = true,
            Filter = "문항 자료 (그림, PDF, 한글)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.pdf;*.hwp;*.hwpx|모든 파일|*.*",
        };
        if (d.ShowDialog(this) == true) await AddFiles(d.FileNames);
    }

    async void Paste_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (Clipboard.ContainsFileDropList())
        {
            await AddFiles(Clipboard.GetFileDropList().Cast<string>().ToArray());
            return;
        }
        if (!Clipboard.ContainsImage()) { Status("붙여 넣을 그림이 없습니다. 문항을 캡처(Win+Shift+S)한 뒤 Ctrl+V 하세요."); return; }
        var bitmap = Clipboard.GetImage();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var j = EnsureJob();
        sources.Add(j.AddImage(ms.ToArray(), $"캡처 {DateTime.Now:HHmmss}"));
        Status("붙여 넣은 그림을 자료에 넣었습니다.");
    }

    async Task AddFiles(string[] files)
    {
        var j = EnsureJob();
        var bad = files.Where(f => !Authoring.IsSupported(f)).ToList();
        await Run("자료를 넣는 중…", async _ =>
        {
            foreach (var f in files.Where(Authoring.IsSupported))
            {
                Status(Authoring.HwpExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                    ? $"한글 파일을 읽을 수 있게 바꾸는 중: {Path.GetFileName(f)} (한글 창은 닫아 두세요)"
                    : $"자료 넣는 중: {Path.GetFileName(f)}");
                var src = await Task.Run(() => j.AddSource(f));
                sources.Add(src);
            }
        });
        if (bad.Count > 0) MessageBox.Show(this, "그림, PDF, 한글 파일만 넣을 수 있습니다:\n" + string.Join("\n", bad.Select(Path.GetFileName)), "자료");
        Status($"자료 {sources.Count}개. [문항 찾기]를 누르세요.");
    }

    void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || SourceList.SelectedItem is not SourceFile s) return;
        job.RemoveSource(s);
        sources.Remove(s);
        foreach (var q in questions.Where(q => q.Source == s.Name).ToList()) questions.Remove(q);
        RefreshResults();
    }

    void SourceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var s = SourceList.SelectedItem as SourceFile;
        string? img = null;
        if (s is not null && job is not null)
        {
            var first = s.ReadFiles.FirstOrDefault();
            if (first is not null && Authoring.ImageExtensions.Contains(Path.GetExtension(first).ToLowerInvariant())) img = Path.Combine(job.Dir, first);
            else if (s.Figures.Count > 0) img = Path.Combine(job.Dir, s.Figures[0]);
        }
        SourcePreview.Source = img is null ? null : LoadImage(img);
        PreviewBox.Visibility = SourcePreview.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || job.Job.Sources.Count == 0) { Status("자료를 먼저 넣으세요."); return; }
        if (questions.Count > 0 && MessageBox.Show(this, "문항을 다시 찾으면 지금 목록과 만든 결과 표시가 바뀝니다. 계속할까요?", "문항 찾기",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run("자료를 읽고 문항을 찾는 중… (자료 양에 따라 1~3분)", async ct =>
        {
            var found = await job.FindQuestionsAsync(Status, ct);
            questions.Clear();
            foreach (var q in found) { q.Variants = (int)(AllVariants.SelectedItem ?? 1); questions.Add(q); }
            job.Save();
            RefreshResults();
            Status(found.Count == 0 ? "문항을 찾지 못했습니다. 자료를 확인하세요." : $"문항 {found.Count}개를 찾았습니다. 문항마다 변형 수를 정하고 [변형 만들기]를 누르세요.");
        });
    }

    // ── ② 변형 ───────────────────────────────────────────

    void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var q in questions) q.Variants = (int)(AllVariants.SelectedItem ?? 1);
        job?.Save();
    }

    async void Make_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || questions.Count == 0) { Status("먼저 [문항 찾기]를 하세요."); return; }
        var todo = questions.Where(q => q.Include && q.ExpectedCount > 0 && q.Status != "완료").ToList();
        if (todo.Count == 0)
        {
            Status("새로 만들 문항이 없습니다. 다시 만들려면 문항을 마우스 오른쪽 단추로 눌러 [이 문항 다시 만들기].");
            return;
        }
        await MakeAsync(todo);
    }

    async void Remake_Click(object sender, RoutedEventArgs e)
    {
        if (QuestionGrid.SelectedItem is FoundQuestion q) await MakeAsync([q]);
    }

    async Task MakeAsync(List<FoundQuestion> todo)
    {
        var j = job!;
        j.Job.Style = StyleBox.SelectedItem as string ?? VariantStyle.Auto;
        j.Job.Request = RequestBox.Text;
        j.Save();
        var failed = new List<string>();
        await Run($"변형 문항을 만드는 중… ({todo.Count}문항, 문항당 몇 분 걸립니다)", async ct =>
        {
            // Claude 두 개를 함께 돌린다(너무 많이 돌리면 사용량 한도에 금방 닿는다).
            using var gate = new SemaphoreSlim(2);
            int done = 0;
            await Task.WhenAll(todo.Select(async q =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    q.Status = "만드는 중";
                    await j.MakeVariantsAsync(q, m => Status($"{q.Number}번: {m}"), ct);
                    q.Status = "완료";
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
                {
                    q.Status = "실패";
                    lock (failed) failed.Add(ex.Message);
                }
                finally
                {
                    gate.Release();
                    Dispatcher.Invoke(RefreshResults);
                    Status($"{Interlocked.Increment(ref done)}/{todo.Count} 문항 끝");
                }
            }));
        });
        if (failed.Count > 0) MessageBox.Show(this, string.Join("\n\n", failed), "만들지 못한 문항", MessageBoxButton.OK, MessageBoxImage.Warning);
        Status($"변형을 만들었습니다. 오른쪽에서 확인하고, 뺄 문항은 체크를 끄고 [시험지 만들기]를 누르세요.");
    }

    // ── ③ 결과·시험지 ─────────────────────────────────────

    void RefreshResults()
    {
        results.Clear();
        if (job is null) return;
        foreach (var q in questions.Where(q => q.Include))
        {
            var items = job.LoadItems(q);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var label = q.KeepOriginal && i == 0 ? "원문항" : $"변형 {(q.KeepOriginal ? i : i + 1)}";
                results.Add(new ResultItem(q.Id, i, r => { if (r.Include) excluded.Remove((r.Id, r.Index)); else excluded.Add((r.Id, r.Index)); })
                {
                    Title = $"{q.Number}번 · {label}  [{Format.Points(item.TotalPoints)}점]",
                    Top = Plain(item.Stem),
                    Image = item.Image is { Path.Length: > 0 } img ? LoadImage(Path.Combine(job.Dir, img.Path)) : null,
                    Bottom = Body(item),
                    Answer = AnswerText(item),
                    Include = !excluded.Contains((q.Id, i)),
                });
            }
        }
    }

    async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (job is null) return;
        var j = job;
        j.Job.Info.Title = TitleBox.Text.Trim();
        j.Job.Info.Subject = SubjectBox.Text.Trim();
        j.Job.Info.Grade = GradeBox.Text.Trim();
        j.Job.Info.Date = DateBox.Text.Trim();
        var form = FormBox.SelectedItem as FormChoice;
        j.Job.FormDir = form?.Dir == FormRendering.DefaultFormDir ? null : form?.Dir;
        j.Save();

        var exam = j.BuildExam(excluded);
        var errors = ExamValidator.Validate(exam, expectedTotal: null).Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "시험지를 만들 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        JsonStore.Save(Path.Combine(j.Dir, "시험.json"), exam);
        var name = string.Concat((exam.Info.Title.Length > 0 ? exam.Info.Title : "시험지").Split(Path.GetInvalidFileNameChars()));
        var output = Path.Combine(j.Dir, name + ".hwp");
        bool pdf = PdfCheck.IsChecked == true, answers = AnswersCheck.IsChecked == true;
        var formDir = j.Job.FormDir ?? FormRendering.DefaultFormDir;

        await Run($"한글로 시험지를 만드는 중… ({exam.Questions.Count}문항, 한글 창은 닫아 두세요)", async _ =>
        {
            await Task.Run(() =>
            {
                FormRendering.Render(exam, formDir, j.Dir, output, original: true, pdf: pdf);
                if (!answers) return;
                var ans = Path.Combine(j.Dir, name + "_정답.hwp");
                using var session = new HwpSession();
                new AnswerSheetRenderer(session).Render(exam, new TextStyle());
                session.SaveAs(ans, HwpFormat.Hwp);
                if (pdf) session.SaveAs(Path.ChangeExtension(ans, ".pdf"), HwpFormat.Pdf);
            });
            Status($"시험지를 만들었습니다: {output} (원안지·{(answers ? "정답지·" : "")}PDF는 같은 폴더)");
            var open = pdf ? Path.ChangeExtension(output, ".pdf") : output;
            Process.Start(new ProcessStartInfo(open) { UseShellExecute = true });
        });
    }

    void LoadForms()
    {
        var forms = new List<FormChoice> { new("표준 시험지 양식(B4, 내장)", FormRendering.DefaultFormDir) };
        if (Directory.Exists(FormsDir))
            forms.AddRange(Directory.EnumerateDirectories(FormsDir).Where(FormRendering.IsFormDir).Select(d => new FormChoice(Path.GetFileName(d), d)));
        FormBox.ItemsSource = forms;
        FormBox.SelectedIndex = 0;
    }

    async void LearnForm_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "학교 시험지 양식 파일 고르기(한글)", Filter = "한글 파일|*.hwp;*.hwpx" };
        if (d.ShowDialog(this) != true) return;
        var extras = new List<string>();
        if (MessageBox.Show(this, "실제로 출제했던 시험지도 함께 주면 문항 간격 같은 실제 사용 방식을 배웁니다.\n더 고르시겠습니까?", "학교 양식 학습",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            var d2 = new OpenFileDialog { Title = "실제 시험지 고르기(여러 개 가능)", Filter = "한글 파일|*.hwp;*.hwpx", Multiselect = true };
            if (d2.ShowDialog(this) == true) extras.AddRange(d2.FileNames);
        }
        var name = Path.GetFileNameWithoutExtension(d.FileName);
        var dir = Path.Combine(FormsDir, name);
        await Run("학교 양식을 학습하는 중… (한글 창은 닫아 두세요)", async _ =>
        {
            var r = await Task.Run(() => FormRendering.Learn(d.FileName, extras, dir, name));
            LoadForms();
            FormBox.SelectedItem = FormBox.Items.Cast<FormChoice>().FirstOrDefault(f => f.Dir == dir);
            Status($"학교 양식을 배웠습니다: {name} ({(r.Form.Kind == Core.Forms.FormKind.Structured ? "완성형" : "무질서형")}, 견본 문항 {r.QuestionCount}개)");
        });
    }

    // ── 공통 ─────────────────────────────────────────────

    async Task Run(string status, Func<CancellationToken, Task> work)
    {
        cts = new CancellationTokenSource();
        SetBusy(true);
        Status(status);
        try { await work(cts.Token); }
        catch (OperationCanceledException) { Status("중지했습니다."); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            Status("실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Test Maker", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            cts.Dispose();
            cts = null;
        }
    }

    void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        CancelButton.IsEnabled = busy;
        foreach (var b in new[] { FindButton, MakeButton, RenderButton }) b.IsEnabled = !busy;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => cts?.Cancel();

    void Status(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Status(message)); return; }
        StatusText.Text = message;
        if (job is not null)
        {
            try { File.AppendAllText(Path.Combine(job.Dir, "log.txt"), $"{DateTime.Now:HH:mm:ss} {message}\n"); }
            catch (IOException) { }
        }
    }

    static ImageSource? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;      // 파일을 잠그지 않게(다시 그리면 덮어쓰므로)
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException) { return null; }
    }

    // ── 미리보기 글 ──────────────────────────────────────

    static string Plain(string s) => s.Replace("**", "").Replace("__", "").Replace("\\$", "§").Replace("$", "").Replace("§", "$");

    static string Body(Question q)
    {
        var sb = new StringBuilder();
        if (q.Passage is { } p) foreach (var l in p.Lines) sb.AppendLine("  " + Plain(l));
        if (q.StemAfter is { Length: > 0 } after) sb.AppendLine(Plain(after));
        if (q.Box is { } box)
        {
            sb.AppendLine(box.Title.Length > 0 ? box.Title : "<보기>");
            var labels = "ㄱㄴㄷㄹㅁㅂㅅ";
            for (int i = 0; i < box.Lines.Count; i++)
            {
                var line = Plain(box.Lines[i]);
                bool labelled = line.Length > 1 && labels.Contains(line[0]) && line[1] == '.';
                sb.AppendLine($"  {(labelled || i >= labels.Length ? "" : labels[i] + ". ")}{line}");
            }
        }
        if (q.Conditions is { } c)
        {
            sb.AppendLine(c.Title);
            foreach (var l in c.Lines) sb.AppendLine("  " + Plain(l));
        }
        for (int i = 0; i < q.SubQuestions.Count; i++) sb.AppendLine($"({i + 1}) {Plain(q.SubQuestions[i].Text)} [{Format.Points(q.SubQuestions[i].Points)}점]");
        if (q.Choices.Count > 0)
            sb.AppendLine(string.Join("   ", q.Choices.Select((ch, i) => $"{Format.ChoiceMark(i + 1)} {Plain(ch)}")));
        return sb.ToString().TrimEnd();
    }

    static string AnswerText(Question q)
    {
        var sb = new StringBuilder();
        if (q.Answer.Count > 0) sb.Append("정답 " + string.Join(", ", q.Answer.Select(Format.ChoiceMark)));
        else if (q.AnswerText is { Length: > 0 } a) sb.Append("모범 답안: " + Plain(a));
        else if (q.SubQuestions.Count > 0)
            sb.Append(string.Join("  ", q.SubQuestions.Select((s, i) => $"({i + 1}) {Plain(s.AnswerText ?? "")}")));
        if (q.Explanation is { Length: > 0 } ex) sb.Append("\n해설: " + Plain(ex));
        return sb.ToString();
    }
}
