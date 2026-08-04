using System;
using Albatross.Collector.News.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Albatross.Collector.News.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNewsCollector(this IServiceCollection services, IConfiguration configuration)
        {
            // 네이버 뉴스 API 등록 (현재 활성화)
            services.AddHttpClient<INewsService, NaverNewsService>();

            /* 기존 NewsApi.org 서비스 (필요 시 주석 해제하여 사용)
            services.AddHttpClient<INewsService, NewsApiService>(client =>
            {
                var endpoint = configuration["NewsApi:Endpoint"];
                if (!string.IsNullOrEmpty(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    client.BaseAddress = uri;
                }
            });
            */

            return services;
        }
    }
}
