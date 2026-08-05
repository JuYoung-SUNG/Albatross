using System.Text.Json;
using Albatross.Shared.Models;
using Microsoft.Data.Sqlite;

namespace Albatross.Collector
{
    /// <summary>
    /// 골프 연습장 콘텐츠 파이프라인의 3~4단계를 담당한다.
    ///   [원천 수집] → [LLM 정제(수작업)] → <b>[JSON 임포트 → SQLite]</b> → <b>[사이트 JSON 내보내기]</b>
    /// LLM이 만든 JSON 파일을 --import-golf 로 넣으면 Slug 기준으로 upsert 하고,
    /// AlbatrossGolf 사이트가 읽는 golf-ranges.json 으로 내보낸다.
    /// </summary>
    internal static class GolfContentImporter
    {
        public static async Task EnsureTablesAsync(SqliteConnection connection, CancellationToken ct)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS GolfRanges (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Slug TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL,
                    Type TEXT,
                    Region TEXT,
                    City TEXT,
                    Address TEXT,
                    Phone TEXT,
                    Hours TEXT,
                    Price TEXT,
                    LeftHanded TEXT,
                    DriverAllowed TEXT,
                    Parking TEXT,
                    Summary TEXT,
                    HighlightsJson TEXT NOT NULL DEFAULT '[]',
                    CautionsJson TEXT NOT NULL DEFAULT '[]',
                    SourceUrlsJson TEXT NOT NULL DEFAULT '[]',
                    InfoAsOf TEXT,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_GolfRanges_Region ON GolfRanges(Region, City);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// LLM이 만든 JSON 파일을 읽어 DB에 upsert 한다.
        /// 파일 형식은 { "ranges": [ ... ] } 또는 [ ... ] (배열만) 둘 다 허용.
        /// </summary>
        public static async Task<(int Added, int Updated)> ImportAsync(string databasePath, string jsonPath, CancellationToken ct)
        {
            var raw = await File.ReadAllTextAsync(jsonPath, ct);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            List<GolfRangeDto> ranges;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('['))
            {
                ranges = JsonSerializer.Deserialize<List<GolfRangeDto>>(raw, options) ?? new();
            }
            else
            {
                ranges = JsonSerializer.Deserialize<GolfContentDto>(raw, options)?.Ranges ?? new();
            }

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await EnsureTablesAsync(connection, ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            var added = 0;
            var updated = 0;

            foreach (var r in ranges)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                var slug = string.IsNullOrWhiteSpace(r.Slug) ? MakeSlug(r.Name) : r.Slug.Trim();

                var existsCmd = connection.CreateCommand();
                existsCmd.Transaction = (SqliteTransaction)tx;
                existsCmd.CommandText = "SELECT COUNT(*) FROM GolfRanges WHERE Slug = $slug;";
                existsCmd.Parameters.AddWithValue("$slug", slug);
                var exists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct)) > 0;

                var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText =
                    """
                    INSERT INTO GolfRanges
                        (Slug, Name, Type, Region, City, Address, Phone, Hours, Price, LeftHanded, DriverAllowed, Parking,
                         Summary, HighlightsJson, CautionsJson, SourceUrlsJson, InfoAsOf)
                    VALUES
                        ($slug, $name, $type, $region, $city, $addr, $phone, $hours, $price, $left, $driver, $parking,
                         $summary, $highlights, $cautions, $sources, $asOf)
                    ON CONFLICT(Slug) DO UPDATE SET
                        Name = excluded.Name, Type = excluded.Type, Region = excluded.Region, City = excluded.City,
                        Address = excluded.Address, Phone = excluded.Phone, Hours = excluded.Hours, Price = excluded.Price,
                        LeftHanded = excluded.LeftHanded, DriverAllowed = excluded.DriverAllowed, Parking = excluded.Parking,
                        Summary = excluded.Summary, HighlightsJson = excluded.HighlightsJson,
                        CautionsJson = excluded.CautionsJson, SourceUrlsJson = excluded.SourceUrlsJson,
                        InfoAsOf = excluded.InfoAsOf, UpdatedAt = CURRENT_TIMESTAMP;
                    """;
                cmd.Parameters.AddWithValue("$slug", slug);
                cmd.Parameters.AddWithValue("$name", r.Name.Trim());
                cmd.Parameters.AddWithValue("$type", (object?)r.Type ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$region", (object?)r.Region ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$city", (object?)r.City ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$addr", (object?)r.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$phone", (object?)r.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$hours", (object?)r.Hours ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$price", (object?)r.Price ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$left", (object?)r.LeftHanded ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$driver", (object?)r.DriverAllowed ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$parking", (object?)r.Parking ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$summary", (object?)r.Summary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$highlights", JsonSerializer.Serialize(r.Highlights));
                cmd.Parameters.AddWithValue("$cautions", JsonSerializer.Serialize(r.Cautions));
                cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(r.SourceUrls));
                cmd.Parameters.AddWithValue("$asOf", (object?)r.UpdatedAt ?? DateTime.Now.ToString("yyyy-MM-dd"));
                await cmd.ExecuteNonQueryAsync(ct);

                if (exists) updated++; else added++;
            }

            await tx.CommitAsync(ct);
            return (added, updated);
        }

        /// <summary>DB의 연습장 정보를 골프 사이트가 읽는 golf-ranges.json 으로 내보낸다.</summary>
        public static async Task<int> ExportAsync(string databasePath, string dataDir, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await EnsureTablesAsync(connection, ct);

            var content = new GolfContentDto();
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT Slug, Name, Type, Region, City, Address, Phone, Hours, Price, LeftHanded, DriverAllowed, Parking,
                       Summary, HighlightsJson, CautionsJson, SourceUrlsJson, InfoAsOf
                FROM GolfRanges ORDER BY Region, City, Name;
                """;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                while (await reader.ReadAsync(ct))
                {
                    content.Ranges.Add(new GolfRangeDto
                    {
                        Slug = reader.GetString(0),
                        Name = reader.GetString(1),
                        Type = S(2), Region = S(3), City = S(4), Address = S(5), Phone = S(6), Hours = S(7),
                        Price = S(8), LeftHanded = S(9), DriverAllowed = S(10), Parking = S(11), Summary = S(12),
                        Highlights = DeserializeList(reader.GetString(13)),
                        Cautions = DeserializeList(reader.GetString(14)),
                        SourceUrls = DeserializeList(reader.GetString(15)),
                        UpdatedAt = S(16)
                    });
                }
            }

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(dataDir, "golf-ranges.json"),
                JsonSerializer.Serialize(content, options), ct);

            return content.Ranges.Count;
        }

        /// <summary>DB 내용으로 검색엔진이 읽을 수 있는 정적 HTML 사이트를 생성한다.</summary>
        public static async Task<int> GenerateSiteAsync(string databasePath, string siteRoot, CancellationToken ct)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await EnsureTablesAsync(connection, ct);

            var ranges = new List<GolfRangeDto>();
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT Slug, Name, Type, Region, City, Address, Phone, Hours, Price, LeftHanded, DriverAllowed, Parking,
                       Summary, HighlightsJson, CautionsJson, SourceUrlsJson, InfoAsOf
                FROM GolfRanges ORDER BY Region, City, Name;
                """;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                while (await reader.ReadAsync(ct))
                {
                    ranges.Add(new GolfRangeDto
                    {
                        Slug = reader.GetString(0),
                        Name = reader.GetString(1),
                        Type = S(2), Region = S(3), City = S(4), Address = S(5), Phone = S(6), Hours = S(7),
                        Price = S(8), LeftHanded = S(9), DriverAllowed = S(10), Parking = S(11), Summary = S(12),
                        Highlights = DeserializeList(reader.GetString(13)),
                        Cautions = DeserializeList(reader.GetString(14)),
                        SourceUrls = DeserializeList(reader.GetString(15)),
                        UpdatedAt = S(16)
                    });
                }
            }

            var publicDir = Path.Combine(siteRoot, "public");
            return await GolfSiteGenerator.GenerateAsync(ranges, publicDir, ct);
        }

        private static List<string> DeserializeList(string json)
        {
            try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
            catch (JsonException) { return new(); }
        }

        /// <summary>
        /// 이름에서 URL용 slug 생성. 한글 URL은 인코딩되면 읽기 어렵고 공유·분석에 불리하므로
        /// 영문/숫자만 남기고, 남는 게 없으면(순한글 이름) 이름 해시로 안정적인 식별자를 만든다.
        /// LLM 결과 JSON에 slug를 직접 넣어주면 그 값이 우선한다(권장).
        /// </summary>
        private static string MakeSlug(string name)
        {
            var ascii = new string(name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
            while (ascii.Contains("--")) ascii = ascii.Replace("--", "-");
            ascii = ascii.Trim('-');

            if (ascii.Length >= 3) return ascii;

            // 순한글 이름 → 이름 기반 짧은 해시 (같은 이름이면 항상 같은 slug라 재임포트해도 URL 유지)
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.Trim()));
            var hash = Convert.ToHexString(bytes)[..8].ToLowerInvariant();
            return $"range-{hash}";
        }
    }
}
