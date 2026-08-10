using System.Text.Json;
using Microsoft.Data.Sqlite;
using Albatross.Collector.News.Services;

namespace Albatross.Collector
{
    /// <summary>
    /// KLPGA 대회 데이터를 DB에 저장하고, 사이트가 쓸 형태로 내보낸다.
    ///
    /// 이 사이트의 차별점은 "이 대회가 어느 정도 급인가"를 알려주는 것이다.
    /// 협회는 등급을 매겨서 공개하지 않으므로 아래 기준으로 직접 산정하고,
    /// 산정 근거를 화면에도 그대로 노출해 판단을 검증할 수 있게 한다.
    /// </summary>
    internal static class GolfTournamentImporter
    {
        /// <summary>
        /// 메이저 대회는 협회가 지정하며 해마다 바뀐다(2025년에는 3개까지 줄었다가 2026년 4개로 복귀).
        /// 상금만으로는 구분되지 않는다 — 2026년 15억 대회 6개 중 메이저는 4개다.
        /// 그래서 연도별로 명시한다. <b>새 시즌마다 확인이 필요하다.</b>
        /// </summary>
        private static readonly Dictionary<int, string[]> MajorKeywords = new()
        {
            // 2025: 한화 클래식이 없어진 뒤 새 메이저를 구하지 못해 3개로 운영
            [2025] = new[] { "한국여자오픈", "KLPGA 챔피언십", "하이트진로" },
            // 2026: KB금융이 합류하며 9년 만에 4대 메이저 체제로 복귀
            [2026] = new[] { "한국여자오픈", "KLPGA 챔피언십", "KB금융", "하이트진로" }
        };

        /// <summary>등급. 값이 곧 화면 뱃지 문구가 된다.</summary>
        private static class Tier
        {
            public const string Major = "메이저";
            public const string Premier = "특급";
            public const string Regular = "정규투어";
            public const string Second = "2부 드림투어";
            public const string Third = "3부 점프투어";
            public const string Champions = "챔피언스";
            public const string Overseas = "해외 공동주관";
            public const string Special = "특별대회";
        }

        public static async Task<(int added, int updated)> ImportAsync(
            string databasePath, List<KlpgaTourService.Tournament> tournaments, CancellationToken ct)
        {
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);

            int added = 0, updated = 0;
            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var t in tournaments)
            {
                var year = int.TryParse(t.StartDate[..4], out var y) ? y : DateTime.Now.Year;
                var (tier, reason) = DecideTier(t, year);
                var slug = MakeSlug(t.GameCode, t.EngTitle, t.Title);

                var exists = conn.CreateCommand();
                exists.Transaction = (SqliteTransaction)tx;
                exists.CommandText = "SELECT COUNT(*) FROM GolfTournaments WHERE GameCode = $c;";
                exists.Parameters.AddWithValue("$c", t.GameCode);
                var isNew = Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0;

                var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO GolfTournaments
                        (GameCode, Slug, Title, EngTitle, TourType, Tier, TierReason, Season,
                         StartDate, EndDate, PrizeMoney, MoneyUnit, Course, Rounds, Par, EntryCount,
                         WinnerName, DefendingName, UpdatedAt)
                    VALUES ($code, $slug, $title, $eng, $type, $tier, $reason, $season,
                            $sd, $ed, $prize, $unit, $course, $rounds, $par, $entry,
                            $winner, $defending, $now)
                    ON CONFLICT(GameCode) DO UPDATE SET
                        Slug=excluded.Slug, Title=excluded.Title, EngTitle=excluded.EngTitle,
                        TourType=excluded.TourType, Tier=excluded.Tier, TierReason=excluded.TierReason,
                        Season=excluded.Season, StartDate=excluded.StartDate, EndDate=excluded.EndDate,
                        PrizeMoney=excluded.PrizeMoney, MoneyUnit=excluded.MoneyUnit, Course=excluded.Course,
                        Rounds=excluded.Rounds, Par=excluded.Par, EntryCount=excluded.EntryCount,
                        WinnerName=excluded.WinnerName, DefendingName=excluded.DefendingName,
                        UpdatedAt=excluded.UpdatedAt;
                    """;
                cmd.Parameters.AddWithValue("$code", t.GameCode);
                cmd.Parameters.AddWithValue("$slug", slug);
                cmd.Parameters.AddWithValue("$title", t.Title);
                cmd.Parameters.AddWithValue("$eng", (object?)t.EngTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$type", t.TourType);
                cmd.Parameters.AddWithValue("$tier", tier);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.Parameters.AddWithValue("$season", year);
                cmd.Parameters.AddWithValue("$sd", t.StartDate);
                cmd.Parameters.AddWithValue("$ed", t.EndDate);
                cmd.Parameters.AddWithValue("$prize", (object?)t.PrizeMoney ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$unit", (object?)t.MoneyUnit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$course", (object?)t.Course ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rounds", (object?)t.Rounds ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$par", (object?)t.Par ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$entry", (object?)t.EntryCount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$winner", (object?)t.WinnerName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$defending", (object?)t.DefendingName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-dd"));
                await cmd.ExecuteNonQueryAsync(ct);

                if (isNew) added++; else updated++;
            }

            await tx.CommitAsync(ct);
            return (added, updated);
        }

        /// <summary>
        /// 등급 산정. 근거 문장을 함께 돌려줘 화면에 그대로 표시한다.
        /// 상금 기준선(12억)은 2026 정규투어 29개 분포에서 상위 3분의 1이 갈리는 지점이다.
        /// </summary>
        private static (string Tier, string Reason) DecideTier(KlpgaTourService.Tournament t, int year)
        {
            switch (t.TourType)
            {
                case "DR": return (Tier.Second, "정규투어 아래 2부 투어입니다. 여기 상금랭킹 상위 선수가 다음 시즌 정규투어 시드를 받습니다.");
                case "JT": return (Tier.Third, "3부 투어로, 프로 입문 단계 선수들이 뛰는 무대입니다.");
                case "SN": return (Tier.Champions, "만 45세 이상 선수들이 출전하는 챔피언스 투어입니다.");
                case "WT": return (Tier.Overseas, "해외 협회와 공동 주관하는 대회입니다.");
                case "NR": return (Tier.Special, "정규투어 상금랭킹에 포함되지 않는 특별 대회입니다.");
            }

            // 정규투어(RE) — 메이저인지 먼저 보고, 아니면 상금 규모로 나눈다
            if (MajorKeywords.TryGetValue(year, out var keys) &&
                keys.Any(k => t.Title.Contains(k, StringComparison.Ordinal)))
            {
                return (Tier.Major, $"{year}시즌 4대 메이저 중 하나입니다. 우승하면 시드 혜택과 명예가 일반 대회와 비교되지 않습니다.");
            }

            if (t.PrizeMoney is >= 1_200_000_000)
                return (Tier.Premier, $"총상금 {Money(t.PrizeMoney!.Value)}으로 메이저를 제외하면 최상위권입니다.");

            return t.PrizeMoney is { } p
                ? (Tier.Regular, $"총상금 {Money(p)} 규모의 정규투어 대회입니다.")
                : (Tier.Regular, "정규투어 대회입니다.");
        }

        private static string Money(long won) =>
            won >= 100_000_000 ? $"{won / 100_000_000.0:0.#}억원" : $"{won / 10000:N0}만원";

        /// <summary>
        /// URL용 식별자. 영문 제목이 있으면 그걸 쓰고, 없으면 대회 코드를 쓴다.
        /// 대회 코드는 협회가 고정으로 부여하므로 해마다 이름이 바뀌어도 주소가 안정적이다.
        /// </summary>
        private static string MakeSlug(string gameCode, string? engTitle, string title)
        {
            if (!string.IsNullOrWhiteSpace(engTitle))
            {
                var ascii = new string(engTitle.Trim()
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
                while (ascii.Contains("--")) ascii = ascii.Replace("--", "-");
                ascii = ascii.Trim('-');
                if (ascii.Length >= 4) return ascii.Length > 60 ? ascii[..60].TrimEnd('-') : ascii;
            }
            return $"t-{gameCode}";
        }

        private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS GolfTournaments (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameCode TEXT NOT NULL UNIQUE,
                    Slug TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    EngTitle TEXT,
                    TourType TEXT NOT NULL,
                    Tier TEXT NOT NULL,
                    TierReason TEXT,
                    Season INTEGER NOT NULL,
                    StartDate TEXT NOT NULL,
                    EndDate TEXT,
                    PrizeMoney INTEGER,
                    MoneyUnit TEXT,
                    Course TEXT,
                    Rounds INTEGER,
                    Par INTEGER,
                    EntryCount INTEGER,
                    WinnerName TEXT,
                    DefendingName TEXT,
                    UpdatedAt TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_GolfTournaments_Season ON GolfTournaments(Season, StartDate);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>사이트가 읽을 JSON으로 내보낸다.</summary>
        public static async Task<int> ExportAsync(string databasePath, string dataDir, CancellationToken ct)
        {
            var rows = await LoadAsync(databasePath, ct);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(dataDir, "golf-tournaments.json"),
                JsonSerializer.Serialize(new { tournaments = rows }, options), ct);
            return rows.Count;
        }

        public sealed record TournamentRow(
            string Slug, string Title, string? EngTitle, string TourType, string Tier, string? TierReason,
            int Season, string StartDate, string? EndDate, long? PrizeMoney, string? MoneyUnit,
            string? Course, int? Rounds, int? Par, int? EntryCount, string? WinnerName, string? DefendingName,
            string? GameCode = null);

        public static async Task<List<TournamentRow>> LoadAsync(string databasePath, CancellationToken ct)
        {
            var list = new List<TournamentRow>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Slug, Title, EngTitle, TourType, Tier, TierReason, Season, StartDate, EndDate,
                       PrizeMoney, MoneyUnit, Course, Rounds, Par, EntryCount, WinnerName, DefendingName,
                       GameCode
                FROM GolfTournaments
                ORDER BY Season DESC, StartDate;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                list.Add(new TournamentRow(
                    r.GetString(0), r.GetString(1), S(2), r.GetString(3), r.GetString(4), S(5),
                    r.GetInt32(6), r.GetString(7), S(8),
                    r.IsDBNull(9) ? null : r.GetInt64(9), S(10),
                    S(11), I(12), I(13), I(14), S(15), S(16), S(17)));
            }
            return list;
        }
    }
}
