using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector.News.Services;

public class KboOfficialSiteService
{
    private readonly HttpClient _http;
    private readonly ILogger<KboOfficialSiteService> _logger;

    private const string TeamRankUrl = "https://www.koreabaseball.com/Record/TeamRank/TeamRankDaily.aspx";
    private const string HitterStatsUrl = "https://www.koreabaseball.com/Record/Player/HitterBasic/Basic1.aspx";
    private const string HitterStatsUrl2 = "https://www.koreabaseball.com/Record/Player/HitterBasic/Basic2.aspx";
    private const string PitcherStatsUrl = "https://www.koreabaseball.com/Record/Player/PitcherBasic/Basic1.aspx";
    private const string PitcherStatsUrl2 = "https://www.koreabaseball.com/Record/Player/PitcherBasic/Basic2.aspx";
    private const string TeamHitterUrl1 = "https://www.koreabaseball.com/Record/Team/Hitter/Basic1.aspx";
    private const string TeamHitterUrl2 = "https://www.koreabaseball.com/Record/Team/Hitter/Basic2.aspx";
    private const string TeamPitcherUrl1 = "https://www.koreabaseball.com/Record/Team/Pitcher/Basic1.aspx";
    private const string TeamPitcherUrl2 = "https://www.koreabaseball.com/Record/Team/Pitcher/Basic2.aspx";
    private const string ScheduleUrl = "https://www.koreabaseball.com/Schedule/Schedule.aspx";
    private const string ScheduleListApiUrl = "https://www.koreabaseball.com/ws/Schedule.asmx/GetScheduleList";
    private const string BoxScoreApiUrl = "https://www.koreabaseball.com/ws/Schedule.asmx/GetBoxScoreScroll";

    public KboOfficialSiteService(HttpClient http, ILogger<KboOfficialSiteService> logger)
    {
        _http = http;
        _logger = logger;

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task CollectAndSaveAsync(string databasePath, CancellationToken ct)
    {
        var standings = await CollectTeamStandingsAsync(ct);
        standings = await MergeTeamExtraStatsAsync(standings, ct);
        var batters = await CollectHitterStatsAsync(ct);
        var pitchers = await CollectPitcherStatsAsync(ct);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        await SaveStandingsAsync(connection, standings, ct);
        await SaveBatterStatsAsync(connection, batters, ct);
        await SavePitcherStatsAsync(connection, pitchers, ct);

        await CollectAndSaveGameResultsAsync(databasePath, ct);

        var topTeam = standings.OrderBy(s => s.Rank).FirstOrDefault();
        var topBatter = batters.FirstOrDefault();
        var topPitcher = pitchers.FirstOrDefault();
        _logger.LogInformation(
            "[KBO 공식] 마지막 갱신 전문\n" +
            "  팀 순위 1위: {team} (경기 {games}, {wins}승 {losses}패 {draws}무, 승률 {winRate}, 게임차 {gamesBehind})\n" +
            "  타자기록 1위: {batter} ({batterTeam}, 타율 {avg}, {batterGames}경기, 안타 {hits}, 홈런 {hr}, 타점 {rbi})\n" +
            "  투수기록 1위: {pitcher} ({pitcherTeam}, 방어율 {era}, {pWins}승 {pLosses}패 {saves}세이브, {innings}이닝, 탈삼진 {strikeouts})",
            topTeam?.TeamName ?? "N/A", topTeam?.Games, topTeam?.Wins, topTeam?.Losses, topTeam?.Draws, topTeam?.WinRate, topTeam?.GamesBehind ?? "-",
            topBatter?.PlayerName ?? "N/A", topBatter?.Team ?? "-", topBatter?.Avg, topBatter?.Games, topBatter?.Hits, topBatter?.HomeRuns, topBatter?.Rbi,
            topPitcher?.PlayerName ?? "N/A", topPitcher?.Team ?? "-", topPitcher?.Era, topPitcher?.Wins, topPitcher?.Losses, topPitcher?.Saves, topPitcher?.Innings ?? "-", topPitcher?.Strikeouts);
    }

    /// <summary>
    /// 경기 일정/스코어만 갱신한다 (팀순위·타자·투수 통계는 건드리지 않음).
    /// --boxscore-only 가벼운 모드에서도 "방금 끝난 경기"를 인식할 수 있도록 별도로 호출 가능하게 분리했다.
    /// </summary>
    public async Task CollectAndSaveGameResultsAsync(string databasePath, CancellationToken ct)
    {
        var games = await CollectGameResultsAsync(ct);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);
        await SaveGameResultsAsync(connection, games, ct);

        var lastGame = games.OrderByDescending(g => g.GameDate).ThenBy(g => g.GameTime).FirstOrDefault();
        _logger.LogInformation(
            "[KBO 공식] 경기결과 마지막 갱신 — {date} {time} {away} {awayScore} : {homeScore} {home}",
            lastGame?.GameDate ?? "N/A", lastGame?.GameTime ?? "-", lastGame?.AwayTeam ?? "-",
            lastGame?.AwayScore, lastGame?.HomeScore, lastGame?.HomeTeam ?? "-");
    }

    /// <summary>
    /// 시즌 시작 달부터 이번 달까지 달별로 순회하며 경기결과(스코어)를 소급 수집한다.
    /// KBO 일정 API는 과거 어떤 달이든 조회 가능해서 가능한 기능 — 팀순위/선수기록은 "현재 누적치"만
    /// 제공되는 페이지라 이 방식으로 소급이 안 되고, 경기결과/박스스코어만 시즌 전체 소급이 가능하다.
    /// </summary>
    public async Task BackfillSeasonGamesAsync(string databasePath, int seasonYear, int startMonth, CancellationToken ct)
    {
        var currentMonth = DateTime.Now.Month;
        for (var month = startMonth; month <= currentMonth; month++)
        {
            _logger.LogInformation("[KBO 공식] 시즌 소급 — {year}년 {month}월 경기결과 수집 중...", seasonYear, month);

            var games = await CollectGameResultsAsync(seasonYear, month, ct);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            await SaveGameResultsAsync(connection, games, ct);

            _logger.LogInformation("[KBO 공식] 시즌 소급 — {year}년 {month}월 — {count}경기 저장", seasonYear, month, games.Count);
        }
    }

    private async Task<List<TeamStanding>> CollectTeamStandingsAsync(CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(TeamRankUrl, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 주의: XPath에서 [1]을 //table[...] 뒤에 바로 붙이면 "문서 전체에서 첫 번째"가 아니라
            // "각 부모 아래에서 첫 번째"로 해석되어, 서로 다른 부모를 가진 두 번째 표(팀간승패표)까지
            // 함께 선택되는 문제가 있었다. 괄호로 묶어 전체 노드셋을 먼저 구한 뒤 인덱싱한다.
            var rows = doc.DocumentNode.SelectNodes("(//table[contains(@class,'tData')])[1]//tbody//tr");
            return rows == null ? new List<TeamStanding>() : ParseStandingsRows(rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to collect KBO team standings. Error: {msg}", ex.Message);
            return new List<TeamStanding>();
        }
    }

    /// <summary>
    /// 팀 순위 표(td 순서: 순위/팀명/경기/승/패/무/승률/게임차)를 파싱한다.
    /// 실시간 수집(CollectTeamStandingsAsync)과 날짜별 소급 수집(BackfillTeamStandingsAsync)이 공유한다.
    /// </summary>
    private static List<TeamStanding> ParseStandingsRows(HtmlNodeCollection rows)
    {
        var results = new List<TeamStanding>();
        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 8) continue;

            results.Add(new TeamStanding(
                Rank: ParseInt(cells[0].InnerText) ?? 0,
                TeamName: cells[1].InnerText.Trim(),
                Games: ParseInt(cells[2].InnerText),
                Wins: ParseInt(cells[3].InnerText),
                Losses: ParseInt(cells[4].InnerText),
                Draws: ParseInt(cells[5].InnerText),
                WinRate: ParseDouble(cells[6].InnerText),
                GamesBehind: cells[7].InnerText.Trim()
            ));
        }

        return results;
    }

    /// <summary>
    /// 팀 순위(TeamRankDaily)에는 승/패/승률만 있고, 팀 타율·출루율·방어율·피안타율은
    /// 별도의 팀 기록 페이지(Record/Team/Hitter, Pitcher)에만 있어서 팀명으로 병합한다.
    /// </summary>
    private async Task<List<TeamStanding>> MergeTeamExtraStatsAsync(List<TeamStanding> standings, CancellationToken ct)
    {
        var avgByTeam = await CollectTeamStatAsync(TeamHitterUrl1, "HRA_RT", ct);
        var obpByTeam = await CollectTeamStatAsync(TeamHitterUrl2, "OBP_RT", ct);
        var eraByTeam = await CollectTeamStatAsync(TeamPitcherUrl1, "ERA_RT", ct);
        var oavgByTeam = await CollectTeamStatAsync(TeamPitcherUrl2, "OAVG_RT", ct);

        return standings.Select(s => s with
        {
            Avg = avgByTeam.GetValueOrDefault(s.TeamName),
            Obp = obpByTeam.GetValueOrDefault(s.TeamName),
            Era = eraByTeam.GetValueOrDefault(s.TeamName),
            Oavg = oavgByTeam.GetValueOrDefault(s.TeamName)
        }).ToList();
    }

    private async Task<Dictionary<string, double?>> CollectTeamStatAsync(string url, string dataId, CancellationToken ct)
    {
        var result = new Dictionary<string, double?>();
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("(//table[contains(@class,'tData')])[1]//tbody//tr");
            if (rows == null) return result;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("td");
                if (cells == null || cells.Count < 3) continue;

                var teamName = cells[1].InnerText.Trim();
                result[teamName] = ParseDouble(GetByDataId(row, dataId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to collect KBO team stat from {url} ({dataId}). Error: {msg}", url, dataId, ex.Message);
        }

        return result;
    }

    /// <summary>
    /// 시즌 시작일부터 lastDate(보통 "어제")까지, 팀 순위 페이지의 날짜별 조회 기능(ASP.NET MS AJAX
    /// UpdatePanel 포스트백)을 이용해 실제 과거 승률/순위/게임차 이력을 하루씩 소급 수집한다.
    /// 팀타율/출루율/방어율/피안타율은 이 페이지에 없으므로 대상에서 제외되고(NULL로 저장), 오늘 날짜는
    /// 실시간 틱이 이미 완전한 값으로 채우므로 절대 포함하지 않는다(호출하는 쪽에서 lastDate를 어제로 넘길 것).
    /// </summary>
    public async Task BackfillTeamStandingsAsync(string databasePath, DateOnly seasonStart, DateOnly lastDate, CancellationToken ct)
    {
        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        string initialHtml;
        try
        {
            initialHtml = await client.GetStringAsync(TeamRankUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[KBO 공식] 팀순위 소급 — 초기 접속 실패로 중단. Error: {msg}", ex.Message);
            return;
        }

        var state = ExtractPostbackState(initialHtml);
        if (state == null)
        {
            _logger.LogWarning("[KBO 공식] 팀순위 소급 — 포스트백 토큰(VIEWSTATE 등)을 찾지 못해 중단합니다.");
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        var totalDays = lastDate.DayNumber - seasonStart.DayNumber + 1;
        var saved = 0;
        var skipped = 0;

        for (var date = seasonStart; date <= lastDate; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            AjaxDeltaResult? delta;
            try
            {
                delta = await PostDateToTeamRankAsync(client, date.ToString("yyyyMMdd"), state, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[KBO 공식] 팀순위 소급 — {date} 요청 실패, 건너뜀. Error: {msg}", date, ex.Message);
                continue;
            }

            if (delta?.UpdatePanelHtml == null)
            {
                _logger.LogWarning("[KBO 공식] 팀순위 소급 — {date} 응답이 비어있어 건너뜀", date);
                continue;
            }

            // 다음 요청에 쓸 토큰 갱신 (응답에 없으면 이전 값 유지)
            state = new PostbackState(
                delta.ViewState ?? state.ViewState,
                delta.ViewStateGenerator ?? state.ViewStateGenerator,
                delta.EventValidation ?? state.EventValidation);

            var fragmentDoc = new HtmlDocument();
            fragmentDoc.LoadHtml(delta.UpdatePanelHtml);
            var rows = fragmentDoc.DocumentNode.SelectNodes("(//table[contains(@class,'tData')])[1]//tbody//tr")
                       ?? fragmentDoc.DocumentNode.SelectNodes("//table[contains(@class,'tData')]//tbody//tr");
            var standings = rows == null ? new List<TeamStanding>() : ParseStandingsRows(rows);
            if (standings.Count == 0)
            {
                _logger.LogWarning("[KBO 공식] 팀순위 소급 — {date} 표를 찾지 못해 건너뜀", date);
                continue;
            }

            // 서버가 실제로 스냅한 날짜(경기 없는 날 요청 시 가장 가까운 실제 경기일로 이동됨)를 키로 사용
            var actualDate = ExtractSearchDateTitle(delta.UpdatePanelHtml) ?? date;
            var actualDateStr = actualDate.ToString("yyyy-MM-dd");

            var existsCmd = connection.CreateCommand();
            existsCmd.CommandText = "SELECT COUNT(*) FROM KboTeamStandings WHERE date(CollectedAt, '+9 hours') = $date;";
            existsCmd.Parameters.AddWithValue("$date", actualDateStr);
            var alreadyExists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct)) > 0;
            if (alreadyExists)
            {
                skipped++;
                continue;
            }

            await SaveStandingsAsync(connection, standings, $"{actualDateStr} 05:00:00", ct);
            saved++;
            _logger.LogInformation("[KBO 공식] 팀순위 소급 — {date} 저장 완료 ({saved}/{total}, 스킵 {skipped})", actualDateStr, saved, totalDays, skipped);

            await Task.Delay(250, ct);
        }

        _logger.LogInformation("[KBO 공식] 팀순위 소급 완료 — 신규 저장 {saved}건, 스킵 {skipped}건", saved, skipped);
    }

    /// <summary>
    /// 팀타율/출루율/방어율/피안타율은 KBO 사이트에 날짜별 조회가 없어서, 이미 수집해 둔 박스스코어
    /// (KboBoxScoreBatting/KboBoxScorePitching, --backfill-season으로 시즌 전체 수집됨)를 날짜순으로
    /// 누적 집계해 그 날짜 시점의 팀 스탯을 직접 재구성한다. HTTP 요청이 전혀 없는 순수 로컬 계산이라
    /// 결과가 결정적이며, 재실행해도 항상 같은 값으로 갱신되는 멱등 연산이다(별도 중복 방지 불필요).
    /// 오늘(KST) 날짜는 lastDate를 "어제"로 넘기는 호출 쪽 계약으로 자연스럽게 제외된다(라이브 틱이 이미 채움).
    /// </summary>
    public async Task BackfillTeamExtraStatsAsync(string databasePath, DateOnly seasonStart, DateOnly lastDate, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        var lastDateStr = lastDate.ToString("yyyy-MM-dd");
        var seasonStartStr = seasonStart.ToString("yyyy-MM-dd");

        var battingByGame = new Dictionary<(string GameId, string Team), (int AtBats, int Hits, int Walks, int Hbp, int SacFly)>();
        var gameDates = new Dictionary<string, string>();

        var battingCmd = connection.CreateCommand();
        battingCmd.CommandText = """
            SELECT GameId, GameDate, Team,
                   COALESCE(SUM(AtBats),0), COALESCE(SUM(Hits),0), COALESCE(SUM(Walks),0), COALESCE(SUM(Hbp),0), COALESCE(SUM(SacFly),0)
            FROM KboBoxScoreBatting
            WHERE GameDate <= $lastDate
            GROUP BY GameId, Team;
            """;
        battingCmd.Parameters.AddWithValue("$lastDate", lastDateStr);
        await using (var reader = await battingCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var gameId = reader.GetString(0);
                var gameDate = reader.GetString(1);
                var team = reader.GetString(2);
                battingByGame[(gameId, team)] = (reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
                gameDates[gameId] = gameDate;
            }
        }

        var pitchingByGame = new Dictionary<(string GameId, string Team), (int EarnedRuns, int Hits, int Outs)>();
        var pitchingCmd = connection.CreateCommand();
        pitchingCmd.CommandText = """
            SELECT GameId, Team, EarnedRuns, Hits, InningsPitched
            FROM KboBoxScorePitching
            WHERE GameDate <= $lastDate;
            """;
        pitchingCmd.Parameters.AddWithValue("$lastDate", lastDateStr);
        await using (var reader = await pitchingCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var gameId = reader.GetString(0);
                var team = reader.GetString(1);
                var earnedRuns = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var hitsAllowed = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                var outs = ParseInningsToOuts(reader.IsDBNull(4) ? null : reader.GetString(4));

                var key = (gameId, team);
                var prev = pitchingByGame.GetValueOrDefault(key);
                pitchingByGame[key] = (prev.EarnedRuns + earnedRuns, prev.Hits + hitsAllowed, prev.Outs + outs);
            }
        }

        var gamesByDate = gameDates
            .GroupBy(kv => kv.Value)
            .Where(g => string.Compare(g.Key, seasonStartStr, StringComparison.Ordinal) >= 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Date: g.Key, GameIds: g.Select(x => x.Key).ToList()))
            .ToList();

        var cumBatting = new Dictionary<string, (long AtBats, long Hits, long Walks, long Hbp, long SacFly)>();
        var cumPitching = new Dictionary<string, (long EarnedRuns, long HitsAllowed, long Outs, long OppAtBats)>();
        var updated = 0;

        foreach (var (date, gameIds) in gamesByDate)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var gameId in gameIds)
            {
                var teamsInGame = battingByGame.Keys.Where(k => k.GameId == gameId).Select(k => k.Team).Distinct().ToList();

                foreach (var team in teamsInGame)
                {
                    var (atBats, hits, walks, hbp, sacFly) = battingByGame.GetValueOrDefault((gameId, team));
                    var prevBat = cumBatting.GetValueOrDefault(team);
                    cumBatting[team] = (prevBat.AtBats + atBats, prevBat.Hits + hits, prevBat.Walks + walks, prevBat.Hbp + hbp, prevBat.SacFly + sacFly);
                }

                foreach (var team in teamsInGame)
                {
                    var opponent = teamsInGame.FirstOrDefault(t => t != team);
                    if (opponent == null) continue;

                    var (earnedRuns, hitsAllowed, outs) = pitchingByGame.GetValueOrDefault((gameId, team));
                    var (oppAtBats, _, _, _, _) = battingByGame.GetValueOrDefault((gameId, opponent));
                    var prevPitch = cumPitching.GetValueOrDefault(team);
                    cumPitching[team] = (prevPitch.EarnedRuns + earnedRuns, prevPitch.HitsAllowed + hitsAllowed, prevPitch.Outs + outs, prevPitch.OppAtBats + oppAtBats);
                }
            }

            foreach (var team in cumBatting.Keys.Union(cumPitching.Keys).Distinct())
            {
                var bat = cumBatting.GetValueOrDefault(team);
                var pit = cumPitching.GetValueOrDefault(team);

                double? avg = bat.AtBats > 0 ? Math.Round((double)bat.Hits / bat.AtBats, 3) : null;
                var obpDenominator = bat.AtBats + bat.Walks + bat.Hbp + bat.SacFly;
                double? obp = obpDenominator > 0 ? Math.Round((double)(bat.Hits + bat.Walks + bat.Hbp) / obpDenominator, 3) : null;
                double? era = pit.Outs > 0 ? Math.Round(9.0 * pit.EarnedRuns / (pit.Outs / 3.0), 2) : null;
                double? oavg = pit.OppAtBats > 0 ? Math.Round((double)pit.HitsAllowed / pit.OppAtBats, 3) : null;

                var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE KboTeamStandings
                    SET Avg = $avg, Obp = $obp, Era = $era, Oavg = $oavg
                    WHERE TeamName = $team AND date(CollectedAt, '+9 hours') = $date;
                    """;
                updateCmd.Parameters.AddWithValue("$avg", (object?)avg ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$obp", (object?)obp ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$era", (object?)era ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$oavg", (object?)oavg ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$team", team);
                updateCmd.Parameters.AddWithValue("$date", date);
                updated += await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        _logger.LogInformation("[KBO 공식] 팀 스탯(타율/출루율/방어율/피안타율) 소급 완료 — {updated}건 갱신", updated);
    }

    /// <summary>
    /// 선수(타자/투수) 홈런 등 개인 기록도 KboBatterStats/KboPitcherStats에 날짜별 스냅샷 행으로 소급 채운다.
    /// 팀순위 소급과 동일하게 CollectedAt을 그 날짜 05:00:00으로 오버라이드해서 저장하고, 라이브 틱이 이미
    /// 채운 날짜(2026-07-05 이후)는 스킵한다. 승/패/세이브는 박스스코어에 판정 텍스트가 저장되어 있지 않아
    /// 소급 불가능 — 그 컬럼들은 NULL로 남기고 요청받은 홈런과, 계산 가능한 타율/출루율/방어율/탈삼진만 채운다.
    /// </summary>
    public async Task BackfillPlayerHomeRunHistoryAsync(string databasePath, DateOnly seasonStart, DateOnly lastDate, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        var lastDateStr = lastDate.ToString("yyyy-MM-dd");
        var seasonStartStr = seasonStart.ToString("yyyy-MM-dd");

        var battingRows = new List<BoxScoreBatting>();
        var battingCmd = connection.CreateCommand();
        battingCmd.CommandText = """
            SELECT GameDate, PlayerName, Team, AtBats, Hits, HomeRuns, Rbi, Walks, Hbp, SacFly, Runs, Doubles, Triples, Strikeouts, StolenBases
            FROM KboBoxScoreBatting
            WHERE GameDate <= $lastDate
            ORDER BY GameDate;
            """;
        battingCmd.Parameters.AddWithValue("$lastDate", lastDateStr);
        await using (var reader = await battingCmd.ExecuteReaderAsync(ct))
        {
            int I(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            while (await reader.ReadAsync(ct))
            {
                battingRows.Add(new BoxScoreBatting(
                    GameId: "", GameDate: reader.GetString(0), Team: reader.GetString(2), PlayerName: reader.GetString(1),
                    AtBats: I(3), Hits: I(4), HomeRuns: I(5), Rbi: I(6), Walks: I(7), Hbp: I(8), SacFly: I(9),
                    Runs: I(10), Doubles: I(11), Triples: I(12), Strikeouts: I(13), StolenBases: I(14)));
            }
        }

        var pitchingRows = new List<(string GameDate, string PlayerName, string Team, int EarnedRuns, int Hits, int HomeRuns, int Strikeouts, int Outs, int Runs, string? Decision)>();
        var pitchingCmd = connection.CreateCommand();
        pitchingCmd.CommandText = """
            SELECT GameDate, PlayerName, Team, EarnedRuns, Hits, HomeRuns, Strikeouts, InningsPitched, Runs, Decision
            FROM KboBoxScorePitching
            WHERE GameDate <= $lastDate
            ORDER BY GameDate;
            """;
        pitchingCmd.Parameters.AddWithValue("$lastDate", lastDateStr);
        await using (var reader = await pitchingCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                pitchingRows.Add((
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    ParseInningsToOuts(reader.IsDBNull(7) ? null : reader.GetString(7)),
                    reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        var battingByDate = battingRows.GroupBy(r => r.GameDate).ToDictionary(g => g.Key, g => g.ToList());
        var pitchingByDate = pitchingRows.GroupBy(r => r.GameDate).ToDictionary(g => g.Key, g => g.ToList());

        var allDates = battingByDate.Keys.Union(pitchingByDate.Keys)
            .Where(d => string.Compare(d, seasonStartStr, StringComparison.Ordinal) >= 0)
            .Distinct()
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        var cumBatters = new Dictionary<(string Name, string Team), BatterCumulative>();
        var cumPitchers = new Dictionary<(string Name, string Team), PitcherCumulative>();

        var savedDates = 0;
        var skippedDates = 0;

        await using var transaction = connection.BeginTransaction();

        foreach (var date in allDates)
        {
            ct.ThrowIfCancellationRequested();

            if (battingByDate.TryGetValue(date, out var todaysBatting))
            {
                foreach (var row in todaysBatting)
                {
                    var key = (row.PlayerName, row.Team);
                    var prev = cumBatters.GetValueOrDefault(key);
                    cumBatters[key] = new BatterCumulative(
                        prev.Games + 1, prev.AtBats + (row.AtBats ?? 0), prev.Hits + (row.Hits ?? 0), prev.HomeRuns + (row.HomeRuns ?? 0),
                        prev.Rbi + (row.Rbi ?? 0), prev.Walks + (row.Walks ?? 0), prev.Hbp + (row.Hbp ?? 0), prev.SacFly + (row.SacFly ?? 0),
                        prev.Runs + (row.Runs ?? 0), prev.Doubles + (row.Doubles ?? 0), prev.Triples + (row.Triples ?? 0),
                        prev.Strikeouts + (row.Strikeouts ?? 0), prev.StolenBases + (row.StolenBases ?? 0));
                }
            }

            if (pitchingByDate.TryGetValue(date, out var todaysPitching))
            {
                foreach (var row in todaysPitching)
                {
                    var key = (row.PlayerName, row.Team);
                    var prev = cumPitchers.GetValueOrDefault(key);
                    cumPitchers[key] = new PitcherCumulative(
                        prev.Games + 1,
                        prev.EarnedRuns + row.EarnedRuns, prev.HitsAllowed + row.Hits, prev.HomeRuns + row.HomeRuns,
                        prev.Strikeouts + row.Strikeouts, prev.Outs + row.Outs, prev.Runs + row.Runs,
                        prev.Wins + (row.Decision == "승" ? 1 : 0),
                        prev.Losses + (row.Decision == "패" ? 1 : 0),
                        prev.Saves + (row.Decision == "세" ? 1 : 0),
                        prev.Holds + (row.Decision == "홀드" ? 1 : 0));
                }
            }

            var existsCmd = connection.CreateCommand();
            existsCmd.Transaction = transaction;
            existsCmd.CommandText = "SELECT COUNT(*) FROM KboBatterStats WHERE date(CollectedAt, '+9 hours') = $date;";
            existsCmd.Parameters.AddWithValue("$date", date);
            if (Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct)) > 0)
            {
                skippedDates++;
                continue;
            }

            var collectedAt = $"{date} 05:00:00";

            foreach (var ((name, team), stat) in cumBatters)
            {
                if (stat.AtBats == 0 && stat.HomeRuns == 0 && stat.Hits == 0) continue;

                double? avg = stat.AtBats > 0 ? Math.Round((double)stat.Hits / stat.AtBats, 3) : null;
                var obpDenominator = stat.AtBats + stat.Walks + stat.Hbp + stat.SacFly;
                double? obp = obpDenominator > 0 ? Math.Round((double)(stat.Hits + stat.Walks + stat.Hbp) / obpDenominator, 3) : null;

                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO KboBatterStats (PlayerName, Team, Avg, Games, Hits, HomeRuns, Rbi, Obp, AtBats, Runs, Doubles, Triples, StolenBases, Walks, Hbp, Strikeouts, CollectedAt)
                    VALUES ($name, $team, $avg, $games, $hits, $hr, $rbi, $obp, $atBats, $runs, $doubles, $triples, $stolenBases, $walks, $hbp, $strikeouts, $collectedAt);
                    """;
                insertCmd.Parameters.AddWithValue("$name", name);
                insertCmd.Parameters.AddWithValue("$team", team);
                insertCmd.Parameters.AddWithValue("$avg", (object?)avg ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$games", stat.Games);
                insertCmd.Parameters.AddWithValue("$hits", stat.Hits);
                insertCmd.Parameters.AddWithValue("$hr", stat.HomeRuns);
                insertCmd.Parameters.AddWithValue("$rbi", stat.Rbi);
                insertCmd.Parameters.AddWithValue("$obp", (object?)obp ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$atBats", stat.AtBats);
                insertCmd.Parameters.AddWithValue("$runs", stat.Runs);
                insertCmd.Parameters.AddWithValue("$doubles", stat.Doubles);
                insertCmd.Parameters.AddWithValue("$triples", stat.Triples);
                insertCmd.Parameters.AddWithValue("$stolenBases", stat.StolenBases);
                insertCmd.Parameters.AddWithValue("$walks", stat.Walks);
                insertCmd.Parameters.AddWithValue("$hbp", stat.Hbp);
                insertCmd.Parameters.AddWithValue("$strikeouts", stat.Strikeouts);
                insertCmd.Parameters.AddWithValue("$collectedAt", collectedAt);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var ((name, team), stat) in cumPitchers)
            {
                if (stat.Outs == 0 && stat.HomeRuns == 0) continue;

                double? era = stat.Outs > 0 ? Math.Round(9.0 * stat.EarnedRuns / (stat.Outs / 3.0), 2) : null;
                var innings = OutsToInningsString(stat.Outs);
                var decisions = stat.Wins + stat.Losses;
                double? winRate = decisions > 0 ? Math.Round((double)stat.Wins / decisions, 3) : null;

                // 볼넷/사구는 박스스코어에 "4사구"로 합산돼 있어 분리 소급이 불가능 — NULL로 남기고 라이브 수집만 채운다
                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO KboPitcherStats (PlayerName, Team, Era, Innings, Strikeouts, HomeRuns,
                                                 Games, Wins, Losses, Saves, Holds, HitsAllowed, RunsAllowed, EarnedRuns, WinRate, InningsDecimal, CollectedAt)
                    VALUES ($name, $team, $era, $innings, $so, $hr,
                            $games, $wins, $losses, $saves, $holds, $hitsAllowed, $runsAllowed, $earnedRuns, $winRate, $inningsDecimal, $collectedAt);
                    """;
                insertCmd.Parameters.AddWithValue("$name", name);
                insertCmd.Parameters.AddWithValue("$team", team);
                insertCmd.Parameters.AddWithValue("$era", (object?)era ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$innings", innings);
                insertCmd.Parameters.AddWithValue("$so", stat.Strikeouts);
                insertCmd.Parameters.AddWithValue("$hr", stat.HomeRuns);
                insertCmd.Parameters.AddWithValue("$games", stat.Games);
                insertCmd.Parameters.AddWithValue("$wins", stat.Wins);
                insertCmd.Parameters.AddWithValue("$losses", stat.Losses);
                insertCmd.Parameters.AddWithValue("$saves", stat.Saves);
                insertCmd.Parameters.AddWithValue("$holds", stat.Holds);
                insertCmd.Parameters.AddWithValue("$hitsAllowed", stat.HitsAllowed);
                insertCmd.Parameters.AddWithValue("$runsAllowed", stat.Runs);
                insertCmd.Parameters.AddWithValue("$earnedRuns", stat.EarnedRuns);
                insertCmd.Parameters.AddWithValue("$winRate", (object?)winRate ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$inningsDecimal", Math.Round(stat.Outs / 3.0, 2));
                insertCmd.Parameters.AddWithValue("$collectedAt", collectedAt);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            savedDates++;
        }

        await transaction.CommitAsync(ct);

        _logger.LogInformation("[KBO 공식] 선수 기록(홈런 등) 소급 완료 — {saved}개 날짜 저장, {skipped}개 날짜 스킵", savedDates, skippedDates);
    }

    private static string OutsToInningsString(long outs)
    {
        var whole = outs / 3;
        var remainder = outs % 3;
        return remainder == 0 ? whole.ToString(CultureInfo.InvariantCulture) : $"{whole} {remainder}/3";
    }

    private static PostbackState? ExtractPostbackState(string html)
    {
        string? Extract(string id)
        {
            var match = System.Text.RegularExpressions.Regex.Match(html, $"id=\"{id}\"[^>]*value=\"([^\"]*)\"");
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        var viewState = Extract("__VIEWSTATE");
        var viewStateGenerator = Extract("__VIEWSTATEGENERATOR");
        var eventValidation = Extract("__EVENTVALIDATION");

        return viewState is null || viewStateGenerator is null || eventValidation is null
            ? null
            : new PostbackState(viewState, viewStateGenerator, eventValidation);
    }

    private async Task<AjaxDeltaResult?> PostDateToTeamRankAsync(HttpClient client, string targetDateYyyyMMdd, PostbackState state, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TeamRankUrl);
        request.Headers.Referrer = new Uri(TeamRankUrl);
        request.Headers.Add("X-MicrosoftAjax", "Delta=true");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__EVENTTARGET"] = "ctl00$ctl00$ctl00$cphContents$cphContents$cphContents$btnCalendarSelect",
            ["__EVENTARGUMENT"] = "",
            ["__VIEWSTATE"] = state.ViewState,
            ["__VIEWSTATEGENERATOR"] = state.ViewStateGenerator,
            ["__EVENTVALIDATION"] = state.EventValidation,
            ["ctl00$ctl00$ctl00$cphContents$cphContents$cphContents$ScriptManager"] =
                "cphContents_cphContents_cphContents_udpRecord|cphContents_cphContents_cphContents_btnCalendarSelect",
            ["ctl00$ctl00$ctl00$cphContents$cphContents$cphContents$hfSearchDate"] = targetDateYyyyMMdd,
            ["__ASYNCPOST"] = "true"
        });

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("KBO team rank postback returned {status} for date {date}", response.StatusCode, targetDateYyyyMMdd);
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return ParseAjaxDelta(text);
    }

    /// <summary>
    /// MS AJAX UpdatePanel의 "delta" 응답(파이프 구분, length|type|id|content| 반복)을 파싱한다.
    /// length는 content만의 문자 길이라, content 안에 '|'가 들어있어도 안전하게 잘라낼 수 있다.
    /// </summary>
    private static AjaxDeltaResult ParseAjaxDelta(string response)
    {
        string? updatePanelHtml = null;
        string? viewState = null;
        string? viewStateGenerator = null;
        string? eventValidation = null;

        var pos = 0;
        while (pos < response.Length)
        {
            var lenPipe = response.IndexOf('|', pos);
            if (lenPipe < 0) break;

            var lengthStr = response.Substring(pos, lenPipe - pos);
            if (!int.TryParse(lengthStr, out var length))
            {
                pos = lenPipe + 1;
                continue;
            }

            var typeStart = lenPipe + 1;
            var typePipe = response.IndexOf('|', typeStart);
            if (typePipe < 0) break;
            var type = response.Substring(typeStart, typePipe - typeStart);

            var idStart = typePipe + 1;
            var idPipe = response.IndexOf('|', idStart);
            if (idPipe < 0) break;
            var id = response.Substring(idStart, idPipe - idStart);

            var contentStart = idPipe + 1;
            if (contentStart + length > response.Length) break;
            var content = response.Substring(contentStart, length);

            if (type == "updatePanel" && id.EndsWith("udpRecord", StringComparison.Ordinal))
            {
                updatePanelHtml = content;
            }
            else if (type == "hiddenField")
            {
                switch (id)
                {
                    case "__VIEWSTATE": viewState = content; break;
                    case "__VIEWSTATEGENERATOR": viewStateGenerator = content; break;
                    case "__EVENTVALIDATION": eventValidation = content; break;
                }
            }

            pos = contentStart + length + 1;
        }

        return new AjaxDeltaResult(updatePanelHtml, viewState, viewStateGenerator, eventValidation);
    }

    private static DateOnly? ExtractSearchDateTitle(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, @"lblSearchDateTitle""[^>]*>(\d{4})\.(\d{2})\.(\d{2})<");
        if (!match.Success) return null;

        return new DateOnly(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private Task<List<GameResult>> CollectGameResultsAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        return CollectGameResultsAsync(now.Year, now.Month, ct);
    }

    private async Task<List<GameResult>> CollectGameResultsAsync(int seasonYear, int gameMonth, CancellationToken ct)
    {
        var results = new List<GameResult>();
        try
        {
            // 세션 쿠키 확보를 위해 일정 페이지를 먼저 GET (HttpClient는 기본적으로 쿠키를 유지함)
            await _http.GetStringAsync(ScheduleUrl, ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, ScheduleListApiUrl);
            request.Headers.Referrer = new Uri(ScheduleUrl);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["leId"] = "1",
                ["srIdList"] = "0,9,6",
                ["seasonId"] = seasonYear.ToString(CultureInfo.InvariantCulture),
                ["gameMonth"] = gameMonth.ToString("D2", CultureInfo.InvariantCulture),
                ["teamId"] = ""
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("KBO schedule API returned {status}", response.StatusCode);
                return results;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rows", out var rowsElement)) return results;

            string? currentDate = null;
            foreach (var rowWrapper in rowsElement.EnumerateArray())
            {
                if (!rowWrapper.TryGetProperty("row", out var cellsElement)) continue;
                var cells = cellsElement.EnumerateArray().ToList();

                var dayCell = cells.FirstOrDefault(c => c.GetProperty("Class").GetString() == "day");
                if (dayCell.ValueKind != JsonValueKind.Undefined)
                {
                    currentDate = NormalizeGameDate(dayCell.GetProperty("Text").GetString(), seasonYear);
                }

                var timeCell = cells.FirstOrDefault(c => c.GetProperty("Class").GetString() == "time");
                var playCell = cells.FirstOrDefault(c => c.GetProperty("Class").GetString() == "play");
                var relayCell = cells.FirstOrDefault(c => c.GetProperty("Class").GetString() == "relay");
                if (currentDate == null || playCell.ValueKind == JsonValueKind.Undefined) continue;

                var parsed = ParsePlayCell(playCell.GetProperty("Text").GetString() ?? "");
                if (parsed == null) continue;

                var timeText = timeCell.ValueKind != JsonValueKind.Undefined
                    ? StripHtml(timeCell.GetProperty("Text").GetString() ?? "")
                    : null;

                var gameId = relayCell.ValueKind != JsonValueKind.Undefined
                    ? ExtractGameId(relayCell.GetProperty("Text").GetString())
                    : null;

                results.Add(parsed with { GameDate = currentDate, GameTime = timeText, GameId = gameId });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to collect KBO game results. Error: {msg}", ex.Message);
        }

        return results;
    }

    private static GameResult? ParsePlayCell(string cellHtml)
    {
        if (string.IsNullOrWhiteSpace(cellHtml)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(cellHtml);
        var spans = doc.DocumentNode.SelectNodes("//span");
        if (spans == null || spans.Count < 2) return null;

        var awayTeam = spans[0].InnerText.Trim();
        var homeTeam = spans[^1].InnerText.Trim();

        int? awayScore = null;
        int? homeScore = null;
        if (spans.Count >= 5)
        {
            awayScore = ParseInt(spans[1].InnerText);
            homeScore = ParseInt(spans[3].InnerText);
        }

        if (string.IsNullOrWhiteSpace(awayTeam) || string.IsNullOrWhiteSpace(homeTeam)) return null;

        return new GameResult(string.Empty, null, awayTeam, awayScore, homeTeam, homeScore);
    }

    private static string? ExtractGameId(string? relayCellHtml)
    {
        if (string.IsNullOrWhiteSpace(relayCellHtml)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(relayCellHtml, "gameId=([A-Za-z0-9]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 아직 박스스코어를 수집하지 않은, 스코어가 확정된(=종료된) 경기만 골라 개인 기록을 수집/저장한다.
    /// (경기별 박스스코어는 한 번 끝나면 값이 바뀌지 않으므로 중복 수집을 피하기 위해 존재 여부를 먼저 확인한다.)
    /// </summary>
    public async Task CollectAndSaveBoxScoresAsync(string databasePath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);

        var pendingCmd = connection.CreateCommand();
        pendingCmd.CommandText =
            """
            SELECT GameId, GameDate, AwayTeam, HomeTeam
            FROM KboGameResults
            WHERE GameId IS NOT NULL AND AwayScore IS NOT NULL AND HomeScore IS NOT NULL
              AND GameId NOT IN (SELECT DISTINCT GameId FROM KboBoxScoreBatting)
            ORDER BY GameDate;
            """;

        var pending = new List<(string GameId, string GameDate, string AwayTeam, string HomeTeam)>();
        await using (var reader = await pendingCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        if (pending.Count == 0) return;

        var collected = 0;
        foreach (var game in pending)
        {
            var (batting, pitching) = await CollectBoxScoreAsync(game.GameId, game.GameDate, game.AwayTeam, game.HomeTeam, ct);
            if (batting.Count == 0 && pitching.Count == 0) continue;

            await SaveBoxScoreBattingAsync(connection, batting, ct);
            await SaveBoxScorePitchingAsync(connection, pitching, ct);
            collected++;
        }

        if (collected > 0)
        {
            _logger.LogInformation("[KBO 공식] 박스스코어 신규 수집 완료 — {count}경기", collected);
        }
    }

    private async Task<(List<BoxScoreBatting> Batting, List<BoxScorePitching> Pitching)> CollectBoxScoreAsync(
        string gameId, string gameDate, string awayTeam, string homeTeam, CancellationToken ct)
    {
        var batting = new List<BoxScoreBatting>();
        var pitching = new List<BoxScorePitching>();
        try
        {
            var seasonId = gameId.Length >= 4 ? gameId[..4] : DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);

            using var request = new HttpRequestMessage(HttpMethod.Post, BoxScoreApiUrl);
            request.Headers.Referrer = new Uri(ScheduleUrl);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["leId"] = "1",
                ["srId"] = "0",
                ["seasonId"] = seasonId,
                ["gameId"] = gameId
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("KBO box score API returned {status} for {gameId}", response.StatusCode, gameId);
                return (batting, pitching);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // 홈런/2루타/3루타/도루는 타자별 스탯 표(table3)에 컬럼이 없고, tableEtc의 요약 줄에서만 선수명으로 확인 가능
            var homeRunHitters = ExtractHomeRunHitters(doc.RootElement);
            var doubleHitters = ExtractNamedCountRow(doc.RootElement, "2루타");
            var tripleHitters = ExtractNamedCountRow(doc.RootElement, "3루타");
            var baseStealers = ExtractNamedCountRow(doc.RootElement, "도루");
            var teams = new[] { awayTeam, homeTeam };

            if (doc.RootElement.TryGetProperty("arrHitter", out var arrHitterEl))
            {
                var idx = 0;
                foreach (var teamEl in arrHitterEl.EnumerateArray())
                {
                    if (idx >= teams.Length) break;
                    ParseHitterTeam(teamEl, gameId, gameDate, teams[idx], homeRunHitters, doubleHitters, tripleHitters, baseStealers, batting);
                    idx++;
                }
            }

            if (doc.RootElement.TryGetProperty("arrPitcher", out var arrPitcherEl))
            {
                var idx = 0;
                foreach (var teamEl in arrPitcherEl.EnumerateArray())
                {
                    if (idx >= teams.Length) break;
                    ParsePitcherTeam(teamEl, gameId, gameDate, teams[idx], pitching);
                    idx++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to collect KBO box score for {gameId}. Error: {msg}", gameId, ex.Message);
        }

        return (batting, pitching);
    }

    private static List<string> ExtractHomeRunHitters(JsonElement root)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("tableEtc", out var tableEtcEl)) return names;

        var tableEtcJson = tableEtcEl.GetString();
        if (string.IsNullOrWhiteSpace(tableEtcJson)) return names;

        var rows = ParseGridRows(tableEtcJson);
        var hrRow = rows.FirstOrDefault(r => r.Count >= 2 && r[0].Trim() == "홈런");
        if (hrRow == null) return names;

        // 한 경기 2홈런 이상은 "김도영7호8호(7회1점 8회2점 …)"처럼 호수가 연달아 붙는 단일 토큰으로 표기된다.
        // (?:\d+호)+로 호수 뭉치를 통째로 잡고 그 안의 호수 개수만큼 이름을 반복해 넣어야 멀티홈런이 누락되지 않는다.
        // 이름 그룹이 [가-힣]+라 "…7호8호(" 중간에서 "호"만 이름으로 오인 매칭되던 문제도 함께 해결된다.
        var matches = System.Text.RegularExpressions.Regex.Matches(hrRow[1], @"([가-힣]+)((?:\d+호)+)\(");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var homeRunCount = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"\d+호").Count;
            for (var i = 0; i < homeRunCount; i++)
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// tableEtc의 "2루타"/"3루타"/"도루" 요약 줄을 선수명 리스트로 변환한다 (개수만큼 이름 반복).
    /// 표기: 단건은 "양의지(3회)", 한 경기 여러 개는 "박승규2(1 7회)"처럼 이름 뒤에 개수가 붙는다.
    /// 홈런 줄만 호수 표기("김도영7호8호(…)")라 형식이 달라서 ExtractHomeRunHitters로 따로 처리한다.
    /// </summary>
    private static List<string> ExtractNamedCountRow(JsonElement root, string label)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("tableEtc", out var tableEtcEl)) return names;

        var tableEtcJson = tableEtcEl.GetString();
        if (string.IsNullOrWhiteSpace(tableEtcJson)) return names;

        var rows = ParseGridRows(tableEtcJson);
        var targetRow = rows.FirstOrDefault(r => r.Count >= 2 && r[0].Trim() == label);
        if (targetRow == null) return names;

        var matches = System.Text.RegularExpressions.Regex.Matches(targetRow[1], @"([가-힣]+)(\d*)\(");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var count = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            for (var i = 0; i < count; i++)
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    // 팀 출루율 소급 계산에 필요한 사사구/희생플라이는 집계 컬럼이 따로 없고, 타석별 결과 텍스트(table2)에서만
    // "4구"(볼넷)/"고4"(고의4구)/"사구"(몸에 맞는 공)/"희비" 포함 텍스트(희생플라이)로 확인 가능하다.
    private static readonly HashSet<string> WalkTokens = ["4구", "고4"];

    private static void ParseHitterTeam(JsonElement teamEl, string gameId, string gameDate, string team,
        List<string> homeRunHitters, List<string> doubleHitters, List<string> tripleHitters, List<string> baseStealers,
        List<BoxScoreBatting> output)
    {
        if (!teamEl.TryGetProperty("table1", out var idEl) || !teamEl.TryGetProperty("table3", out var statEl)) return;

        var identityRows = ParseGridRows(idEl.GetString() ?? "");
        var statRows = ParseGridRows(statEl.GetString() ?? "");
        var outcomeRows = teamEl.TryGetProperty("table2", out var outcomeEl)
            ? ParseGridRows(outcomeEl.GetString() ?? "")
            : new List<List<string>>();

        var count = Math.Min(identityRows.Count, statRows.Count);
        for (var i = 0; i < count; i++)
        {
            var idRow = identityRows[i];
            var statRow = statRows[i];
            if (idRow.Count < 3 || statRow.Count < 4) continue;

            var playerName = idRow[2].Trim();
            if (string.IsNullOrWhiteSpace(playerName)) continue;

            // 같은 이닝에 두 번 타석에 서면 한 셀에 "4구<br />/ 우비"처럼 두 결과가 합쳐진다 —
            // 구분자로 쪼갠 뒤 타석 단위로 판정해야 복합 셀의 볼넷/삼진이 누락되지 않는다.
            var outcomes = (i < outcomeRows.Count ? outcomeRows[i] : new List<string>())
                .SelectMany(c => c.Split(["<br />", "<br/>", "<br>", "/"], StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            output.Add(new BoxScoreBatting(
                GameId: gameId,
                GameDate: gameDate,
                Team: team,
                PlayerName: playerName,
                // 주의: table3의 실제 컬럼 순서는 [타수, 안타, 타점, 득점, 타율]이다. 팀 단위 합계로는
                // 타점≈득점이라 구분이 안 되고, 선수별 시즌 합계를 공식 기록(RBI_CN)과 대조해야 확정된다
                // (검증: 4명 표본에서 col[2] 시즌합이 공식 타점과 전원 정확히 일치).
                AtBats: ParseInt(statRow[0]),
                Hits: ParseInt(statRow[1]),
                Rbi: ParseInt(statRow[2]),
                Runs: ParseInt(statRow[3]),
                HomeRuns: homeRunHitters.Count(n => n == playerName),
                Walks: outcomes.Count(t => WalkTokens.Contains(t)),
                Hbp: outcomes.Count(t => t == "사구"),
                SacFly: outcomes.Count(t => t.Contains("희비")),
                Doubles: doubleHitters.Count(n => n == playerName),
                Triples: tripleHitters.Count(n => n == playerName),
                // "삼진"(루킹/헛스윙 공통) + "스낫"/"루낫"(낫아웃 — 폭투 등으로 출루해도 공식 기록은 삼진)
                Strikeouts: outcomes.Count(t => t.Contains("삼진") || t.Contains("낫")),
                StolenBases: baseStealers.Count(n => n == playerName)
            ));
        }
    }

    private static void ParsePitcherTeam(JsonElement teamEl, string gameId, string gameDate, string team, List<BoxScorePitching> output)
    {
        if (!teamEl.TryGetProperty("table", out var tableEl)) return;

        var rows = ParseGridRows(tableEl.GetString() ?? "");
        foreach (var row in rows)
        {
            if (row.Count < 16) continue;

            var playerName = row[0].Trim();
            if (string.IsNullOrWhiteSpace(playerName)) continue;

            var decision = StripHtml(row[2]).Trim();
            output.Add(new BoxScorePitching(
                GameId: gameId,
                GameDate: gameDate,
                Team: team,
                PlayerName: playerName,
                InningsPitched: row[6].Trim(),
                Hits: ParseInt(row[10]),
                HomeRuns: ParseInt(row[11]),
                Walks: ParseInt(row[12]),
                Strikeouts: ParseInt(row[13]),
                Runs: ParseInt(row[14]),
                EarnedRuns: ParseInt(row[15]),
                Decision: string.IsNullOrWhiteSpace(decision) ? null : decision
            ));
        }
    }

    /// <summary>
    /// KBO 박스스코어 API의 표 하나({"headers":[...],"rows":[{"row":[{"Text":...}, ...]}]})를
    /// 셀 텍스트만 뽑아 순서대로 나열한 문자열 리스트의 리스트로 변환한다.
    /// </summary>
    private static List<List<string>> ParseGridRows(string tableJson)
    {
        var result = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(tableJson)) return result;

        using var doc = JsonDocument.Parse(tableJson);
        if (!doc.RootElement.TryGetProperty("rows", out var rowsEl)) return result;

        foreach (var rowWrapper in rowsEl.EnumerateArray())
        {
            if (!rowWrapper.TryGetProperty("row", out var cellsEl)) continue;

            var rowValues = new List<string>();
            foreach (var cell in cellsEl.EnumerateArray())
            {
                rowValues.Add(cell.TryGetProperty("Text", out var textEl) ? textEl.GetString() ?? "" : "");
            }
            result.Add(rowValues);
        }

        return result;
    }

    private static async Task SaveBoxScoreBattingAsync(SqliteConnection connection, List<BoxScoreBatting> rows, CancellationToken ct)
    {
        foreach (var b in rows)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO KboBoxScoreBatting (GameId, GameDate, Team, PlayerName, AtBats, Runs, Hits, Rbi, HomeRuns, Walks, Hbp, SacFly, Doubles, Triples, Strikeouts, StolenBases)
                VALUES ($gameId, $gameDate, $team, $playerName, $atBats, $runs, $hits, $rbi, $homeRuns, $walks, $hbp, $sacFly, $doubles, $triples, $strikeouts, $stolenBases);
                """;
            cmd.Parameters.AddWithValue("$gameId", b.GameId);
            cmd.Parameters.AddWithValue("$gameDate", b.GameDate);
            cmd.Parameters.AddWithValue("$team", b.Team);
            cmd.Parameters.AddWithValue("$playerName", b.PlayerName);
            cmd.Parameters.AddWithValue("$atBats", (object?)b.AtBats ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runs", (object?)b.Runs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hits", (object?)b.Hits ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rbi", (object?)b.Rbi ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$homeRuns", (object?)b.HomeRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$walks", (object?)b.Walks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hbp", (object?)b.Hbp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sacFly", (object?)b.SacFly ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$doubles", (object?)b.Doubles ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$triples", (object?)b.Triples ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$strikeouts", (object?)b.Strikeouts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stolenBases", (object?)b.StolenBases ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SaveBoxScorePitchingAsync(SqliteConnection connection, List<BoxScorePitching> rows, CancellationToken ct)
    {
        foreach (var p in rows)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO KboBoxScorePitching (GameId, GameDate, Team, PlayerName, InningsPitched, Hits, HomeRuns, Walks, Strikeouts, Runs, EarnedRuns, Decision)
                VALUES ($gameId, $gameDate, $team, $playerName, $innings, $hits, $homeRuns, $walks, $strikeouts, $runs, $earnedRuns, $decision);
                """;
            cmd.Parameters.AddWithValue("$gameId", p.GameId);
            cmd.Parameters.AddWithValue("$gameDate", p.GameDate);
            cmd.Parameters.AddWithValue("$team", p.Team);
            cmd.Parameters.AddWithValue("$playerName", p.PlayerName);
            cmd.Parameters.AddWithValue("$innings", (object?)p.InningsPitched ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hits", (object?)p.Hits ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$homeRuns", (object?)p.HomeRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$walks", (object?)p.Walks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$strikeouts", (object?)p.Strikeouts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runs", (object?)p.Runs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$earnedRuns", (object?)p.EarnedRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$decision", (object?)p.Decision ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string StripHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.InnerText.Trim();
    }

    private static string? NormalizeGameDate(string? dayText, int currentYear)
    {
        // "07.01(수)" 형태에서 월/일만 추출해 "yyyy-MM-dd"로 변환
        if (string.IsNullOrWhiteSpace(dayText)) return null;
        var digitsOnly = new string(dayText.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = digitsOnly.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return null;
        return $"{currentYear:D4}-{month:D2}-{day:D2}";
    }

    private async Task<List<PlayerBattingRecord>> CollectHitterStatsAsync(CancellationToken ct)
    {
        var batters = await CollectPlayerStatsAsync(HitterStatsUrl, ParseBatterRow, ct);
        // 출루율/볼넷/사구/삼진은 Basic1이 아닌 Basic2 페이지에만 있어서 별도로 가져와 이름+팀으로 병합한다.
        var extraByPlayer = await CollectPlayerStatsAsync(HitterStatsUrl2, ParseHitterExtraRow, ct);
        var extraLookup = extraByPlayer.ToDictionary(x => (x.PlayerName, x.Team));

        return batters
            .Select(b => extraLookup.TryGetValue((b.PlayerName, b.Team), out var e)
                ? b with { Obp = e.Obp, Walks = e.Walks, Hbp = e.Hbp, Strikeouts = e.Strikeouts }
                : b)
            .ToList();
    }

    private static HitterExtraStat? ParseHitterExtraRow(HtmlNode row)
    {
        var cells = row.SelectNodes("td");
        if (cells == null || cells.Count < 3) return null;

        var playerName = cells[1].SelectSingleNode(".//a")?.InnerText.Trim() ?? cells[1].InnerText.Trim();
        return new HitterExtraStat(
            PlayerName: playerName,
            Team: cells[2].InnerText.Trim(),
            Obp: ParseDouble(GetByDataId(row, "OBP_RT")),
            Walks: ParseInt(GetByDataId(row, "BB_CN")),
            Hbp: ParseInt(GetByDataId(row, "HP_CN")),
            Strikeouts: ParseInt(GetByDataId(row, "KK_CN")));
    }

    private async Task<List<PlayerPitchingRecord>> CollectPitcherStatsAsync(CancellationToken ct)
    {
        var pitchers = await CollectPlayerStatsAsync(PitcherStatsUrl, ParsePitcherRow, ct);
        // 피안타율(OAVG)은 Basic1이 아닌 Basic2 페이지에만 있어서 별도로 가져와 이름+팀으로 병합한다.
        var oavgByPlayer = await CollectPlayerStatsAsync(PitcherStatsUrl2, row => ParseSecondaryStatRow(row, "OAVG_RT"), ct);
        var oavgLookup = oavgByPlayer.ToDictionary(x => (x.PlayerName, x.Team), x => x.Value);

        return pitchers
            .Select(p => oavgLookup.TryGetValue((p.PlayerName, p.Team), out var oavg) ? p with { Oavg = oavg } : p)
            .ToList();
    }

    private static PlayerSecondaryStat? ParseSecondaryStatRow(HtmlNode row, string dataId)
    {
        var cells = row.SelectNodes("td");
        if (cells == null || cells.Count < 3) return null;

        var playerName = cells[1].SelectSingleNode(".//a")?.InnerText.Trim() ?? cells[1].InnerText.Trim();
        return new PlayerSecondaryStat(playerName, cells[2].InnerText.Trim(), ParseDouble(GetByDataId(row, dataId)));
    }

    private async Task<List<T>> CollectPlayerStatsAsync<T>(string url, Func<HtmlNode, T?> rowParser, CancellationToken ct) where T : class
    {
        var results = new List<T>();
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("(//table[contains(@class,'tData01')])[1]//tbody//tr");
            if (rows == null) return results;

            foreach (var row in rows)
            {
                var parsed = rowParser(row);
                if (parsed != null) results.Add(parsed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to collect KBO player stats from {url}. Error: {msg}", url, ex.Message);
        }

        return results;
    }

    private static PlayerBattingRecord? ParseBatterRow(HtmlNode row)
    {
        var cells = row.SelectNodes("td");
        if (cells == null || cells.Count < 3) return null;

        var playerName = cells[1].SelectSingleNode(".//a")?.InnerText.Trim() ?? cells[1].InnerText.Trim();
        return new PlayerBattingRecord(
            PlayerName: playerName,
            Team: cells[2].InnerText.Trim(),
            Avg: ParseDouble(GetByDataId(row, "HRA_RT")),
            Games: ParseInt(GetByDataId(row, "GAME_CN")),
            Hits: ParseInt(GetByDataId(row, "HIT_CN")),
            HomeRuns: ParseInt(GetByDataId(row, "HR_CN")),
            Rbi: ParseInt(GetByDataId(row, "RBI_CN")),
            AtBats: ParseInt(GetByDataId(row, "AB_CN")),
            Runs: ParseInt(GetByDataId(row, "RUN_CN")),
            Doubles: ParseInt(GetByDataId(row, "H2_CN")),
            Triples: ParseInt(GetByDataId(row, "H3_CN"))
            // 도루(SB)는 Basic1/Basic2 어디에도 없어서 라이브 수집으로는 채울 수 없음 — 박스스코어 소급만 채운다
        );
    }

    private static PlayerPitchingRecord? ParsePitcherRow(HtmlNode row)
    {
        var cells = row.SelectNodes("td");
        if (cells == null || cells.Count < 3) return null;

        var playerName = cells[1].SelectSingleNode(".//a")?.InnerText.Trim() ?? cells[1].InnerText.Trim();
        return new PlayerPitchingRecord(
            PlayerName: playerName,
            Team: cells[2].InnerText.Trim(),
            Era: ParseDouble(GetByDataId(row, "ERA_RT")),
            Wins: ParseInt(GetByDataId(row, "W_CN")),
            Losses: ParseInt(GetByDataId(row, "L_CN")),
            Saves: ParseInt(GetByDataId(row, "SV_CN")),
            Innings: GetByDataId(row, "INN2_CN"),
            Strikeouts: ParseInt(GetByDataId(row, "KK_CN")),
            HomeRuns: ParseInt(GetByDataId(row, "HR_CN")),
            Games: ParseInt(GetByDataId(row, "GAME_CN")),
            Holds: ParseInt(GetByDataId(row, "HOLD_CN")),
            HitsAllowed: ParseInt(GetByDataId(row, "HIT_CN")),
            RunsAllowed: ParseInt(GetByDataId(row, "R_CN")),
            EarnedRuns: ParseInt(GetByDataId(row, "ER_CN")),
            Walks: ParseInt(GetByDataId(row, "BB_CN")),
            Hbp: ParseInt(GetByDataId(row, "HP_CN")),
            WinRate: ParseDouble(GetByDataId(row, "WRA_RT")),
            InningsDecimal: Math.Round(ParseInningsToOuts(GetByDataId(row, "INN2_CN")) / 3.0, 2)
        );
    }

    private static string? GetByDataId(HtmlNode row, string dataId) =>
        row.SelectSingleNode($".//td[@data-id='{dataId}']")?.InnerText.Trim();

    private static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// KBO 이닝 표기("6", "1/3", "5 1/3" 등)를 아웃 카운트로 변환한다. 분수의 분자가 그대로 아웃 수(1~2)다.
    /// 팀 방어율 소급 집계에서 이닝을 그대로 합산하면 안 되고(예: 1/3 + 2/3 = "3/6"이 아니라 1이닝) 아웃 단위로
    /// 합산한 뒤 3으로 나눠야 해서 이 변환이 필요하다.
    /// </summary>
    private static int ParseInningsToOuts(string? innings)
    {
        if (string.IsNullOrWhiteSpace(innings)) return 0;

        var outs = 0;
        foreach (var part in innings.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var slashIdx = part.IndexOf('/');
            if (slashIdx > 0 && int.TryParse(part[..slashIdx], out var fractionOuts))
            {
                outs += fractionOuts;
            }
            else if (int.TryParse(part, out var wholeInnings))
            {
                outs += wholeInnings * 3;
            }
        }

        return outs;
    }

    private static Task SaveStandingsAsync(SqliteConnection connection, List<TeamStanding> standings, CancellationToken ct) =>
        SaveStandingsAsync(connection, standings, collectedAtOverride: null, ct);

    /// <summary>
    /// collectedAtOverride가 없으면(실시간 수집) CollectedAt 컬럼을 생략해 기본값 CURRENT_TIMESTAMP가 적용되고,
    /// 있으면(날짜별 소급 수집) 그 값을 그대로 저장한다. CURRENT_TIMESTAMP는 SQL 키워드라 파라미터로
    /// 바인딩할 수 없어서 INSERT 문 자체를 두 가지로 분기한다.
    /// </summary>
    private static async Task SaveStandingsAsync(SqliteConnection connection, List<TeamStanding> standings, string? collectedAtOverride, CancellationToken ct)
    {
        foreach (var s in standings)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = collectedAtOverride is null
                ? """
                  INSERT INTO KboTeamStandings (TeamName, Rank, Games, Wins, Losses, Draws, WinRate, GamesBehind, Avg, Obp, Era, Oavg)
                  VALUES ($teamName, $rank, $games, $wins, $losses, $draws, $winRate, $gamesBehind, $avg, $obp, $era, $oavg);
                  """
                : """
                  INSERT INTO KboTeamStandings (TeamName, Rank, Games, Wins, Losses, Draws, WinRate, GamesBehind, Avg, Obp, Era, Oavg, CollectedAt)
                  VALUES ($teamName, $rank, $games, $wins, $losses, $draws, $winRate, $gamesBehind, $avg, $obp, $era, $oavg, $collectedAt);
                  """;
            cmd.Parameters.AddWithValue("$teamName", s.TeamName);
            cmd.Parameters.AddWithValue("$rank", s.Rank);
            cmd.Parameters.AddWithValue("$games", (object?)s.Games ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wins", (object?)s.Wins ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$losses", (object?)s.Losses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$draws", (object?)s.Draws ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$winRate", (object?)s.WinRate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gamesBehind", (object?)s.GamesBehind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$avg", (object?)s.Avg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$obp", (object?)s.Obp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$era", (object?)s.Era ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$oavg", (object?)s.Oavg ?? DBNull.Value);
            if (collectedAtOverride is not null)
            {
                cmd.Parameters.AddWithValue("$collectedAt", collectedAtOverride);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SaveBatterStatsAsync(SqliteConnection connection, List<PlayerBattingRecord> batters, CancellationToken ct)
    {
        foreach (var b in batters)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO KboBatterStats (PlayerName, Team, Avg, Games, Hits, HomeRuns, Rbi, Obp, AtBats, Runs, Doubles, Triples, StolenBases, Walks, Hbp, Strikeouts)
                VALUES ($playerName, $team, $avg, $games, $hits, $homeRuns, $rbi, $obp, $atBats, $runs, $doubles, $triples, $stolenBases, $walks, $hbp, $strikeouts);
                """;
            cmd.Parameters.AddWithValue("$playerName", b.PlayerName);
            cmd.Parameters.AddWithValue("$team", b.Team);
            cmd.Parameters.AddWithValue("$avg", (object?)b.Avg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$games", (object?)b.Games ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hits", (object?)b.Hits ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$homeRuns", (object?)b.HomeRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rbi", (object?)b.Rbi ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$obp", (object?)b.Obp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$atBats", (object?)b.AtBats ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runs", (object?)b.Runs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$doubles", (object?)b.Doubles ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$triples", (object?)b.Triples ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stolenBases", (object?)b.StolenBases ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$walks", (object?)b.Walks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hbp", (object?)b.Hbp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$strikeouts", (object?)b.Strikeouts ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SavePitcherStatsAsync(SqliteConnection connection, List<PlayerPitchingRecord> pitchers, CancellationToken ct)
    {
        foreach (var p in pitchers)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO KboPitcherStats (PlayerName, Team, Era, Wins, Losses, Saves, Innings, Strikeouts, Oavg, HomeRuns,
                                             Games, Holds, HitsAllowed, RunsAllowed, EarnedRuns, Walks, Hbp, WinRate, InningsDecimal)
                VALUES ($playerName, $team, $era, $wins, $losses, $saves, $innings, $strikeouts, $oavg, $homeRuns,
                        $games, $holds, $hitsAllowed, $runsAllowed, $earnedRuns, $walks, $hbp, $winRate, $inningsDecimal);
                """;
            cmd.Parameters.AddWithValue("$playerName", p.PlayerName);
            cmd.Parameters.AddWithValue("$team", p.Team);
            cmd.Parameters.AddWithValue("$era", (object?)p.Era ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wins", (object?)p.Wins ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$losses", (object?)p.Losses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$saves", (object?)p.Saves ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$innings", (object?)p.Innings ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$strikeouts", (object?)p.Strikeouts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$oavg", (object?)p.Oavg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$homeRuns", (object?)p.HomeRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$games", (object?)p.Games ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$holds", (object?)p.Holds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hitsAllowed", (object?)p.HitsAllowed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runsAllowed", (object?)p.RunsAllowed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$earnedRuns", (object?)p.EarnedRuns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$walks", (object?)p.Walks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hbp", (object?)p.Hbp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$winRate", (object?)p.WinRate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inningsDecimal", (object?)p.InningsDecimal ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SaveGameResultsAsync(SqliteConnection connection, List<GameResult> games, CancellationToken ct)
    {
        foreach (var g in games)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO KboGameResults (GameDate, GameTime, AwayTeam, AwayScore, HomeTeam, HomeScore, GameId)
                VALUES ($gameDate, $gameTime, $awayTeam, $awayScore, $homeTeam, $homeScore, $gameId)
                ON CONFLICT(GameDate, AwayTeam, HomeTeam) DO UPDATE SET
                    GameTime = excluded.GameTime,
                    AwayScore = excluded.AwayScore,
                    HomeScore = excluded.HomeScore,
                    GameId = excluded.GameId;
                """;
            cmd.Parameters.AddWithValue("$gameDate", g.GameDate);
            cmd.Parameters.AddWithValue("$gameTime", (object?)g.GameTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$awayTeam", g.AwayTeam);
            cmd.Parameters.AddWithValue("$awayScore", (object?)g.AwayScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$homeTeam", g.HomeTeam);
            cmd.Parameters.AddWithValue("$homeScore", (object?)g.HomeScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gameId", (object?)g.GameId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private record TeamStanding(int Rank, string TeamName, int? Games, int? Wins, int? Losses, int? Draws, double? WinRate, string? GamesBehind,
        double? Avg = null, double? Obp = null, double? Era = null, double? Oavg = null);
    private record PlayerBattingRecord(string PlayerName, string Team, double? Avg, int? Games, int? Hits, int? HomeRuns, int? Rbi, double? Obp = null,
        int? AtBats = null, int? Runs = null, int? Doubles = null, int? Triples = null, int? StolenBases = null, int? Walks = null, int? Hbp = null, int? Strikeouts = null);
    // 타자 Basic2 페이지에서만 얻을 수 있는 부가 지표 묶음 (이름+팀으로 Basic1 결과에 병합)
    private record HitterExtraStat(string PlayerName, string Team, double? Obp, int? Walks, int? Hbp, int? Strikeouts);
    // 소급 집계용 타자/투수 누적치 — readonly record struct라 딕셔너리 GetValueOrDefault의 기본값이 전부 0이 된다
    private readonly record struct BatterCumulative(long Games, long AtBats, long Hits, long HomeRuns, long Rbi, long Walks, long Hbp, long SacFly,
        long Runs, long Doubles, long Triples, long Strikeouts, long StolenBases);
    private readonly record struct PitcherCumulative(long Games, long EarnedRuns, long HitsAllowed, long HomeRuns, long Strikeouts, long Outs, long Runs,
        long Wins, long Losses, long Saves, long Holds);
    private record PlayerPitchingRecord(string PlayerName, string Team, double? Era, int? Wins, int? Losses, int? Saves, string? Innings, int? Strikeouts, double? Oavg = null, int? HomeRuns = null,
        int? Games = null, int? Holds = null, int? HitsAllowed = null, int? RunsAllowed = null, int? EarnedRuns = null, int? Walks = null, int? Hbp = null, double? WinRate = null, double? InningsDecimal = null);
    private record PlayerSecondaryStat(string PlayerName, string Team, double? Value);
    private record GameResult(string GameDate, string? GameTime, string AwayTeam, int? AwayScore, string HomeTeam, int? HomeScore, string? GameId = null);
    private record BoxScoreBatting(string GameId, string GameDate, string Team, string PlayerName, int? AtBats, int? Runs, int? Hits, int? Rbi, int? HomeRuns,
        int? Walks = null, int? Hbp = null, int? SacFly = null, int? Doubles = null, int? Triples = null, int? Strikeouts = null, int? StolenBases = null);
    // Walks 컬럼 주의: 박스스코어 투수 표의 해당 열은 "4사구"(볼넷+사구 합산)라 순수 볼넷이 아니다.
    // Decision은 그 경기 판정 텍스트("승"/"패"/"홀드"/"세", 없으면 null) — 승/패/세이브/홀드 소급 집계용.
    private record BoxScorePitching(string GameId, string GameDate, string Team, string PlayerName, string? InningsPitched, int? Hits, int? HomeRuns, int? Walks, int? Strikeouts, int? Runs, int? EarnedRuns,
        string? Decision = null);
    private record PostbackState(string ViewState, string ViewStateGenerator, string EventValidation);
    private record AjaxDeltaResult(string? UpdatePanelHtml, string? ViewState, string? ViewStateGenerator, string? EventValidation);
}
