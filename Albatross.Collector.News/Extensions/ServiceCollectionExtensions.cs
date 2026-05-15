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
            // Registers a typed HttpClient for INewsService implemented by NewsApiService
            services.AddHttpClient<INewsService, NewsApiService>(client =>
            {
                var endpoint = configuration["NewsApi:Endpoint"];
                if (!string.IsNullOrEmpty(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    client.BaseAddress = uri;
                }
            });

            return services;
        }
    }
}
