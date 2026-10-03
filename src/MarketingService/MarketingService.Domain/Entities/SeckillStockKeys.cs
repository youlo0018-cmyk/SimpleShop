namespace MarketingService.Domain.Entities;

/// <summary>秒杀相关的 Redis 键前缀。</summary>
/// <remarks>
/// 单独抽出来是因为<b>发布场次写这个键、抢购读这个键</b>，两处各写一遍字符串的话，
/// 改了一处就会导致「发布了但抢购读不到余量」，而现象是所有人抢到 0 件——
/// 这种错现场几乎看不出来，只能对键名。
/// </remarks>
public static class SeckillStockKeys
{
    /// <summary>秒杀余量键前缀，后接商品 Id。</summary>
    /// <remarks>值 = 剩余可抢数量，抢购时 DECBY 1。</remarks>
    public const string Stock = "seckill:stock:";
}