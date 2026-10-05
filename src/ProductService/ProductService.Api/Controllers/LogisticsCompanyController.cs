using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Logistics;

namespace ProductService.Api.Controllers;

/// <summary>物流公司字典（DATA_SPEC 5.23）。发货表单下拉的数据源。</summary>
/// <remarks>
/// 路由用复数 <c>logistics-companies</c> 而不是 <c>logistics</c>：
/// 「物流」这个词单独出现时指的是**物流公司这张字典表**，不是物流单号轨迹。
/// 以后真要加轨迹查询，<c>shipments/tracks</c> 才是它的位置，两者不会混。
/// </remarks>
[ApiController]
[Route("logistics-companies")]
public sealed class LogisticsCompanyController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public LogisticsCompanyController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询物流公司。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>物流公司分页结果。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedResult<LogisticsCompanyItem>>> List(
        [FromBody] QueryLogisticsCompaniesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>取启用中的物流公司，供发货表单下拉使用。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项，只含 Id / 名称 / 编码。</returns>
    /// <remarks>
    /// 刻意<b>不分页</b>：它是下拉数据源，字典表也就几十行，
    /// 一把取回比前端翻页查更省事，也避免「选了第 3 页的公司」这种奇怪状态。
    /// </remarks>
    [HttpPost("Options")]
    public Task<ApiResponse<List<LogisticsOptionItem>>> Options(
        [FromBody] QueryLogisticsOptionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>新建物流公司。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新物流公司 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create(
        [FromBody] CreateLogisticsCompanyCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑物流公司。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update(
        [FromBody] UpdateLogisticsCompanyCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除物流公司（软删）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>发货单存的是公司名快照，所以删除字典项不会影响历史发货记录。</remarks>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete(
        [FromBody] DeleteLogisticsCompanyCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
