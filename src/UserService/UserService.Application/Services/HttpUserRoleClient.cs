using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace UserService.Application.Services;

/// <summary>走内网 HTTP 调用权限中心的实现。</summary>
/// <remarks>
/// 建号与角色绑定分属两个服务、两个数据库，这里没有分布式事务。
/// 所以策略是：账号先建成功，角色绑定失败只告警不回滚建号——
/// 账号没角色是「什么都做不了」的可恢复状态，而回滚会留下一个建到一半的账号，更难收拾。
/// 调用方（建号处理器）已经把返回的 false 记进日志。
/// </remarks>
public sealed class HttpUserRoleClient : IUserRoleClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpUserRoleClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向权限中心的 HttpClient，由 Program.cs 注册为带名字的强类型客户端。</param>
    /// <param name="logger">日志器，绑定失败要留痕。</param>
    public HttpUserRoleClient(HttpClient http, ILogger<HttpUserRoleClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 预检失败（400）要把权限中心的原因原样带回来——那是给运营看的具体提示
    /// （例如「角色『商户管理员』的租户范围与目标账号不符」），
    /// 换成一句「角色校验失败」等于把可操作的信息丢掉。
    /// 网络异常同样算不通过：宁可让这次建号失败，也不要造一个没角色的账号。
    /// </remarks>
    public async Task<RoleScopeCheckResult> ValidateScopesAsync(
        IReadOnlyCollection<long> roleIds, int tenantType, CancellationToken ct = default)
    {
        if (roleIds.Count == 0) return new RoleScopeCheckResult(true, string.Empty);

        var payload = new ValidateRoleScopesRequest(roleIds.ToArray(), tenantType);

        try
        {
            var response = await _http.PostAsJsonAsync("internal/permissions/ValidateRoleScopes", payload, ct);

            // 400 也要读 body：里面是具体原因，不是「参数错误」四个字
            var result = await response.Content.ReadFromJsonAsync<ApiResponse<string>>(ct);

            if (result is null)
            {
                _logger.LogError("角色作用域预检返回了空响应体，HTTP {Status}", (int)response.StatusCode);
                return new RoleScopeCheckResult(false, "角色校验失败：权限中心返回了空响应");
            }

            return result.Success
                ? new RoleScopeCheckResult(true, string.Empty)
                : new RoleScopeCheckResult(false, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "角色作用域预检调用异常，按不通过处理");
            return new RoleScopeCheckResult(false, "角色校验失败：权限中心暂时不可用，请稍后重试");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 角色作用域校验（AllowedScopes 与租户类型是否匹配）在权限中心做：
    /// 角色表在那边，这边连角色有几个都不知道。这里只负责把目标账号的租户类型带上。
    /// </remarks>
    public async Task<bool> ReplaceAsync(
        long userId,
        IReadOnlyCollection<long> roleIds,
        long platformId,
        int tenantType,
        CancellationToken ct = default)
    {
        var payload = new BindUserRolesRequest(userId, roleIds?.ToArray() ?? Array.Empty<long>(), platformId, tenantType);

        try
        {
            var response = await _http.PostAsJsonAsync("internal/permissions/BindUserRoles", payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "账号 {UserId} 的角色绑定失败，权限中心返回 {Status}，需要人工补绑",
                    userId, (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResponse<int>>(ct);
            if (result is null || !result.Success)
            {
                _logger.LogError(
                    "账号 {UserId} 的角色绑定失败，权限中心返回业务失败：{Message}",
                    userId, result?.Message ?? "(空响应)");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 网络异常、超时、JSON 解析失败都归到这里：账号已建成，只是还没角色
            _logger.LogError(ex, "账号 {UserId} 的角色绑定调用异常，需要人工补绑", userId);
            return false;
        }
    }

    /// <summary>绑定请求体，字段名必须与权限中心的命令一致。</summary>
    private sealed record BindUserRolesRequest(
        [property: JsonPropertyName("userId")] long UserId,
        [property: JsonPropertyName("roleIds")] long[] RoleIds,
        [property: JsonPropertyName("platformId")] long PlatformId,
        [property: JsonPropertyName("tenantType")] int TenantType);

    /// <summary>预检请求体，字段名必须与权限中心的命令一致。</summary>
    private sealed record ValidateRoleScopesRequest(
        [property: JsonPropertyName("roleIds")] long[] RoleIds,
        [property: JsonPropertyName("tenantType")] int TenantType);
}
