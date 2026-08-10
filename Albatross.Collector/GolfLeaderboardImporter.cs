using Microsoft.Data.Sqlite;
using Albatross.Collector.News.Services;

namespace Albatross.Collector
{
    /// <summary>
    /// 대회별 최종 순위를 저장한다.
    ///
    /// 응답이 대회당 1~2MB라 이미 받아둔 대회는 다시 부르지 않는다.
    /// 그래야 주간 갱신 때 새로 끝난 대회 한두 개만 받고 끝난다.
    /// </summary>
    internal static class GolfLeaderboardImporter
    {
        public sealed record LeaderboardRow(
            string GameCode, int Rank, string PlayerCode, string PlayerName,
            string? TotalUnderPar, int? TotalStrokes, int? R1, int? R2, int? R3, int? R4);

        /// <summary>리더보드가 아직 없는 "끝난 대회" 목록. 최근 시즌부터 채운다.</summary>
        public sealed record Pending(string GameCode, string Title, int Rounds, int Season);

        public static async Task<List<Pending>> FindMissingAsync(
            string databasePath, int fromSeason, int maxCount, CancellationToken ct)
        {
            var list = new List<Pending>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.GameCode, t.Title, COALESCE(t.Rounds, 4), t.Season
                FROM GolfTournaments t
                WHERE t.Season >= $from
                  AND t.TourType = 'RE'
                  AND t.WinnerName IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM GolfLeaderboards l WHERE l.GameCode = t.GameCode)
                ORDER BY t.StartDate DESC
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$from", fromSeason);
            cmd.Parameters.AddWithValue("$max", maxCount);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new Pending(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
            return list;
        }

        public static async Task<int> SaveAsync(
            string databasePath, string gameCode, List<KlpgaLeaderboardService.LeaderboardEntry> entries,
            CancellationToken ct)
        {
            if (entries.Count == 0) return 0;

            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var del = conn.CreateCommand();
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM GolfLeaderboards WHERE GameCode = $g;";
            del.Parameters.AddWithValue("$g", gameCode);
            await del.ExecuteNonQueryAsync(ct);

            foreach (var e in entries)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO GolfLeaderboards
                        (GameCode, Rank, PlayerCode, PlayerName, TotalUnderPar, TotalStrokes, R1, R2, R3, R4)
                    VALUES ($g, $rk, $pc, $pn, $up, $ts, $r1, $r2, $r3, $r4);
                    """;
                cmd.Parameters.AddWithValue("$g", gameCode);
                cmd.Parameters.AddWithValue("$rk", e.Rank);
                cmd.Parameters.AddWithValue("$pc", e.PlayerCode);
                cmd.Parameters.AddWithValue("$pn", e.PlayerName);
                cmd.Parameters.AddWithValue("$up", (object?)e.TotalUnderPar ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", (object?)e.TotalStrokes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r1", (object?)e.Round1 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r2", (object?)e.Round2 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r3", (object?)e.Round3 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r4", (object?)e.Round4 ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return entries.Count;
        }

        /// <summary>대회 상세에 쓸 상위 N명. 사이트 생성 때 통째로 읽어 메모리에서 나눠 쓴다.</summary>
        public static async Task<List<LeaderboardRow>> LoadAsync(
            string databasePath, int topN, CancellationToken ct)
        {
            var list = new List<LeaderboardRow>();
            await using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT GameCode, Rank, PlayerCode, PlayerName, TotalUnderPar, TotalStrokes, R1, R2, R3, R4
                FROM GolfLeaderboards
                WHERE Rank <= $top
                ORDER BY GameCode, Rank;
                """;
            cmd.Parameters.AddWithValue("$top", topN);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                list.Add(new LeaderboardRow(
                    r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), I(5), I(6), I(7), I(8), I(9)));
            }
            return list;
        }

        private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS GolfLeaderboards (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameCode TEXT NOT NULL,
                    Rank INTEGER NOT NULL,
                    PlayerCode TEXT NOT NULL,
                    PlayerName TEXT NOT NULL,
                    TotalUnderPar TEXT,
                    TotalStrokes INTEGER,
                    R1 INTEGER, R2 INTEGER, R3 INTEGER, R4 INTEGER
                );
                CREATE INDEX IF NOT EXISTS IX_GolfLeaderboards_Game ON GolfLeaderboards(GameCode, Rank);
                CREATE INDEX IF NOT EXISTS IX_GolfLeaderboards_Player ON GolfLeaderboards(PlayerCode);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
