using System.Text.Json.Serialization;

namespace CustomerService.Application.Services;

/// <summary>商品摘要的跨服务契约，收藏页一次批量取回展示信息。</summary>
public interface IProductSummaryClient
{
    /// <summary>按 SPU Id 集合取商品摘要。</summary>
    /// <param name="spuIds">SPU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SPU Id → 摘要；未命中或商品服务不可用时返回空字典。</returns>
    /// <remarks>
    /// 返回空字典而不是抛异常：收藏记录本身在客户服务里，商品服务抖动不该让收藏页打不开。
    /// 调用方对缺失项回退成「只有 Id 和收藏时间」的展示数据。
    /// </remarks>
    Task<IReadOnlyDictionary<long, ProductSummary>> GetSpuSummariesAsync(
        IReadOnlyCollection<long> spuIds, CancellationToken ct = default);
}

/// <summary>收藏页展示所需的商品摘要，字段与 ProductService 的内部接口对齐。</summary>
/// <param name="SpuId">商品 SPU Id。</param>
/// <param name="SpuName">商品名。</param>
/// <param name="MainImage">主图。</param>
/// <param name="MinPrice">最低售价。</param>
/// <param name="OriginalPrice">划线原价。</param>
/// <param name="AuditStatus">审核状态。</param>
/// <param name="AuditStatusName">审核状态中文名。</param>
/// <param name="Status">上下架状态。</param>
/// <param name="StatusName">上下架状态中文名。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="DeliveryTypeName">配送方式中文名。</param>
/// <param name="Available">是否可购买：审核通过且已上架。</param>
/// <param name="MerchantId">归属商户 Id。</param>
/// <param name="PlatformId">归属平台 Id。</param>
public sealed record ProductSummary(
    [property: JsonPropertyName("spuId")] long SpuId,
    [property: JsonPropertyName("spuName")] string SpuName,
    [property: JsonPropertyName("mainImage")] string MainImage,
    [property: JsonPropertyName("minPrice")] decimal MinPrice,
    [property: JsonPropertyName("originalPrice")] decimal OriginalPrice,
    [property: JsonPropertyName("auditStatus")] int AuditStatus,
    [property: JsonPropertyName("auditStatusName")] string AuditStatusName,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("statusName")] string StatusName,
    [property: JsonPropertyName("deliveryType")] int DeliveryType,
    [property: JsonPropertyName("deliveryTypeName")] string DeliveryTypeName,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("merchantId")] long MerchantId,
    [property: JsonPropertyName("platformId")] long PlatformId);
