using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PointService.Application.Features.Admin;
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

    /// <summary>读取积分规则（后台「积分规则维护」页）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前规则 + 规格默认值。</returns>
    /// <remarks>
    /// 与 C 端的 <c>Records</c>（某个客户自己的流水）刻意分开：
    /// 后台要看的是<b>全站</b>流水，而这里那条必须带 customerId 才能查。
    /// 两者混在一个接口上，权限点就没法只绑后台那一个。
    /// </remarks>
    [HttpPost("Rules")]
    public Task<ApiResponse<PointRulesView>> Rules(
        [FromBody] QueryPointRulesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>保存积分规则（整组覆盖）。</summary>
    /// <param name="command">命令，7 条规则全量提交。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>不追溯已发放的积分</b>：改了有效期或余额上限只对之后的发放生效。
    /// 已发放批次保持原有到期时间——用户手里的积分是「已承诺」的，
    /// 追溯变更等于单方面改合同。
    /// </remarks>
    [HttpPost("SaveRules")]
    public Task<ApiResponse> SaveRules(
        [FromBody] SavePointRulesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
