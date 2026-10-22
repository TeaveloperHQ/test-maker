using System.Windows;

namespace TestMaker.App;

/// <summary>작업 이름(+학년·과목)을 받는다. 새 작업·고치기·복제에 같이 쓴다.</summary>
public partial class NewJobWindow : Window
{
    public string JobName => NameBox.Text.Trim();
    public string Grade => GradeBox.Text.Trim();
    public string Subject => SubjectBox.Text.Trim();

    public NewJobWindow(string? suggestion = null) : this("새 작업", "만들기", null, suggestion, null, null) { }

    /// <param name="hint">null이면 새 작업 설명 그대로.</param>
    /// <param name="showInfo">학년·과목 칸을 보일지(복제는 이름만).</param>
    public NewJobWindow(string header, string ok, string? hint, string? name, string? grade, string? subject, bool showInfo = true)
    {
        InitializeComponent();
        Title = header;
        HeaderText.Text = header;
        OkButton.Content = ok;
        if (hint is not null) HintText.Text = hint;
        NameBox.Text = name ?? "";
        if (grade is not null) GradeBox.Text = grade;
        if (subject is not null) SubjectBox.Text = subject;
        if (!showInfo)
            foreach (var e in new FrameworkElement[] { GradeBox, SubjectBox }) e.IsEnabled = false;
        NameBox.SelectAll();
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (JobName.Length == 0)
        {
            MessageBox.Show(this, "작업 이름을 적으세요. 예: 2026 2학기 1차고사", Title);
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
