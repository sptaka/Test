using Microsoft.Data.Sqlite;
using SampleViewer.Services;
using System.Globalization;

namespace SampleViewer.Repositories;

// 比較ウィンドウの「情報」欄に出す、画像1枚分の情報
public class CompareImageInfo
{
    public double? Pages { get; set; }
    public string ColdHot { get; set; } = "";
    public string MeasuredAt { get; set; } = "";

    // パターンの色（Y/M/C/K/Color）
    public string PatternColor { get; set; } = "";

    // トナー色ごとの測定値（Y/M/C/K の順）。0 と空は欠測
    public double?[] Vb { get; } = new double?[4];
    public double?[] QM { get; } = new double?[4];
    public double?[] MA { get; } = new double?[4];

    // 特徴量の値（登録順）
    public List<(string Name, double Value)> Features { get; } = new();
}

public class CompareInfoRepository
{
    public CompareImageInfo? Get(string taikyuCode, string imageName)
    {
        const string sql = @"
SELECT i.image_id, m.pages, m.cold_hot, m.measurement_datetime, p.color,
       m.Vb1, m.Vb2, m.Vb3, m.Vb4,
       m.QM1, m.QM2, m.QM3, m.QM4,
       m.MA1, m.MA2, m.MA3, m.MA4
FROM image i
INNER JOIN measurement m ON i.measurement_id = m.measurement_id
INNER JOIN taikyu t ON m.taikyu_id = t.taikyu_id
INNER JOIN pattern p ON i.pattern_id = p.pattern_id
WHERE t.taikyu_code = @taikyuCode AND i.image_name = @imageName
LIMIT 1";

        using var conn = DbHelper.OpenConnection();

        long imageId;
        var info = new CompareImageInfo();

        using (var cmd = new SqliteCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@taikyuCode", taikyuCode);
            cmd.Parameters.AddWithValue("@imageName", imageName);

            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return null;

            imageId = Convert.ToInt64(reader[0]);
            info.Pages = ToNumber(reader[1]);
            info.ColdHot = reader[2]?.ToString() ?? "";
            info.MeasuredAt = reader[3]?.ToString() ?? "";
            info.PatternColor = reader[4]?.ToString() ?? "";

            for (int i = 0; i < 4; i++)
            {
                info.Vb[i] = ToMeasurement(reader[5 + i]);
                info.QM[i] = ToMeasurement(reader[9 + i]);
                info.MA[i] = ToMeasurement(reader[13 + i]);
            }
        }

        using (var cmd = new SqliteCommand(@"
SELECT f.feature_name, ifv.value
FROM image_feature_value ifv
INNER JOIN feature f ON ifv.feature_id = f.feature_id
WHERE ifv.image_id = @imageId
ORDER BY f.feature_id", conn))
        {
            cmd.Parameters.AddWithValue("@imageId", imageId);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string name = reader[0]?.ToString() ?? "";
                var value = ToNumber(reader[1]);

                if (name != "" && value.HasValue && info.Features.All(x => x.Name != name))
                    info.Features.Add((name, value.Value));
            }
        }

        return info;
    }

    // 画像IDからパターン名を引く（DBに無い画像は含まれない）
    public Dictionary<long, string> GetPatterns(IEnumerable<long> imageIds)
    {
        var result = new Dictionary<long, string>();

        using var conn = DbHelper.OpenConnection();

        foreach (long imageId in imageIds.Distinct())
        {
            using var cmd = new SqliteCommand(@"
SELECT p.pattern_name
FROM image i
INNER JOIN pattern p ON i.pattern_id = p.pattern_id
WHERE i.image_id = @imageId", conn);
            cmd.Parameters.AddWithValue("@imageId", imageId);

            if (cmd.ExecuteScalar() is string pattern)
                result[imageId] = pattern;
        }

        return result;
    }

    private static double? ToNumber(object? value)
    {
        if (value == null || value is DBNull)
            return null;

        return double.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : null;
    }

    // 測定値は 0 を欠測として扱う（トレンドグラフと同じ）
    private static double? ToMeasurement(object? value)
    {
        var number = ToNumber(value);
        return number == 0 ? null : number;
    }
}
