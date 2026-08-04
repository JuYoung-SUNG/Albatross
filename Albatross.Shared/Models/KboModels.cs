using System.Collections.Generic;

namespace Albatross.Shared.Models
{
    public class KboStandingDto
    {
        public string TeamName { get; set; } = string.Empty;
        public int Rank { get; set; }
        public int? Games { get; set; }
        public int? Wins { get; set; }
        public int? Losses { get; set; }
        public int? Draws { get; set; }
        public double? WinRate { get; set; }
        public string? GamesBehind { get; set; }
        public double? Avg { get; set; }
        public double? Obp { get; set; }
        public double? Era { get; set; }
        public double? Oavg { get; set; }
    }

    public class KboBatterStatDto
    {
        public string PlayerName { get; set; } = string.Empty;
        public string? Team { get; set; }
        public double? Avg { get; set; }
        public int? Games { get; set; }
        public int? Hits { get; set; }
        public int? HomeRuns { get; set; }
        public int? Rbi { get; set; }
        public double? Obp { get; set; }
        public int? AtBats { get; set; }
        public int? Runs { get; set; }
        public int? Doubles { get; set; }
        public int? Triples { get; set; }
        public int? StolenBases { get; set; }
        public int? Walks { get; set; }
        public int? Hbp { get; set; }
        public int? Strikeouts { get; set; }
    }

    public class KboPitcherStatDto
    {
        public string PlayerName { get; set; } = string.Empty;
        public string? Team { get; set; }
        public double? Era { get; set; }
        public int? Wins { get; set; }
        public int? Losses { get; set; }
        public int? Saves { get; set; }
        public string? Innings { get; set; }
        public int? Strikeouts { get; set; }
        public double? Oavg { get; set; }
        public int? Games { get; set; }
        public int? Holds { get; set; }
        public int? HitsAllowed { get; set; }
        public int? HomeRunsAllowed { get; set; }
        public int? RunsAllowed { get; set; }
        public int? EarnedRuns { get; set; }
        public int? Walks { get; set; }
        public int? Hbp { get; set; }
        public double? WinRate { get; set; }

        // 이닝 문자열("92 2/3")을 정렬/차트용 숫자로 변환한 값 (92.67)
        public double? InningsDecimal { get; set; }
    }

    public class KboPlayerStatsDto
    {
        public List<KboBatterStatDto> Batters { get; set; } = new();
        public List<KboPitcherStatDto> Pitchers { get; set; } = new();
    }

    public class KboGameDto
    {
        public string? GameTime { get; set; }
        public string AwayTeam { get; set; } = string.Empty;
        public int? AwayScore { get; set; }
        public string HomeTeam { get; set; } = string.Empty;
        public int? HomeScore { get; set; }
    }

    public class KboGameDayDto
    {
        public string GameDate { get; set; } = string.Empty;
        public string? Highlight { get; set; }
        public List<KboGameDto> Games { get; set; } = new();

        // 박스스코어 기반 데이터 이슈 (예: "OOO(팀) 5경기 연속 안타") — 뉴스 원문이 아니라 직접 계산한 사실이라 저작권 이슈 없음
        public List<string> Issues { get; set; } = new();
    }

    public class KboMetricTrendDto
    {
        public string PlayerName { get; set; } = string.Empty;
        public string? Team { get; set; }

        // Dates 배열과 같은 순서/길이로, 그 날짜에 수집된 스냅샷이 없으면 null
        public List<double?> Values { get; set; } = new();
    }

    public class KboPlayerTrendsDto
    {
        public List<string> Dates { get; set; } = new();

        public List<KboMetricTrendDto> BatterHomeRuns { get; set; } = new();
        public List<KboMetricTrendDto> BatterAvg { get; set; } = new();
        public List<KboMetricTrendDto> BatterHits { get; set; } = new();
        public List<KboMetricTrendDto> BatterObp { get; set; } = new();
        public List<KboMetricTrendDto> BatterRbi { get; set; } = new();
        public List<KboMetricTrendDto> BatterGames { get; set; } = new();
        public List<KboMetricTrendDto> BatterAtBats { get; set; } = new();
        public List<KboMetricTrendDto> BatterRuns { get; set; } = new();
        public List<KboMetricTrendDto> BatterDoubles { get; set; } = new();
        public List<KboMetricTrendDto> BatterTriples { get; set; } = new();
        public List<KboMetricTrendDto> BatterStolenBases { get; set; } = new();
        public List<KboMetricTrendDto> BatterWalks { get; set; } = new();
        public List<KboMetricTrendDto> BatterHbp { get; set; } = new();
        public List<KboMetricTrendDto> BatterStrikeouts { get; set; } = new();

        public List<KboMetricTrendDto> PitcherStrikeouts { get; set; } = new();
        public List<KboMetricTrendDto> PitcherEra { get; set; } = new();
        public List<KboMetricTrendDto> PitcherOavg { get; set; } = new();
        public List<KboMetricTrendDto> PitcherGames { get; set; } = new();
        public List<KboMetricTrendDto> PitcherWins { get; set; } = new();
        public List<KboMetricTrendDto> PitcherLosses { get; set; } = new();
        public List<KboMetricTrendDto> PitcherSaves { get; set; } = new();
        public List<KboMetricTrendDto> PitcherHolds { get; set; } = new();
        public List<KboMetricTrendDto> PitcherInnings { get; set; } = new();
        public List<KboMetricTrendDto> PitcherHitsAllowed { get; set; } = new();
        public List<KboMetricTrendDto> PitcherHomeRunsAllowed { get; set; } = new();
        public List<KboMetricTrendDto> PitcherRunsAllowed { get; set; } = new();
        public List<KboMetricTrendDto> PitcherEarnedRuns { get; set; } = new();
        public List<KboMetricTrendDto> PitcherWalks { get; set; } = new();
        public List<KboMetricTrendDto> PitcherHbp { get; set; } = new();
        public List<KboMetricTrendDto> PitcherWinRate { get; set; } = new();
    }

    public class KboTeamTrendDto
    {
        public string TeamName { get; set; } = string.Empty;

        // Dates 배열과 같은 순서/길이로, 그 날짜에 수집된 스냅샷이 없으면 null
        public List<double?> Values { get; set; } = new();
    }

    public class KboTeamTrendsDto
    {
        public List<string> Dates { get; set; } = new();
        public List<KboTeamTrendDto> WinRates { get; set; } = new();
        public List<KboTeamTrendDto> Avgs { get; set; } = new();
        public List<KboTeamTrendDto> Obps { get; set; } = new();
        public List<KboTeamTrendDto> Eras { get; set; } = new();
        public List<KboTeamTrendDto> Oavgs { get; set; } = new();
    }
}
