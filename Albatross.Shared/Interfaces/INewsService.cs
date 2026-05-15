using System.Collections.Generic;
using System.Threading.Tasks;
using Albatross.Shared.Models;

namespace Albatross.Shared.Interfaces
{
    public interface INewsService
    {
        Task<IEnumerable<NewsItem>> GetLatestAsync();
    }
}
