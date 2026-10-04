namespace ProductService.Application.Features.Product;

/// <summary>商品变更事件载荷（product.changed）。</summary>
/// <remarks>
/// 消费方拿到这些就够了：索引同步要 Id 与上下架状态，搜索重算要平台 / 商户归属。
/// <b>不放整份商品实体</b>——事件会长期留在队列里，实体字段一改，
/// 几个月前排队的旧消息就反序列化不出来了。
/// </remarks>
public sealed record ProductChangedEvent(
    long ProductId,
    string SpuName,
    long PlatformId,
    long MerchantId,
    int AuditStatus,
    int Status);
