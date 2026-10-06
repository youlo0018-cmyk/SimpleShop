using Collaboration.Domain.Common;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MerchantPlatformService.Api.Controllers;

/// <summary>平台内部接口。网关不路由 /internal 前缀。</summary>
/// <remarks>
/// 运费是**平台级配置**（BUSINESS.md 6.2），但运费是订单金额的一部分，
/// 算账的职责在订单服务。两边都不管的话，运费就只能由客户端上报 ——
/// 而客户端报什么，商家就收什么。
/// </remarks>
[ApiController]
[Route("internal/platforms")]
public sealed class InternalPlatformController : ControllerBase
{
    private readonly IFreeSql _db;

    /// <summary>构造控制器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    public InternalPlatformController(IFreeSql db) => _db = db;

    /// <summary>取某平台的运费配置（订单服务算运费时调用）。</summary>
    /// <param name="platformId">平台 Id；0 表示平台自营。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台运费与满额包邮门槛。</returns>
    /// <remarks>
    /// <para><b>查不到平台时返回 0 元配置而不是 404。</b>
    /// 平台自营（platformId = 0）本来就没有对应的 platform 行，而运费配置在这套设计里
    /// 始终是「默认 0 元」。抛错会让自营单直接下不了 —— 用一个后端根本没法查、
    /// 只能去数据库里看的事实去拦住下单，是把配置问题变成业务故障。</para>
    /// </remarks>
    [HttpGet("shipping-config")]
    public async Task<ActionResult<ApiResponse<ShippingConfigSnapshot>>> ShippingConfig(
        [FromQuery] long platformId, CancellationToken ct)
    {
        var row = await _db.Select<Platform>()
            .Where(a => a.Id == platformId)
            .FirstAsync(a => new ShippingConfigSnapshot(
                a.Id, a.ShippingFee, a.FreeShippingThreshold), ct)
            .ConfigureAwait(false);

        return Ok(ApiResults.Ok(row ?? new ShippingConfigSnapshot(platformId, 0m, 0m)));
    }
}

/// <summary>平台运费配置快照。</summary>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="ShippingFee">平台运费，仅对实物快递收取，两位小数。</param>
/// <param name="FreeShippingThreshold">满额包邮门槛，按商品实付判定；0 表示不启用。</param>
public sealed record ShippingConfigSnapshot(long PlatformId, decimal ShippingFee, decimal FreeShippingThreshold);

