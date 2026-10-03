using Collaboration.Domain.Common;
using Collaboration.Domain.Infrastructure;
using MediatR;
using Microsoft.Extensions.Logging;
// 本命名空间 Features.Product 会遮蔽同名实体 Product，枚举同样从别名命名空间取。
using ProductEnums = ProductService.Domain.Entities;
using ProductService.Application.Services;
using ProductService.Domain.IRepository;
using ProductService.Application.Features.Category;   // CategoryLevels 在这一层
using ProductEntity = ProductService.Domain.Entities.Product;
using ProductSpec = ProductService.Domain.Entities.ProductSpec;
using ProductSpecValue = ProductService.Domain.Entities.ProductSpecValue;
using Sku = ProductService.Domain.Entities.Sku;
using SkuSpecValue = ProductService.Domain.Entities.SkuSpecValue;

namespace ProductService.Application.Features.Product;

/// <summary>保存商品处理器（新建 / 编辑）。</summary>
/// <remarks>
/// 注意 using：当前命名空间是 ...Features.Product，而实体也叫 Product，
/// 必须给实体起类型别名，否则编译器会把裸写的 Product 当成命名空间（CS0118）。
/// 这是 CODING_STANDARD 里的第 1 号陷阱，本项目已踩多次。
/// </remarks>
public sealed class SaveProductHandler : IRequestHandler<SaveProductCommand, ApiResponse<long>>
{
    private readonly IProductRepository _products;
    private readonly ICategoryRepository _categories;
    private readonly IBrandRepository _brands;
    private readonly IInventoryClient _inventory;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<SaveProductHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="categories">分类仓储。</param>
    /// <param name="brands">品牌仓储。</param>
    /// <param name="inventory">库存服务客户端，用于建商品时初始化 SKU 库存。</param>
    /// <param name="search">商品搜索索引，用于保存后同步到 ES。</param>
    /// <param name="logger">日志器。</param>
    public SaveProductHandler(
        IProductRepository products,
        ICategoryRepository categories,
        IBrandRepository brands,
        IInventoryClient inventory,
        IProductSearchIndex search,
        ILogger<SaveProductHandler> logger)
    {
        _products = products;
        _categories = categories;
        _brands = brands;
        _inventory = inventory;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行保存。</summary>
    /// <param name="request">保存命令，形状已由校验器保证。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回商品 Id。</returns>
    public async Task<ApiResponse<long>> Handle(SaveProductCommand request, CancellationToken ct)
    {
        var specs = request.Specs!;
        var skuInputs = request.Skus!;

        // ---- 1. 分类必须是第 3 级（叶子）----
        var category = await _categories.GetByIdAsync(request.CategoryId, ct);
        if (category is null) return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "商品分类不存在");
        if (category.Level != CategoryLevels.Max)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BadRequest,
                $"商品只能挂在第 {CategoryLevels.Max} 级（叶子）分类下，「{category.CategoryName}」是第 {category.Level} 级");
        }

        // ---- 2. 品牌是选填项，但填了就必须存在且启用 ----
        var brandName = string.Empty;
        if (request.BrandId > 0)
        {
            var brand = await _brands.GetByIdAsync(request.BrandId, ct);
            if (brand is null) return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "所选品牌不存在");
            brandName = brand.BrandName;
        }

        // ---- 3. 规格：项名不重复、每项至少一个值、项内值不重复 ----
        var specNames = new List<string>();
        var specValueNames = new List<List<string>>();

        foreach (var spec in specs)
        {
            var specName = spec.SpecName.Trim();
            if (specName.Length is < 1 or > 32)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "规格项名必须为 1-32 个字符");
            }

            if (specNames.Contains(specName, StringComparer.Ordinal))
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"规格项「{specName}」重复");
            }

            if (spec.SpecValues.Count == 0)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"规格项「{specName}」至少要有一个取值");
            }

            var values = new List<string>();
            foreach (var raw in spec.SpecValues)
            {
                var valueName = raw.Trim();
                if (valueName.Length is < 1 or > 32)
                {
                    return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"规格值名必须为 1-32 个字符（规格项「{specName}」）");
                }

                if (values.Contains(valueName, StringComparer.Ordinal))
                {
                    return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"规格项「{specName}」下的取值「{valueName}」重复");
                }

                values.Add(valueName);
            }

            specNames.Add(specName);
            specValueNames.Add(values);
        }

        // ---- 4. SKU 校验 ----
        var codes = new List<string>();
        var resolvedSkus = new List<Sku>();
        // 请求的初始库存。<b>刻意不进 Sku 实体</b>：库存归 InventoryService 管，
        // 商品这边存一份就成了两份真相。所以这里用平行数组带过去，下完单即弃。
        var requestedStocks = new List<int>();
        var resolvedNames = new List<List<string>>();

        foreach (var input in skuInputs)
        {
            var code = input.SkuCode.Trim();
            if (code.Length is < 1 or > 64)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "SKU 编码必须为 1-64 个字符");
            }

            if (codes.Contains(code, StringComparer.Ordinal))
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"SKU 编码「{code}」在本次提交里重复");
            }

            // 编码全局唯一：既不能撞本商品的旧编码，也不能撞别的商品的
            var existing = await _products.GetSkuByCodeAsync(code, ct);
            if (existing is not null && existing.ProductId != request.ProductId)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"SKU 编码「{code}」已被其它商品占用");
            }

            if (input.Price <= 0)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"SKU「{code}」的售价必须大于 0");
            }

            if (input.OriginalPrice != 0 && input.OriginalPrice < input.Price)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"SKU「{code}」的划线原价不能低于售价");
            }

            if (input.Stock < 0)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, $"SKU「{code}」的初始库存不能为负数");
            }

            // 规格值必须**恰好覆盖每一个规格项**。
            // 少一个：用户选到这个 SKU 时有个维度选不出来；多一个：对不上规格项。
            if (input.SpecValues.Count != specs.Count)
            {
                return ApiResults.Fail<long>(
                    BaseApiResponseCode.BadRequest,
                    $"SKU「{code}」必须为全部 {specs.Count} 个规格项各选一个取值，当前给了 {input.SpecValues.Count} 个");
            }

            var picked = new List<string>();
            for (var i = 0; i < specs.Count; i++)
            {
                var pickedValue = input.SpecValues[i].Trim();
                if (!specValueNames[i].Contains(pickedValue, StringComparer.Ordinal))
                {
                    return ApiResults.Fail<long>(
                        BaseApiResponseCode.BadRequest,
                        $"SKU「{code}」在规格项「{specNames[i]}」上选了「{pickedValue}」，但该规格项没有这个取值");
                }

                picked.Add(pickedValue);
            }

            codes.Add(code);
            resolvedNames.Add(picked);
            requestedStocks.Add(input.Stock);
            resolvedSkus.Add(new Sku
            {
                Id = existing?.Id ?? 0,
                ProductId = request.ProductId,
                SkuCode = code,
                SkuName = request.SpuName.Trim() + " " + string.Join(" / ", picked),
                SkuSpecText = string.Join(" / ", picked),
                Price = Round2(input.Price),
                OriginalPrice = Round2(input.OriginalPrice),
                Image = input.Image?.Trim() ?? string.Empty,
                Status = input.Status
            });
        }

        // 商品级划线原价不得低于最高的 SKU 售价，否则会出现「划线价低于售价」
        var maxSkuPrice = resolvedSkus.Count == 0 ? 0m : resolvedSkus.Max(a => a.Price);
        var productOriginalPrice = Round2(request.OriginalPrice);
        if (productOriginalPrice != 0 && productOriginalPrice < maxSkuPrice)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BadRequest,
                $"划线原价（{productOriginalPrice:0.00}）不能低于最高的 SKU 售价（{maxSkuPrice:0.00}）");
        }

        // ---- 5. 落库 ----
        ProductEntity product;
        bool isCreate = request.ProductId <= 0;

        if (isCreate)
        {
            // 新建固定待审核 + 默认下架。传入的 Status 只在前台明确要上架时才生效，
            // 但审核未通过时上架请求会在下面的上架校验里被拒。
            product = new ProductEntity
            {
                SpuName = request.SpuName.Trim(),
                SubTitle = request.SubTitle?.Trim() ?? string.Empty,
                BrandId = request.BrandId,
                BrandName = brandName,
                CategoryId = request.CategoryId,
                CategoryName = category.CategoryName,
                DeliveryType = request.DeliveryType,
                MainImage = request.MainImage.Trim(),
                Images = request.Images ?? string.Empty,
                DetailImages = request.DetailImages ?? string.Empty,
                OriginalPrice = productOriginalPrice,
                Description = request.Description ?? string.Empty,
                AuditStatus = ProductEnums.AuditStatuses.Pending,
                Status = request.Status,
                SortOrder = request.SortOrder,
                Remark = request.Remark?.Trim() ?? string.Empty,
                PlatformId = request.PlatformId,
                MerchantId = request.MerchantId
            };

            var newId = await _products.InsertAsync(product, ct);
            foreach (var sku in resolvedSkus) sku.ProductId = newId;
            product.Id = newId;
        }
        else
        {
            product = await _products.GetByIdAsync(request.ProductId, ct)
                ?? throw new InvalidOperationException("商品不存在，处理器不应走到这里。");

            product.SpuName = request.SpuName.Trim();
            product.SubTitle = request.SubTitle?.Trim() ?? string.Empty;
            product.BrandId = request.BrandId;
            product.BrandName = brandName;
            product.CategoryId = request.CategoryId;
            product.CategoryName = category.CategoryName;
            product.DeliveryType = request.DeliveryType;
            product.MainImage = request.MainImage.Trim();
            product.Images = request.Images ?? string.Empty;
            product.DetailImages = request.DetailImages ?? string.Empty;
            product.OriginalPrice = productOriginalPrice;
            product.Description = request.Description ?? string.Empty;
            product.SortOrder = request.SortOrder;
            product.Remark = request.Remark?.Trim() ?? string.Empty;

            // 刻意**不**碰 AuditStatus：编辑不重置审核状态，需重新提交才回到待审核（DATA_SPEC 5.6）
            if (request.Status == ProductEnums.ListingStatuses.OnShelf && product.AuditStatus != ProductEnums.AuditStatuses.Approved)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "审核未通过的商品不能上架");
            }

            product.Status = request.Status;
            await _products.UpdateAsync(product, ct);
        }

        // 规格重建：先软删旧的再插入新的。
        // 不做增量 diff 是因为 SKU 的 SpecValueIds 是指向具体取值行的，
        // 值行一旦被复用/重建，历史 SKU 的关联就会指向不该指的东西。
        await _products.SoftDeleteSpecsAsync(product.Id, ct);

        var newSpecs = new List<ProductSpec>();
        var newValues = new List<ProductSpecValue>();
        var valueIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);

        for (var i = 0; i < specNames.Count; i++)
        {
            var spec = new ProductSpec
            {
                Id = SnowflakeId.NewId(),
                ProductId = product.Id,
                SpecName = specNames[i],
                SortOrder = i,
                CreatedAt = DateTime.UtcNow
            };
            newSpecs.Add(spec);

            for (var j = 0; j < specValueNames[i].Count; j++)
            {
                var value = new ProductSpecValue
                {
                    Id = SnowflakeId.NewId(),
                    SpecId = spec.Id,
                    ProductId = product.Id,
                    ValueName = specValueNames[i][j],
                    SortOrder = j,
                    CreatedAt = DateTime.UtcNow
                };
                newValues.Add(value);

                // 键用「规格项名 + 分隔符 + 取值名」，避免不同规格项下的同名取值撞车
                // （比如「颜色/红」与「尺码/红」不是同一个值）
                valueIdByKey[$"{specNames[i]}\u0001{specValueNames[i][j]}"] = value.Id;
            }
        }

        await _products.InsertSpecsAsync(newSpecs, newValues, ct);

        // 用「SKU 编码 → 规格值模板」而不是「SkuId → 规格值」：
        // 新建商品的 SKU 此刻还没有 Id（雪花 Id 是 insert 时才生成的），
        // 现在就拼链接的话多个 SKU 的链接全是 (0, 红值)，复合主键直接撞车。
        // 仓储拿到真实 Id 之后自己补链接。
        var linksBySkuCode = new Dictionary<string, IReadOnlyList<SkuSpecLink>>(StringComparer.Ordinal);
        for (var i = 0; i < resolvedSkus.Count; i++)
        {
            var sku = resolvedSkus[i];
            var picked = resolvedNames[i];

            var templates = new List<SkuSpecLink>();
            for (var j = 0; j < specNames.Count; j++)
            {
                var key = $"{specNames[j]}\u0001{picked[j]}";
                templates.Add(new SkuSpecLink(newSpecs[j].Id, valueIdByKey[key]));
            }

            linksBySkuCode[sku.SkuCode] = templates;
        }

        await _products.UpsertSkusAsync(resolvedSkus, linksBySkuCode, ct);
        await _products.SoftDeleteSkusNotInAsync(product.Id, codes, ct);
        await _products.RefreshPriceRangeAsync(product.Id, ct);

        // 🔴 只有**新建**才初始化库存：编辑页不提供改库存的入口（DATA_SPEC 5.7.2），
        // 库存之后的增减只能走 InventoryService 的锁定 / 扣减 / 释放 / 回补。
        // 库存初始化失败要让整个保存失败——一个没有库存记录的 SKU 是永远买不了的，
        // 静默放过会留下一批「看着正常、实际缺货」的商品，事后极难排查。
        if (isCreate)
        {
            for (var i = 0; i < resolvedSkus.Count; i++)
            {
                var sku = resolvedSkus[i];
                var initialStock = requestedStocks[i];

                // BizNo 用 SKU 编码：同一编码重复提交只会初始化一次，天然幂等
                var ok = await _inventory.InitAsync(
                    sku.Id, initialStock, request.SpuName.Trim(), sku.SkuSpecText, 0, sku.SkuCode, ct);

                if (!ok)
                {
                    return ApiResults.Fail<long>(
                        BaseApiResponseCode.BusinessError,
                        $"SKU「{sku.SkuCode}」初始化库存失败，商品未保存。请确认库存服务可用后重试。");
                }
            }
        }

        // 保存成功后同步搜索索引。
        // 🔴 索引失败**不返回错误**：保存商品是主链路，ES 只是加速手段。
        // 索引靠补偿任务补齐（见 AI_HANDOFF 的「还没做」清单）。
        // 这里返回失败会让用户以为商品没保存，于是再点一次保存——那才是真的重复商品。
        await SyncSearchAsync(product, ct);

        return ApiResults.Ok(product.Id, isCreate ? "创建成功" : "保存成功");
    }

    /// <summary>金额统一四舍五入到两位小数。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数的金额。</returns>
    /// <remarks>用 MidpointRounding.AwayFromZero 而不是默认的 ToEven：
    /// 「四舍五入」在中文语境里指 0.5 进位，而 .NET 默认的银行家舍入会把 2.345 舍成 2.34。</remarks>
    internal static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    /// <summary>把商品同步到搜索索引。失败只记日志，不影响业务结果。</summary>
    /// <param name="product">已保存的商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task SyncSearchAsync(ProductEntity product, CancellationToken ct)
    {
        try
        {
            await _search.IndexAsync(product, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "同步商品到搜索索引失败：{ProductId}", product.Id);
        }
    }
}
