using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TestMaker.Ai;
using TestMaker.Core.Models;

namespace TestMaker.App;

/// <summary>
/// ④ 전체 화면 편집 — 왼쪽은 순서(끌어 옮기기), 오른쪽은 문항 전체. 주 창의 목록을 그대로 고치므로 바로 저장된다.
/// </summary>
public partial class ExamEditWindow : Window
{
    readonly ObservableCollection<ExamEntry> exam, practice, removed;
    /// <summary>문항을 다른 탭으로 보내거나(행선지) 뺀다(null). 이번 작업 문항은 ③의 행선지가 바뀐다.</summary>
    readonly Action<ExamEntry, string?> send;
    Point dragStart;
    ExamEntry? dragItem;

    public ExamEditWindow(ObservableCollection<ExamEntry> exam, ObservableCollection<ExamEntry> practice, ObservableCollection<ExamEntry> removed,
                          bool practiceShown, Action<ExamEntry, string?> send, ExamEntry? focus = null)
    {
        InitializeComponent();
        this.exam = exam;
        this.practice = practice;
        this.removed = removed;
        this.send = send;
        RemovedList.ItemsSource = removed;
        exam.CollectionChanged += List_Changed;
        practice.CollectionChanged += List_Changed;
        removed.CollectionChanged += List_Changed;
        Closed += (_, _) =>
        {
            exam.CollectionChanged -= List_Changed; practice.CollectionChanged -= List_Changed; removed.CollectionChanged -= List_Changed;
        };
        (practiceShown ? PracticeTab : ExamTab).IsChecked = true;
        ShowTab();
        if (focus is not null && Current.Contains(focus)) Select(focus);
    }

    public bool PracticeShown => PracticeTab.IsChecked == true;
    ObservableCollection<ExamEntry> Current => PracticeShown ? practice : exam;

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || exam is null) return;
        ShowTab();
    }

    void ShowTab()
    {
        OrderList.ItemsSource = Current;
        CardList.ItemsSource = Current;
        UpdateSummary();
        if (Current.Count > 0) Select(Current[0]);
    }

    void List_Changed(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSummary();

    void UpdateSummary()
    {
        ExamTab.Content = exam.Count > 0 ? $"시험지 {exam.Count}" : "시험지";
        PracticeTab.Content = practice.Count > 0 ? $"예상문제 {practice.Count}" : "예상문제";
        var cur = Current;
        SummaryText.Text = cur.Count == 0 ? "" : $"{cur.Count}문항 · 총 {Format.Points(cur.Sum(x => x.Question.TotalPoints))}점";
        EmptyText.Visibility = cur.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedBox.Visibility = removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedHeader.Text = $"뺀 문항 {removed.Count}";
    }

    // ── 고르기: 왼쪽 순서와 오른쪽 문항을 맞춘다 ──────────────────

    /// <summary>그 문항을 고르고 오른쪽에서 맨 위로 올린다.</summary>
    void Select(ExamEntry entry)
    {
        OrderList.SelectedItem = entry;
        CardList.SelectedItem = entry;
        OrderList.ScrollIntoView(entry);
        ScrollCardToTop(entry);
    }

    void OrderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (tracking || OrderList.SelectedItem is not ExamEntry entry || CardList.SelectedItem == entry) return;
        CardList.SelectedItem = entry;
        ScrollCardToTop(entry);
    }

    void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (tracking || CardList.SelectedItem is not ExamEntry entry || OrderList.SelectedItem == entry) return;
        OrderList.SelectedItem = entry;
        OrderList.ScrollIntoView(entry);
    }

    ScrollViewer? cardScroll;
    ScrollViewer? CardScroll => cardScroll ??= FindChild<ScrollViewer>(CardList);
    /// <summary>프로그램이 굴리는 중(이때는 굴린 자리로 고른 문항을 바꾸지 않는다).</summary>
    DateTime scrolledByCode;
    /// <summary>굴린 자리에 맞춰 고르는 중.</summary>
    bool tracking;

    void ScrollCardToTop(ExamEntry entry)
    {
        scrolledByCode = DateTime.Now;
        CardList.ScrollIntoView(entry);
        CardList.UpdateLayout();
        if (CardScroll is { } sv && CardList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem row)
            sv.ScrollToVerticalOffset(sv.VerticalOffset + row.TranslatePoint(new Point(), sv).Y);
    }

    /// <summary>
    /// 마우스로 굴리면 위쪽에 보이는 문항을 고른다 — 왼쪽 순서에 지금 보는 문항이 표시되고,
    /// 번호가 화면 위로 지나간 긴 문항도 몇 번인지 알 수 있다.
    /// </summary>
    void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != CardScroll || e.VerticalChange == 0 || (DateTime.Now - scrolledByCode).TotalMilliseconds < 400 || CardScroll is not { } sv) return;
        ExamEntry? top = null;
        foreach (var item in Current)
        {
            if (CardList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row) continue;
            var y = row.TranslatePoint(new Point(), sv).Y;
            if (y + row.ActualHeight > 60) { top = item; break; }   // 화면 위쪽 60px 아래까지 남아 있는 첫 문항
        }
        if (top is null || CardList.SelectedItem == top) return;
        tracking = true;
        try
        {
            CardList.SelectedItem = top;
            OrderList.SelectedItem = top;
            OrderList.ScrollIntoView(top);
        }
        finally { tracking = false; }
    }

    static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    // ── 옮기기 ─────────────────────────────────────────────

    void MoveTo(ExamEntry entry, int index)
    {
        var list = Current;
        int i = list.IndexOf(entry);
        index = Math.Clamp(index, 0, list.Count - 1);
        if (i < 0 || i == index) return;
        list.Move(i, index);
        Select(entry);
    }

    static ExamEntry? EntryOf(object sender) => (sender as FrameworkElement)?.Tag as ExamEntry;

    void First_Click(object sender, RoutedEventArgs e) { if (EntryOf(sender) is { } x) MoveTo(x, 0); }
    void Last_Click(object sender, RoutedEventArgs e) { if (EntryOf(sender) is { } x) MoveTo(x, Current.Count - 1); }
    void Up_Click(object sender, RoutedEventArgs e) { if (EntryOf(sender) is { } x) MoveTo(x, Current.IndexOf(x) - 1); }
    void Down_Click(object sender, RoutedEventArgs e) { if (EntryOf(sender) is { } x) MoveTo(x, Current.IndexOf(x) + 1); }

    void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.DataContext is not ExamEntry entry) return;
        if (int.TryParse(box.Text.Trim(), out var n)) MoveTo(entry, n - 1);
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        box.SelectAll();
        e.Handled = true;
    }

    // 번호를 고치다 말고 나가면 원래 번호로 되돌린다.
    void NumberBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

    /// <summary>같은 단원끼리 모은다. 단원은 처음 나오는 순서대로, 단원 안의 순서는 그대로.</summary>
    void GroupByUnit_Click(object sender, RoutedEventArgs e)
    {
        var list = Current;
        var units = list.Select(x => x.Unit).Distinct().ToList();
        var ordered = list.OrderBy(x => units.IndexOf(x.Unit)).ToList();   // OrderBy는 안정 정렬
        for (int k = 0; k < ordered.Count; k++)
        {
            int i = list.IndexOf(ordered[k]);
            if (i != k) list.Move(i, k);
        }
    }

    void OrderList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OrderList.SelectedItem is not ExamEntry entry) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        int i = Current.IndexOf(entry);
        if (alt && key == Key.Up) MoveTo(entry, i - 1);
        else if (alt && key == Key.Down) MoveTo(entry, i + 1);
        else if (key == Key.Delete) Remove(entry);
        else return;
        e.Handled = true;
        (OrderList.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem)?.Focus();
    }

    // ── 끌어서 옮기기(왼쪽 순서) ──────────────────────────────

    void OrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(OrderList);
        dragItem = ItemAt(e.OriginalSource as DependencyObject);
    }

    void OrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragItem is null) return;
        var d = e.GetPosition(OrderList) - dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        var item = dragItem;
        dragItem = null;
        DragDrop.DoDragDrop(OrderList, new DataObject(typeof(ExamEntry), item), DragDropEffects.Move);
    }

    void OrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ExamEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void OrderList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ExamEntry)) is not ExamEntry moved) return;
        // 놓은 줄의 자리로(줄의 아래쪽 절반에 놓으면 그 다음 자리로).
        var target = ItemAt(e.OriginalSource as DependencyObject);
        int index = Current.Count - 1;
        if (target is not null && OrderList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem row)
        {
            index = Current.IndexOf(target);
            bool below = e.GetPosition(row).Y > row.ActualHeight / 2;
            int from = Current.IndexOf(moved);
            if (below && from > index) index++;
            else if (!below && from < index) index--;
        }
        MoveTo(moved, index);
    }

    ExamEntry? ItemAt(DependencyObject? d)
    {
        while (d is not null && d is not ListBoxItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as ExamEntry;
    }

    // ── 다른 탭으로 / 빼기 ───────────────────────────────────

    void SendButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var other = PracticeShown ? Destination.Exam : Destination.Practice;
        b.Content = $"{other}로";
        b.ToolTip = $"④ {other} 탭으로 보냅니다.";
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        var other = PracticeShown ? Destination.Exam : Destination.Practice;
        AfterRemoving(entry, () => send(entry, other));
    }

    void Remove_Click(object sender, RoutedEventArgs e) { if (EntryOf(sender) is { } x) Remove(x); }

    void Remove(ExamEntry entry) => AfterRemoving(entry, () => send(entry, null));

    /// <summary>빠진 자리의 다음 문항을 고른다.</summary>
    void AfterRemoving(ExamEntry entry, Action act)
    {
        int i = Current.IndexOf(entry);
        act();
        if (Current.Count > 0) Select(Current[Math.Clamp(i, 0, Current.Count - 1)]);
    }

    void RemovedToPractice_Click(object sender, RoutedEventArgs e) => Restore(sender, Destination.Practice);
    void RemovedToExam_Click(object sender, RoutedEventArgs e) => Restore(sender, Destination.Exam);

    /// <summary>뺀 문항을 그 탭의 맨 뒤로 되돌리고, 그 탭을 펼쳐 고른다.</summary>
    void Restore(object sender, string dest)
    {
        if (EntryOf(sender) is not { } entry) return;
        send(entry, dest);
        (dest == Destination.Practice ? PracticeTab : ExamTab).IsChecked = true;
        if (Current.FirstOrDefault(x => x.Key == entry.Key) is { } back) Select(back);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
