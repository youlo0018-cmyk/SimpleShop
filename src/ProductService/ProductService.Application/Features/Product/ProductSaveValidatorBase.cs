using FluentValidation;
// 本命名空间 Features.Product 会遮蔽同名实体 Product，所以整个 Entities 命名空间起别名。
using ProductEnums = ProductService.Domain.Entities;

namespace ProductService.Application.Features.Product;

/// <summary>
/// 商品「保存类」命令的<strong>共用校验规则</strong>。
/// </summary>
/// <typeparam name="T">命令类型，必须同时实现 <see cref="IProductSaveShape"/>。</typeparam>
/// <remarks>
/// <para><b>为什么要抽成泛型基类而不是直接 Include</b>：FluentValidation 的
/// <c>Include</c> 要求被包含的校验器与当前校验器<strong>泛型参数一致</strong>。
/// <c>SaveProductCommand</c> 与 <c>CreateProductCommand</c> 是两个不同的 record，
/// 互相 <c>Include</c> 在编译期就通不过。所以规则必须挂在一个「与命令类型无关」的基类上。</para>
///
/// <para><b>为什么值得抽</b>：新建与编辑是同一个后台表单的两个入口，字段完全相同。
/// 规则复制一份的后果很具体——以后给商品加了「保质期」字段并只改了编辑的校验，
/// 新建接口就会<strong>静默地不校验它</strong>，而运营用的是同一个表单，
/// 从界面上完全看不出差别，只会在某天出现一条脏数据。</para>
/// </remarks>
public abstract class ProductSaveValidatorBase<T> : AbstractValidator<T> where T : IProductSaveShape
{
    /// <summary>构造基类并挂上共用规则。</summary>
    protected ProductSaveValidatorBase()
    {
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
            .Must(d => d is ProductEnums.DeliveryTypes.PhysicalExpress
                       or ProductEnums.DeliveryTypes.Virtual
                       or ProductEnums.DeliveryTypes.PhysicalSelfPickup)
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

/// <summary>
/// 商品保存类命令的<strong>字段形状</strong>约束。
/// </summary>
/// <remarks>
/// 只声明「两边都有、且校验规则需要用到」的字段。
/// <c>ProductId</c> 刻意<strong>不在这里</strong>：新建没有这个字段，
/// 把它塞进接口就得给个默认值 0，而 0 在编辑语义里恰好是「新建」——
/// 一个「默认值恰好等于另一个语义的哨兵值」的字段，是这类接口最容易出 bug 的地方。
/// </remarks>
public interface IProductSaveShape
{
    /// <summary>商品名。</summary>
    string SpuName { get; }

    /// <summary>副标题。</summary>
    string SubTitle { get; }

    /// <summary>商品分类 Id，必须是第 3 级叶子分类。</summary>
    long CategoryId { get; }

    /// <summary>品牌 Id，0 表示不选。</summary>
    long BrandId { get; }

    /// <summary>主图 URL。</summary>
    string MainImage { get; }

    /// <summary>轮播图，JSON 数组文本。</summary>
    string Images { get; }

    /// <summary>详情图，JSON 数组文本。</summary>
    string DetailImages { get; }

    /// <summary>商品描述。</summary>
    string Description { get; }

    /// <summary>备注。</summary>
    string Remark { get; }

    /// <summary>划线原价，两位小数，0 表示不展示。</summary>
    decimal OriginalPrice { get; }

    /// <summary>排序，小的在前。</summary>
    int SortOrder { get; }

    /// <summary>上下架状态。1 上架 / 2 下架。</summary>
    int Status { get; }

    /// <summary>配送方式。1 实物快递 / 2 虚拟商品 / 3 实物自提。</summary>
    int DeliveryType { get; }

    /// <summary>规格项定义。</summary>
    IReadOnlyList<SpecInput>? Specs { get; }

    /// <summary>SKU 列表。</summary>
    IReadOnlyList<SkuInput>? Skus { get; }
}
