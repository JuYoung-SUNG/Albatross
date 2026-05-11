using System.Collections.Generic;
using System.Threading.Tasks;

namespace Albatross.Services
{
    public interface INewsService
    {
        Task<IEnumerable<NewsItem>> GetLatestAsync();
    }
}
