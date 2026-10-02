using Collaboration.Domain.Common;
using MediatR;

namespace PermissionService.Application.Features.Internal;

/// <summary>
/// 网关 RBAC 用的「接口路径 → 权限点」映射。仅供 Gateway 调用。
/// </summary>
/// <remarks>
/// 网关靠它判断「这个请求需要哪个权限点」，而不是把映射硬编码在路由表里——
/// 权限点是运行时可维护的实体（新增 / 停用 / 改 ApiPath 都在权限中心操作），
/// 硬编码会让「改权限点立即生效于网关」这条需求（BUSINESS 5.4）无法实现。
///
/// 只返回**启用状态**且绑定了路径的权限点：停用的权限点不该再参与校验，
/// 树里停用节点会标注「含 n 项不可用」，网关侧同样不放行。
/// </remarks>
public record GetRouteMapCommand : IRequest<ApiResponse<List<RouteMapEntry>>>;

/// <summary>一条路径 → 权限点的映射。</summary>
/// <param name="Path">网关侧的请求路径，如 /gateway/users/List。</param>
/// <param name="Code">权限点编码，如 user:read。</param>
public record RouteMapEntry(string Path, string Code);