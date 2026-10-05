namespace Collaboration.Domain.Common;

/// <summary>统一分页信封。</summary>
/// <typeparam name="T">行类型。</typeparam>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <remarks>
/// 放在 Collaboration 而不是某个业务服务里：这个形状**所有服务的列表接口都要用**。
/// 之前它定义在 OrderService.Application，于是下一个需要分页的服务就会复制一份，
/// 而两份 `PagedResult` 的字段名一旦改得不一致，前端就得为每个服务写一套取值逻辑。
/// </remarks>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);
