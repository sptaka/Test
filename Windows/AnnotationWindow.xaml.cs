using SampleViewer.Models;
using System.Windows;
using System.Windows.Input;

namespace SampleViewer.Windows
{
    // メモの入力画面。新規のときは対象（画像／測定／耐久）を選べる。編集のときは対象を変えられない
    public partial class AnnotationWindow : Window
    {
        private readonly Annotation _annotation;
        private readonly IconItem? _icon;
        private readonly IReadOnlyList<AnnotationCategory> _categories;

        // 保存が押されたときの内容（キャンセルは null）
        public Annotation? Result { get; private set; }

        // 新規：選択中の画像を元に作る
        public AnnotationWindow(IconItem icon, IReadOnlyList<AnnotationCategory> categories)
            : this(new Annotation(), categories)
        {
            _icon = icon;

            string pages = icon.Pages.HasValue ? $"{icon.Pages:#,0}枚" : "枚数不明";

            RB_Image.Content = Label($"この画像（{icon.ImageName}）");
            RB_Measurement.Content = Label($"この測定（{icon.DurabilityId} {pages} のすべての画像）");
            RB_Durability.Content = Label($"この耐久（{icon.DurabilityId} 全体）");
            RB_Image.IsChecked = true;
            RB_Measurement.IsEnabled = icon.MeasurementId > 0;
        }

        // 文字列をそのまま Content に入れると「_」がショートカットキーの印として消えるので、TextBlock で出す
        private static System.Windows.Controls.TextBlock Label(string text) => new() { Text = text };

        // 編集：カテゴリとコメントだけ変えられる
        public AnnotationWindow(Annotation annotation, IReadOnlyList<AnnotationCategory> categories)
        {
            InitializeComponent();

            _annotation = annotation;
            _categories = categories;

            // 今の設定に無いカテゴリのメモも、そのカテゴリのまま編集できるようにする
            var names = categories.Select(x => x.Name).ToList();

            if (annotation.Category != "" && !names.Contains(annotation.Category))
                names.Add(annotation.Category);

            CB_Category.ItemsSource = names;
            CB_Category.SelectedItem = annotation.Category == "" ? names.FirstOrDefault() : annotation.Category;
            UpdateStatusChoices(annotation.DisplayStatus);
            TB_Comment.Text = annotation.Comment;

            RB_Image.Content = "画像";
            RB_Measurement.Content = "測定";
            RB_Durability.Content = "耐久";
            RB_Image.IsChecked = annotation.TargetType == Annotation.TargetImage;
            RB_Measurement.IsChecked = annotation.TargetType == Annotation.TargetMeasurement;
            RB_Durability.IsChecked = annotation.TargetType == Annotation.TargetDurability;

            if (annotation.Comment != "")
            {
                Title = "メモの編集";
                RB_Image.IsEnabled = RB_Measurement.IsEnabled = RB_Durability.IsEnabled = false;
            }

            Loaded += (_, _) => TB_Comment.Focus();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Save();
                    e.Handled = true;
                }
            };
        }

        private void Category_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (IsInitialized && _categories != null)
                UpdateStatusChoices(CB_Status.SelectedItem as string ?? "");
        }

        // 状態を持つカテゴリのときだけ、状態を選べるようにする
        private void UpdateStatusChoices(string current)
        {
            var states = _categories.FirstOrDefault(x => x.Name == CB_Category.SelectedItem as string)?.States ?? new List<string>();

            CB_Status.ItemsSource = states;
            CB_Status.SelectedItem = states.Contains(current) ? current : states.FirstOrDefault();
            CB_Status.Visibility = TB_StatusLabel.Visibility = states.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            string comment = TB_Comment.Text.Trim();

            if (comment == "")
            {
                TB_Error.Text = "コメントを入力してください";
                TB_Error.Visibility = Visibility.Visible;
                return;
            }

            var now = DateTime.Now;

            if (_icon != null)
            {
                // 新規：対象に応じて、画像・測定のIDを入れる
                _annotation.TargetType =
                    RB_Durability.IsChecked == true ? Annotation.TargetDurability
                    : RB_Measurement.IsChecked == true ? Annotation.TargetMeasurement
                    : Annotation.TargetImage;

                bool isImage = _annotation.TargetType == Annotation.TargetImage;
                bool isDurability = _annotation.TargetType == Annotation.TargetDurability;

                _annotation.DurabilityId = _icon.DurabilityId;
                _annotation.MeasurementId = isDurability ? null : _icon.MeasurementId;
                _annotation.ImageId = isImage && long.TryParse(_icon.ImageId, out var imageId) ? imageId : null;
                _annotation.ImageName = isImage ? _icon.ImageName : "";
                _annotation.Pages = isDurability ? null : _icon.Pages;
                _annotation.Author = Environment.UserName;
                _annotation.Machine = Environment.MachineName;
                _annotation.CreatedAt = now;
            }

            _annotation.Category = CB_Category.SelectedItem as string ?? "";
            _annotation.Status = CB_Status.Visibility == Visibility.Visible ? CB_Status.SelectedItem as string ?? "" : "";
            _annotation.DisplayStatus = _annotation.Status;
            _annotation.Comment = comment;
            _annotation.UpdatedAt = now;

            Result = _annotation;
            DialogResult = true;
        }
    }
}
