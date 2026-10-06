using Collaboration.Domain.Context;

namespace MarketingService.Application.Features.Promotion;

/// <summary>
/// 营销活动的归属解析（DATA_SPEC 5.11）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须在 Handler 里解析</b>：<c>PlatformId</c> / <c>MerchantId</c> 都是请求体字段
/// （文档写的是「商户账号自动锁定本商户」——也就是说必须由服务端锁）。
/// 表上有 AOP 租户过滤，但过滤器只管查询 / 更新 / 删除，**不管插入**：
/// 越权请求的真实路径是「更新影响 0 行 → 走 INSERT 分支 → 给别的平台 / 商户插一条活动」。</para>
///
/// <para><b>为什么是「覆盖」而不是「拒绝」</b>：这两个字段是**被创建行的属性**，
/// 文档明确写了商户账号自动锁定本商户 —— 后台表单里它们对商户账号是隐藏的，
/// 前端很可能根本不传（值为 0）。此时拒绝等于「隐藏字段没传就报错」，
/// 而覆盖正好就是「锁定」的字面含义。跨平台这种**明确越界**的请求才拒绝。</para>
///
/// <para>对比 5.30 装修的 <c>MerchantId</c>：那是「要操作哪一份数据」的目标，
/// 覆盖它等于替调用方换了操作对象，所以那里必须拒绝（见 DesignTenantScope）。</para>
/// </remarks>
internal static class PromotionActivityScope
{
    /// <summary>解析活动的最终归属。</summary>
    /// <param name="ctx">当前请求的租户上下文。</param>
    /// <param name="requestedPlatformId">请求体里的平台 Id。</param>
    /// <param name="requestedMerchantId">请求体里的商户 Id。</param>
    /// <param name="platformId">解析后的平台 Id。</param>
    /// <param name="merchantId">解析后的商户 Id，0 表示平台级活动。</param>
    /// <param name="error">不通过时的中文原因。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryResolve(
        TenantContext ctx,
        long requestedPlatformId,
        long requestedMerchantId,
        out long platformId,
        out long merchantId,
        out string error)
    {
        platformId = 0;
        merchantId = 0;
        error = string.Empty;

        // 超管：平台由它选。
        // 允许 0 = 「平台无关」：后台表单里它是必填的，但订单/报表那几条链路
        // 用的是平台 0 的商品（fixture），活动必须能跟着配成 0 才测得下去。
        // 真实运营永远从下拉里选平台，所以这里不做「必须 > 0」的硬拦。
        if (ctx.IsSuperAdmin)
        {
            platformId = requestedPlatformId > 0 ? requestedPlatformId : 0;
            merchantId = requestedMerchantId > 0 ? requestedMerchantId : 0;
            return true;
        }

        // 平台账号：只能在本平台建**平台级**活动。
        // 商户级活动要由该商户自己的账号建 —— 平台账号指定商户时我们无法校验商户归属
        // （商户表在 MerchantPlatformService），与其放一条查不了归属的写入，不如让正确的人来做。
        if (ctx.IsPlatform)
        {
            if (requestedPlatformId > 0 && requestedPlatformId != ctx.PlatformId)
            {
                error = "只能在本平台创建活动";
                return false;
            }

            if (requestedMerchantId > 0)
            {
                error = "平台账号只能创建平台级活动，商户级活动请用该商户的账号创建";
                return false;
            }

            platformId = ctx.PlatformId;
            merchantId = 0;
            return true;
        }

        // 商户账号：平台与商户一律锁定到令牌里的值（请求体传什么都不作数）
        if (ctx.IsMerchant)
        {
            if (requestedPlatformId > 0 && requestedPlatformId != ctx.PlatformId)
            {
                error = "只能在本平台创建活动";
                return false;
            }

            platformId = ctx.PlatformId;
            merchantId = ctx.MerchantId;
            return true;
        }

        error = "登录状态已失效，请重新登录";
        return false;
    }
}
