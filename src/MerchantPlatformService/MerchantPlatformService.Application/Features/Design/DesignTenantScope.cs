using Collaboration.Domain.Context;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>
/// 装修配置的租户边界判定（DATA_SPEC 5.29 / 5.30）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要显式判定</b>：<c>platformId</c> / <c>merchantId</c> 都是请求体里的字段
/// （DATA_SPEC 5.30 写的是「只读；锁定本商户」，也就是说服务端必须锁，前端隐藏不算锁）。
/// 表上有 AOP 租户过滤，但过滤器**只管查询 / 更新 / 删除，不管插入** ——
/// 越权保存的真实路径是：更新影响 0 行 → 走 INSERT 分支 → 给别人的平台 / 商户插一条装修配置。
/// 目标已有配置时撞唯一索引回 500，没有配置时更糟：直接替对方建了一条草稿。</para>
///
/// <para>越权一律回 404（不泄露对方是否存在），与 TEST_CASES 6.2 一致。</para>
/// </remarks>
internal static class DesignTenantScope
{
    /// <summary>统一提示。</summary>
    public const string NotFoundMessage = "装修配置不存在";

    /// <summary>判断调用方能否操作指定平台的装修。</summary>
    /// <param name="ctx">租户上下文。</param>
    /// <param name="platformId">目标平台 Id。</param>
    /// <returns>允许返回 true。</returns>
    public static bool CanUsePlatform(TenantContext ctx, long platformId)
    {
        if (platformId <= 0) return false;

        // 超管不受平台限制
        if (ctx.IsSuperAdmin) return true;

        // 平台账号只能动自己那一份
        return ctx.IsPlatform && ctx.PlatformId == platformId;
    }

    /// <summary>判断调用方能否操作指定商户的店铺装修。</summary>
    /// <param name="ctx">租户上下文。</param>
    /// <param name="merchantId">目标商户 Id。</param>
    /// <returns>允许返回 true。</returns>
    /// <remarks>
    /// 平台账号放行：它要能给本平台的商户配店铺页。
    /// 跨平台的情况由商户查询本身挡住（商户表有租户过滤，查不到就是 404），
    /// 所以这里不必再回查一次商户归属。
    /// </remarks>
    public static bool CanUseMerchant(TenantContext ctx, long merchantId)
    {
        if (merchantId <= 0) return false;

        if (ctx.IsSuperAdmin || ctx.IsPlatform) return true;

        return ctx.IsMerchant && ctx.MerchantId == merchantId;
    }
}
