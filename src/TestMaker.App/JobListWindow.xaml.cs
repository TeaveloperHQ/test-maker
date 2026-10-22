using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using TestMaker.Ai;
using TestMaker.Core.Serialization;

namespace TestMaker.App;

/// <summary>작업 목록의 한 줄.</summary>
public sealed class JobRow(JobSummary s, bool current)
{
    public JobSummary Summary { get; } = s;
    public string Name => Summary.Name;
    public string When => Summary.Modified.Date == DateTime.Today ? $"오늘 {Summary.Modified:HH:mm}" : $"{Summary.Modified:yyyy-MM-dd}";
    public string Detail => $"자료 {Summary.Sources} · 찾은 문항 {Summary.Questions} · 시험지 {Summary.ExamQuestions}문항";
    public bool IsCurrent { get; } = current;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 작업 목록: 열기, 새로 만들기, 고치기(이름·학년·과목), 복제, 지우기(휴지통).
/// 닫힌 뒤 <see cref="Chosen"/>(열 작업), <see cref="WantsNew"/>, 지금 작업이 바뀌었으면 <see cref="CurrentDir"/>(지웠으면 null).
/// </summary>
public partial class JobListWindow : Window
{
    readonly string jobsRoot;

    public string? Chosen { get; private set; }
    public bool WantsNew { get; private set; }
    /// <summary>지금 열린 작업의 폴더(고치기로 바뀌었을 수 있다). 지웠으면 null.</summary>
    public string? CurrentDir { get; private set; }
    /// <summary>지금 열린 작업을 고치거나 지웠는지.</summary>
    public bool CurrentChanged { get; private set; }

    public JobListWindow(string jobsRoot, string? currentDir)
    {
        this.jobsRoot = jobsRoot;
        CurrentDir = currentDir;
        InitializeComponent();
        Reload(select: null);
    }

    static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    void Reload(string? select)
    {
        var rows = Authoring.List(jobsRoot).Select(s => new JobRow(s, Same(s.Dir, CurrentDir))).ToList();
        JobList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JobList.SelectedItem = rows.FirstOrDefault(r => Same(r.Summary.Dir, select)) ?? rows.FirstOrDefault();
    }

    JobRow? Selected => JobList.SelectedItem as JobRow;

    void JobList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        bool any = Selected is not null;
        foreach (var b in new[] { OpenButton, EditButton, CopyButton, DeleteButton, FolderButton }) b.IsEnabled = any;
    }

    void JobList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        Chosen = row.Summary.Dir;
        DialogResult = true;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        WantsNew = true;
        DialogResult = true;
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        AuthoringJob job;
        try { job = JsonStore.Load<AuthoringJob>(Path.Combine(row.Summary.Dir, AuthoringJob.FileName)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { Fail(ex); return; }

        var w = new NewJobWindow("작업 고치기", "저장", "작업 이름과 학년·과목을 바꿉니다. 폴더 이름도 새 이름으로 바뀝니다.",
                                 row.Name, job.Info.Grade, job.Info.Subject) { Owner = this };
        if (w.ShowDialog() != true) return;
        try
        {
            var dir = Authoring.Edit(row.Summary.Dir, w.JobName, w.Grade, w.Subject);
            if (row.IsCurrent) { CurrentDir = dir; CurrentChanged = true; }
            if (Same(dir, row.Summary.Dir) && FolderName(w.JobName) != Path.GetFileName(dir.TrimEnd('\\')))
                MessageBox.Show(this, "이름은 바꿨지만, 폴더 안 파일이 다른 프로그램에서 열려 있어 폴더 이름은 그대로 두었습니다.", "작업 고치기");
            Reload(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { Fail(ex); }
    }

    static string FolderName(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var w = new NewJobWindow("작업 복제", "복제", "자료·문항·결과·시험지 목록을 새 이름의 작업으로 복사합니다(이미 만든 시험지 파일은 빼고).",
                                 row.Name + " 복사본", null, null, showInfo: false) { Owner = this };
        if (w.ShowDialog() != true) return;
        try { Reload(Authoring.Duplicate(row.Summary.Dir, w.JobName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { Fail(ex); }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var current = row.IsCurrent ? "\n\n지금 열려 있는 작업입니다. 지우면 화면이 비워집니다." : "";
        if (MessageBox.Show(this, $"'{row.Name}' 작업을 지울까요?\n자료·문항·만든 시험지가 모두 휴지통으로 갑니다(휴지통에서 되살릴 수 있음).{current}",
                "작업 지우기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            FileSystem.DeleteDirectory(row.Summary.Dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            if (row.IsCurrent) { CurrentDir = null; CurrentChanged = true; }
            Reload(select: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { Fail(ex); }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) Process.Start(new ProcessStartInfo(row.Summary.Dir) { UseShellExecute = true });
    }

    void Fail(Exception ex) => MessageBox.Show(this, ex.Message, "작업 목록", MessageBoxButton.OK, MessageBoxImage.Warning);
}
