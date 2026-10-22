using System.Diagnostics;
using System.IO;
using System.Windows;
using TestMaker.Ai;

namespace TestMaker.App;

/// <summary>작업 목록의 한 줄.</summary>
public sealed class JobRow(JobSummary s, bool current)
{
    public JobSummary Summary { get; } = s;
    public string Name => Summary.Name;
    public string When => Summary.Modified.Date == DateTime.Today ? $"오늘 {Summary.Modified:HH:mm}" : $"{Summary.Modified:yyyy-MM-dd}";
    public string Detail => $"자료 {Summary.Sources} · 찾은 문항 {Summary.Questions} · 시험지 {Summary.ExamQuestions}문항";
    public Visibility CurrentVisibility => current ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>작업 목록: 골라서 열거나 새로 만든다. 결과는 <see cref="Chosen"/>(열 작업 폴더) 또는 <see cref="WantsNew"/>.</summary>
public partial class JobListWindow : Window
{
    public string? Chosen { get; private set; }
    public bool WantsNew { get; private set; }

    public JobListWindow(string jobsRoot, string? currentDir)
    {
        InitializeComponent();
        var rows = Authoring.List(jobsRoot)
            .Select(s => new JobRow(s, currentDir is not null && Path.GetFullPath(s.Dir) == Path.GetFullPath(currentDir))).ToList();
        JobList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) JobList.SelectedIndex = 0;
    }

    void JobList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenButton.IsEnabled = JobList.SelectedItem is JobRow;

    void JobList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow row) return;
        Chosen = row.Summary.Dir;
        DialogResult = true;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        WantsNew = true;
        DialogResult = true;
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is JobRow row) Process.Start(new ProcessStartInfo(row.Summary.Dir) { UseShellExecute = true });
    }
}
