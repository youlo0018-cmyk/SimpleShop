using Collaboration.Domain.Common;
using FreeSql;
using MediatR;
using PermissionEntity = PermissionService.Domain.Entities.Permission;

namespace PermissionService.Application.Features.Internal;

/// <summary>网关 RBAC 映射查询处理器。</summary>
public sealed class GetRouteMapHandler : IRequestHandler<GetRouteMapCommand, ApiResponse<List<RouteMapEntry>>>
{
    private readonly IFreeSql _freeSql;

    /// <summary>构造处理器。</summary>
    /// <param name="freeSql">FreeSql 实例。全局过滤已在注册时装好（软删 / 租户）。</param>
    public GetRouteMapHandler(IFreeSql freeSql) => _freeSql = freeSql;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全部启用且绑定了路径的权限点映射。</returns>
    /// <remarks>
    /// 这里必须给 Permission 加类型别名：当前命名空间是 ...Features.Internal，
    /// 而 using PermissionService.Domain.Entities 会把 `Permission` 带进来——
    /// 但 `Permission` 同时还是一个命名空间（Features/Permission），
    /// 编译器会把裸写的 Permission 当成命名空间，直接报 CS0118。
    /// 这是 CODING_STANDARD 里记的第 1 号陷阱，已经踩过多次。
    /// </remarks>
    public async Task<ApiResponse<List<RouteMapEntry>>> Handle(GetRouteMapCommand request, CancellationToken ct)
    {
        var rows = await _freeSql.Select<PermissionEntity>()
            .Where(a => a.Status == 1 && a.ApiPath != null && a.ApiPath != string.Empty)
            .ToListAsync(ct);

        var entries = rows
            .Select(a => new RouteMapEntry(a.ApiPath.Trim(), a.Code.Trim()))
            // 同一路径理论上只绑定一个权限点；真出现重复时保留排序靠前的那个，
            // 不能让网关随机挑一个——那会让「这个接口到底要什么权限」变成薛定谔的。
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ApiResults.Ok(entries);
    }
}