using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Albatross.Services
{
    public class MockNewsService : INewsService
    {
        public Task<IEnumerable<NewsItem>> GetLatestAsync()
        {
            var list = new List<NewsItem>
            {
                new NewsItem { Title = "Local Startup Raises Series A", Summary = "A local startup raised funding to expand its AI platform.", PublishedAt = DateTime.UtcNow.AddHours(-1) },
                new NewsItem { Title = "City Council Approves New Park", Summary = "The city council approved the development of a new downtown park.", PublishedAt = DateTime.UtcNow.AddHours(-5) },
                new NewsItem { Title = "Sports Team Wins Championship", Summary = "The hometown team clinched the championship in a thrilling final.", PublishedAt = DateTime.UtcNow.AddDays(-1) }
            };

            return Task.FromResult<IEnumerable<NewsItem>>(list);
        }
    }
}
