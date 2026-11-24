using SampleViewer.Models;
using SampleViewer.Repositories;
using SampleViewer.Services;
using System.IO;
using System.Windows;

namespace SampleViewer.Windows
{
    // メモ（画像・測定・耐久に付ける気づき）
    public partial class MainWindow
    {
        private AnnotationRepository? _annotationRepo;
        private AnnotationIndex _annotations = new(Array.Empty<Annotation>());
        private List<AnnotationCategory> _annotationCategories = AnnotationCategory.Defaults.ToList();
        private AnnotationListWindow? _memoList;

        // 最後に選んだ画像（メモの一覧と、メモを付ける対象）
        private IconItem? _selectedIcon;

        private void InitializeAnnotations()
        {
            try
            {
                // カテゴリと、カテゴリごとの状態は、アプリのフォルダの設定ファイルで増減できる
                var categories = AnnotationCategory.Load(
                    Path.Combine(settings.Local!.ExeSettingsDir, "annotationCategories.json"));

                if (categories.Count > 0)
                    _annotationCategories = categories;

                _annotationRepo = new AnnotationRepository(settings.Local.AnnotationDbPath);
                _annotations = LoadAnnotations();
            }
            catch (Exception ex)
            {
                // メモが使えなくても、閲覧はできるようにする
                _annotationRepo = null;
                ShowStatus($"メモを読み込めませんでした：{ex.Message}");
            }

            UpdateMemoPanel();
        }

        // メモを読み込み、状態を持つカテゴリで未設定のものは既定（先頭）の状態として扱う
        private AnnotationIndex LoadAnnotations()
        {
            var all = _annotationRepo!.GetAll();

            foreach (var annotation in all)
            {
                int order = _annotationCategories.FindIndex(x => x.Name == annotation.Category);
                var category = order < 0 ? null : _annotationCategories[order];
                var states = category?.States;

                annotation.DisplayStatus = states == null || states.Count == 0
                    ? ""
                    : states.Contains(annotation.Status) ? annotation.Status : states[0];

                // 札の表示：今の設定に無いカテゴリは、カテゴリ名のまま既定の色で出す
                annotation.CategoryOrder = order < 0 ? int.MaxValue : order;
                annotation.TagText = string.IsNullOrWhiteSpace(category?.Short) ? annotation.Category : category.Short;
                annotation.TagColor = category?.Color ?? AnnotationCategory.DefaultColor;
                annotation.IsDone = category != null && category.DoneStates.Contains(annotation.DisplayStatus);
            }

            return new AnnotationIndex(all);
        }

        #region 表示への反映

        // 画像に、その画像と測定に付いたメモの件数と内容を付ける（検索は裏でも動くので、画面には触れない）
        private static void ApplyMemos(IEnumerable<IconItem> icons, AnnotationIndex annotations)
        {
            foreach (var icon in icons)
            {
                var memos = long.TryParse(icon.ImageId, out var imageId)
                    ? annotations.ForImage(imageId, icon.MeasurementId)
                    : new List<Annotation>();

                icon.MemoTags = MemoTag.Build(memos);
                icon.MemoCount = memos.Count;
                icon.MemoToolTip = string.Join("\n", memos.Select(x => x.Summary));
            }
        }

        // 耐久の見出し（通常表示・比較行）に、耐久そのものに付いたメモを付ける
        private void ApplyMemos(DurabilityImageGroup group)
        {
            var memos = _annotations.DurabilityLevel(group.DurabilityId);

            group.MemoTags = MemoTag.Build(memos, withCount: true);
            group.MemoCount = memos.Count;
            group.MemoToolTip = string.Join("\n", memos.Select(x => x.Summary));
        }

        // 左の「メモ」欄：選択中の画像に関係するメモ（画像・測定・耐久）
        private void UpdateMemoPanel()
        {
            var memos = new List<Annotation>();

            if (_selectedIcon != null)
            {
                if (long.TryParse(_selectedIcon.ImageId, out var imageId))
                    memos.AddRange(_annotations.ForImage(imageId, _selectedIcon.MeasurementId));

                memos.AddRange(_annotations.DurabilityLevel(_selectedIcon.DurabilityId));
            }

            IC_Memos.ItemsSource = memos;
            BT_AddMemo.IsEnabled = _selectedIcon != null && _annotationRepo != null;
            TB_NoMemo.Text = _selectedIcon == null ? "画像を選ぶとメモが表示されます" : "メモはありません";
            TB_NoMemo.Visibility = memos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // メモを変更したあと、表示中の画像・見出し・グラフに反映する（並びやスクロール位置は変えない）
        private void RefreshAfterAnnotationChange()
        {
            if (_annotationRepo == null)
                return;

            _annotations = LoadAnnotations();

            // 「メモあり」で絞り込んでいるときは、対象が変わるので検索し直す
            if (CB_MemoOnly.IsChecked == true)
            {
                UpdateSearchResult(CollectSearchConditionsFromUI());
            }
            else
            {
                ApplyMemos(IconItems, _annotations);

                foreach (var group in DurabilityImageGroups)
                    ApplyMemos(group);

                UpdateNormalHeader();
                RefreshTrendGraph();
            }

            UpdateMemoPanel();
            _memoList?.Reload();
        }

        #endregion

        #region メモ一覧（逆引き）

        private void ShowMemoList_Click(object sender, RoutedEventArgs e)
        {
            if (_annotationRepo == null)
            {
                ShowStatus("メモの保存先を開けないため、一覧を表示できません");
                return;
            }

            if (_memoList != null)
            {
                _memoList.Activate();
                return;
            }

            _memoList = new AnnotationListWindow(_annotationCategories, () => _annotations.All)
            {
                Owner = this,
                ShowRequested = ShowAnnotationTarget,
                CompareRequested = CompareAnnotationImages,
                StatusChangeRequested = ChangeAnnotationStatus,
                EditRequested = EditAnnotation,
                DeleteRequested = DeleteAnnotations
            };
            _memoList.Closed += (_, _) => _memoList = null;
            _memoList.Show();
        }

        // メモの対象をメイン画面に表示する：その耐久だけを選び、画像（測定・耐久のメモは最初の画像）を選択する
        private async void ShowAnnotationTarget(Annotation annotation)
        {
            var target = LB_taikyu_ID.Items.OfType<DurabilityDisplayItem>().FirstOrDefault(x => x.Id == annotation.DurabilityId);

            if (target == null)
            {
                ShowStatus($"耐久ID「{annotation.DurabilityId}」が一覧にありません（メーカーなどの絞り込みを外すか、アイコンデータを取得してください）");
                return;
            }

            var selected = LB_taikyu_ID.SelectedItems.OfType<DurabilityDisplayItem>().ToList();

            if (selected.Count != 1 || selected[0].Id != annotation.DurabilityId)
            {
                bool wasUpdating = _isUpdating;
                _isUpdating = true;

                try
                {
                    LB_taikyu_ID.SelectedItems.Clear();
                    LB_taikyu_ID.SelectedItems.Add(target);
                    LB_taikyu_ID.ScrollIntoView(target);
                }
                finally
                {
                    _isUpdating = wasUpdating;
                }

                UpdateSearchResult(CollectSearchConditionsFromUI());
            }

            // 一覧の作り直しが終わってから画像を探す
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var icons = IsCompareMode
                ? DurabilityImageGroups.SelectMany(x => x.Cells).Select(x => x.Icon).OfType<IconItem>().ToList()
                : IconItems.ToList();

            var icon = annotation.TargetType switch
            {
                Annotation.TargetImage => icons.FirstOrDefault(x => x.ImageId == annotation.ImageId?.ToString()),
                Annotation.TargetMeasurement => icons.FirstOrDefault(x => x.DurabilityId == annotation.DurabilityId && x.MeasurementId == annotation.MeasurementId),
                _ => icons.FirstOrDefault(x => x.DurabilityId == annotation.DurabilityId)
            };

            if (icon == null)
            {
                ShowStatus("このメモの画像は、今の絞り込み（パターン・印字枚数など）では表示されていません");
                return;
            }

            RevealIcon(icon.ImageId);
            ShowImageDetails(icon);
            Activate();
        }

        // 画像に付けたメモの画像を、比較ウィンドウに並べる
        private void CompareAnnotationImages(IReadOnlyList<Annotation> annotations)
        {
            var images = annotations
                .Where(x => x.TargetType == Annotation.TargetImage && x.ImageId.HasValue && x.ImageName != "")
                .GroupBy(x => x.ImageId!.Value)
                .Select(x => x.First())
                .ToList();

            var patterns = new CompareInfoRepository().GetPatterns(images.Select(x => x.ImageId!.Value));

            var items = CreateCompareItems(
                images
                    .Where(x => patterns.ContainsKey(x.ImageId!.Value))
                    .Select(x => (x.DurabilityId, x.ImageName, x.Pages, patterns[x.ImageId!.Value])),
                resolve: false);

            if (items.Count == 0)
            {
                ShowStatus("比較ウィンドウで開ける画像がありません（DBに無い画像のメモです）");
                return;
            }

            OpenCompareWindow(items);
        }

        private void ChangeAnnotationStatus(IReadOnlyList<Annotation> annotations, string status)
        {
            try
            {
                foreach (var annotation in annotations)
                    _annotationRepo!.SetStatus(annotation.Id, status);

                ShowStatus($"{annotations.Count}件のメモを「{status}」にしました");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"メモを更新できませんでした。\n{ex.Message}");
            }

            RefreshAfterAnnotationChange();
        }

        private void EditAnnotation(Annotation annotation)
        {
            if (!annotation.IsMine)
                return;

            var dialog = new AnnotationWindow(annotation, _annotationCategories) { Owner = (Window?)_memoList ?? this };

            if (dialog.ShowDialog() == true && dialog.Result != null)
                SaveAnnotation(dialog.Result, "メモを更新しました");
        }

        private void DeleteAnnotations(IReadOnlyList<Annotation> annotations)
        {
            if (annotations.Count == 0 || annotations.Any(x => !x.IsMine))
                return;

            string text = annotations.Count == 1 ? annotations[0].Summary : $"{annotations.Count}件のメモ";

            var answer = MessageBox.Show(
                $"メモを削除します。よろしいですか？\n\n{text}",
                "メモの削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                foreach (var annotation in annotations)
                    _annotationRepo!.Delete(annotation.Id);

                ShowStatus("メモを削除しました");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"メモを削除できませんでした。\n{ex.Message}");
            }

            RefreshAfterAnnotationChange();
        }

        #endregion

        #region 追加・編集・削除

        private void AddMemo_MenuClick(object sender, RoutedEventArgs e)
        {
            var icon = GetSelectedIcons().FirstOrDefault() ?? _selectedIcon;

            if (icon != null)
                AddMemo(icon);
        }

        private void AddMemoForSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIcon != null)
                AddMemo(_selectedIcon);
        }

        private void AddMemo(IconItem icon)
        {
            if (_annotationRepo == null)
            {
                ShowStatus("メモの保存先を開けないため、メモを付けられません");
                return;
            }

            var dialog = new AnnotationWindow(icon, _annotationCategories) { Owner = this };

            if (dialog.ShowDialog() == true && dialog.Result != null)
                SaveAnnotation(dialog.Result, "メモを保存しました");
        }

        private void EditMemo_Click(object sender, RoutedEventArgs e)
        {
            var annotation = FindAnnotation(sender);

            if (annotation == null || !annotation.IsMine)
                return;

            var dialog = new AnnotationWindow(annotation, _annotationCategories) { Owner = this };

            if (dialog.ShowDialog() == true && dialog.Result != null)
                SaveAnnotation(dialog.Result, "メモを更新しました");
        }

        private void DeleteMemo_Click(object sender, RoutedEventArgs e)
        {
            var annotation = FindAnnotation(sender);

            if (annotation == null || !annotation.IsMine)
                return;

            var answer = MessageBox.Show(
                $"このメモを削除します。よろしいですか？\n\n{annotation.Summary}",
                "メモの削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                _annotationRepo!.Delete(annotation.Id);
                ShowStatus("メモを削除しました");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"メモを削除できませんでした。\n{ex.Message}");
            }

            RefreshAfterAnnotationChange();
        }

        private Annotation? FindAnnotation(object sender)
        {
            string id = (sender as FrameworkElement)?.Tag as string ?? "";
            return _annotations.All.FirstOrDefault(x => x.Id == id);
        }

        private void SaveAnnotation(Annotation annotation, string message)
        {
            try
            {
                _annotationRepo!.Save(annotation);
                ShowStatus(message);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"メモを保存できませんでした。\n{ex.Message}");
            }

            RefreshAfterAnnotationChange();
        }

        private void MemoOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _isUpdating)
                return;

            UpdateSearchResult(CollectSearchConditionsFromUI());
        }

        #endregion
    }
}
