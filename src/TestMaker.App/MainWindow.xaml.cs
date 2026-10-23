using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
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

/// <summary>② 문항 카드: 찾은 문항(정하기 칸은 Q에 묶임)과 그 원문(자료에서 잘라 낸 그림 → 없으면 옮긴 글).</summary>
public sealed class QuestionCard(FoundQuestion q) : Observable
{
    public FoundQuestion Q { get; } = q;
    public ImageSource? Snapshot { get; private set; }
    public string Top { get; private set; } = "";
    public ImageSource? Figure { get; private set; }
    public string Bottom { get; private set; } = "";
    public Visibility SnapshotVisibility => Snapshot is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => Snapshot is null && Top.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingVisibility { get; private set; } = Visibility.Collapsed;

    public void Load(Authoring job, bool canTranscribe)
    {
        var snap = job.SnapshotOf(Q);
        Snapshot = Preview.LoadImage(snap, decodeWidth: 1000);
        var original = snap is null ? job.LoadOriginalForView(Q) : null;
        Top = original is null ? "" : Preview.Plain(original.Stem);
        Figure = original?.Image is { Path.Length: > 0 } img ? Preview.LoadImage(Path.Combine(job.Dir, img.Path), decodeWidth: 900) : null;
        Bottom = original is null ? "" : Preview.Body(original);
        MissingVisibility = Snapshot is null && original is null && canTranscribe ? Visibility.Visible : Visibility.Collapsed;
        OnChanged("");
    }
}

/// <summary>③ 결과 한 칸(만든 문항 하나)과 그 행선지(시험지 / 예상문제 / 버림=null).</summary>
public sealed class ResultItem(FoundQuestion question, string key, Question item, string? destination, Action<ResultItem> changed) : Observable
{
    public FoundQuestion Question { get; } = question;
    /// <summary>작업 안에서 이 결과를 가리키는 이름(예: "q01/원", "q01/1"). 행선지 단추 묶음 이름으로도 쓴다.</summary>
    public string Key { get; } = key;
    public string Source => Question.Source;
    public Question Item { get; } = item;
    public string Title { get; init; } = "";
    /// <summary>④ 목록·문항함에 보일 이름(문항 요약 포함).</summary>
    public string LongTitle { get; init; } = "";
    public string Top { get; init; } = "";
    public string Bottom { get; init; } = "";
    public string Answer { get; init; } = "";
    public ImageSource? Image { get; init; }

    string? destination = destination;
    public string? Destination
    {
        get => destination;
        set
        {
            if (!Set(ref destination, value)) return;
            OnChanged(nameof(IsExam)); OnChanged(nameof(IsPractice)); OnChanged(nameof(IsNone));
            changed(this);
        }
    }
    // 단추 셋(라디오)에 묶는다. 꺼지는 쪽(false)은 무시한다.
    public bool IsExam { get => destination == Ai.Destination.Exam; set { if (value) Destination = Ai.Destination.Exam; } }
    public bool IsPractice { get => destination == Ai.Destination.Practice; set { if (value) Destination = Ai.Destination.Practice; } }
    public bool IsNone { get => destination is null; set { if (value) Destination = null; } }
}

/// <summary>④ 목록의 문항 하나(이번 작업의 결과이거나 문항함의 문항).</summary>
public sealed class ExamEntry(string key, string title, string sub, Question question, bool fromJob) : Observable
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Sub { get; } = sub;
    /// <summary>그림 경로를 절대 경로로 바꾼 사본.</summary>
    public Question Question { get; } = question;
    public bool FromJob { get; } = fromJob;
    /// <summary>문항함 문항이면 그 폴더(작업에 저장해 두었다가 다시 열 때 읽는다).</summary>
    public string? BankDir { get; init; }

    int number;
    public int Number { get => number; set => Set(ref number, value); }
}

public partial class MainWindow : Window
{
    /// <summary>작업·양식·문항함을 두는 곳. (개발용) TESTMAKER_HOME 으로 바꿀 수 있다 — 선생님 문서 폴더를 건드리지 않고 시험할 때.</summary>
    static readonly string Home = Environment.GetEnvironmentVariable("TESTMAKER_HOME") is { Length: > 0 } h ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TestMaker");
    static readonly string JobsDir = Path.Combine(Home, "작업");
    static readonly string FormsDir = Path.Combine(Home, "양식");
    static readonly string BankDir = Path.Combine(Home, "문항함");

    readonly ObservableCollection<SourceFile> sources = [];
    readonly ObservableCollection<FoundQuestion> questions = [];
    /// <summary>② 문항 카드(①에서 고른 자료의 문항만, 원문이 그려진 채로).</summary>
    readonly ObservableCollection<QuestionCard> cards = [];
    /// <summary>카드를 그린 자료.</summary>
    string? cardsFor = "\0";
    readonly ObservableCollection<ResultItem> results = [];
    /// <summary>④ 시험지 탭 / 예상문제 탭.</summary>
    readonly ObservableCollection<ExamEntry> examEntries = [], practiceEntries = [];
    readonly QuestionBank bank = new(BankDir);
    readonly ClaudeCode? claude;
    Authoring? job;
    CancellationTokenSource? cts;
    /// <summary>작업을 화면에 펼치는 중(이때 생기는 변경은 저장하지 않는다).</summary>
    bool loading;
    /// <summary>①에서 고른 자료 이름. ②·③은 이 자료의 것만 보인다.</summary>
    string? activeSource;

    IEnumerable<FoundQuestion> VisibleQuestions => questions.Where(q => activeSource is null || q.Source == activeSource);
    IEnumerable<ResultItem> VisibleResults => results.Where(r => activeSource is null || r.Source == activeSource);

    bool PracticeShown => PracticeTab.IsChecked == true;
    ObservableCollection<ExamEntry> Current => PracticeShown ? practiceEntries : examEntries;
    string CurrentDestination => PracticeShown ? Destination.Practice : Destination.Exam;

    /// <summary>프로그램 설정(마지막에 연 작업).</summary>
    sealed class AppSettings
    {
        public string? LastJob { get; set; }
    }
    static readonly string SettingsFile = Path.Combine(Home, "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        SourceList.ItemsSource = sources;
        QuestionCards.ItemsSource = cards;
        ResultList.ItemsSource = results;
        // ②·③은 ①에서 고른 자료의 것만 보인다(④는 모든 자료가 쌓인다). ②는 RefreshCards가 고른 자료의 문항만 그린다.
        CollectionViewSource.GetDefaultView(results).Filter = o => activeSource is null || ((ResultItem)o).Source == activeSource;
        sources.CollectionChanged += (_, _) => DropHint.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        examEntries.CollectionChanged += (_, _) => { Renumber(); SaveLists(); };
        practiceEntries.CollectionChanged += (_, _) => { Renumber(); SaveLists(); };
        LoadForms();
        ShowTab();

        if (ClaudeCode.Find() is { } exe) claude = new ClaudeCode(exe);
        // TestMaker.exe <작업 폴더> 로 실행하면 그 작업을, 아니면 마지막에 연 작업을 연다.
        var args = Environment.GetCommandLineArgs();
        var start = args.Length > 1 ? args[1] : LoadSettings().LastJob;
        if (start is not null && File.Exists(Path.Combine(start, AuthoringJob.FileName)))
        {
            try { job = Authoring.Open(claude, start, HwpSource.Convert); job.RenderPages = PdfPages.Save; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) { job = null; }
        }
        ShowJob();
        if (claude is null) Status("Claude Code를 찾지 못했습니다. 문항 찾기·변형을 하려면 설치하고 로그인하세요(시험지 만들기는 됩니다).");
        else if (job is null) Status("[새 작업…]으로 시험 하나(예: 2026 2학기 1차고사)를 만들거나, [작업 목록…]에서 여세요.");
        else Status($"'{job.Name}' 작업을 열었습니다.");

        // (개발용) TESTMAKER_SNAPSHOT=파일.png: 화면을 그 파일로 저장하고 끝낸다(화면 캡처 없이 확인).
        if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT") is { Length: > 0 } snap)
            ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                // TESTMAKER_SNAPSHOT_SOURCE=n 이면 ① n번째 자료를 골라 찍는다.
                if (int.TryParse(Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_SOURCE"), out var si) && si < sources.Count)
                {
                    SourceList.SelectedIndex = si;
                    await Task.Delay(1000);
                }
                Snapshot(this, snap);
                // TESTMAKER_SNAPSHOT_ORIGINAL=파일.png 이면 ② 첫 문항의 원문 보기 창도 찍는다.
                if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_ORIGINAL") is { Length: > 0 } oSnap && job is not null && cards.Count > 0)
                {
                    var at = int.TryParse(Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_ORIGINAL_ROW"), out var n) ? n : 0;
                    var w = new OriginalWindow(job, cards[Math.Min(at, cards.Count - 1)].Q) { Owner = this };
                    w.Show();
                    await Task.Delay(1200);
                    Snapshot(w, oSnap);
                    w.Close();
                }
                // TESTMAKER_SNAPSHOT_PRACTICE=파일.png 이면 ④ 예상문제 탭을 연 화면도 찍는다.
                if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_PRACTICE") is { Length: > 0 } pSnap)
                {
                    PracticeTab.IsChecked = true;
                    await Task.Delay(700);
                    Snapshot(this, pSnap);
                }
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
                // TESTMAKER_SNAPSHOT_JOBS=파일.png 이면 작업 목록 창도 찍는다.
                if (Environment.GetEnvironmentVariable("TESTMAKER_SNAPSHOT_JOBS") is { Length: > 0 } jobsSnap)
                {
                    var w = new JobListWindow(JobsDir, job?.Dir) { Owner = this };
                    w.Show();
                    await Task.Delay(1500);
                    Snapshot(w, jobsSnap);
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

    // ── 작업(프로필) ───────────────────────────────────────

    /// <summary>지금 작업. 없으면 새 작업을 만들지 묻는다(취소하면 null).</summary>
    Authoring? EnsureJob()
    {
        if (job is null) NewJob();
        return job;
    }

    /// <summary>새 작업: 이름을 받아 만들고 연다.</summary>
    bool NewJob()
    {
        var w = new NewJobWindow { Owner = this };
        if (w.ShowDialog() != true) return false;
        var j = Authoring.Create(claude, JobsDir, w.JobName, HwpSource.Convert);
        j.Job.Info.Title = w.JobName;
        j.Job.Info.Grade = w.Grade;
        j.Job.Info.Subject = w.Subject.Length > 0 ? w.Subject : "과학";
        j.Save();
        SwitchTo(j);
        Status($"'{j.Name}' 작업을 만들었습니다. 자료를 넣으세요.");
        return true;
    }

    void SwitchTo(Authoring j)
    {
        j.RenderPages = PdfPages.Save;      // PDF 자료의 쪽 그림(원문 캡처·그림 자르기에 씀)
        job = j;
        SaveSettings(new AppSettings { LastJob = j.Dir });
        ShowJob();
    }

    /// <summary>작업의 모든 것을 화면에 펼친다: 자료, 문항 표, 결과와 행선지, ④ 두 탭의 목록과 순서, 시험 정보·양식.</summary>
    void ShowJob()
    {
        loading = true;
        try
        {
            sources.Clear(); questions.Clear(); results.Clear(); examEntries.Clear(); practiceEntries.Clear();
            JobNameText.Text = job?.Name ?? "작업 없음";
            JobText.Text = job is null ? "[새 작업…]으로 시작하세요." : $"작업 폴더: {job.Dir}";
            if (job is null) return;
            ReloadSourcesAndQuestions();
            RequestBox.Text = job.Job.Request;
            var info = job.Job.Info;
            SubjectBox.Text = info.Subject.Length > 0 ? info.Subject : "과학";
            GradeBox.Text = info.Grade; DateBox.Text = info.Date;
            FormBox.SelectedItem = FormBox.Items.Cast<FormChoice>().FirstOrDefault(f => f.Dir == (job.Job.FormDir ?? FormRendering.DefaultFormDir))
                                   ?? FormBox.Items[0];
            PdfCheck.IsChecked = job.Job.Pdf;
            AnswersCheck.IsChecked = job.Job.Answers;
            RefreshResults();
            RestoreList(examEntries, job.Job.ExamList);
            RestoreList(practiceEntries, job.Job.PracticeList);
            ExamTab.IsChecked = true;
        }
        finally { loading = false; }
        ShowTab();
    }

    /// <summary>자료 목록과 ② 문항 표를 작업에서 다시 읽는다(자료 순서대로 묶임).</summary>
    void ReloadSourcesAndQuestions()
    {
        var keep = activeSource;
        sources.Clear(); questions.Clear();
        if (job is null) { SelectSource(null); return; }
        foreach (var s in job.Job.Sources) sources.Add(s);
        foreach (var q in job.Job.Questions)
        {
            q.Status = File.Exists(Path.Combine(job.Dir, q.ItemFile)) ? "완료" : "";
            q.PropertyChanged -= Question_Changed;
            q.PropertyChanged += Question_Changed;
            questions.Add(q);
        }
        SelectSource(sources.FirstOrDefault(s => s.Name == keep) ?? sources.FirstOrDefault());
    }

    /// <summary>①에서 자료를 고른다(②·③이 그 자료로 바뀐다).</summary>
    void SelectSource(SourceFile? s)
    {
        if (SourceList.SelectedItem != s) SourceList.SelectedItem = s;   // → SourceList_SelectionChanged
        else ApplySourceFilter();
    }

    void ApplySourceFilter()
    {
        activeSource = (SourceList.SelectedItem as SourceFile)?.Name;
        // 카드는 자료가 바뀔 때만 다시 그린다(카드에서 고치는 중에 다시 그리면 자리가 흔들린다).
        if (activeSource != cardsFor || cards.Count != VisibleQuestions.Count()) RefreshCards();
        CollectionViewSource.GetDefaultView(results).Refresh();
        var label = activeSource is null ? "자료를 고르세요" : $"📄 {activeSource}";
        ActiveSourceText2.Text = label;
        ActiveSourceText3.Text = label;

        var src = SourceList.SelectedItem as SourceFile;
        QuestionEmpty.Text = src is null ? (sources.Count == 0 ? "①에 자료를 넣으세요." : "①에서 자료를 고르세요.")
            : !src.Analyzed ? "아직 문항을 찾지 않은 자료입니다.\n①의 [문항 찾기]를 누르세요."
            : "이 자료에서 찾은 문항이 없습니다.";
        QuestionEmpty.Visibility = VisibleQuestions.Any() ? Visibility.Collapsed : Visibility.Visible;
        ResultEmpty.Text = VisibleQuestions.Any() ? "아직 만든 결과가 없습니다.\n②에서 [변형 만들기]를 누르세요." : "";
        ResultEmpty.Visibility = VisibleResults.Any() ? Visibility.Collapsed : Visibility.Visible;
        UpdateDestSummary();
    }

    void UpdateDestSummary()
    {
        var shown = VisibleResults.ToList();
        var ex = shown.Count(r => r.Destination == Destination.Exam);
        var pr = shown.Count(r => r.Destination == Destination.Practice);
        DestSummary.Text = shown.Count == 0 ? "" : $"이 자료: 시험지 {ex} · 예상문제 {pr} · 버림 {shown.Count - ex - pr}";
    }

    /// <summary>저장해 둔 목록대로 문항함 문항을 다시 넣고 순서를 맞춘다(이번 작업 결과는 행선지대로 이미 들어와 있다).</summary>
    void RestoreList(ObservableCollection<ExamEntry> list, List<ExamListEntry> saved)
    {
        var missing = 0;
        foreach (var s in saved.Where(s => s.Key.StartsWith("bank:")))
        {
            if (list.Any(e => e.Key == s.Key)) continue;
            if (s.BankDir is not null && QuestionBank.LoadItem(s.BankDir) is { } item) list.Add(BankEntryOf(item));
            else missing++;
        }
        var order = saved.Select(s => s.Key).ToList();
        var sorted = list.OrderBy(e => order.IndexOf(e.Key) is var i && i >= 0 ? i : int.MaxValue).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var from = list.IndexOf(sorted[i]);
            if (from != i) list.Move(from, i);
        }
        if (missing > 0) Status($"④에 넣어 두었던 문항함 문항 {missing}개를 찾지 못했습니다(문항함에서 지워졌을 수 있음).");
    }

    static ExamEntry BankEntryOf(BankItem item) =>
        new("bank:" + item.Id, item.Title, $"문항함 · {item.CategoryText}", QuestionBank.Absolutize(item.Question, item.Dir), fromJob: false)
        { BankDir = item.Dir };

    void SaveLists()
    {
        if (loading || job is null) return;
        static List<ExamListEntry> Of(IEnumerable<ExamEntry> l) => l.Select(e => new ExamListEntry { Key = e.Key, BankDir = e.BankDir }).ToList();
        job.Job.ExamList = Of(examEntries);
        job.Job.PracticeList = Of(practiceEntries);
        job.Save();
    }

    /// <summary>시험 정보·양식·추가 요청·PDF/정답지 선택을 바뀔 때마다 작업에 저장한다. 이름 칸은 지금 탭의 것.</summary>
    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || job is null || !IsInitialized) return;
        var j = job.Job;
        if (PracticeShown) j.PracticeTitle = TitleBox.Text.Trim();
        else j.Info.Title = TitleBox.Text.Trim();
        j.Info.Subject = SubjectBox.Text.Trim();
        j.Info.Grade = GradeBox.Text.Trim();
        j.Info.Date = DateBox.Text.Trim();
        j.Request = RequestBox.Text;
        var form = FormBox.SelectedItem as FormChoice;
        j.FormDir = form is null || form.Dir == FormRendering.DefaultFormDir ? null : form.Dir;
        j.Pdf = PdfCheck.IsChecked == true;
        j.Answers = AnswersCheck.IsChecked == true;
        job.Save();
    }

    void Question_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (loading || job is null || e.PropertyName == nameof(FoundQuestion.Status)) return;
        job.Save();
        // 쓰기·원문항을 바꾸면 ③이 바로 바뀐다(원문항을 [문항 찾기] 때 옮겨 둔 문항).
        if (e.PropertyName is nameof(FoundQuestion.Include) or nameof(FoundQuestion.KeepOriginal)) RefreshResults();
    }

    static AppSettings LoadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonStore.Load<AppSettings>(SettingsFile) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) { return new(); }
    }

    static void SaveSettings(AppSettings s)
    {
        try { JsonStore.Save(SettingsFile, s); } catch (IOException) { }
    }

    void NewJob_Click(object sender, RoutedEventArgs e) => NewJob();

    void OpenJob_Click(object sender, RoutedEventArgs e)
    {
        var w = new JobListWindow(JobsDir, job?.Dir) { Owner = this };
        var ok = w.ShowDialog() == true;
        // 목록에서 지금 작업을 고쳤거나(이름·폴더) 지웠으면 화면을 맞춘다.
        if (w.CurrentChanged)
        {
            if (w.CurrentDir is { } moved) SwitchTo(Authoring.Open(claude, moved, HwpSource.Convert));
            else
            {
                job = null;
                SaveSettings(new AppSettings());
                ShowJob();
                Status("열려 있던 작업을 지웠습니다. [새 작업…]이나 [작업 목록…]에서 시작하세요.");
            }
        }
        if (!ok) return;
        if (w.WantsNew) { NewJob(); return; }
        if (w.Chosen is null) return;
        try
        {
            SwitchTo(Authoring.Open(claude, w.Chosen, HwpSource.Convert));
            Status($"'{job!.Name}' 작업을 열었습니다.");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"작업을 열지 못했습니다: {ex.Message}", "작업 열기");
        }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = job?.Dir ?? JobsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void ExamsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (job is null) return;
        var dir = Path.Combine(job.Dir, AuthoringJob.ExamsDir);
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
        if (EnsureJob() is not { } j) return;
        var added = j.AddImage(ms.ToArray(), $"캡처 {DateTime.Now:HHmmss}");
        sources.Add(added);
        SelectSource(added);
        Status("붙여 넣은 그림을 자료에 넣었습니다. [문항 찾기]를 누르세요.");
    }

    async Task AddFiles(string[] files)
    {
        if (EnsureJob() is not { } j) return;
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
                SelectSource(src);           // 새로 넣은 자료를 고른다
            }
        });
        if (bad.Count > 0) MessageBox.Show(this, "그림, PDF, 한글 파일만 넣을 수 있습니다:\n" + string.Join("\n", bad.Select(Path.GetFileName)), "자료");
        Status($"자료 {sources.Count}개. 새 자료는 [문항 찾기]를 누르세요.");
    }

    void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || SourceList.SelectedItem is not SourceFile s) return;
        var n = questions.Count(q => q.Source == s.Name);
        if (n > 0 && MessageBox.Show(this, $"'{s.Name}'을(를) 빼면 이 자료에서 찾은 문항 {n}개와 그 결과(시험지·예상문제로 보낸 것 포함)도 함께 빠집니다. 뺄까요?",
                "자료 빼기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        job.RemoveSource(s, bank, JobsDir);
        ReloadSourcesAndQuestions();
        RefreshResults();
        Status($"'{s.Name}'을(를) 뺐습니다.");
    }

    void SourceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySourceFilter();
        // 새 카드를 다 그린 뒤 맨 위로.
        Dispatcher.BeginInvoke(CardScroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>② 문항 카드를 지금 고른 자료의 문항으로 다시 그린다(원문 그림·옮긴 글을 읽음).</summary>
    void RefreshCards()
    {
        cards.Clear();
        cardsFor = activeSource;
        if (job is null) return;
        foreach (var q in VisibleQuestions)
        {
            var card = new QuestionCard(q);
            card.Load(job, claude is not null);
            cards.Add(card);
        }
    }

    /// <summary>[크게]: 원문을 큰 창으로.</summary>
    void ShowOriginal_Click(object sender, RoutedEventArgs e)
    {
        var q = (sender as FrameworkElement)?.Tag as FoundQuestion
                ?? ((sender as FrameworkElement)?.DataContext as QuestionCard)?.Q;
        if (q is not null) ShowOriginal(q);
    }

    void ShowOriginal(FoundQuestion q)
    {
        if (job is null) return;
        new OriginalWindow(job, q, TranscribeAsync) { Owner = this }.Show();
    }

    /// <summary>카드의 [원문 옮기기]: 그 자료 문항들의 원문을 한 번에 옮긴다.</summary>
    async void CardTranscribe_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || (sender as FrameworkElement)?.Tag is not FoundQuestion q
            || job.Job.Sources.FirstOrDefault(s => s.Name == q.Source) is not { } src) return;
        await TranscribeAsync(src);
    }

    /// <summary>원문 보기 창의 [원문 옮기기]: 그 자료 문항들의 원문을 옮긴다(문항 목록과 결과는 그대로).</summary>
    async Task<bool> TranscribeAsync(SourceFile s)
    {
        if (job is null) return false;
        var j = job;
        int saved = 0;
        await Run($"'{s.Name}' 문항들의 원문을 옮기는 중… (1~3분)", async ct =>
        {
            saved = await j.TranscribeOriginalsAsync(s, Status, ct);
            RefreshResults();
            RefreshCards();
            Status(saved > 0 ? $"원문 {saved}개를 옮겼습니다. ②의 [보기]로 볼 수 있습니다." : "옮길 원문이 없었습니다.");
        });
        return saved > 0;
    }

    /// <summary>[문항 찾기]: 아직 문항을 찾지 않은 자료만 읽어 ② 표에 더한다.</summary>
    async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || job.Job.Sources.Count == 0) { Status("자료를 먼저 넣으세요."); return; }
        var fresh = job.Job.Sources.Count(s => !s.Analyzed);
        if (fresh == 0)
        {
            Status("새로 넣은 자료가 없습니다. 자료를 더 넣거나, 다시 찾으려면 자료를 마우스 오른쪽 단추로 눌러 [이 자료에서 문항 다시 찾기].");
            return;
        }
        await FindAsync(null, $"새 자료 {fresh}개를 읽고 문항을 찾는 중… (자료 양에 따라 1~3분)");
    }

    async void RefindSource_Click(object sender, RoutedEventArgs e)
    {
        if (job is null || SourceList.SelectedItem is not SourceFile s) return;
        if (questions.Any(q => q.Source == s.Name) && MessageBox.Show(this,
                $"'{s.Name}'에서 찾은 문항과 그 결과(시험지·예상문제로 보낸 것 포함)를 지우고 다시 찾습니다. 계속할까요?",
                "다시 찾기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await FindAsync(s, $"'{s.Name}'에서 문항을 다시 찾는 중…");
    }

    async Task FindAsync(SourceFile? again, string message)
    {
        var j = job!;
        await Run(message, async ct =>
        {
            var found = await j.FindQuestionsAsync(Status, ct, again: again, bank: bank, jobsRoot: JobsDir);
            loading = true;
            try { ReloadSourcesAndQuestions(); }
            finally { loading = false; }
            RefreshResults();
            Status(found.Count == 0 ? "문항을 찾지 못했습니다. 자료를 확인하세요."
                                    : $"문항 {found.Count}개를 찾았습니다(② 표에 자료별로). 변형 방식과 수를 정하고 [변형 만들기]를 누르세요.");
        });
    }

    // ── ② 변형 ───────────────────────────────────────────

    /// <summary>
    /// 변형 수 칸(쳐 넣을 수 있는 드롭다운)에서 ↑↓: 목록이 닫혀 있으면 값을 바꾸지 않고 위·아래 문항의 변형 수 칸으로(엑셀처럼).
    /// 목록이 열려 있으면 원래대로 목록 안에서 움직인다.
    /// </summary>
    void QuestionCards_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)) return;
        var box = FindParent<System.Windows.Controls.ComboBox>(e.OriginalSource as DependencyObject);
        if (box is null || (string?)box.Tag != "Variants" || box.IsDropDownOpen || box.DataContext is not QuestionCard card) return;
        e.Handled = true;
        int i = cards.IndexOf(card) + (e.Key == System.Windows.Input.Key.Down ? 1 : -1);
        if (i < 0 || i >= cards.Count) return;
        if (QuestionCards.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement next
            && FindChild<System.Windows.Controls.ComboBox>(next, c => (string?)c.Tag == "Variants") is { } nextBox)
        {
            next.BringIntoView();
            nextBox.Focus();
        }
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
        if (job is null || !VisibleQuestions.Any()) { Status("①에서 자료를 고르고, 먼저 [문항 찾기]를 하세요."); return; }
        // 지금 보이는 자료(①에서 고른 것)의 문항만 만든다.
        var todo = VisibleQuestions.Where(q => q.Include && q.ExpectedCount > 0 && q.Status != "완료").ToList();
        if (todo.Count == 0)
        {
            Status("이 자료에서 새로 만들 문항이 없습니다. 다시 만들려면 문항을 마우스 오른쪽 단추로 눌러 [이 문항 다시 만들기].");
            return;
        }
        await MakeAsync(todo);
    }

    async void Remake_Click(object sender, RoutedEventArgs e)
    {
        if (((sender as FrameworkElement)?.DataContext as QuestionCard)?.Q is not { } q) return;
        if (results.Any(r => r.Question == q && r.Destination is not null) && MessageBox.Show(this,
                $"{q.Number}번을 다시 만들면 지금 결과의 행선지(시험지·예상문제)가 지워지고 새 결과를 다시 골라야 합니다. 계속할까요?",
                "다시 만들기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await MakeAsync([q]);
    }

    async Task MakeAsync(List<FoundQuestion> todo)
    {
        var j = job!;
        j.Job.Request = RequestBox.Text;
        // 다시 만드는 문항은 예전 결과의 행선지를 지운다(문항함에 자동으로 들어간 것도 정리).
        foreach (var q in todo) j.ForgetResults(q, bank, JobsDir);
        j.Save();
        RefreshResults();
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
        Status("변형을 만들었습니다. ③에서 문항마다 시험지·예상문제 중 보낼 곳을 고르세요(고르지 않으면 버려집니다).");
    }

    // ── ③ 결과와 행선지 ───────────────────────────────────

    void RefreshResults()
    {
        results.Clear();
        if (job is not null)
        {
            foreach (var q in questions.Where(q => q.Include))
            {
                foreach (var res in job.Results(q))
                {
                    var item = res.Item;
                    results.Add(new ResultItem(q, res.Key, item, job.DestinationOf(res.Key), OnDestinationChanged)
                    {
                        Title = $"{q.Number}번 · {res.Label}  [{Format.Points(item.TotalPoints)}점]",
                        LongTitle = Authoring.ResultTitle(q, res.Label),
                        Top = Preview.Plain(item.Stem),
                        Image = item.Image is { Path.Length: > 0 } img ? Preview.LoadImage(Path.Combine(job.Dir, img.Path)) : null,
                        Bottom = Preview.Body(item),
                        Answer = Preview.Answer(item),
                    });
                }
            }
        }
        SyncJobEntries();
        if (IsInitialized) ApplySourceFilter();
    }

    /// <summary>③에서 행선지를 바꾸면: 작업에 적고(문항함 자동 정리), ④의 두 탭을 맞춘다.</summary>
    void OnDestinationChanged(ResultItem r)
    {
        if (job is null) return;
        try { job.SetDestination(r.Question, r.Key, r.Destination, bank, JobsDir); }
        catch (IOException ex) { Status("문항함에 넣지 못했습니다: " + ex.Message); }
        SyncJobEntries();
    }

    void AllExam_Click(object sender, RoutedEventArgs e) => SetAll(Destination.Exam);
    void AllPractice_Click(object sender, RoutedEventArgs e) => SetAll(Destination.Practice);
    void AllNone_Click(object sender, RoutedEventArgs e)
    {
        if (VisibleResults.Any(r => r.Destination is not null) && MessageBox.Show(this, "이 자료의 결과를 모두 '버림'으로 바꿀까요? ④ 두 탭에서도 빠집니다.",
                "모두 버림", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SetAll(null);
    }

    /// <summary>③의 '모두': 지금 보이는 자료(①에서 고른 것)의 결과만.</summary>
    void SetAll(string? dest)
    {
        foreach (var r in VisibleResults.ToList()) r.Destination = dest;
        Status(dest is null ? "이 자료의 결과를 모두 버림으로 바꿨습니다." : $"이 자료의 결과를 모두 {dest}(으)로 보냈습니다.");
    }

    // ── ④ 시험지 / 예상문제 ───────────────────────────────

    /// <summary>④의 '이번 작업' 문항을 ③의 행선지와 맞춘다. 문항함에서 넣은 문항과 순서는 그대로 둔다.</summary>
    void SyncJobEntries()
    {
        foreach (var (list, dest) in new[] { (examEntries, Destination.Exam), (practiceEntries, Destination.Practice) })
        {
            var wanted = results.Where(r => r.Destination == dest).ToList();
            var keys = wanted.Select(r => "job:" + r.Key).ToHashSet();
            foreach (var stale in list.Where(e => e.FromJob && !keys.Contains(e.Key)).ToList()) list.Remove(stale);
            foreach (var r in wanted)
            {
                var entry = new ExamEntry("job:" + r.Key, r.LongTitle, $"이번 작업 · {r.Source}", QuestionBank.Absolutize(r.Item, job!.Dir), fromJob: true);
                var i = list.ToList().FindIndex(e => e.Key == entry.Key);
                if (i >= 0) list[i] = entry;          // 다시 만든 문항이면 새 것으로
                else list.Add(entry);
            }
        }
        UpdateDestSummary();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ShowTab();
    }

    /// <summary>④에서 고른 탭(시험지/예상문제)을 펼친다: 목록, 이름 칸, 만들기 단추.</summary>
    void ShowTab()
    {
        if (ExamList is null) return;
        ExamList.ItemsSource = Current;
        var wasLoading = loading;
        loading = true;
        try
        {
            if (PracticeShown)
            {
                TitleLabel.Text = "이름";
                TitleBox.Text = job is null ? "" : job.Job.PracticeTitle.Length > 0 ? job.Job.PracticeTitle : $"{job.Name} 예상문제";
                RenderButton.Content = "예상문제지 만들기";
                TabHint.Text = "③에서 '예상문제'로 보낸 문항이 들어옵니다. 문항함에서 더 넣고 ↑↓로 순서를 정하세요.";
            }
            else
            {
                TitleLabel.Text = "시험 이름";
                TitleBox.Text = job?.Job.Info.Title ?? "";
                RenderButton.Content = "시험지 만들기";
                TabHint.Text = "③에서 '시험지'로 보낸 문항이 들어옵니다. 문항함에서 더 넣고 ↑↓로 순서를 정하세요.";
            }
        }
        finally { loading = wasLoading; }
        Renumber();
    }

    void Renumber()
    {
        foreach (var list in new[] { examEntries, practiceEntries })
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        if (ExamTab is null) return;
        ExamTab.Content = examEntries.Count > 0 ? $"시험지 {examEntries.Count}" : "시험지";
        PracticeTab.Content = practiceEntries.Count > 0 ? $"예상문제 {practiceEntries.Count}" : "예상문제";
        var cur = Current;
        ExamEmpty.Visibility = cur.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExamSummary.Text = cur.Count == 0 ? "" : $"{cur.Count}문항 · 총 {Format.Points(cur.Sum(e => e.Question.TotalPoints))}점";
    }

    void AddFromBank_Click(object sender, RoutedEventArgs e) => ShowBank();
    void OpenBank_Click(object sender, RoutedEventArgs e) => ShowBank();

    /// <summary>문항함 창. 고른 문항은 ④의 지금 탭에 들어간다.</summary>
    void ShowBank()
    {
        var w = new BankWindow(bank, CurrentDestination) { Owner = this };
        if (w.ShowDialog() != true) return;
        var list = Current;
        int added = 0;
        foreach (var item in w.Picked)
        {
            var key = "bank:" + item.Id;
            // 이번 작업에서 보낸 문항(문항함에 자동으로 들어간 것)은 이미 들어 있다.
            if (list.Any(x => x.Key == key) || job?.Job.Archived.ContainsValue(item.Dir) == true) continue;
            list.Add(BankEntryOf(item));
            added++;
        }
        Status($"문항함에서 {added}문항을 ④ {CurrentDestination}에 넣었습니다.");
    }

    void EntryUp_Click(object sender, RoutedEventArgs e) => MoveEntry(sender, -1);
    void EntryDown_Click(object sender, RoutedEventArgs e) => MoveEntry(sender, +1);

    void MoveEntry(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not ExamEntry entry) return;
        var list = Current;
        int i = list.IndexOf(entry), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.Move(i, j);
    }

    void EntryRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExamEntry entry) return;
        // 이번 작업의 문항은 ③에서 '버림'으로 바꾸는 것과 같다(다시 고르면 돌아온다).
        if (entry.FromJob && results.FirstOrDefault(r => "job:" + r.Key == entry.Key) is { } r) r.Destination = null;
        else Current.Remove(entry);
    }

    void ClearExam_Click(object sender, RoutedEventArgs e)
    {
        var list = Current;
        if (list.Count == 0) return;
        if (MessageBox.Show(this, $"④ {CurrentDestination} 목록을 비울까요? 이번 작업의 문항은 ③에서 '버림'이 됩니다.", "비우기",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var r in results.Where(r => r.Destination == CurrentDestination).ToList()) r.Destination = null;
        list.Clear();
    }

    async void Render_Click(object sender, RoutedEventArgs e)
    {
        var list = Current;
        var kind = CurrentDestination;
        if (list.Count == 0) { Status($"④ {kind}에 넣은 문항이 없습니다. ③에서 '{kind}'로 보내거나 [문항함에서 넣기]를 하세요."); return; }
        if (job is null && EnsureJob() is null) return;
        var j = job!;
        Settings_Changed(this, e);
        var info = new ExamInfo
        {
            Title = TitleBox.Text.Trim(), Subject = SubjectBox.Text.Trim(), Grade = GradeBox.Text.Trim(), Date = DateBox.Text.Trim(),
        };
        var form = FormBox.SelectedItem as FormChoice;

        var exam = new Exam { Info = info, Questions = list.Select(x => x.Question).ToList() };
        var errors = ExamValidator.Validate(exam, expectedTotal: null).Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), $"{kind}를 만들 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = string.Concat((info.Title.Length > 0 ? info.Title : kind).Split(Path.GetInvalidFileNameChars()));
        var dir = Path.Combine(j.Dir, AuthoringJob.ExamsDir, $"{DateTime.Now:yyyy-MM-dd HHmm} {kind}");
        Directory.CreateDirectory(dir);
        JsonStore.Save(Path.Combine(dir, "시험.json"), exam);
        var output = Path.Combine(dir, name + ".hwp");
        bool pdf = PdfCheck.IsChecked == true, answers = AnswersCheck.IsChecked == true;
        var formDir = form?.Dir ?? FormRendering.DefaultFormDir;

        await Run($"한글로 {kind}를 만드는 중… ({exam.Questions.Count}문항, 한글 창은 닫아 두세요)", async _ =>
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
            Status($"{kind}를 만들었습니다: {dir} (학생용·원안지{(answers ? "·정답지" : "")}{(pdf ? "·PDF" : "")})");
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
}
