using SampleViewer.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SampleViewer.Windows
{
    // メモ一覧：カテゴリ・状態などで絞り込んで、メモから画像を逆引きする
    // メモの保存や画像の表示はメイン画面が行う（この画面は依頼するだけ）
    public partial class AnnotationListWindow : Window
    {
        private const string All = "すべて";

        private readonly IReadOnlyList<AnnotationCategory> _categories;
        private readonly Func<IReadOnlyList<Annotation>> _source;
        private bool _updating;

        // メイン画面への依頼
        public Action<Annotation>? ShowRequested { get; init; }
        public Action<IReadOnlyList<Annotation>>? CompareRequested { get; init; }
        public Action<IReadOnlyList<Annotation>, string>? StatusChangeRequested { get; init; }
        public Action<Annotation>? EditRequested { get; init; }
        public Action<IReadOnlyList<Annotation>>? DeleteRequested { get; init; }

        public AnnotationListWindow(IReadOnlyList<AnnotationCategory> categories, Func<IReadOnlyList<Annotation>> source)
        {
            InitializeComponent();

            _categories = categories;
            _source = source;

            _updating = true;
            CB_Category.ItemsSource = new[] { All }.Concat(categories.Select(x => x.Name)).ToList();
            CB_Category.SelectedIndex = 0;
            _updating = false;

            UpdateStatusChoices();
            Reload();
        }

        private List<Annotation> SelectedMemos => DG_Memos.SelectedItems.OfType<Annotation>().ToList();

        // メモが変わったときにメイン画面から呼ばれる。絞り込みと選択は保つ
        public void Reload()
        {
            var all = _source();
            var selectedIds = SelectedMemos.Select(x => x.Id).ToHashSet();

            _updating = true;

            string author = CB_Author.SelectedItem as string ?? All;
            var authors = new[] { All }.Concat(all.Select(x => x.Author).Distinct().OrderBy(x => x)).ToList();
            CB_Author.ItemsSource = authors;
            CB_Author.SelectedItem = authors.Contains(author) ? author : All;

            _updating = false;

            string category = CB_Category.SelectedItem as string ?? All;
            string status = CB_Status.SelectedItem as string ?? All;
            author = CB_Author.SelectedItem as string ?? All;
            string[] words = TB_Search.Text.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);

            var rows = all
                .Where(x => category == All || x.Category == category)
                .Where(x => status == All || x.DisplayStatus == status)
                .Where(x => author == All || x.Author == author)
                .Where(x => words.All(w =>
                    x.Comment.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                    x.DurabilityId.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                    x.ImageName.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();

            DG_Memos.ItemsSource = rows;

            foreach (var row in rows.Where(x => selectedIds.Contains(x.Id)))
                DG_Memos.SelectedItems.Add(row);

            TB_Count.Text = rows.Count == all.Count ? $"{rows.Count:#,0}件" : $"{rows.Count:#,0}件（全{all.Count:#,0}件）";
            TB_Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateButtons();
        }

        // 状態の選択肢は、選んだカテゴリのもの。カテゴリが「すべて」のときは全カテゴリの状態
        private void UpdateStatusChoices()
        {
            string category = CB_Category.SelectedItem as string ?? All;

            var states = _categories
                .Where(x => category == All || x.Name == category)
                .SelectMany(x => x.States)
                .Distinct()
                .ToList();

            _updating = true;
            CB_Status.ItemsSource = new[] { All }.Concat(states).ToList();
            CB_Status.SelectedIndex = 0;
            CB_Status.IsEnabled = states.Count > 0;
            _updating = false;
        }

        private void Category_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updating)
                return;

            UpdateStatusChoices();
            Reload();
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating)
                Reload();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_updating && IsInitialized)
                Reload();
        }

        private void Selection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating)
                UpdateButtons();
        }

        private void UpdateButtons()
        {
            var selected = SelectedMemos;

            BT_Show.IsEnabled = selected.Count == 1;
            BT_Compare.IsEnabled = selected.Any(x => x.TargetType == Annotation.TargetImage);
            BT_Edit.IsEnabled = selected.Count == 1 && selected[0].IsMine;
            BT_Delete.IsEnabled = selected.Count > 0 && selected.All(x => x.IsMine);

            // 状態を変えるボタン：選んだ行が同じカテゴリで、状態を持つときだけ出す
            SP_StatusButtons.Children.Clear();

            var names = selected.Select(x => x.Category).Distinct().ToList();
            var category = names.Count == 1 ? _categories.FirstOrDefault(x => x.Name == names[0]) : null;

            if (category == null)
                return;

            foreach (var state in category.States.Where(s => selected.Any(x => x.DisplayStatus != s)))
            {
                var button = new Button { Content = $"「{state}」にする", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0) };
                button.Click += (_, _) => StatusChangeRequested?.Invoke(SelectedMemos, state);
                SP_StatusButtons.Children.Add(button);
            }
        }

        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 見出しや余白のダブルクリックは対象外
            if ((e.OriginalSource as DependencyObject) is { } source && FindRow(source) != null)
                ShowSelected();
        }

        private static DataGridRow? FindRow(DependencyObject source)
        {
            DependencyObject? current = source;

            while (current != null && current is not DataGridRow)
            {
                current = current is System.Windows.Media.Visual
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return current as DataGridRow;
        }

        private void Show_Click(object sender, RoutedEventArgs e) => ShowSelected();

        private void ShowSelected()
        {
            var selected = SelectedMemos;

            if (selected.Count == 1)
                ShowRequested?.Invoke(selected[0]);
        }

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            var images = SelectedMemos.Where(x => x.TargetType == Annotation.TargetImage).ToList();

            if (images.Count > 0)
                CompareRequested?.Invoke(images);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedMemos;

            if (selected.Count == 1 && selected[0].IsMine)
                EditRequested?.Invoke(selected[0]);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedMemos;

            if (selected.Count > 0 && selected.All(x => x.IsMine))
                DeleteRequested?.Invoke(selected);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
