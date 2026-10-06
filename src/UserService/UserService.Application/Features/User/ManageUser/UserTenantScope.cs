using Collaboration.Domain.Context;
using UserEntity = UserService.Domain.Entities.User;

namespace UserService.Application.Features.User.ManageUser;

/// <summary>
/// 后台账号管理的租户边界判定（DATA_SPEC 5.18「租户锁定」）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须在 Handler 里再判一次</b>：网关的 RBAC 只回答「有没有 user:create / user:update」。
/// 而按 DATA_SPEC 5.21，内置的 platform-admin / merchant-admin 角色绑定了**全部**权限点，
/// 平台运营角色也包含 user:*。只看权限点的话有两个直通超管的洞：
/// 一是建号时传 <c>platformId = 0</c> 再绑上 platform-admin 角色（一步提权）；
/// 二是重置超管账号的密码后直接登录（账号接管）。租户边界**不看权限点，只看租户身份**。</para>
///
/// <para><b>匿名一律拒绝</b>：账号管理是纯后台能力，正常流量必经网关。
/// 没有可信租户头（未带内部口令 / 直连服务端口 / 客户令牌）时，
/// 我们无法回答「这个人能管哪个租户」，此时放行等于把整张账号表交给任何能打通端口的人。
/// 内部服务间调用不走这些端点（认证走 /internal/users/**，那里是另一条路径）。</para>
/// </remarks>
public static class UserTenantScope
{
    /// <summary>判断调用方能否管理「目标租户」的账号。</summary>
    /// <param name="ctx">当前请求的租户上下文。</param>
    /// <param name="targetTenantType">目标账号租户类型：1 平台 / 2 商户。</param>
    /// <param name="targetPlatformId">目标账号所属平台 Id，超管为 0。</param>
    /// <param name="targetMerchantId">目标账号所属商户 Id，平台账号为 0。</param>
    /// <returns>在调用方租户范围内返回 true。</returns>
    public static bool CanManage(
        TenantContext ctx, int targetTenantType, long targetPlatformId, long targetMerchantId)
    {
        // 超管（PlatformId = 0）不受平台限制，可以建任意租户的账号，也包括新建别的超管
        if (ctx.IsSuperAdmin) return true;

        // 平台账号：只能管本平台。平台账号（含超管）之外的一切都在平台之外，一律拒绝。
        if (ctx.IsPlatform)
        {
            if (targetPlatformId != ctx.PlatformId) return false;

            return targetTenantType switch
            {
                // 本平台的平台账号：平台 Id 已核对过
                TenantTypes.Platform => true,
                // 本平台的商户账号：必须指明商户，否则是一条「挂在本平台但无归属」的悬空账号
                TenantTypes.Merchant => targetMerchantId > 0,
                _ => false
            };
        }

        // 商户账号：只能管本商户的商户账号，不能建平台账号，也不能碰兄弟商户
        if (ctx.IsMerchant)
        {
            return targetTenantType == TenantTypes.Merchant
                   && targetMerchantId > 0
                   && targetMerchantId == ctx.MerchantId;
        }

        return false;
    }

    /// <summary>判断调用方能否管理某个已存在的账号实体。</summary>
    /// <param name="ctx">当前请求的租户上下文。</param>
    /// <param name="target">目标账号。</param>
    /// <returns>在调用方租户范围内返回 true。</returns>
    public static bool CanManage(TenantContext ctx, UserEntity target)
        => CanManage(ctx, target.TenantType, target.PlatformId, target.MerchantId);

    /// <summary>越权时的统一提示。不区分「不存在」与「不属于你」，避免泄露账号是否存在。</summary>
    public const string DeniedMessage = "该账号不在你的管理范围内";
}
