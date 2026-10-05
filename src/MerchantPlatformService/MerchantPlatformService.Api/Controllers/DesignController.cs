using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Application.Features.Design;
using MerchantPlatformService.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace MerchantPlatformService.Api.Controllers;

/// <summary>装修（拖拽搭建器）。平台装修与商户装修共用这一组接口。</summary>
/// <remarks>
/// 权限点：<c>design:read</c> / <c>design:update</c>（DATA_SPEC 5.29、5.30）。
/// 两类装修共用接口而不是各写一套：搭建器、组件注册表、校验规则完全一样，
/// 只有「可用组件清单」和「可建页面」不同——那是数据差异，交给参数而不是复制代码。
/// </remarks>
[ApiController]
[Route("design")]
public sealed class DesignController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public DesignController(IMediator mediator) => _mediator = mediator;

    /// <summary>组件库（左侧面板）。</summary>
    /// <param name="forMerchant">true 取商户可用组件。</param>
    /// <param name="page">页面标识；平台装修按它过滤可用组件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>组件列表，已按分类顺序排好。</returns>
    /// <remarks>
    /// 可用组件<b>按页面过滤</b>：首页可用通用组件，我的页额外可用会员与服务宫格，
    /// 店铺页仅店铺类与通用组件。让后台自己过滤的话，加了新组件就会漏改前端。
    /// </remarks>
    [HttpGet("Components")]
    public async Task<ActionResult<ApiResponse<List<ComponentDefDto>>>> Components(
        [FromQuery] bool forMerchant = false,
        [FromQuery] string page = DesignPages.Index,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new QueryComponentLibraryCommand(forMerchant, page), ct);
        return Ok(result);
    }

    /// <summary>读平台装修（草稿优先）。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>装修配置。</returns>
    [HttpGet("Platform")]
    public async Task<ActionResult<ApiResponse<DesignResult>>> Platform(
        [FromQuery] long platformId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new QueryPlatformDesignCommand(platformId), ct);
        return Ok(result);
    }

    /// <summary>读商户装修（草稿优先，只有店铺页）。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>装修配置。</returns>
    [HttpGet("Merchant")]
    public async Task<ActionResult<ApiResponse<DesignResult>>> Merchant(
        [FromQuery] long merchantId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new QueryMerchantDesignCommand(merchantId), ct);
        return Ok(result);
    }

    /// <summary>小程序读取商户已发布店铺装修，无需登录。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已发布装修配置；草稿永不外露。</returns>
    [HttpGet("Store")]
    public async Task<ActionResult<ApiResponse<DesignResult>>> Store(
        [FromQuery] long merchantId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new QueryPublicMerchantDesignCommand(merchantId), ct);
        return Ok(result);
    }

    /// <summary>小程序按平台编码读取已发布平台装修，无需登录。</summary>
    /// <param name="platformCode">平台编码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已发布装修配置；草稿永不外露。</returns>
    [HttpGet("PlatformStore")]
    public async Task<ActionResult<ApiResponse<DesignResult>>> PlatformStore(
        [FromQuery] string platformCode, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new QueryPublicPlatformDesignCommand(platformCode), ct);
        return Ok(result);
    }

    /// <summary>保存平台装修草稿。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存后的配置。</returns>
    /// <remarks>
    /// <b>保存草稿不影响线上</b>——草稿与已发布是两份独立数据，
    /// 这里只写草稿；小程序读的是已发布那一份。
    /// </remarks>
    [HttpPost("SavePlatformDraft")]
    public Task<ApiResponse<DesignResult>> SavePlatformDraft(
        [FromBody] SavePlatformDraftCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>保存商户装修草稿。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存后的配置。</returns>
    [HttpPost("SaveMerchantDraft")]
    public Task<ApiResponse<DesignResult>> SaveMerchantDraft(
        [FromBody] SaveMerchantDraftCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发布平台装修：草稿转已发布，版本 +1。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号。</returns>
    [HttpPost("PublishPlatform")]
    public Task<ApiResponse<int>> PublishPlatform(
        [FromBody] PublishPlatformDesignCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发布商户装修：草稿转已发布，版本 +1。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号。</returns>
    [HttpPost("PublishMerchant")]
    public Task<ApiResponse<int>> PublishMerchant(
        [FromBody] PublishMerchantDesignCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
