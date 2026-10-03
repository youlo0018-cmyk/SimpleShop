using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PointService.Application.Features.Operations;

namespace PointService.Api.Controllers;

/// <summary>积分（后台只读 + C 端自助签到 / 查询）。</summary>
/// <remarks>
/// 积分的**写入**接口（发放 / 冻结 / 实扣 / 回收）都在 InternalPointController，
/// 由注册、订单、支付、退款链路调用，不对前端暴露。
/// </remarks>
[ApiController]
[Route("points")]
public sealed class PointController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public PointController(IMediator mediator) => _mediator = mediator;

    /// <summary>每日签到。当日幂等，重复点击返回「今日已签到」。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>签到结果。</returns>
    /// <remarks>
    /// 客户 Id 从令牌来而不是从入参来——否则改一下请求体就能给别人签到。
    /// 这里由网关注入的租户上下文解析，网关已把 customerId 映射到请求上下文。
    /// </remarks>
    [HttpPost("SignIn")]
    public Task<ApiResponse<PointSignInResult>> SignIn([FromQuery] long customerId, CancellationToken ct)
        => _mediator.Send(new SignInPointsCommand(customerId), ct);

    /// <summary>查询积分余额。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>余额。没发放过也返回全 0，不报 404。</returns>
    [HttpGet("Balance")]
    public Task<ApiResponse<PointBalance>> Balance([FromQuery] long customerId, CancellationToken ct)
        => _mediator.Send(new QueryPointAccountCommand(customerId), ct);

    /// <summary>分页查询积分流水。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表，倒序。</returns>
    [HttpGet("Records")]
    public Task<ApiResponse<List<PointRecordItem>>> Records(
        [FromQuery] long customerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
        => _mediator.Send(new QueryPointRecordsCommand(customerId, page, pageSize), ct);
}