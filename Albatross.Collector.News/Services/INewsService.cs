using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Albatross.Collector.News.Models;

namespace Albatross.Collector.News.Services;

public interface INewsService
{
    Task<IEnumerable<NewsItem>> GetLatestAsync(CancellationToken cancellationToken = default);
}
