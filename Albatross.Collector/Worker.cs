using System.Net.Http;
using System.Text;
using System.Text.Json;
using Albatross.Collector.News.Models;
using Albatross.Collector.News.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly INewsService _news;
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public Worker(ILogger<Worker> logger, INewsService news, IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _news = news;
            _config = config;
            _httpClient = httpClientFactory.CreateClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Collector worker starting");

            var singleRun = Environment.GetCommandLineArgs().Contains("--once");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Collector tick at: {time}", DateTimeOffset.Now);

                    var dataDir = ResolveDataDirectory();
                    Directory.CreateDirectory(dataDir);

                    var databasePath = _config["Collector:DatabasePath"];
                    if (string.IsNullOrWhiteSpace(databasePath))
                    {
                        databasePath = Path.Combine(dataDir, "albatross-news.db");
                    }

                    await InitializeDatabaseAsync(databasePath, stoppingToken);

                    var fetchedItems = (await _news.GetLatestAsync(stoppingToken)).ToList();
                    _logger.LogInformation("Fetched {count} news items", fetchedItems.Count);

                    var insertedOrUpdated = await SaveRawNewsAsync(databasePath, fetchedItems, stoppingToken);
                    _logger.LogInformation("Saved {count} raw news rows into SQLite", insertedOrUpdated);

                    var rawItems = await LoadRawNewsAsync(databasePath, stoppingToken);
                    _logger.LogInformation("Loaded {count} raw news rows from SQLite for Gemini analysis", rawItems.Count);

                    if (rawItems.Count > 0)
                    {
                        var summarizedNews = (await AnalyzeNewsWithAI(rawItems, stoppingToken)).ToList();
                        _logger.LogInformation("Gemini returned {count} summarized news rows", summarizedNews.Count);

                        if (summarizedNews.Count > 0)
                        {
                            await ReplaceSummarizedNewsAsync(databasePath, summarizedNews, stoppingToken);

                            var outPath = Path.Combine(dataDir, "news.json");
                            await ExportSummarizedNewsToJsonAsync(databasePath, outPath, stoppingToken);
                            _logger.LogInformation("Successfully wrote news file to: {path}", outPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during collection");
                }

                if (singleRun)
                {
                    _logger.LogInformation("Single-run mode, exiting");
                    break;
                }

                var intervalMinutes = _config.GetValue<int>("Collector:IntervalMinutes", 10);
                var delayTime = TimeSpan.FromMinutes(intervalMinutes);

#if DEBUG
                delayTime = TimeSpan.FromHours(1);
                _logger.LogInformation("Debug mode active: Next tick will run in 1 hour.");
#endif

                await Task.Delay(delayTime, stoppingToken);
            }

            _logger.LogInformation("Collector worker stopping");
        }

        private static string ResolveDataDirectory()
        {
            var baseDir = AppContext.BaseDirectory;

            if (baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                return Path.Combine(solutionRoot, "Albatross.Web", "wwwroot", "data");
            }

            return Path.Combine(baseDir, "data");
        }

        private static async Task InitializeDatabaseAsync(string databasePath, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS RawNews (
                    Id TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    Url TEXT NOT NULL UNIQUE,
                    PublishedAt TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Country TEXT NOT NULL,
                    ImageUrl TEXT NULL,
                    RawJson TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_RawNews_PublishedAt ON RawNews(PublishedAt DESC);

                CREATE TABLE IF NOT EXISTS SummarizedNews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    ImageUrl TEXT NULL,
                    Category TEXT NOT NULL,
                    PublishedAt TEXT NOT NULL,
                    RelatedUrlsJson TEXT NOT NULL,
                    SummaryJson TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_SummarizedNews_PublishedAt ON SummarizedNews(PublishedAt DESC);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> SaveRawNewsAsync(string databasePath, IEnumerable<NewsItem> items, CancellationToken cancellationToken)
        {
            var saved = 0;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Url)))
            {
                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT INTO RawNews
                        (Id, Title, Summary, Url, PublishedAt, Source, Category, Country, ImageUrl, RawJson, UpdatedAt)
                    VALUES
                        ($id, $title, $summary, $url, $publishedAt, $source, $category, $country, $imageUrl, $rawJson, CURRENT_TIMESTAMP)
                    ON CONFLICT(Url) DO UPDATE SET
                        Id = excluded.Id,
                        Title = excluded.Title,
                        Summary = excluded.Summary,
                        PublishedAt = excluded.PublishedAt,
                        Source = excluded.Source,
                        Category = excluded.Category,
                        Country = excluded.Country,
                        ImageUrl = excluded.ImageUrl,
                        RawJson = excluded.RawJson,
                        UpdatedAt = CURRENT_TIMESTAMP;
                    """;

                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$summary", item.Summary);
                command.Parameters.AddWithValue("$url", item.Url);
                command.Parameters.AddWithValue("$publishedAt", item.PublishedAt.ToString("O"));
                command.Parameters.AddWithValue("$source", item.Source);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$country", item.Country);
                command.Parameters.AddWithValue("$imageUrl", (object?)item.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$rawJson", JsonSerializer.Serialize(item, JsonOptions));

                saved += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return saved;
        }

        private static async Task<List<NewsItem>> LoadRawNewsAsync(string databasePath, CancellationToken cancellationToken)
        {
            var limit = 200;
            var items = new List<NewsItem>();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT RawJson
                FROM RawNews
                ORDER BY PublishedAt DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawJson = reader.GetString(0);
                var item = JsonSerializer.Deserialize<NewsItem>(rawJson, JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        private static async Task ReplaceSummarizedNewsAsync(
            string databasePath,
            IEnumerable<Albatross.Shared.Models.NewsItem> items,
            CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM SummarizedNews;";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    item.Id = Guid.NewGuid().ToString("N");
                }

                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT OR REPLACE INTO SummarizedNews
                        (Id, Title, Content, ImageUrl, Category, PublishedAt, RelatedUrlsJson, SummaryJson)
                    VALUES
                        ($id, $title, $content, $imageUrl, $category, $publishedAt, $relatedUrlsJson, $summaryJson);
                    """;

                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$content", item.Content);
                command.Parameters.AddWithValue("$imageUrl", (object?)item.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$publishedAt", item.PublishedAt);
                command.Parameters.AddWithValue("$relatedUrlsJson", JsonSerializer.Serialize(item.RelatedUrls, JsonOptions));
                command.Parameters.AddWithValue("$summaryJson", JsonSerializer.Serialize(item, JsonOptions));

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task ExportSummarizedNewsToJsonAsync(
            string databasePath,
            string outPath,
            CancellationToken cancellationToken)
        {
            var items = new List<Albatross.Shared.Models.NewsItem>();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT SummaryJson
                FROM SummarizedNews
                ORDER BY datetime(PublishedAt) DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var summaryJson = reader.GetString(0);
                var item = JsonSerializer.Deserialize<Albatross.Shared.Models.NewsItem>(summaryJson, JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            var jsonString = JsonSerializer.Serialize(items, JsonOptions);
            await File.WriteAllTextAsync(outPath, jsonString, cancellationToken);
        }

        private async Task<IEnumerable<Albatross.Shared.Models.NewsItem>> AnalyzeNewsWithAI(IEnumerable<NewsItem> items, CancellationToken stoppingToken)
        {
            var apiKey = _config["GEMINI_API_KEY"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("GEMINI_API_KEY is not configured.");
                return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }

            var model = _config["Gemini:Model"];
            if (string.IsNullOrWhiteSpace(model))
            {
                model = "gemini-2.5-flash";
            }

            var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var nowKst = GetKoreaNow();

            var prompt = $@"
다음 뉴스 기사들을 주제별로 그룹핑하고 간단히 정리해줘.
응답은 반드시 아래 JSON 배열 형식만 반환하고, 설명 문장은 포함하지 마.

[분석 지침]
1. 주제나 사건이 같은 뉴스는 하나로 묶어줘.
2. title은 묶인 기사 내용을 종합한 한국어 제목으로 작성해줘.
3. content는 핵심만 100자 내외의 한국어 문장으로 정리해줘.
4. imageUrl은 관련 기사 이미지 중 가장 적절한 URL을 선택하고 없으면 null로 둬.
5. category는 정치, 사회, 경제, 스포츠, 연예, IT, 일반 중 하나로 분류해줘.
6. relatedUrls에는 분석에 사용한 원본 기사 URL을 모두 넣어줘.

[데이터]
{JsonSerializer.Serialize(items.Select(i => new { i.Title, i.Summary, i.Url, i.ImageUrl, i.Source, i.Category, i.Country, i.PublishedAt }), JsonOptions)}

[응답 형식]
[
  {{
    ""id"": ""문자열"",
    ""title"": ""정리된 제목"",
    ""content"": ""간단한 분석 내용"",
    ""imageUrl"": ""이미지 URL 또는 null"",
    ""category"": ""뉴스 분류"",
    ""publishedAt"": ""{nowKst:yyyy-MM-dd HH:mm:ss}"",
    ""relatedUrls"": [""url1"", ""url2""]
  }}
]
";

            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json"
                }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            try
            {
                _logger.LogInformation("Calling Gemini API: {url}", apiUrl.Split('?')[0] + "?key=HIDDEN");
                var response = await _httpClient.PostAsync(apiUrl, requestContent, stoppingToken);
                var responseContent = await response.Content.ReadAsStringAsync(stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gemini API Error: {status} - {msg}", response.StatusCode, responseContent);
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                using var doc = JsonDocument.Parse(responseContent);
                var jsonText = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                var analyzedData = JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(jsonText, JsonOptions);
                return analyzedData ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Google Gemini API.");
                return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }
        }

        private static DateTime GetKoreaNow()
        {
            try
            {
                var kstZone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kstZone);
            }
            catch (TimeZoneNotFoundException)
            {
                var kstZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kstZone);
            }
        }
    }
}
