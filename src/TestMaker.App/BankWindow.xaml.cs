using System.IO;
using System.Windows;
using System.Windows.Media;
using TestMaker.Ai;
using TestMaker.Core.Bank;
using TestMaker.Core.Models;

namespace TestMaker.App;

/// <summary>문항함 목록의 한 칸.</summary>
public sealed class BankEntry(BankItem item, Action<BankEntry> pickedChanged) : Observable
{
    static readonly Brush ExamBrush = Frozen("#4F46E5"), PracticeBrush = Frozen("#0891B2");

    public BankItem Item { get; } = item;
    public string Title => Item.Title;
    public string Category => Item.CategoryText;
    public Brush BadgeBrush => Item.Category == BankCategory.Exam ? ExamBrush : PracticeBrush;
    public string Snippet { get; } = Preview.Plain(item.Question.Stem);
    public string Info => string.Join(" · ", new[] { Preview.Meta(Item.Question), $"{Format.Points(Item.Question.TotalPoints)}점", $"{Item.SavedAt:yyyy-MM-dd} 보관" }
                                                  .Where(s => s.Length > 0));

    ImageSource? thumb;
    bool thumbLoaded;
    public ImageSource? Thumb
    {
        get
        {
            if (!thumbLoaded && Item.Question.Image is { Path.Length: > 0 } img)
                thumb = Preview.LoadImage(Path.Combine(Item.Dir, img.Path), decodeWidth: 220);
            thumbLoaded = true;
            return thumb;
        }
    }

    bool picked;
    public bool Picked { get => picked; set { if (Set(ref picked, value)) pickedChanged(this); } }

    /// <summary>찾기 글이 들어 있는지(발문·보기·선택지·단원·제목).</summary>
    public bool Matches(string text)
    {
        if (text.Length == 0) return true;
        var q = Item.Question;
        IEnumerable<string?> all = [Item.Title, q.Stem, q.StemAfter, q.Meta.Unit, .. q.Box?.Lines ?? [], .. q.Passage?.Lines ?? [], .. q.Choices];
        return all.Any(s => s?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 문항함 창: 보관한 문항을 칸(시험용·예상문제)별로 보고, 찾고, 옮기고, 지우고, 골라서 시험지(④)에 넣는다.
/// </summary>
public partial class BankWindow : Window
{
    readonly QuestionBank bank;
    List<BankEntry> all = [];
    readonly List<BankEntry> pickOrder = [];

    /// <summary>[고른 문항을 시험지에 넣기]로 닫았을 때, 고른 순서대로.</summary>
    public List<BankItem> Picked { get; } = [];

    /// <param name="target">고른 문항이 들어갈 ④의 탭("시험지"/"예상문제"). 그 칸을 먼저 보여 준다.</param>
    public BankWindow(QuestionBank bank, string target = Destination.Exam)
    {
        this.bank = bank;
        InitializeComponent();
        PickButton.Content = $"고른 문항을 ④ {target}에 넣기";
        (target == Destination.Practice ? PracticeTab : ExamTab).IsChecked = true;
        Reload();
    }

    void Reload()
    {
        pickOrder.Clear();
        all = bank.Load().Select(i => new BankEntry(i, OnPicked)).ToList();
        AllTab.Content = $"전체 {all.Count}";
        ExamTab.Content = $"시험용 {all.Count(e => e.Item.Category == BankCategory.Exam)}";
        PracticeTab.Content = $"예상문제 {all.Count(e => e.Item.Category == BankCategory.Practice)}";
        ApplyFilter();
        UpdatePicked();
    }

    void ApplyFilter()
    {
        if (ItemList is null) return;   // 창을 만드는 중(라디오 단추 Checked가 먼저 온다)
        BankCategory? cat = ExamTab.IsChecked == true ? BankCategory.Exam : PracticeTab.IsChecked == true ? BankCategory.Practice : null;
        var text = SearchBox.Text.Trim();
        var shown = all.Where(e => (cat is null || e.Item.Category == cat) && e.Matches(text)).ToList();
        ItemList.ItemsSource = shown;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count > 0 && shown.Count == 0) EmptyText.Text = "조건에 맞는 문항이 없습니다.";
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    void OnPicked(BankEntry e)
    {
        if (e.Picked) pickOrder.Add(e); else pickOrder.Remove(e);
        UpdatePicked();
    }

    void UpdatePicked()
    {
        PickButton.IsEnabled = pickOrder.Count > 0;
        if (pickOrder.Count > 0)
            PickedText.Text = $"{pickOrder.Count}문항 고름 · {Format.Points(pickOrder.Sum(p => p.Item.Question.TotalPoints))}점 (체크한 순서대로 들어갑니다)";
    }

    void ItemList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is not BankEntry entry) return;
        var q = entry.Item.Question;
        PvTitle.Text = $"[{entry.Category}] {entry.Title}";
        PvTop.Text = Preview.Plain(q.Stem);
        PvImage.Source = q.Image is { Path.Length: > 0 } img ? Preview.LoadImage(Path.Combine(entry.Item.Dir, img.Path)) : null;
        PvBottom.Text = Preview.Body(q);
        PvAnswer.Text = Preview.Answer(q);
        PvAnswerBox.Visibility = PvAnswer.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void MoveExam_Click(object sender, RoutedEventArgs e) => MoveSelected(BankCategory.Exam);
    void MovePractice_Click(object sender, RoutedEventArgs e) => MoveSelected(BankCategory.Practice);

    void MoveSelected(BankCategory to)
    {
        if (ItemList.SelectedItem is not BankEntry entry) return;
        try { bank.Move(entry.Item, to); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "문항함"); }
        Reload();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not BankEntry entry) return;
        if (MessageBox.Show(this, $"이 문항을 문항함에서 지울까요? 되돌릴 수 없습니다.\n\n{entry.Title}", "문항 지우기",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { bank.Delete(entry.Item); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "문항함"); }
        Reload();
    }

    void Pick_Click(object sender, RoutedEventArgs e)
    {
        Picked.AddRange(pickOrder.Select(p => p.Item));
        DialogResult = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
