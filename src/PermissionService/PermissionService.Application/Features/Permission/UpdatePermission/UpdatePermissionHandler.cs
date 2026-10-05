using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Permission.UpdatePermission;

/// <summary>编辑权限点处理器。</summary>
public sealed class UpdatePermissionHandler : IRequestHandler<UpdatePermissionCommand, ApiResponse>
{
    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    public UpdatePermissionHandler(IPermissionRepository permissions) => _permissions = permissions;

    /// <summary>执行编辑。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编辑结果。</returns>
    public async Task<ApiResponse> Handle(UpdatePermissionCommand request, CancellationToken ct)
    {
        var entity = await _permissions.GetByIdAsync(request.PermissionId, ct);
        if (entity is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "权限点不存在");
        }

        if (entity.IsBuiltin)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.Forbidden, "内置权限点只能停用，不能编辑");
        }

        var name = request.Name.Trim();
        if (await _permissions.ExistsByNameAsync(name, entity.ParentId, entity.Id, ct))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "同一父节点下已存在同名权限");
        }

        var code = request.Code?.Trim();
        if (!string.IsNullOrEmpty(code) &&
            await _permissions.ExistsByCodeAsync(code, entity.Id, ct))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该权限编码已存在");
        }

        entity.Name = name;
        entity.Code = code ?? string.Empty;
        entity.ApiPath = request.ApiPath?.Trim() ?? string.Empty;
        entity.SortOrder = request.SortOrder;
        entity.Description = request.Description ?? string.Empty;

        var affected = await _permissions.UpdateAsync(entity, ct);
        return affected > 0
            ? ApiResponseFactory.Ok("编辑成功")
            : ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "权限点不存在");
    }
}
