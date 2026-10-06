using PermissionService.Domain.Entities;
using RoleEntity = PermissionService.Domain.Entities.Role;

namespace PermissionService.Application.Features.Role;

/// <summary>
/// 角色与账号租户类型的作用域匹配规则（DATA_SPEC 5.18「作用域校验」）。
/// </summary>
/// <remarks>
/// <para>规则：角色的 AllowedScopes 为 1 平台时只能绑给平台账号，2 商户时只能绑给商户账号，3 两者皆可。</para>
///
/// <para><b>为什么抽成静态规则类</b>：这条规则有两个入口——建号时的预检
/// （<c>ValidateRoleScopes</c>）与实际绑定（<c>BindUserRoles</c>）。
/// 写成两份的话，早晚会出现「预检放行、绑定拒绝」或者反过来的不一致，
/// 而症状是「接口说创建成功但账号没权限」，非常难查。</para>
/// </remarks>
public static class RoleScopeRules
{
    /// <summary>校验角色集合是否可用于指定租户类型的账号。</summary>
    /// <param name="roles">按 Id 查到的角色实体（可能少于请求数量）。</param>
    /// <param name="requestedDistinctCount">请求里去重后的角色数量。</param>
    /// <param name="tenantType">目标账号租户类型，1 平台 / 2 商户。</param>
    /// <returns>通过返回 null；否则返回面向用户的中文原因。</returns>
    public static string? Validate(IReadOnlyCollection<RoleEntity> roles, int requestedDistinctCount, int tenantType)
    {
        // 先查存在性：绑一堆不存在的角色等于「绑了但没权限」，是最难查的一类静默失败
        if (roles.Count != requestedDistinctCount)
        {
            return "存在无效的角色 Id";
        }

        var mismatched = roles
            .Where(r => r.AllowedScopes != 3 && r.AllowedScopes != tenantType)
            .ToList();

        if (mismatched.Count == 0) return null;

        var names = string.Join('、', mismatched.Select(r => r.RoleName));
        var expected = tenantType == 1 ? "平台" : "商户";
        return $"角色「{names}」的租户范围与目标账号不符：{expected}账号只能绑定平台/商户范围匹配的角色";
    }
}
