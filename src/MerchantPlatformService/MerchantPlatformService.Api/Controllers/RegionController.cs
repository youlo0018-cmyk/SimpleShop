using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Application.Features.Region;
using Microsoft.AspNetCore.Mvc;

namespace MerchantPlatformService.Api.Controllers;

/// <summary>地区地址配置接口。</summary>
/// <remarks>
/// 权限点：<c>region:read</c> / <c>region:update</c>（DATA_SPEC 5.31）。
/// 路由前缀与规格一致用 <c>platform-configs</c>（地区数据挂在平台配置上，不是独立资源）。
/// </remarks>
[ApiController]
[Route("regions")]
public sealed class RegionController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public RegionController(IMediator mediator) => _mediator = mediator;

    /// <summary>读取某平台的地区数据。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地区 JSON 数组；<c>isCustom = false</c> 表示回落的内置默认。</returns>
    /// <remarks>
    /// 内置默认库目前**只有省级**（34 个），市 / 区县需要运营在后台「保存自定义」导入。
    /// 返回结果带 <c>isCustom</c>，前端据此提示运营「当前用的是内置默认，建议导入完整数据」。
    /// </remarks>
    [HttpGet("Get")]
    public async Task<ActionResult<ApiResponse<RegionsResult>>> Get(
        [FromQuery] long platformId, CancellationToken ct)
    {
        var result = await _mediator.Send(new QueryRegionsCommand(platformId), ct);
        return Ok(result);
    }

    /// <summary>保存地区数据。<b>传空串 = 恢复内置默认。</b></summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 校验在 Handler 里做：合法 JSON、顶层非空数组、每级带 name、不超过 2MB。
    /// 「恢复默认」是清空字符串而不是传 null——传 null 分不清是「恢复默认」还是「参数漏了」。
    /// </remarks>
    [HttpPost("Save")]
    public Task<ApiResponse> Save([FromBody] SaveRegionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
