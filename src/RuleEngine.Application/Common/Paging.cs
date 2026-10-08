using Microsoft.EntityFrameworkCore;

namespace RuleEngine.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> selector) =>
        new(Items.Select(selector).ToList(), Page, PageSize, TotalCount);
}

/// <summary>Base for list queries bound from the query string (<c>?page=1&amp;pageSize=20</c>).</summary>
public abstract class PageQuery
{
    public int? Page { get; set; }

    public int? PageSize { get; set; }
}

public static class Pagination
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var normalizedPage = page is null or < 1 ? 1 : page.Value;
        var normalizedSize = pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        return (normalizedPage, normalizedSize);
    }

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        PageQuery pageQuery,
        CancellationToken cancellationToken)
    {
        var (page, pageSize) = Normalize(pageQuery.Page, pageQuery.PageSize);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page, pageSize, total);
    }
}
