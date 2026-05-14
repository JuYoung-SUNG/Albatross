using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Albatross.Collector
{
    using Albatross.Collector.News.Services;

    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly INewsService _news;
        private readonly IConfiguration _config;


        public Worker(ILogger<Worker> logger, INewsService news, IConfiguration config)
        {
            _logger = logger;
            _news = news;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Collector worker starting");
            // support a single-run mode when the process is started with `--once`
            var args = Environment.GetCommandLineArgs();
            var singleRun = args.Contains("--once");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Collector tick at: {time}", DateTimeOffset.Now);

                    var items = await _news.GetLatestAsync(stoppingToken);
                    var count = items?.Count() ?? 0;
                    _logger.LogInformation("Fetched {count} news items", count);

                    // 서비스 환경에서도 안전하게 파일 경로를 찾도록 수정
                    try
                    {
                        var baseDir = AppContext.BaseDirectory;
                        // 프로젝트 구조상 Collector/bin/Debug/net10.0 에 있다면 상위로 4번 이동해야 솔루션 루트
                        var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                        var dataDir = Path.Combine(solutionRoot, "Albatross.Web", "wwwroot", "data");
                        
                        Directory.CreateDirectory(dataDir);
                        var outPath = Path.Combine(dataDir, "news.json");
                        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true };

                        // convert to simple DTO for frontend
                        var dto = items.Select(i => new { id = i.Id, title = i.Title, summary = i.Summary, url = i.Url, publishedAt = i.PublishedAt, source = i.Source, category = i.Category, country = i.Country });
                        await System.IO.File.WriteAllTextAsync(outPath, System.Text.Json.JsonSerializer.Serialize(dto, opts), stoppingToken);
                        _logger.LogInformation("Wrote {path}", outPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to write news file");
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

                var interval = _config.GetValue<int>("Collector:IntervalMinutes", 10);
                await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
            }

            _logger.LogInformation("Collector worker stopping");
        }
    }
}
