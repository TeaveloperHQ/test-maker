using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TestMaker.Ai;
using TestMaker.Core.Bank;
using TestMaker.Core.Models;
using TestMaker.Core.Serialization;
using TestMaker.Core.Templates;
using TestMaker.Core.Validation;
using TestMaker.Hwp;

namespace TestMaker.App;

public static class Choices
{
    public static readonly string[] Styles = VariantStyle.All;
    public static readonly int[] Counts = Enumerable.Range(0, 11).ToArray();
}

public sealed record FormChoice(string Name, string Dir);

/// <summary>③ 결과 미리보기 한 칸(만든 문항 하나).</summary>
public sealed class ResultItem(string id, int index, Question item, Action<ResultItem> changed) : Observable
{
    public string Id { get; } = id;
    public int Index { get; } = index;
    /// <summary>작업 안에서 이 결과를 가리키는 이름(예: "q01/0").</summary>
    public string Key => $"{Id}/{Index}";
    public Question Item { get; } = item;
    public string Title { get; init; } = "";
    /// <summary>문항함에 보관할 때의 이름(문항 요약 포함).</summary>
    public string BankTitle { get; init; } = "";
    public string Top { get; init; } = "";
    public string Bottom { get; init; } = "";
    public string Answer { get; init; } = "";
    public ImageSource? Image { get; init; }

    bool include = true;
    public bool Include { get => include; set { if (Set(ref include, value)) changed(this); } }

    string archived = "";
    /// <summary>"시험용에 보관됨" 같은 표시. 비어 있으면 아직 보관하지 않음.</summary>
    public string Archived
    {
        get => archived;
        set { if (Set(ref archived, value)) OnChanged(nameof(ArchivedVisibility)); }
    }
    public Visibility ArchivedVisibility => archived.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>④ 시험지에 들어갈 문항 하나(이번 작업의 결과이거나 문항함의 문항).</summary>
public sealed class ExamEntry(string key, string title, string sub, Question question, bool fromJob) : Observable
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Sub { get; } = sub;
    /// <summary>그림 경로를 절대 경로로 바꾼 사본.</summary>
    public Question Question { get; } = question;
    public bool FromJob { get; } = fromJob;

    int number;
    public int Number { get => number; set => Set(ref number, value); }
}

public partial class MainWindow : Window
{
    /// <summary>작업·양식·문항함·시험지를 두는 곳. (개발용) TESTMAKER_HOME 으로 바꿀 수 있다 — 선생님 문서 폴더를 건드리지 않고 시험할 때.</summary>
    static readonly string Home = Environment.GetEnvironmentVariable("TESTMAKER_HOME") is { Length: > 0 } h ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TestMaker");
    static readonly string JobsDir = Path.Combine(Home, "작업");
    static readonly string FormsDir = Path.Combine(Home, "양식");
    static readonly string ExamsDir = Path.Combine(Home, "시험지");
    static readonly string BankDir = Path.Combine(Home, "문항함");

    readonly ObservableCollection<SourceFile> sources = [];
    readonly ObservableCollection<FoundQuestion> questions = [];
    readonly ObservableCollection<ResultItem> results = [];
    readonly ObservableCollection<ExamEntry> examEntries = [];
    readonly HashSet<(string Id, int Index)> excluded = [];
    readonly QuestionBank bank = new(BankDir);
    readonly ClaudeCode? claude;
    Authoring? job;
    CancellationTokenSource? cts;
    bool refreshing;

    public MainWindow()
    {
        InitializeComponent();
        SourceList.ItemsSource = sources;
        QuestionGrid.ItemsSource = questions;
        ResultList.ItemsSource = results;
        ExamList.ItemsSource = examEntries;
        sources.CollectionChanged += (_, _) => DropHint.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        examEntries.CollectionChanged += (_, _) => Renumber();
        LoadForms();
        Renumber();

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
                Snapshot(this, snap);
                // TESTMAKER_SNAPSHOT_BANK=파일.png 이면 문항함 창도 찍는다.
                if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_BANK") is { Length: > 0 } bankSnap)
                {
                    var w = new BankWindow(bank) { Owner = this };
                    w.Show();
                    await Task.Delay(1500);
                    if (w.ItemList.Items.Count > 0) w.ItemList.SelectedIndex = 0;
                    await Task.Delay(500);
                    Snapshot(w, bankSnap);
                    w.Close();
                }
                Close();
            };
    }

    static void Snapshot(Window w, string path)
    {
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render((Visual)w.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
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
        if (job is null) { SyncJobEntries(); return; }
        foreach (var s in job.Job.Sources) sources.Add(s);
        foreach (var q in job.Job.Questions)
        {
            q.Status = File.Exists(Path.Combine(job.Dir, q.ItemFile)) ? "완료" : "";
            questions.Add(q);
        }
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
        SourcePreview.Source = Preview.LoadImage(img);
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
            foreach (var q in found) questions.Add(q);
            job.Save();
            RefreshResults();
            Status(found.Count == 0 ? "문항을 찾지 못했습니다. 자료를 확인하세요." : $"문항 {found.Count}개를 찾았습니다. 문항마다 변형 수를 정하고 [변형 만들기]를 누르세요.");
        });
    }

    // ── ② 변형 ───────────────────────────────────────────

    /// <summary>
    /// 변형 수 칸(쳐 넣을 수 있는 드롭다운)에서 ↑↓: 목록이 닫혀 있으면 값을 바꾸지 않고 위·아래 문항의 변형 수 칸으로(엑셀처럼).
    /// 목록이 열려 있으면 원래대로 목록 안에서 움직인다.
    /// </summary>
    void QuestionGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)) return;
        var box = FindParent<System.Windows.Controls.ComboBox>(e.OriginalSource as DependencyObject);
        if (box is null || (string?)box.Tag != "Variants" || box.IsDropDownOpen) return;
        e.Handled = true;
        int i = questions.IndexOf((FoundQuestion)box.DataContext) + (e.Key == System.Windows.Input.Key.Down ? 1 : -1);
        if (i < 0 || i >= questions.Count) return;
        QuestionGrid.ScrollIntoView(questions[i]);
        QuestionGrid.UpdateLayout();
        if (QuestionGrid.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject row
            && FindChild<System.Windows.Controls.ComboBox>(row, c => (string?)c.Tag == "Variants") is { } next)
            next.Focus();
    }

    /// <summary>변형 수 칸에 들어가면 숫자를 통째로 골라 둔다 — 바로 치면 바뀐다.</summary>
    void VariantsBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox box
            && box.Template.FindName("PART_EditableTextBox", box) is System.Windows.Controls.TextBox tb)
            Dispatcher.BeginInvoke(tb.SelectAll);
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    static T? FindChild<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject
    {
        for (int k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++)
        {
            var c = VisualTreeHelper.GetChild(d, k);
            if (c is T t && match(t)) return t;
            if (FindChild(c, match) is { } found) return found;
        }
        return null;
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
        if (QuestionGrid.CurrentItem is FoundQuestion q) await MakeAsync([q]);
    }

    async Task MakeAsync(List<FoundQuestion> todo)
    {
        var j = job!;
        QuestionGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, exitEditingMode: true);
        j.Job.Request = RequestBox.Text;
        // 다시 만드는 문항은 예전 결과의 보관 표시를 지운다(문항함에 있는 것은 그대로 남는다).
        foreach (var q in todo)
            foreach (var key in j.Job.Archived.Keys.Where(k => k.StartsWith(q.Id + "/")).ToList()) j.Job.Archived.Remove(key);
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
        Status("변형을 만들었습니다. ③에서 확인하고 보관하거나, ④에서 바로 시험지를 만드세요.");
    }

    // ── ③ 결과 확인과 보관 ─────────────────────────────────

    void RefreshResults()
    {
        refreshing = true;
        results.Clear();
        if (job is not null)
        {
            foreach (var q in questions.Where(q => q.Include))
            {
                var items = job.LoadItems(q);
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var label = q.KeepOriginal && i == 0 ? "원문항" : $"변형 {(q.KeepOriginal ? i : i + 1)}";
                    var r = new ResultItem(q.Id, i, item, OnResultChanged)
                    {
                        Title = $"{q.Number}번 · {label}  [{Format.Points(item.TotalPoints)}점]",
                        BankTitle = $"{q.Number}번 {label} · {q.Summary}",
                        Top = Preview.Plain(item.Stem),
                        Image = item.Image is { Path.Length: > 0 } img ? Preview.LoadImage(Path.Combine(job.Dir, img.Path)) : null,
                        Bottom = Preview.Body(item),
                        Answer = Preview.Answer(item),
                        Include = !excluded.Contains((q.Id, i)),
                    };
                    if (job.Job.Archived.TryGetValue(r.Key, out var cat)) r.Archived = $"{cat}에 보관됨";
                    results.Add(r);
                }
            }
        }
        refreshing = false;
        SyncJobEntries();
    }

    void OnResultChanged(ResultItem r)
    {
        if (r.Include) excluded.Remove((r.Id, r.Index)); else excluded.Add((r.Id, r.Index));
        if (!refreshing) SyncJobEntries();
    }

    void ArchiveExam_Click(object sender, RoutedEventArgs e) => Archive(BankCategory.Exam);
    void ArchivePractice_Click(object sender, RoutedEventArgs e) => Archive(BankCategory.Practice);

    /// <summary>③에서 체크한 문항을 문항함에 보관한다. 이미 보관한 것은 건너뛴다.</summary>
    void Archive(BankCategory category)
    {
        if (job is null) return;
        var picked = results.Where(r => r.Include).ToList();
        if (picked.Count == 0) { Status("보관할 문항을 ③에서 체크하세요."); return; }
        var name = QuestionBank.CategoryName(category);
        int added = 0, skipped = 0;
        try
        {
            foreach (var r in picked)
            {
                if (job.Job.Archived.ContainsKey(r.Key)) { skipped++; continue; }
                bank.Add(r.Item, job.Dir, category, r.BankTitle, source: Path.GetFileName(job.Dir));
                job.Job.Archived[r.Key] = name;
                r.Archived = $"{name}에 보관됨";
                added++;
            }
        }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "보관하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning); }
        job.Save();
        Status($"{added}문항을 문항함({name})에 보관했습니다." + (skipped > 0 ? $" 이미 보관한 {skipped}문항은 건너뛰었습니다(칸을 바꾸려면 [문항함]에서 옮기세요)." : ""));
    }

    // ── ④ 시험지 ─────────────────────────────────────────

    /// <summary>④의 '이번 작업' 문항을 ③의 체크와 맞춘다. 문항함에서 넣은 문항과 순서는 그대로 둔다.</summary>
    void SyncJobEntries()
    {
        var wanted = job is null ? [] : results.Where(r => r.Include).ToList();
        var keys = wanted.Select(r => "job:" + r.Key).ToHashSet();
        foreach (var stale in examEntries.Where(e => e.FromJob && !keys.Contains(e.Key)).ToList()) examEntries.Remove(stale);
        foreach (var r in wanted)
        {
            var entry = new ExamEntry("job:" + r.Key, r.BankTitle, "이번 작업", QuestionBank.Absolutize(r.Item, job!.Dir), fromJob: true);
            var i = examEntries.ToList().FindIndex(e => e.Key == entry.Key);
            if (i >= 0) examEntries[i] = entry;          // 다시 만든 문항이면 새 것으로
            else examEntries.Add(entry);
        }
    }

    void Renumber()
    {
        for (int i = 0; i < examEntries.Count; i++) examEntries[i].Number = i + 1;
        ExamEmpty.Visibility = examEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExamSummary.Text = examEntries.Count == 0 ? "" :
            $"{examEntries.Count}문항 · 총 {Format.Points(examEntries.Sum(e => e.Question.TotalPoints))}점";
    }

    void AddFromBank_Click(object sender, RoutedEventArgs e) => ShowBank();
    void OpenBank_Click(object sender, RoutedEventArgs e) => ShowBank();

    void ShowBank()
    {
        var w = new BankWindow(bank) { Owner = this };
        if (w.ShowDialog() != true) return;
        int added = 0;
        foreach (var item in w.Picked)
        {
            var key = "bank:" + item.Id;
            if (examEntries.Any(x => x.Key == key)) continue;
            examEntries.Add(new ExamEntry(key, item.Title, $"문항함 · {item.CategoryText}", QuestionBank.Absolutize(item.Question, item.Dir), fromJob: false));
            added++;
        }
        Status($"문항함에서 {added}문항을 ④ 시험지에 넣었습니다.");
    }

    void EntryUp_Click(object sender, RoutedEventArgs e) => MoveEntry(sender, -1);
    void EntryDown_Click(object sender, RoutedEventArgs e) => MoveEntry(sender, +1);

    void MoveEntry(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not ExamEntry entry) return;
        int i = examEntries.IndexOf(entry), j = i + delta;
        if (j < 0 || j >= examEntries.Count) return;
        examEntries.Move(i, j);
    }

    void EntryRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExamEntry entry) return;
        // 이번 작업의 문항은 ③의 체크를 끄는 것과 같다(다시 체크하면 돌아온다).
        if (entry.FromJob && results.FirstOrDefault(r => "job:" + r.Key == entry.Key) is { } r) r.Include = false;
        else examEntries.Remove(entry);
    }

    void ClearExam_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in results) r.Include = false;
        examEntries.Clear();
    }

    async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (examEntries.Count == 0) { Status("④에 넣은 문항이 없습니다. ③에서 체크하거나 [문항함에서 넣기]를 하세요."); return; }
        var info = new ExamInfo
        {
            Title = TitleBox.Text.Trim(), Subject = SubjectBox.Text.Trim(), Grade = GradeBox.Text.Trim(), Date = DateBox.Text.Trim(),
        };
        var form = FormBox.SelectedItem as FormChoice;
        if (job is not null)
        {
            job.Job.Info = info;
            job.Job.FormDir = form?.Dir == FormRendering.DefaultFormDir ? null : form?.Dir;
            job.Save();
        }

        var exam = new Exam { Info = info, Questions = examEntries.Select(x => x.Question).ToList() };
        var errors = ExamValidator.Validate(exam, expectedTotal: null).Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "시험지를 만들 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = string.Concat((info.Title.Length > 0 ? info.Title : "시험지").Split(Path.GetInvalidFileNameChars()));
        var dir = Path.Combine(ExamsDir, $"{DateTime.Now:yyyy-MM-dd HHmm} {name}");
        Directory.CreateDirectory(dir);
        JsonStore.Save(Path.Combine(dir, "시험.json"), exam);
        var output = Path.Combine(dir, name + ".hwp");
        bool pdf = PdfCheck.IsChecked == true, answers = AnswersCheck.IsChecked == true;
        var formDir = form?.Dir ?? FormRendering.DefaultFormDir;

        await Run($"한글로 시험지를 만드는 중… ({exam.Questions.Count}문항, 한글 창은 닫아 두세요)", async _ =>
        {
            await Task.Run(() =>
            {
                FormRendering.Render(exam, formDir, dir, output, original: true, pdf: pdf);
                if (!answers) return;
                var ans = Path.Combine(dir, name + "_정답.hwp");
                using var session = new HwpSession();
                new AnswerSheetRenderer(session).Render(exam, new TextStyle());
                session.SaveAs(ans, HwpFormat.Hwp);
                if (pdf) session.SaveAs(Path.ChangeExtension(ans, ".pdf"), HwpFormat.Pdf);
            });
            Status($"시험지를 만들었습니다: {dir} (학생용·원안지{(answers ? "·정답지" : "")}{(pdf ? "·PDF" : "")})");
            var open = pdf ? Path.ChangeExtension(output, ".pdf") : output;
            Process.Start(new ProcessStartInfo(open) { UseShellExecute = true });
        });
    }

    void LoadForms()
    {
        var forms = new List<FormChoice> { new("표준(B4)", FormRendering.DefaultFormDir) };
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
        foreach (var b in new[] { FindButton, MakeButton, RenderButton, ArchiveExamButton, ArchivePracticeButton }) b.IsEnabled = !busy;
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
}
