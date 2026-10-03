using Collaboration.Domain.Common;
using EvaluateService.Application.Features.Evaluate;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EvaluateService.Api.Controllers;

/// <summary>C 端评价接口（商品详情页评价区 + 我的评价）。</summary>
/// <remarks>
/// 与后台的 <c>evaluates/admin</c> 分开：这个前缀是客户自己发的操作，
/// 混进后台会让「客户能不能发评价」和「管理员能不能下架评价」两套权限纠缠在一起。
/// </remarks>
[ApiController]
[Route("evaluates")]
public sealed class EvaluateController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public EvaluateController(IMediator mediator) => _mediator = mediator;

    /// <summary>商品评价列表（商品详情页评价区）。</summary>
    /// <param name="command">命令；SkuId 传 0 表示展示整个 SPU 的评价。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页，含均分与评价数。</returns>
    /// <remarks>
    /// 均分是服务端算好下发的，不让前端自己平均：前端只拿到当前页的 10 条，
    /// 自己平均得到的是「这一页的均分」，翻页就变，用户会以为系统在乱跳。
    /// </remarks>
    [HttpPost("List")]
    public Task<ApiResponse<EvaluatePageResult>> List(
        [FromBody] QuerySpuEvaluatesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>我的评价列表。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页，含已被隐藏的评价。</returns>
    [HttpPost("My")]
    public Task<ApiResponse<EvaluatePageResult>> My(
        [FromBody] QueryMyEvaluatesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>可评价商品（我的订单 → 某一单 → 去评价）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>该订单下每个 SPU 的评价状态。</returns>
    [HttpPost("Evaluable")]
    public Task<ApiResponse<List<EvaluableItemDto>>> Evaluable(
        [FromBody] QueryEvaluableCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发表首评（SPU 级）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 Id 与自动标记的 SKU 清单。</returns>
    /// <remarks>
    /// 一个订单内**同一 SPU 只能有一条首评**：即使买了该 SPU 的多个 SKU，
    /// 或分多次下单买了同一个商品，也只评一次。这条由数据库唯一索引兜底，
    /// 不是「先查再插」——并发下两个请求都可能查到「还没评过」。
    /// </remarks>
    [HttpPost("Publish")]
    public Task<ApiResponse<PublishEvaluateResult>> Publish(
        [FromBody] PublishEvaluateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>追评。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追评 Id。</returns>
    /// <remarks>
    /// 最多 3 条、首评后 30 天内。<b>追评不单独计入商品均分</b>，
    /// 否则「先打 5 星、再追评差评把分数拉低」就成了刷分路径，反过来也一样。
    /// </remarks>
    [HttpPost("Append")]
    public Task<ApiResponse<long>> Append(
        [FromBody] AppendEvaluateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
