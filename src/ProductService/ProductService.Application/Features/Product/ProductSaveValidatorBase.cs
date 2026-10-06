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

        // 图片字段的完整规则（DATA_SPEC 5.6）：轮播图 ≤ 6 张、详情图 ≤ 9 张，每张地址 ≤ 255 字符。
        // 只校验总长度是不够的：2000 个字符能塞下十几张短地址，而前台轮播只按 6 张渲染 ——
        // 多出来的图运营配了、用户看不见，谁也不知道是哪几张丢了。
        RuleFor(x => x.Images).Must(v => ParseImageList(v) is not null)
            .WithMessage("轮播图格式不正确（应为 JSON 数组）");
        RuleFor(x => x.Images).Must(v => (ParseImageList(v)?.Count ?? 0) <= 6)
            .WithMessage("轮播图最多 6 张");
        RuleFor(x => x.Images).Must(v => AllUrlsWithin(v, 255))
            .WithMessage("轮播图每张地址不超过 255 个字符");

        RuleFor(x => x.DetailImages).Must(v => ParseImageList(v) is not null)
            .WithMessage("详情图格式不正确（应为 JSON 数组）");
        RuleFor(x => x.DetailImages).Must(v => (ParseImageList(v)?.Count ?? 0) <= 9)
            .WithMessage("详情图最多 9 张");
        RuleFor(x => x.DetailImages).Must(v => AllUrlsWithin(v, 255))
            .WithMessage("详情图每张地址不超过 255 个字符");
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
            // ⚠️ CascadeMode.Stop 是**必须的**：FluentValidation 默认的级联是 Continue，
            // 也就是 NotNull() 失败之后，同一条链上的 Must(...) **照样会执行**。
            // 于是 `s!.Count > 0` 在 s 为 null 时照样跑——那个 `!` 只是编译期断言，
            // 运行时不做任何检查，结果是 NullReferenceException。
            //
            // 症状极难定位：接口回 500「服务器内部错误」而不是 400「请填写规格」，
            // 日志里只有一句 NullReferenceException，看不出是哪条规则炸的。
            // 真实踩过：products/Save 与 products/Create 传空规格时**都**是 500。
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("必须提供规格定义")
            .Must(s => s!.Count > 0).WithMessage("至少要有一个规格项")
            .Must(s => s!.Count <= 5).WithMessage("规格项最多 5 个");

        RuleFor(x => x.Skus)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("必须提供 SKU 列表")
            .Must(s => s!.Count > 0).WithMessage("至少要有一个 SKU")
            .Must(s => s!.Count <= 100).WithMessage("SKU 最多 100 个");
    }

    /// <summary>解析图片 JSON 数组。</summary>
    /// <param name="json">JSON 数组文本，允许为空（视为空数组）。</param>
    /// <returns>图片地址列表；<b>非法 JSON 返回 null</b>（与「空数组」区分开）。</returns>
    /// <remarks>
    /// 刻意吞掉解析异常返回 null：校验器里抛异常会变成 500，
    /// 而运营填错格式应该得到一句 400 的提示。
    /// </remarks>
    private static List<string>? ParseImageList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>所有图片地址是否都在长度上限内。</summary>
    /// <param name="json">JSON 数组文本。</param>
    /// <param name="maxLength">单张地址的长度上限。</param>
    /// <returns>都合规返回 true；格式非法时返回 true（交给「格式不正确」那条规则报）。</returns>
    private static bool AllUrlsWithin(string? json, int maxLength)
    {
        var list = ParseImageList(json);
        return list is null || list.All(a => a is not null && a.Length <= maxLength);
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
