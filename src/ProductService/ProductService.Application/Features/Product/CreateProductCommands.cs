using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
// 本命名空间 Features.Product 会遮蔽同名实体 Product，所以整个 Entities 命名空间起别名。
using ProductEnums = ProductService.Domain.Entities;

namespace ProductService.Application.Features.Product;

/// <summary>
/// 新建商品（DATA_SPEC 5.6 的 <c>products/Create</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么明明有 Save 还要单独一个 Create</b>：因为权限点要能分开授予。
/// <c>product:create</c>（建档）与 <c>product:update</c>（改价、改分类）是两种能力，
/// 现实中经常只给前者。只靠 Save 一个端点时，「能建档」与「能改价」就只能绑在一起，
/// 于是只能二选一：要么给超管以外的账号建档的权限（等于给了改价权限），
/// 要么谁都建不了档。</para>
///
/// <para>字段与 <see cref="SaveProductCommand"/> 完全一致，所以后台的新建与编辑表单
/// 可以共用同一个组件——**刻意不引入第二个字段形状**，两套字段迟早会漂移。</para>
/// </remarks>
public record CreateProductCommand(
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
    IReadOnlyList<SkuInput>? Skus = null) : IRequest<ApiResponse<long>>, IProductSaveShape;

/// <summary>新建商品的处理器。转成 Save 走同一条链路，不重复实现业务规则。</summary>
public sealed class CreateProductHandler : IRequestHandler<CreateProductCommand, ApiResponse<long>>
{
    private readonly IMediator _mediator;

    /// <summary>构造处理器。</summary>
    /// <param name="mediator">MediatR 入口，用来派发 Save。</param>
    /// <remarks>
    /// 处理器里注入 IMediator 而**不是**把 <c>SaveProductHandler</c> 当依赖注入：
    /// MediatR 的处理器是无状态的，直接 new 一个出来会在校验管道之外，
    /// 于是 Save 的校验被跳过——症状是能提交出越界数据。
    /// </remarks>
    public CreateProductHandler(IMediator mediator) => _mediator = mediator;

    /// <inheritdoc />
    /// <remarks>
    /// 只做一件事：<b>把 ProductId 钉死成 0</b>（新建）后交给 Save。
    /// 其余规则（分类必须是第 3 级、划线原价不得低于售价、规格值必须覆盖全部规格项……）
    /// 一律由 Save 的处理器负责，不在这里复制第二份。
    /// </remarks>
    public Task<ApiResponse<long>> Handle(CreateProductCommand request, CancellationToken ct)
        => _mediator.Send(
            new SaveProductCommand(
                ProductId: 0,
                SpuName: request.SpuName,
                CategoryId: request.CategoryId,
                DeliveryType: request.DeliveryType,
                MainImage: request.MainImage,
                PlatformId: request.PlatformId,
                MerchantId: request.MerchantId,
                SubTitle: request.SubTitle,
                BrandId: request.BrandId,
                Images: request.Images,
                DetailImages: request.DetailImages,
                OriginalPrice: request.OriginalPrice,
                Description: request.Description,
                Status: request.Status,
                SortOrder: request.SortOrder,
                Remark: request.Remark,
                Specs: request.Specs,
                Skus: request.Skus),
            ct);
}

/// <summary>新建商品命令的校验器注册。</summary>
public static class CreateProductValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>
    /// 校验器写在嵌套静态类里，<c>AddValidatorsFromAssembly</c> 扫不到，必须显式注册
    /// （漏注册的 symptom 是「该命令完全没有校验」，比启动报错难查得多）。
    /// </remarks>
    public static void AddCreateProductValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CreateProductCommand>, CreateProductValidator>();

    /// <summary>
    /// 新建商品的入参校验。
    /// </summary>
    /// <remarks>
    /// 规则与 <c>SaveProductValidator</c> 完全一致：<b>用 Include 复用而不是复制</b>。
    /// 复制一份的后果很具体——以后给商品加了一个「保质期」字段并只改了 Save 的校验，
    /// 新建接口就会静默地不校验它，而新建和编辑用的是同一个表单，运营根本发现不了。
    /// </remarks>
    private sealed class CreateProductValidator : ProductSaveValidatorBase<CreateProductCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateProductValidator()
        {
            // ProductId 是 Save 独有的字段，这里不校验——
            // 新建命令里根本没有它，传进来也会被忽略（CreateProductHandler 写死 0）。
        }
    }
}
