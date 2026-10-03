using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace MarketingService.Domain.Entities;

/// <summary>秒杀场次。</summary>
/// <remarks>
/// 本期只做单场次，但**所有实体与接口都按场次 Id 寻址**，
/// 「当前场次」只是查询条件之一。要加多场次（每天一场、每两小时一场）时不用改表。
/// </remarks>
[Table(Name = "seckill_session")]
public class SeckillSession : AdminEntityBase
{
    /// <summary>场次名，2-128 字符。</summary>
    [Column(Name = "session_name", StringLength = 128)]
    public string SessionName { get; set; } = string.Empty;

    /// <summary>开始时间（UTC）。</summary>
    [Column(Name = "start_time")]
    public DateTime StartTime { get; set; }

    /// <summary>结束时间（UTC）。</summary>
    [Column(Name = "end_time")]
    public DateTime EndTime { get; set; }

    /// <summary>场次状态，见 <see cref="SeckillSessionStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = SeckillSessionStatuses.NotStarted;

    /// <summary>常规库存是否**已经划出**到本场次。</summary>
    /// <remarks>
    /// 这个标志是整个库存方案（BUSINESS.md 12.4）的关键：重复点「发布」不能把库存再划一遍。
    /// 没有它的话，运营手抖点两次发布，常规库存就被扣走两份，
    /// 而秒杀池子里只有一份货——等于凭空蒸发一批库存。
    /// </remarks>
    [Column(Name = "stock_transferred")]
    public bool StockTransferred { get; set; }

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }
}

/// <summary>场次状态。</summary>
public static class SeckillSessionStatuses
{
    /// <summary>未开始。</summary>
    public const int NotStarted = 10;

    /// <summary>进行中。</summary>
    public const int Running = 20;

    /// <summary>已结束。</summary>
    public const int Ended = 30;

    /// <summary>已取消（手动中止）。<b>剩余库存立即回补常规库存。</b></summary>
    public const int Cancelled = 40;
}

/// <summary>秒杀场次商品。</summary>
/// <remarks>
/// 库存与常规库存<b>完全隔离</b>：发布时从常规库存<b>划出</b>固定数量到 <see cref="SeckillStock"/>，
/// 之后抢购只动这个池子，<b>不再碰常规库存</b>。这样秒杀既不会超卖，也不会吃掉常规库存
/// （BUSINESS.md 12.4）。
/// </remarks>
[Table(Name = "seckill_item")]
public class SeckillItem : AdminEntityBase
{
    /// <summary>场次 Id。<b>所有查询按它寻址。</b></summary>
    [Column(Name = "session_id")]
    public long SessionId { get; set; }

    /// <summary>SPU Id。</summary>
    [Column(Name = "spu_id")]
    public long SpuId { get; set; }

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>商品名快照。</summary>
    [Column(Name = "product_name", StringLength = 128)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格文本快照。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>商品图快照。</summary>
    [Column(Name = "image", StringLength = 512)]
    public string Image { get; set; } = string.Empty;

    /// <summary>秒杀价，两位小数。</summary>
    /// <remarks>
    /// <b>必须低于该 SKU 的常规售价</b>。比原价还贵的「秒杀」没有意义，
    /// 而且会让用户对价格体系失去信任——这是运营配置错误里最容易被滥用的一个。
    /// </remarks>
    [Column(Name = "seckill_price")]
    public decimal SeckillPrice { get; set; }

    /// <summary>划线原价快照。加商品时取当时的售价存下来，之后商品调价不影响本场次展示。</summary>
    /// <remarks>
    /// 前台要展示「原价划线 + 秒杀价大字」（BUSINESS.md 12.7）。
    /// 实时去商品服务查原价是不行的：场次往往提前几天建好，期间商品可能调价甚至下架，
    /// 展示出来的「划线价」会变成一个用户从没见过的数字。
    /// </remarks>
    [Column(Name = "original_price")]
    public decimal OriginalPrice { get; set; }

    /// <summary>已从常规库存划出的数量。</summary>
    [Column(Name = "seckill_stock")]
    public int SeckillStock { get; set; }

    /// <summary>每人每场次限购，1 起。</summary>
    [Column(Name = "per_user_limit")]
    public int PerUserLimit { get; set; } = 1;

    /// <summary>已抢数量。<b>必须 ≤ <see cref="SeckillStock"/></b>（数据库约束兜底）。</summary>
    [Column(Name = "sold_count")]
    public int SoldCount { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;

    /// <summary>配送方式快照，1 实物快递 / 2 虚拟商品 / 3 实物自提。</summary>
    /// <remarks>
    /// 从商品服务取快照存下来。抢购时不再回查商品：秒杀的并发很高，
    /// 每单多一次跨服务调用会把下单链路拖垮；而且场次是提前建好的，
    /// 用「建场次那一刻」的配送方式才与场次里展示的一致。
    /// </remarks>
    [Column(Name = "delivery_type")]
    public int DeliveryType { get; set; } = 1;

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>剩余可抢数量。</summary>
    public int Remaining => Math.Max(0, SeckillStock - SoldCount);
}

/// <summary>秒杀商品状态。</summary>
public static class SeckillItemStatuses
{
    /// <summary>启用。</summary>
    public const int Enabled = 1;

    /// <summary>停用：从秒杀池子里拿掉，不再展示与抢购。</summary>
    public const int Disabled = 2;
}

/// <summary>抢购请求（一行一次尝试）。</summary>
/// <remarks>
/// 存在这张表而不是纯 Redis 的原因很实际：<b>限购额度是钱</b>。
/// Redis 里记「这个客户抢过」在 Redis 丢数据时会失效，用户就能反复抢同一份限购额度。
/// 数据库这一条唯一索引是最后防线。
/// </remarks>
[Table(Name = "seckill_grab")]
public class SeckillGrab : AdminEntityBase
{
    /// <summary>请求 Id，客户端拿它轮询抢购结果。</summary>
    [Column(Name = "request_id", StringLength = 64)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>幂等键，格式 <c>{seckillItemId}:{customerId}</c>。</summary>
    [Column(Name = "biz_no", StringLength = 128)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>场次 Id。</summary>
    [Column(Name = "session_id")]
    public long SessionId { get; set; }

    /// <summary>场次商品 Id。</summary>
    [Column(Name = "item_id")]
    public long ItemId { get; set; }

    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>购买数量，默认 1。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; } = 1;

    /// <summary>抢购结果，见 <see cref="SeckillGrabResults"/>。</summary>
    [Column(Name = "result_status")]
    public int ResultStatus { get; set; } = SeckillGrabResults.Processing;

    /// <summary>结果说明（中文，面向用户）。</summary>
    [Column(Name = "result_message", StringLength = 128)]
    public string ResultMessage { get; set; } = string.Empty;

    /// <summary>下单成功后的订单 Id，0 表示还没下单 / 下单失败。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>下单成功后的订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;
}

/// <summary>抢购结果。</summary>
public static class SeckillGrabResults
{
    /// <summary>处理中（异步下单时用；同步下单一般不会停在这个状态）。</summary>
    public const int Processing = 0;

    /// <summary>成功，订单已创建。</summary>
    public const int Success = 1;

    /// <summary>已被抢完。</summary>
    public const int SoldOut = 2;

    /// <summary>不在抢购中（场次未开始 / 已结束 / 已取消，或商品停用）。</summary>
    public const int NotOnSale = 3;

    /// <summary>超出限购。</summary>
    public const int LimitExceeded = 4;

    /// <summary>下单失败（库存 / 券 / 积分等）。</summary>
    public const int OrderFailed = 5;
}