using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace SampleViewer.Models
{
    // メモのカテゴリ。States があるカテゴリは、メモごとに状態を持つ（先頭が既定）
    // 例：原因＝推定／確定、B-Action＝未完了／完了
    public class AnnotationCategory
    {
        public string Name { get; set; } = "";
        public List<string> States { get; set; } = new();

        // 画像などに付く札の文字（短い名前）と色。色は #RRGGBB
        public string Short { get; set; } = "";
        public string Color { get; set; } = DefaultColor;

        // 片付いたことを表す状態（完了、対応済）。この状態のメモの札は、白地の控えめな表示にする
        public List<string> DoneStates { get; set; } = new();

        public const string DefaultColor = "#D97706";

        public static readonly IReadOnlyList<AnnotationCategory> Defaults = new[]
        {
            new AnnotationCategory { Name = "気づき・所感", Short = "所感", Color = "#D97706" },
            new AnnotationCategory { Name = "原因", Short = "原因", Color = "#DC2626", States = { "推定", "確定" } },
            new AnnotationCategory { Name = "試験条件・注記", Short = "条件", Color = "#64748B" },
            new AnnotationCategory { Name = "スキャン", Short = "スキャン", Color = "#7C3AED", States = { "未対応", "対応済" }, DoneStates = { "対応済" } },
            new AnnotationCategory { Name = "B-Action", Short = "B-Act", Color = "#2563EB", States = { "未完了", "完了" }, DoneStates = { "完了" } }
        };

        // 設定ファイルから読む。名前だけを並べた形（"気づき" など）と、状態付きの形の両方を受け付ける
        public static List<AnnotationCategory> Load(string path)
        {
            var result = new List<AnnotationCategory>();

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var category = new AnnotationCategory();

                if (element.ValueKind == JsonValueKind.String)
                {
                    category.Name = element.GetString() ?? "";
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("Name", out var name))
                        category.Name = name.GetString() ?? "";

                    category.States = ReadList(element, "States");
                    category.DoneStates = ReadList(element, "DoneStates");

                    if (element.TryGetProperty("Short", out var shortName))
                        category.Short = shortName.GetString()?.Trim() ?? "";

                    if (element.TryGetProperty("Color", out var color) && MemoTag.TryParseColor(color.GetString(), out _))
                        category.Color = color.GetString()!.Trim();
                }

                category.Name = category.Name.Trim();

                if (category.Name != "" && result.All(x => x.Name != category.Name))
                    result.Add(category);
            }

            return result;
        }

        private static List<string> ReadList(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<string>();

            return list.EnumerateArray()
                .Select(x => x.GetString()?.Trim() ?? "")
                .Where(x => x != "")
                .Distinct()
                .ToList();
        }
    }

    // メモの分類が分かる札（画像の右下・耐久の見出し・メモ欄・一覧に出す）
    // 色はカテゴリごと。片付いた状態（完了、対応済）は白地に色文字、それ以外は色地に白文字
    public class MemoTag
    {
        public string Text { get; init; } = "";
        public Brush Background { get; init; } = Brushes.Transparent;
        public Brush Foreground { get; init; } = Brushes.White;
        public Brush Border { get; init; } = Brushes.Transparent;

        public static bool TryParseColor(string? text, out Color color)
        {
            color = default;

            try
            {
                if (string.IsNullOrWhiteSpace(text) || ColorConverter.ConvertFromString(text.Trim()) is not Color parsed)
                    return false;

                color = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static Color ColorOf(Annotation annotation) =>
            TryParseColor(annotation.TagColor, out var color) ? color : (Color)ColorConverter.ConvertFromString(AnnotationCategory.DefaultColor);

        public static MemoTag Create(string text, Annotation annotation)
        {
            // 検索は裏でも動くので、どのスレッドからでも使えるよう固定する
            var brush = new SolidColorBrush(ColorOf(annotation));
            brush.Freeze();

            return annotation.IsDone
                ? new MemoTag { Text = text, Background = Brushes.White, Foreground = brush, Border = brush }
                : new MemoTag { Text = text, Background = brush, Foreground = Brushes.White, Border = brush };
        }

        // メモの集まりから札を作る：カテゴリごとに1つ（片付いたものは別の札）。withCount は「原因 2」のように件数を付ける
        public static List<MemoTag> Build(IEnumerable<Annotation> annotations, bool withCount = false)
        {
            return annotations
                .GroupBy(x => (x.CategoryOrder, x.TagText, x.IsDone))
                .OrderBy(g => g.Key.IsDone)
                .ThenBy(g => g.Key.CategoryOrder)
                .Select(g => Create(withCount && g.Count() > 1 ? $"{g.Key.TagText} {g.Count()}" : g.Key.TagText, g.First()))
                .ToList();
        }
    }

    // 画像・測定・耐久に付けるメモ
    public class Annotation
    {
        public const string TargetImage = "image";
        public const string TargetMeasurement = "measurement";
        public const string TargetDurability = "taikyu";

        // 複数のPCのメモを後で統合しても重ならないよう、連番ではなくGUIDにする
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // 何に付けたメモか（image / measurement / taikyu）
        public string TargetType { get; set; } = TargetImage;

        public string DurabilityId { get; set; } = "";
        public long? MeasurementId { get; set; }
        public long? ImageId { get; set; }

        // メインのDBが無くても読めるよう、画像名と枚数も一緒に持つ
        public string ImageName { get; set; } = "";
        public double? Pages { get; set; }

        public string Category { get; set; } = "";

        // カテゴリごとの状態（推定／確定、未完了／完了 など）。状態を持たないカテゴリは空
        public string Status { get; set; } = "";

        // 表示する状態。状態を持つカテゴリで未設定のメモは、既定（先頭）の状態として扱う（読み込み時に入れる）
        public string DisplayStatus { get; set; } = "";

        public string Comment { get; set; } = "";

        public string Author { get; set; } = "";
        public string Machine { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public string TargetLabel => TargetType switch
        {
            TargetMeasurement => Pages.HasValue ? $"測定（{Pages:#,0}枚）" : "測定",
            TargetDurability => "耐久",
            _ => "画像"
        };

        // 編集・削除できるのは書いた本人だけ
        public bool IsMine => string.Equals(Author, Environment.UserName, StringComparison.OrdinalIgnoreCase);

        public string Footer => $"{Author}  {UpdatedAt:yyyy/MM/dd HH:mm}";

        public string CategoryLabel => DisplayStatus == "" ? Category : $"{Category}：{DisplayStatus}";

        // 札の表示（カテゴリの設定から、読み込み時に入れる）
        public string TagText { get; set; } = "";
        public string TagColor { get; set; } = AnnotationCategory.DefaultColor;
        public bool IsDone { get; set; }
        public int CategoryOrder { get; set; } = int.MaxValue;

        // メモ欄・一覧に出す札（カテゴリと状態）
        public MemoTag Tag => MemoTag.Create(CategoryLabel, this);

        public string Summary => $"［{CategoryLabel}］{Comment}（{Author}）";

        // 一覧用
        public string TargetName => TargetType switch
        {
            TargetMeasurement => "測定",
            TargetDurability => "耐久",
            _ => "画像"
        };

        public string CommentLine => Comment.Replace("\r", "").Replace("\n", " ");
    }

    // メモを対象ごとに引けるようにまとめたもの（検索のたびに作り直さず、変更時だけ読み直す）
    public class AnnotationIndex
    {
        private readonly Dictionary<long, List<Annotation>> _byImage = new();
        private readonly Dictionary<long, List<Annotation>> _byMeasurement = new();
        private readonly Dictionary<string, List<Annotation>> _byDurability = new();

        public IReadOnlyList<Annotation> All { get; }

        public AnnotationIndex(IReadOnlyList<Annotation> annotations)
        {
            All = annotations;

            foreach (var annotation in annotations)
            {
                if (annotation.TargetType == Annotation.TargetImage && annotation.ImageId.HasValue)
                    Add(_byImage, annotation.ImageId.Value, annotation);
                else if (annotation.TargetType == Annotation.TargetMeasurement && annotation.MeasurementId.HasValue)
                    Add(_byMeasurement, annotation.MeasurementId.Value, annotation);

                Add(_byDurability, annotation.DurabilityId, annotation);
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<Annotation>> map, TKey key, Annotation annotation)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<Annotation>();

            list.Add(annotation);
        }

        // その画像に付いたメモと、その画像の測定に付いたメモ
        public List<Annotation> ForImage(long imageId, long measurementId)
        {
            var result = new List<Annotation>();

            if (_byImage.TryGetValue(imageId, out var image))
                result.AddRange(image);

            if (_byMeasurement.TryGetValue(measurementId, out var measurement))
                result.AddRange(measurement);

            return result;
        }

        // その耐久に関係するすべてのメモ（画像・測定・耐久）
        public IReadOnlyList<Annotation> ForDurability(string durabilityId)
        {
            return _byDurability.TryGetValue(durabilityId, out var list) ? list : Array.Empty<Annotation>();
        }

        // 耐久そのものに付いたメモ
        public List<Annotation> DurabilityLevel(string durabilityId)
        {
            return ForDurability(durabilityId).Where(x => x.TargetType == Annotation.TargetDurability).ToList();
        }
    }
}
