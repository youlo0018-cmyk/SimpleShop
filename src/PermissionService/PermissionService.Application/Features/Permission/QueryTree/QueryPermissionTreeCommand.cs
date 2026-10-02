using Collaboration.Domain.Common;
using MediatR;

namespace PermissionService.Application.Features.Permission.QueryTree;

/// <summary>查询权限树。仅后台权限管理使用，IncludeDisabled 控制是否带出已停用节点。</summary>
public record QueryPermissionTreeCommand(bool IncludeDisabled = false) : IRequest<ApiResponse<List<PermissionNodeDto>>>;

/// <summary>
/// 权限树节点。
/// Id：业务大类与模块有真实 Id，虚拟根「全部权限」为 0。
/// Name：中文名，直接展示，不显示编码（DESIGN_SPEC 6）。Code：仅叶子有值。
/// ApiPath：仅叶子有值。Level：1 业务大类 / 2 功能模块 / 3 权限点。
/// Status：1 启用 / 2 停用。IsBuiltin：内置权限点不可删除，只能停用。
/// Children：子节点。
/// </summary>
public record PermissionNodeDto(
    string Id,
    string Name,
    string? Code,
    string ApiPath,
    int Level,
    int Status,
    bool IsBuiltin,
    List<PermissionNodeDto> Children);

