using System.Diagnostics;
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
        private readonly IHostApplicationLifetime _appLifetime;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public Worker(
            ILogger<Worker> logger,
            INewsService news,
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            IHostApplicationLifetime appLifetime)
        {
            _logger = logger;
            _news = news;
            _config = config;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _appLifetime = appLifetime;
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
                    else
                    {
                        databasePath = Path.GetFullPath(databasePath);
                    }

                    var databaseDirectory = Path.GetDirectoryName(databasePath);
                    if (!string.IsNullOrWhiteSpace(databaseDirectory))
                    {
                        Directory.CreateDirectory(databaseDirectory);
                    }

                    await InitializeDatabaseAsync(databasePath, stoppingToken);

                    var fetchedItems = (await _news.GetLatestAsync(stoppingToken)).ToList();
                    _logger.LogInformation("Fetched {count} news items", fetchedItems.Count);

                    _logger.LogInformation("Starting SQLite save for raw news rows. Count: {count}", fetchedItems.Count);
                    var insertedOrUpdated = await SaveRawNewsAsync(databasePath, fetchedItems, stoppingToken);
                    _logger.LogInformation("Completed SQLite save for raw news rows. Saved rows: {count}", insertedOrUpdated);

                    var rawItems = await LoadRawNewsAsync(databasePath, stoppingToken);
                    _logger.LogInformation("Loaded {count} raw news rows from SQLite for Gemini analysis", rawItems.Count);

                    if (rawItems.Count > 0)
                    {
                        var summarizedNews = (await AnalyzeNewsWithAI(rawItems, stoppingToken)).ToList();
                        _logger.LogInformation("Gemini returned {count} summarized news rows", summarizedNews.Count);

                        if (summarizedNews.Count > 0)
                        {
                            _logger.LogInformation("Starting SQLite save for summarized news rows. Count: {count}", summarizedNews.Count);
                            await ReplaceSummarizedNewsAsync(databasePath, summarizedNews, stoppingToken);
                            _logger.LogInformation("Completed SQLite save for summarized news rows. Saved rows: {count}", summarizedNews.Count);

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
                    _appLifetime.StopApplication();
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
            var configuredDataDir =
                Environment.GetEnvironmentVariable("COLLECTOR_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("Collector__DataDirectory");

            if (!string.IsNullOrWhiteSpace(configuredDataDir))
            {
                return Path.GetFullPath(configuredDataDir);
            }

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
                    RelatedArticlesJson TEXT NOT NULL DEFAULT '[]',
                    SummaryJson TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_SummarizedNews_PublishedAt ON SummarizedNews(PublishedAt DESC);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);

            var migrateCommand = connection.CreateCommand();
            migrateCommand.CommandText =
                """
                ALTER TABLE SummarizedNews
                ADD COLUMN RelatedArticlesJson TEXT NOT NULL DEFAULT '[]';
                """;

            try
            {
                await migrateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // Existing databases already have the column.
            }
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
                        (Id, Title, Content, ImageUrl, Category, PublishedAt, RelatedUrlsJson, RelatedArticlesJson, SummaryJson)
                    VALUES
                        ($id, $title, $content, $imageUrl, $category, $publishedAt, $relatedUrlsJson, $relatedArticlesJson, $summaryJson);
                    """;

                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$content", item.Content);
                command.Parameters.AddWithValue("$imageUrl", (object?)item.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$publishedAt", item.PublishedAt);
                command.Parameters.AddWithValue("$relatedUrlsJson", JsonSerializer.Serialize(item.RelatedUrls, JsonOptions));
                command.Parameters.AddWithValue("$relatedArticlesJson", JsonSerializer.Serialize(item.RelatedArticles, JsonOptions));
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
            var sourceItems = items.ToList();
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
            var titleByUrl = sourceItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .GroupBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
            var jsonEscapeRules =
                """
                [JSON escape rules]
                - Return valid JSON only.
                - Do not write invalid escape sequences such as \uH, \u/, or a single backslash.
                - If a string contains a backslash, escape it as \\.
                - Do not use \u escapes. Write normal UTF-8 text directly.
                - For relatedArticles.title, use an empty string. The application will attach original titles by URL.
                """;

            var prompt = $@"
{jsonEscapeRules}
다음 뉴스 기사들을 주제별로 그룹핑하고 간단히 정리해줘.
응답은 반드시 아래 JSON 배열 형식만 반환하고, 설명 문장은 포함하지 마.

[분석 지침]
1. 주제나 사건이 같은 뉴스는 하나로 묶어줘.
2. title은 묶인 기사 내용을 종합한 한국어 제목으로 작성해줘.
3. content는 핵심만 100자 내외의 한국어 문장으로 정리해줘.
4. imageUrl은 관련 기사 이미지 중 가장 적절한 URL을 선택하고 없으면 null로 둬.
5. category는 정치, 사회, 경제, 스포츠, 연예, IT, 일반 중 하나로 분류해줘.
6. relatedArticles에는 분석에 사용한 원본 기사 제목과 URL을 모두 넣어줘.
7. relatedUrls에는 기존 호환성을 위해 relatedArticles의 URL만 같은 순서로 넣어줘.

[데이터]
{JsonSerializer.Serialize(sourceItems.Select(i => new { i.Title, i.Summary, i.Url, i.ImageUrl, i.Source, i.Category, i.Country, i.PublishedAt }), JsonOptions)}

[응답 형식]
[
  {{
    ""id"": ""문자열"",
    ""title"": ""정리된 제목"",
    ""content"": ""간단한 분석 내용"",
    ""imageUrl"": ""이미지 URL 또는 null"",
    ""category"": ""뉴스 분류"",
    ""publishedAt"": ""{nowKst:yyyy-MM-dd HH:mm:ss}"",
    ""relatedArticles"": [
      {{ ""title"": ""원본 기사 제목 1"", ""url"": ""url1"" }},
      {{ ""title"": ""원본 기사 제목 2"", ""url"": ""url2"" }}
    ],
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
                _logger.LogInformation(
                    "Calling Gemini API: {url}. Model: {model}, source news count: {count}, prompt length: {promptLength}",
                    apiUrl.Split('?')[0] + "?key=HIDDEN",
                    model,
                    sourceItems.Count,
                    prompt.Length);

                var stopwatch = Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(apiUrl, requestContent, stoppingToken);
                stopwatch.Stop();
                var responseContent = await response.Content.ReadAsStringAsync(stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Gemini API Error after {elapsedMs} ms: {status} - {msg}",
                        stopwatch.ElapsedMilliseconds,
                        response.StatusCode,
                        responseContent);
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                _logger.LogInformation("Gemini API call succeeded in {elapsedMs} ms", stopwatch.ElapsedMilliseconds);

                string? jsonText;
                try
                {
                    using var doc = JsonDocument.Parse(responseContent);
                    jsonText = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();
                }
                catch (JsonException ex)
                {
                    var failedPath = await SaveFailedGeminiResponseAsync(responseContent, "gemini-wrapper-json", stoppingToken);
                    _logger.LogError(ex, "Failed to parse Gemini API wrapper response. Raw response saved to: {path}", failedPath);
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                var analyzedData = await DeserializeGeminiNewsAsync(jsonText, apiUrl, model, sourceItems.Count, stoppingToken);
                var normalizedData = NormalizeRelatedArticles(analyzedData, titleByUrl).ToList();
                if (normalizedData.Count > 0)
                {
                    _logger.LogInformation("Gemini analysis design succeeded. Summarized rows: {count}", normalizedData.Count);
                }

                return normalizedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Google Gemini API.");
                return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }
        }

        private async Task<IEnumerable<Albatross.Shared.Models.NewsItem>> DeserializeGeminiNewsAsync(
            string jsonText,
            string apiUrl,
            string model,
            int sourceItemCount,
            CancellationToken cancellationToken)
        {
            try
            {
                return JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(jsonText, JsonOptions)
                    ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
            }
            catch (JsonException ex)
            {
                var failedPath = await SaveFailedGeminiResponseAsync(jsonText, "gemini-analysis-json", cancellationToken);
                _logger.LogError(ex, "Failed to parse Gemini analysis JSON. Raw response saved to: {path}", failedPath);

                var repairPrompt = $@"
The previous response was invalid JSON and failed to parse.
Return only a corrected, valid JSON array. Do not add markdown or explanations.
Do not use \u escapes. Write UTF-8 text directly.
If a string contains a backslash, escape it as \\.
For relatedArticles.title, use an empty string and preserve each URL.

[Invalid JSON to fix]
{jsonText}
";

                var repairedJsonText = await CallGeminiRepairAsync(apiUrl, model, sourceItemCount, repairPrompt, cancellationToken);
                if (string.IsNullOrWhiteSpace(repairedJsonText))
                {
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }

                try
                {
                    return JsonSerializer.Deserialize<IEnumerable<Albatross.Shared.Models.NewsItem>>(repairedJsonText, JsonOptions)
                        ?? Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }
                catch (JsonException retryEx)
                {
                    var retryFailedPath = await SaveFailedGeminiResponseAsync(repairedJsonText, "gemini-analysis-json-retry", cancellationToken);
                    _logger.LogError(retryEx, "Failed to parse repaired Gemini analysis JSON. Raw response saved to: {path}", retryFailedPath);
                    return Enumerable.Empty<Albatross.Shared.Models.NewsItem>();
                }
            }
        }

        private async Task<string?> CallGeminiRepairAsync(
            string apiUrl,
            string model,
            int sourceItemCount,
            string prompt,
            CancellationToken cancellationToken)
        {
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

            _logger.LogInformation(
                "Retrying Gemini JSON repair: {url}. Model: {model}, source news count: {count}, prompt length: {promptLength}",
                apiUrl.Split('?')[0] + "?key=HIDDEN",
                model,
                sourceItemCount,
                prompt.Length);

            var stopwatch = Stopwatch.StartNew();
            var response = await _httpClient.PostAsync(apiUrl, requestContent, cancellationToken);
            stopwatch.Stop();
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Gemini JSON repair API Error after {elapsedMs} ms: {status} - {msg}",
                    stopwatch.ElapsedMilliseconds,
                    response.StatusCode,
                    responseContent);
                return null;
            }

            _logger.LogInformation("Gemini JSON repair call succeeded in {elapsedMs} ms", stopwatch.ElapsedMilliseconds);

            try
            {
                using var doc = JsonDocument.Parse(responseContent);
                return doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
            }
            catch (JsonException ex)
            {
                var failedPath = await SaveFailedGeminiResponseAsync(responseContent, "gemini-repair-wrapper-json", cancellationToken);
                _logger.LogError(ex, "Failed to parse Gemini JSON repair wrapper response. Raw response saved to: {path}", failedPath);
                return null;
            }
        }

        private static async Task<string> SaveFailedGeminiResponseAsync(string content, string prefix, CancellationToken cancellationToken)
        {
            var dataDir = ResolveDataDirectory();
            var failureDir = Path.Combine(dataDir, "gemini-failures");
            Directory.CreateDirectory(failureDir);

            var fileName = $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json";
            var path = Path.Combine(failureDir, fileName);
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
            return path;
        }

        private static IEnumerable<Albatross.Shared.Models.NewsItem> NormalizeRelatedArticles(
            IEnumerable<Albatross.Shared.Models.NewsItem> items,
            IReadOnlyDictionary<string, string> titleByUrl)
        {
            foreach (var item in items)
            {
                if (item.RelatedArticles.Count == 0 && item.RelatedUrls.Count > 0)
                {
                    item.RelatedArticles = item.RelatedUrls
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url => new Albatross.Shared.Models.RelatedArticle
                        {
                            Title = titleByUrl.TryGetValue(url, out var title) ? title : url,
                            Url = url
                        })
                        .ToList();
                }

                if (item.RelatedUrls.Count == 0 && item.RelatedArticles.Count > 0)
                {
                    item.RelatedUrls = item.RelatedArticles
                        .Where(article => !string.IsNullOrWhiteSpace(article.Url))
                        .Select(article => article.Url)
                        .ToList();
                }

                foreach (var article in item.RelatedArticles.Where(article => !string.IsNullOrWhiteSpace(article.Url)))
                {
                    article.Title = titleByUrl.TryGetValue(article.Url, out var title) ? title : article.Url;
                }

                yield return item;
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
