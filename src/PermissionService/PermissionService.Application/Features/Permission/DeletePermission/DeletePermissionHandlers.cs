using Collaboration.Domain.Common;
using FreeSql;
using MediatR;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Permission.DeletePermission;

/// <summary>删除权限点处理器。</summary>
/// <remarks>
/// 内置权限点**不可删除，只能停用**（BUSINESS 5.4）：内置权限点对应真实接口路径，
/// 删掉会让这些路径失去鉴权保护。删除非内置点时级联清掉它的角色绑定。
/// </remarks>
public sealed class DeletePermissionHandler : IRequestHandler<DeletePermissionCommand, ApiResponse>
{
    private readonly IPermissionRepository _permissions;
    private readonly IFreeSql _db;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    /// <param name="db">FreeSql 实例，用于级联清理角色绑定。</param>
    public DeletePermissionHandler(IPermissionRepository permissions, IFreeSql db)
    {
        _permissions = permissions;
        _db = db;
    }

    /// <summary>执行删除。</summary>
    /// <param name="request">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；内置点或不存在返回 400。</returns>
    public async Task<ApiResponse> Handle(DeletePermissionCommand request, CancellationToken ct)
    {
        var entity = await _permissions.GetByIdAsync(request.PermissionId, ct);
        if (entity is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "权限点不存在");

        if (entity.IsBuiltin)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "内置权限点不可删除，只能停用");
        }

        await _permissions.DeleteAsync(request.PermissionId, ct);

        // 级联清理角色绑定，避免留下指向已删权限点的悬空记录
        await _db.Delete<RolePermission>().Where(a => a.PermissionId == request.PermissionId).ExecuteAffrowsAsync(ct);
        return ApiResponseFactory.Ok("删除成功");
    }
}

/// <summary>启用 / 停用权限点处理器。内置权限点允许停用（这是内置点唯一的「下架」方式）。</summary>
public sealed class ChangePermissionStatusHandler : IRequestHandler<ChangePermissionStatusCommand, ApiResponse>
{
    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    public ChangePermissionStatusHandler(IPermissionRepository permissions) => _permissions = permissions;

    /// <summary>执行状态变更。</summary>
    /// <param name="request">状态变更命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；状态非法或权限点不存在返回 400。</returns>
    public async Task<ApiResponse> Handle(ChangePermissionStatusCommand request, CancellationToken ct)
    {
        if (request.Status is not (1 or 2))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "状态只能是 1 启用 或 2 停用");
        }

        var entity = await _permissions.GetByIdAsync(request.PermissionId, ct);
        if (entity is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "权限点不存在");

        entity.Status = request.Status;
        await _permissions.UpdateAsync(entity, ct);
        return ApiResponseFactory.Ok("状态已更新");
    }
}