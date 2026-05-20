using Microsoft.Extensions.Logging;
using Albatross.Shared.Interfaces;
using Serilog; // 1. Serilog 네임스페이스 추가

namespace Albatross
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // 2. 로그를 저장할 실제 실행 경로 및 파일명 지정
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string logFilePath = Path.Combine(baseDir, "logs", "albatross_log-.txt");

            // 3. Serilog 초기화 및 설정 (출력창 + 파일 저장 공시 지정)
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // 디버그 레벨부터 모든 로그를 잡음
                .WriteTo.Debug()      // Visual Studio 출력(Output) 창에 찍기
                .WriteTo.File(        // 텍스트 파일로 저장하기
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day, // 날짜별로 파일 쪼개기 (albatross_log-20260519.txt)
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}" // 로그 포맷
                )
                .CreateLogger();

            try
            {
                Log.Information("==================================================");
                Log.Information("Albatross MAUI Hybrid App Starting...");
                Log.Information($"Log File Path: {logFilePath}");
                Log.Information("==================================================");


                var builder = MauiApp.CreateBuilder();
                builder
                    .UseMauiApp<App>()  
                    .ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    });

                // 4. 구글/MS 닷넷 표준 로깅 시스템에 Serilog 연결
                builder.Logging.ClearProviders(); // 기본 내장 로그 기능 비우기
                builder.Logging.AddSerilog(dispose: true); // 마이크로소프트 로깅을 Serilog가 대행하도록 지정

                builder.Services.AddMauiBlazorWebView();


                // INewsService will be provided by Albatross.Web or a backend API.

#if DEBUG
                builder.Services.AddBlazorWebViewDeveloperTools();
                builder.Logging.AddDebug();
#endif


                return builder.Build();
            }
            catch (Exception ex)
            {
                // 앱이 구동되다 터지면 무조건 파일에 에러 원인을 기록
                Log.Fatal(ex, "App 호스트가 예기치 않게 종료되었습니다.");
                throw;
            }
            finally
            {
                // 프로그램 종료 시 로그 버퍼 비우기
                Log.CloseAndFlush();
            }
        }
    }
}
