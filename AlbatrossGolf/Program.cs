using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AlbatrossGolf;
using Microsoft.Extensions.Logging;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// ──────────────────────────────────────────────────────────
// 🔥 [로그 추가] 빌더가 만들어지자마자 브라우저 콘솔에 찍을 임시 로거 생성
var logger = builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>();
logger.LogInformation("[2단계] Program.cs - C# 진입점(Main) 실행 완료. HttpClient 및 인프라 서비스를 주입하는 중입니다.");
// ──────────────────────────────────────────────────────────

// HttpClient 등록 - JSON 파일(data/news.json)을 읽기 위해 BaseAddress 설정
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
