using Microsoft.Data.Sqlite;
using Albatross.Collector.News.Services;

namespace Albatross.Collector
{
    /// <summary>
    /// 부문별 랭킹과 선수 정보를 저장한다.
    ///
    /// 선수는 별도 테이블로 뽑아둔다. 랭킹은 부문마다 상위 몇 명만 나오지만,
    /// 35개 부문을 합치면 "이번 시즌 잘하고 있는 선수" 명단이 자연스럽게 모인다.
    /// 여기에 대회 우승 기록을 붙이면 선수 페이지가 된다.
    /// </summary>
    internal static class GolfRecordImporter
    {
        /// <summary>화면에서 위에 놓을 주요 부문. 나머지는 이 뒤에 원래 순서대로 붙는다.</summary>
        public static readonly string[] FeaturedCategories =
        {
            "상금순위", "대상포인트", "신인상포인트", "평균타수",
            "드라이브 거리", "그린적중률", "평균퍼팅", "페어웨이안착률"
        };

        public sealed record RankingRow(string Category, int Rank, string PlayerCode, string PlayerName, string? Team, string Value, int Season);
        public sealed record PlayerRow(string PlayerCode, string Name, string? Team, int Season, int RankCount, string? BestCategory, int? BestRank);

        public static async Task<int> ImportAsync(
            string databasePath, int season, List<KlpgaRecordService.RankingCategory> categories, CancellationToken ct)
        {
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            // 같은 시즌은 통째로 교체한다 — 랭킹은 누적이 아니라 스냅샷이라 순위가 밀린 선수가 남으면 안 된다
            var del = conn.CreateCommand();
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM GolfRankings WHERE Season = $s; DELETE FROM GolfPlayers WHERE Season = $s;";
            del.Parameters.AddWithValue("$s", season);
            await del.ExecuteNonQueryAsync(ct);

            var players = new Dictionary<string, (string Name, string? Team, int Count, string BestCat, int BestRank)>();
            var total = 0;

            foreach (var cat in categories)
            {
                foreach (var e in cat.Entries)
                {
                    var cmd = conn.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = """
                        INSERT INTO GolfRankings (Season, Category, Rank, PlayerCode, PlayerName, Team, Value)
                        VALUES ($s, $c, $r, $pc, $pn, $t, $v);
                        """;
                    cmd.Parameters.AddWithValue("$s", season);
                    cmd.Parameters.AddWithValue("$c", cat.Category);
                    cmd.Parameters.AddWithValue("$r", e.Rank);
                    cmd.Parameters.AddWithValue("$pc", e.PlayerCode);
                    cmd.Parameters.AddWithValue("$pn", e.PlayerName);
                    cmd.Parameters.AddWithValue("$t", (object?)e.Team ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$v", e.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                    total++;

                    // 선수별로 "몇 개 부문에 이름을 올렸는지"와 "가장 높은 순위"를 모은다
                    if (players.TryGetValue(e.PlayerCode, out var p))
                    {
                        var better = e.Rank < p.BestRank;
                        players[e.PlayerCode] = (p.Name, p.Team ?? e.Team, p.Count + 1,
                            better ? cat.Category : p.BestCat, better ? e.Rank : p.BestRank);
                    }
                    else
                    {
                        players[e.PlayerCode] = (e.PlayerName, e.Team, 1, cat.Category, e.Rank);
                    }
                }
            }

            foreach (var (code, p) in players)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO GolfPlayers (PlayerCode, Season, Name, Team, RankCount, BestCategory, BestRank)
                    VALUES ($pc, $s, $n, $t, $rc, $bc, $br);
                    """;
                cmd.Parameters.AddWithValue("$pc", code);
                cmd.Parameters.AddWithValue("$s", season);
                cmd.Parameters.AddWithValue("$n", p.Name);
                cmd.Parameters.AddWithValue("$t", (object?)p.Team ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rc", p.Count);
                cmd.Parameters.AddWithValue("$bc", p.BestCat);
                cmd.Parameters.AddWithValue("$br", p.BestRank);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return total;
        }

        public static async Task<List<RankingRow>> LoadRankingsAsync(string databasePath, CancellationToken ct)
        {
            var list = new List<RankingRow>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Category, Rank, PlayerCode, PlayerName, Team, Value, Season
                FROM GolfRankings
                WHERE Season = (SELECT MAX(Season) FROM GolfRankings)
                ORDER BY Category, Rank;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new RankingRow(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetInt32(6)));
            return list;
        }

        public static async Task<List<PlayerRow>> LoadPlayersAsync(string databasePath, CancellationToken ct)
        {
            var list = new List<PlayerRow>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT PlayerCode, Name, Team, Season, RankCount, BestCategory, BestRank
                FROM GolfPlayers
                WHERE Season = (SELECT MAX(Season) FROM GolfPlayers)
                ORDER BY BestRank, RankCount DESC, Name;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new PlayerRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6)));
            return list;
        }

        private static async Task EnsureTablesAsync(SqliteConnection conn, CancellationToken ct)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS GolfRankings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Season INTEGER NOT NULL,
                    Category TEXT NOT NULL,
                    Rank INTEGER NOT NULL,
                    PlayerCode TEXT NOT NULL,
                    PlayerName TEXT NOT NULL,
                    Team TEXT,
                    Value TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_GolfRankings_Season ON GolfRankings(Season, Category, Rank);

                CREATE TABLE IF NOT EXISTS GolfPlayers (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlayerCode TEXT NOT NULL,
                    Season INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Team TEXT,
                    RankCount INTEGER NOT NULL DEFAULT 0,
                    BestCategory TEXT,
                    BestRank INTEGER,
                    UNIQUE(PlayerCode, Season)
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
