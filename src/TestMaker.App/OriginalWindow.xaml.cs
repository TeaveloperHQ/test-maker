using System.Diagnostics;
using System.IO;
using System.Windows;
using TestMaker.Ai;

namespace TestMaker.App;

/// <summary>
/// ② 문항의 원문 보기 — 그 문항만. 자료에서 문항 영역만 잘라 낸 그림(원문 그대로)과 옮긴 글(원문항)을 보인다.
/// 둘 다 없으면(예전에 찾은 문항) 그 자료 문항들의 원문을 바로 옮기고 보인다.
/// </summary>
public partial class OriginalWindow : Window
{
    readonly Authoring job;
    readonly FoundQuestion question;
    readonly SourceFile? source;
    readonly string? sourceFile;
    /// <summary>원문 옮기기: 그 자료 문항들의 원문을 옮긴다(주 창이 바쁨 표시·오류 처리를 맡는다). 옮겼으면 true.</summary>
    readonly Func<SourceFile, Task<bool>>? transcribe;
    bool working;

    public OriginalWindow(Authoring job, FoundQuestion q, Func<SourceFile, Task<bool>>? transcribe = null)
    {
        InitializeComponent();
        this.job = job;
        question = q;
        this.transcribe = transcribe;
        Title = $"{q.Number}번 원문";
        TitleText.Text = $"{q.Number}번 원문 · {q.Summary}";
        source = job.Job.Sources.FirstOrDefault(s => s.Name == q.Source);
        SourceText.Text = $"자료: {q.Source}";
        sourceFile = source?.ReadFiles.FirstOrDefault() is { } f ? Path.Combine(job.Dir, f) : null;
        OpenSourceButton.IsEnabled = sourceFile is not null && File.Exists(sourceFile);
        // 바로 보인다(Claude는 [원문 옮기기]를 눌렀을 때만).
        Loaded += (_, _) => Show();
    }

    /// <summary>있는 것을 보인다. 원문 그림이나 옮긴 글이 하나라도 있으면 true.</summary>
    bool Show()
    {
        var snapshot = job.SnapshotOf(question);
        SnapshotImage.Source = Preview.LoadImage(snapshot);
        SnapshotLabel.Visibility = SnapshotBox.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;

        var original = job.LoadOriginalForView(question);
        TextLabel.Visibility = original is not null && snapshot is not null ? Visibility.Visible : Visibility.Collapsed;
        TopText.Text = original is null ? "" : Preview.Plain(original.Stem);
        FigureImage.Source = original?.Image is { Path.Length: > 0 } img ? Preview.LoadImage(Path.Combine(job.Dir, img.Path)) : null;
        BottomText.Text = original is null ? "" : Preview.Body(original);
        AnswerText.Text = original is null ? "" : Preview.Answer(original);
        AnswerBox.Visibility = AnswerText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool any = snapshot is not null || original is not null;
        // 원문 그림이나 옮긴 글 중 빠진 것이 있을 때만(옮기기는 빠진 것만 채운다).
        TranscribeButton.Visibility = transcribe is not null && source is not null && (snapshot is null || original is null)
            ? Visibility.Visible : Visibility.Collapsed;
        TranscribeButton.Content = any ? "원문 그림 채우기" : "원문 옮기기";
        if (!working)
        {
            NoticeBox.Visibility = any && snapshot is not null ? Visibility.Collapsed : Visibility.Visible;
            NoticeText.Text = any
                ? "원문 그림(자료에서 이 문항 영역만 잘라 낸 것)이 아직 없습니다. [원문 그림 채우기]를 누르면 만들어 보여 줍니다."
                : $"이 문항은 원문을 아직 옮기지 않았습니다. [원문 옮기기]를 누르면 자료에서 {question.Number}번만 잘라 보여 줍니다.";
        }
        return any;
    }

    async Task TranscribeAsync()
    {
        if (transcribe is null || source is null || working) return;
        working = true;
        TranscribeButton.IsEnabled = false;
        NoticeBox.Visibility = Visibility.Visible;
        Working.Visibility = Visibility.Visible;
        NoticeText.Text = $"'{source.Name}' 문항들의 원문을 옮기는 중입니다(1~3분). 끝나면 {question.Number}번만 보입니다. 한 번 옮기면 다음부터는 바로 열립니다.";
        try { await transcribe(source); }
        finally
        {
            working = false;
            Working.Visibility = Visibility.Collapsed;
            TranscribeButton.IsEnabled = true;
            if (!Show()) NoticeText.Text = "원문을 옮기지 못했습니다. [자료 열기]로 원래 파일에서 확인하세요.";
        }
    }

    async void Transcribe_Click(object sender, RoutedEventArgs e) => await TranscribeAsync();

    void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (sourceFile is not null && File.Exists(sourceFile))
            Process.Start(new ProcessStartInfo(sourceFile) { UseShellExecute = true });
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
