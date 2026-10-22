using System.Windows;

namespace TestMaker.App;

/// <summary>새 작업: 이름(+학년·과목)을 받는다.</summary>
public partial class NewJobWindow : Window
{
    public string JobName => NameBox.Text.Trim();
    public string Grade => GradeBox.Text.Trim();
    public string Subject => SubjectBox.Text.Trim();

    public NewJobWindow(string? suggestion = null)
    {
        InitializeComponent();
        NameBox.Text = suggestion ?? "";
        NameBox.SelectAll();
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (JobName.Length == 0)
        {
            MessageBox.Show(this, "작업 이름을 적으세요. 예: 2026 2학기 1차고사", "새 작업");
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
