using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Shop;

namespace ProductService.Api.Controllers;

/// <summary>商品搜索索引维护（BUSINESS「搜索索引」模块，权限点 <c>search:reindex</c>）。</summary>
/// <remarks>
/// 路由挂在 <c>products/SearchIndex</c> 下。索引是一份**独立的数据副本**，
/// 它的动作（重建 / 对账）和商品本身的增删改是两回事，所以不塞进 <c>products</c> 的 CRUD 里。
/// 权限点用 <c>/gateway/products/SearchIndex/*</c> 通配一次覆盖这两个动作。
/// </remarks>
[ApiController]
[Route("products/SearchIndex")]
public sealed class AdminSearchIndexController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public AdminSearchIndexController(IMediator mediator) => _mediator = mediator;

    /// <summary>索引对账（<b>只读</b>）。只算差异，不写 ES。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>两边的差异统计与样本 Id。</returns>
    /// <remarks>
    /// 运营可以随时点这个按钮看「索引现在健不健康」，不必担心点了就引发全量写入——
    /// 这是它与 <c>Reindex</c> 拆成两个端点的原因。
    /// 商品量大时扫库有上限，结果里 <c>Truncated</c> 会告诉你这次只扫了一部分，
    /// 此时 <c>Consistent</c> 恒为 false（宁可说「不知道」，也不报一个假的「一致」）。
    /// </remarks>
    [HttpPost("Reconcile")]
    public Task<ApiResponse<ReconcileResult>> Reconcile(
        [FromBody] ReconcileSearchIndexCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>重建索引：补写缺失的、清理孤儿文档。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本轮补写 / 清理 / 失败的条数。</returns>
    /// <remarks>
    /// 走的是**差集对账**而不是「删索引重建」：重建期间索引是空的，
    /// 那段时间用户搜索会拿到零结果，而且越是热门商品越容易触发重建。
    /// 只有切换分词器那种必须换索引的场景才需要 <c>RecreateIndexAsync</c>。
    /// </remarks>
    [HttpPost("Reindex")]
    public Task<ApiResponse<ReindexResult>> Reindex(
        [FromBody] ReindexProductsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
