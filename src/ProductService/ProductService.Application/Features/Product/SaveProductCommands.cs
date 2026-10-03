using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
// 本命名空间 Features.Product 会遮蔽同名实体 Product，所以整个 Entities 命名空间起别名，
// 之后统一写 ProductEnums.ListingStatuses / ProductEnums.DeliveryTypes。
using ProductEnums = ProductService.Domain.Entities;

namespace ProductService.Application.Features.Product;

/// <summary>
/// 保存商品（新建 / 编辑同一个入口）。
/// </summary>
/// <remarks>
/// <b>输入用规格值<b>名称</b>而不是 Id，这一点与 DATA_SPEC 5.7.2 写的 SpecValueIds 不同，是有意为之：</b>
/// 新建时规格值还没落库，Id 根本不存在，前端拿不到；编辑时前端手里也是从编辑页读到的名称。
/// 所以入参用 <see cref="SkuInput"/> 的 SpecValues（按 Specs 顺序对齐，每个元素对上一个规格项的取值），
/// 服务端按名称解析成 Id。<b>响应</b>侧仍然返回解析后的 SpecValueIds，两边都有。
/// 少一层「先建规格再回填」的往返，也不会出现前端拿着过期的 Id 提交。
/// </remarks>
public record SaveProductCommand(
    long ProductId,
    string SpuName,
    long CategoryId,
    int DeliveryType,
    string MainImage,
    long PlatformId = 0,
    long MerchantId = 0,
    string SubTitle = "",
    long BrandId = 0,
    string Images = "",
    string DetailImages = "",
    decimal OriginalPrice = 0,
    string Description = "",
    int Status = ProductEnums.ListingStatuses.OffShelf,
    int SortOrder = 0,
    string Remark = "",
    IReadOnlyList<SpecInput>? Specs = null,
    IReadOnlyList<SkuInput>? Skus = null) : IRequest<ApiResponse<long>>;

/// <summary>规格项及其取值。</summary>
/// <param name="SpecName">规格项名，如「颜色」「尺码」。</param>
/// <param name="SpecValues">取值列表，如 红 / 蓝。</param>
public record SpecInput(string SpecName, IReadOnlyList<string> SpecValues);

/// <summary>SKU 输入。</summary>
/// <param name="SkuCode">SKU 编码，全局唯一，是 Upsert 的依据。</param>
/// <param name="SpecValues">按 Specs 顺序对齐的规格值名称，每项一个。</param>
/// <param name="Price">售价，必须大于 0。</param>
/// <param name="OriginalPrice">划线原价，0 或不小于售价。</param>
/// <param name="Stock">初始库存，只用于初始化，之后由 InventoryService 维护。</param>
/// <param name="Image">SKU 图。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
public record SkuInput(
    string SkuCode,
    IReadOnlyList<string> SpecValues,
    decimal Price,
    decimal OriginalPrice = 0,
    int Stock = 0,
    string Image = "",
    int Status = 1);

/// <summary>提交审核 / 审核。</summary>
/// <param name="ProductId">商品 Id。</param>
/// <param name="AuditStatus">审核结果，20 已通过 / 30 已驳回。</param>
/// <param name="Reason">审核理由。</param>
public record ChangeProductAuditCommand(long ProductId, int AuditStatus, string Reason = "") : IRequest<ApiResponse>;

/// <summary>提交审核（把商品送回待审核）。</summary>
/// <param name="ProductId">商品 Id。</param>
public record SubmitProductAuditCommand(long ProductId) : IRequest<ApiResponse>;

/// <summary>上下架。上架的前置条件是审核已通过。</summary>
/// <param name="ProductId">商品 Id。</param>
/// <param name="Status">1 上架 / 2 下架。</param>
public record ChangeProductListingCommand(long ProductId, int Status) : IRequest<ApiResponse>;

/// <summary>删除商品。</summary>
/// <param name="ProductId">商品 Id。</param>
public record DeleteProductCommand(long ProductId) : IRequest<ApiResponse>;

/// <summary>商品详情。</summary>
/// <param name="ProductId">商品 Id。</param>
public record QueryProductDetailCommand(long ProductId) : IRequest<ApiResponse<ProductDetailDto>>;

/// <summary>分页查询商品。</summary>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">按商品名模糊搜索。</param>
/// <param name="CategoryId">按分类过滤。</param>
/// <param name="BrandId">按品牌过滤。</param>
/// <param name="Status">按上下架过滤，0 不限。</param>
/// <param name="AuditStatus">按审核状态过滤，0 不限。</param>
public record QueryProductsCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    long CategoryId = 0,
    long BrandId = 0,
    int Status = 0,
    int AuditStatus = 0) : IRequest<ApiResponse<List<ProductListItem>>>;

/// <summary>商品详情。含规格、SKU 与解析后的规格值 Id。</summary>
public record ProductDetailDto(
    string Id,
    string SpuName,
    string SubTitle,
    long BrandId,
    string BrandName,
    long CategoryId,
    string CategoryName,
    int DeliveryType,
    string MainImage,
    string Images,
    string DetailImages,
    decimal OriginalPrice,
    decimal MinPrice,
    decimal MaxPrice,
    string Description,
    int AuditStatus,
    int Status,
    long Sales,
    decimal EvaluationScore,
    int EvaluationCount,
    IReadOnlyList<ProductSpecDto> Specs,
    IReadOnlyList<SkuDto> Skus);

/// <summary>规格项及其取值（含取值 Id，供编辑页回填）。</summary>
/// <param name="SpecId">规格项 Id。</param>
/// <param name="SpecName">规格项名。</param>
/// <param name="Values">取值列表。</param>
public record ProductSpecDto(string SpecId, string SpecName, IReadOnlyList<ProductSpecValueDto> Values);

/// <summary>规格值。</summary>
/// <param name="ValueId">规格值 Id。</param>
/// <param name="ValueName">规格值名。</param>
public record ProductSpecValueDto(string ValueId, string ValueName);

/// <summary>SKU 明细。</summary>
public record SkuDto(
    string Id,
    string SkuCode,
    string SkuName,
    string SkuSpecText,
    decimal Price,
    decimal OriginalPrice,
    string Image,
    int Status,
    IReadOnlyList<string> SpecValueIds);

/// <summary>商品列表项。冗余返回品牌名 / 分类名 / 价格区间，前端不必二次查询（DATA_SPEC 4.4）。</summary>
public record ProductListItem(
    string Id,
    string SpuName,
    string MainImage,
    string BrandName,
    string CategoryName,
    int DeliveryType,
    decimal MinPrice,
    decimal MaxPrice,
    int AuditStatus,
    int Status,
    long Sales);

/// <summary>商品命令的校验器注册。</summary>
public static class ProductValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddProductValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<SaveProductCommand>, SaveProductValidator>();
        services.AddScoped<IValidator<ChangeProductAuditCommand>, ChangeProductAuditValidator>();
        services.AddScoped<IValidator<SubmitProductAuditCommand>, SubmitProductAuditValidator>();
        services.AddScoped<IValidator<ChangeProductListingCommand>, ChangeProductListingValidator>();
        services.AddScoped<IValidator<DeleteProductCommand>, DeleteProductValidator>();
    }

    /// <summary>保存商品的入参校验。</summary>
    /// <remarks>
    /// 这里只管「形状」（长度、范围、数量上限），<b>跨字段的业务规则</b>放在处理器里：
    /// 例如「SKU 的规格值必须覆盖全部规格项」「划线原价不得低于售价」「分类必须是第 3 级」
    /// —— 它们要查库或比对集合，不是单字段校验能表达的。
    /// </remarks>
    private sealed class SaveProductValidator : AbstractValidator<SaveProductCommand>
    {
        /// <summary>构造校验器。</summary>
        public SaveProductValidator()
        {
            RuleFor(x => x.ProductId).GreaterThanOrEqualTo(0).WithMessage("商品 Id 不能为负数");
            RuleFor(x => x.SpuName).NotEmpty().Length(2, 128).WithMessage("商品名必须为 2-128 个字符");
            RuleFor(x => x.SubTitle).MaximumLength(200).WithMessage("副标题最多 200 个字符");
            RuleFor(x => x.CategoryId).GreaterThan(0).WithMessage("必须选择商品分类");
            RuleFor(x => x.BrandId).GreaterThanOrEqualTo(0).WithMessage("品牌 Id 不能为负数");
            RuleFor(x => x.MainImage).NotEmpty().MaximumLength(512).WithMessage("请上传商品主图");
            RuleFor(x => x.Images).MaximumLength(2000).WithMessage("轮播图数据过长");
            RuleFor(x => x.DetailImages).MaximumLength(2000).WithMessage("详情图数据过长");
            RuleFor(x => x.Description).MaximumLength(4000).WithMessage("商品描述最多 4000 个字符");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
            RuleFor(x => x.OriginalPrice).GreaterThanOrEqualTo(0).WithMessage("划线原价不能为负数");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 上架 或 2 下架");

            RuleFor(x => x.DeliveryType)
                .Must(d => d is ProductEnums.DeliveryTypes.PhysicalExpress or ProductEnums.DeliveryTypes.Virtual or ProductEnums.DeliveryTypes.PhysicalSelfPickup)
                .WithMessage("配送方式只能是 1 实物快递 / 2 虚拟商品 / 3 实物自提");

            // 金额一律两位小数（DATA_SPEC「金额舍入口径」）
            RuleFor(x => x.OriginalPrice)
                .Must(p => p == decimal.Round(p, 2))
                .WithMessage("划线原价最多两位小数");

            RuleFor(x => x.Specs)
                .NotNull().WithMessage("必须提供规格定义")
                .Must(s => s!.Count > 0).WithMessage("至少要有一个规格项")
                .Must(s => s!.Count <= 5).WithMessage("规格项最多 5 个");

            RuleFor(x => x.Skus)
                .NotNull().WithMessage("必须提供 SKU 列表")
                .Must(s => s!.Count > 0).WithMessage("至少要有一个 SKU")
                .Must(s => s!.Count <= 100).WithMessage("SKU 最多 100 个");
        }
    }

    /// <summary>审核命令校验。</summary>
    private sealed class ChangeProductAuditValidator : AbstractValidator<ChangeProductAuditCommand>
    {
        /// <summary>构造校验器。</summary>
        public ChangeProductAuditValidator()
        {
            RuleFor(x => x.ProductId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
            RuleFor(x => x.AuditStatus)
                .Must(s => s == ProductEnums.AuditStatuses.Approved || s == ProductEnums.AuditStatuses.Rejected)
                .WithMessage("审核结果只能是 20 已通过 或 30 已驳回");
            RuleFor(x => x.Reason).MaximumLength(512).WithMessage("审核理由最多 512 个字符");
        }
    }

    /// <summary>上下架命令校验。</summary>
    private sealed class ChangeProductListingValidator : AbstractValidator<ChangeProductListingCommand>
    {
        /// <summary>构造校验器。</summary>
        public ChangeProductListingValidator()
            => RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 上架 或 2 下架");
    }

    /// <summary>提交审核命令校验。</summary>
    private sealed class SubmitProductAuditValidator : AbstractValidator<SubmitProductAuditCommand>
    {
        /// <summary>构造校验器。</summary>
        public SubmitProductAuditValidator()
            => RuleFor(x => x.ProductId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
    }

    /// <summary>删除命令校验。</summary>
    private sealed class DeleteProductValidator : AbstractValidator<DeleteProductCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteProductValidator()
            => RuleFor(x => x.ProductId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
    }
}
