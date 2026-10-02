using Collaboration.Domain.Common;
using MediatR;
using ProductService.Domain.IRepository;
using BrandEntity = ProductService.Domain.Entities.Brand;

namespace ProductService.Application.Features.Brand;

/// <summary>新建品牌处理器。</summary>
public sealed class CreateBrandHandler : IRequestHandler<CreateBrandCommand, ApiResponse<long>>
{
    private readonly IBrandRepository _brands;

    /// <summary>构造处理器。</summary>
    /// <param name="brands">品牌仓储。</param>
    public CreateBrandHandler(IBrandRepository brands) => _brands = brands;

    /// <summary>执行新建。</summary>
    /// <param name="request">新建命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新品牌 Id。</returns>
    public async Task<ApiResponse<long>> Handle(CreateBrandCommand request, CancellationToken ct)
    {
        var name = request.BrandName.Trim();
        if (await _brands.ExistsByNameAsync(name, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该品牌名已存在");
        }

        var brand = new BrandEntity
        {
            BrandName = name,
            Logo = request.Logo?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder,
            Status = request.Status,
            PlatformId = request.PlatformId
        };

        var id = await _brands.InsertAsync(brand, ct);
        return ApiResults.Ok(id, "创建成功");
    }
}

/// <summary>编辑品牌处理器。</summary>
public sealed class UpdateBrandHandler : IRequestHandler<UpdateBrandCommand, ApiResponse>
{
    private readonly IBrandRepository _brands;

    /// <summary>构造处理器。</summary>
    /// <param name="brands">品牌仓储。</param>
    public UpdateBrandHandler(IBrandRepository brands) => _brands = brands;

    /// <summary>执行编辑。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(UpdateBrandCommand request, CancellationToken ct)
    {
        var brand = await _brands.GetByIdAsync(request.BrandId, ct);
        if (brand is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "品牌不存在");

        var name = request.BrandName.Trim();
        if (await _brands.ExistsByNameAsync(name, brand.Id, ct))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该品牌名已存在");
        }

        brand.BrandName = name;
        brand.Logo = request.Logo?.Trim() ?? string.Empty;
        brand.SortOrder = request.SortOrder;
        brand.Status = request.Status;

        await _brands.UpdateAsync(brand, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>删除品牌处理器。</summary>
public sealed class DeleteBrandHandler : IRequestHandler<DeleteBrandCommand, ApiResponse>
{
    private readonly IBrandRepository _brands;

    /// <summary>构造处理器。</summary>
    /// <param name="brands">品牌仓储。</param>
    public DeleteBrandHandler(IBrandRepository brands) => _brands = brands;

    /// <summary>执行删除。</summary>
    /// <param name="request">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；有商品时返回 400。</returns>
    public async Task<ApiResponse> Handle(DeleteBrandCommand request, CancellationToken ct)
    {
        var brand = await _brands.GetByIdAsync(request.BrandId, ct);
        if (brand is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "品牌不存在");

        var productCount = await _brands.CountProductsAsync(brand.Id, ct);
        if (productCount > 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BadRequest,
                $"该品牌下还有 {productCount} 个商品，请先处理商品或改为停用");
        }

        await _brands.DeleteAsync(brand.Id, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>分页查询品牌处理器。</summary>
public sealed class QueryBrandsHandler : IRequestHandler<QueryBrandsCommand, ApiResponse<List<BrandListItem>>>
{
    private readonly IBrandRepository _brands;

    /// <summary>构造处理器。</summary>
    /// <param name="brands">品牌仓储。</param>
    public QueryBrandsHandler(IBrandRepository brands) => _brands = brands;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>品牌列表。</returns>
    public async Task<ApiResponse<List<BrandListItem>>> Handle(QueryBrandsCommand request, CancellationToken ct)
    {
        var (items, _) = await _brands.QueryPagedAsync(
            request.Page, request.PageSize, request.Keyword, request.IncludeDisabled, ct);

        var list = items
            .Select(a => new BrandListItem(a.Id.ToString(), a.BrandName, a.Logo, a.SortOrder, a.Status))
            .ToList();

        return ApiResults.Ok(list);
    }
}