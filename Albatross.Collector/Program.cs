using Albatross.Collector;
using Albatross.Collector.News.Services;
using Serilog;
using Serilog.Events;

// 1. 수집기 전용 로그 경로 세팅 (실행 경로 내부 logs 폴더)
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string logFilePath = Path.Combine(baseDir, "logs", "collector_log-.txt");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    // 🔥 [이 줄을 추가!] 마이크로소프트 닷넷 호스팅 시스템 로그는 '경고(Warning)' 이상만 보겠다!
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .WriteTo.Debug()
    .WriteTo.File(
        path: logFilePath,
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

try
{
    Log.Information("==================================================");
    Log.Information("Albatross Collector Service Starting...");
    Log.Information($"Collector Log Path: {logFilePath}");
    Log.Information("==================================================");

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "AlbatrossCollector";
        })
        .ConfigureServices((context, services) =>
        {
            // 2. 표준 로깅을 Serilog로 대체
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });

            // RSS 스크래퍼 등록
            services.AddHttpClient<INewsService, RssNewsScraperService>();
            services.AddHostedService<Worker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Collector 호스트가 예기치 않게 종료되었습니다.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}