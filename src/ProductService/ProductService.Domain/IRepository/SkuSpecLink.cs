namespace ProductService.Domain.IRepository;

/// <summary>
/// 「SKU 编码 → 它要挂哪些规格值」的模板，<b>刻意不带 SkuId</b>。
/// </summary>
/// <remarks>
/// 带 SkuId 的写法有个隐蔽的坑：新建商品时 SKU 还没有 Id（Id 是雪花，
/// 要等真正 insert 时才生成），如果调用方提前把链接建好，链接里的 SkuId 全是 0，
/// 四个 SKU 的链接会撞成 (0, 红值) 这样的重复主键，插入直接报唯一键冲突。
/// 所以这里只描述「这个编码要连哪些规格值」，具体的 SkuId 由仓储在拿到真实 Id 后补上。
/// </remarks>
public sealed record SkuSpecLink(long SpecId, long SpecValueId);