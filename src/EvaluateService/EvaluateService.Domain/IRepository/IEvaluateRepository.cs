using EvaluateService.Domain.Entities;
using EvaluateService.Domain.Exceptions;

namespace EvaluateService.Domain.IRepository;

/// <summary>评价仓储。</summary>
public interface IEvaluateRepository
{
    /// <summary>按 Id 取首评（忽略隐藏状态）。</summary>
    /// <param name="evaluateId">评价 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<Evaluate?> GetByIdAsync(long evaluateId, CancellationToken ct = default);

    /// <summary>取首评及其 SKU 标记。</summary>
    /// <param name="evaluateId">评价 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>首评；找不到返回 null。</returns>
    Task<EvaluateWithRefs?> GetWithRefsAsync(long evaluateId, CancellationToken ct = default);

    /// <summary>插入首评与 SKU 标记（同事务）。</summary>
    /// <param name="evaluate">首评。</param>
    /// <param name="skuRefs">SKU 标记。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>首评 Id。</returns>
    /// <exception cref="DuplicateEvaluateException">该订单已评过这个 SPU。</exception>
    Task<long> InsertWithRefsAsync(Evaluate evaluate, IReadOnlyCollection<EvaluateSkuRef> skuRefs,
        CancellationToken ct = default);

    /// <summary>取某个订单已评价的 SPU Id 集合。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已评价的 SPU Id 集合。</returns>
    Task<IReadOnlySet<long>> GetEvaluatedSpuIdsAsync(string orderNo, CancellationToken ct = default);

    /// <summary>取订单下某个 SPU 的首评。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="spuId">SPU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回首评，否则 null。</returns>
    Task<Evaluate?> GetByOrderAndSpuAsync(string orderNo, long spuId, CancellationToken ct = default);

    /// <summary>分页查某 SPU 的可见首评（最新优先）。</summary>
    /// <param name="spuId">SPU Id。</param>
    /// <param name="skuId">按 SKU 过滤，0 表示不过滤。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedEvaluates> PageBySpuAsync(long spuId, long skuId, int page, int pageSize,
        CancellationToken ct = default);

    /// <summary>分页查某客户的评价（我的评价）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedEvaluates> PageByCustomerAsync(long customerId, int page, int pageSize,
        CancellationToken ct = default);

    /// <summary>分页查评价（后台，支持按 SPU / 商户 / 星级 / 隐藏状态过滤）。</summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedEvaluates> PageForAdminAsync(EvaluateAdminFilter filter, CancellationToken ct = default);

    /// <summary>隐藏 / 取消隐藏。</summary>
    /// <param name="evaluateId">评价 Id。</param>
    /// <param name="isHidden">是否隐藏。</param>
    /// <param name="reason">隐藏原因。</param>
    /// <param name="operatorId">操作人 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> SetHiddenAsync(long evaluateId, bool isHidden, string reason, long operatorId,
        CancellationToken ct = default);

    /// <summary>取某首评的追评列表（按时间升序）。</summary>
    /// <param name="evaluateId">首评 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追评列表。</returns>
    Task<IReadOnlyList<EvaluateAppend>> GetAppendsAsync(long evaluateId, CancellationToken ct = default);

    /// <summary>取追评数量。</summary>
    /// <param name="evaluateId">首评 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追评条数。</returns>
    Task<int> CountAppendsAsync(long evaluateId, CancellationToken ct = default);

    /// <summary>插入追评。</summary>
    /// <param name="append">追评。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追评 Id。</returns>
    Task<long> InsertAppendAsync(EvaluateAppend append, CancellationToken ct = default);

    /// <summary>取某首评（含追评）的全部回复。</summary>
    /// <param name="evaluateId">首评 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回复列表。</returns>
    Task<IReadOnlyList<EvaluateReply>> GetRepliesAsync(long evaluateId, CancellationToken ct = default);

    /// <summary>批量取一批评价的 SKU 标记。</summary>
    /// <param name="evaluateIds">评价 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 Id → SKU 标记列表。</returns>
    /// <remarks>
    /// 必须批量而不是逐条查：列表一页 10 条，配套的 SKU 标记 / 追评 / 回复各要查一次，
    /// 逐条查就是 1 + 10×3 = 31 次数据库往返。批量后固定 4 次，与页大小无关。
    /// </remarks>
    Task<IReadOnlyDictionary<long, List<EvaluateSkuRef>>> GetSkuRefsBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default);

    /// <summary>批量取一批评价的追评。</summary>
    /// <param name="evaluateIds">评价 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 Id → 追评列表（按时间升序）。</returns>
    Task<IReadOnlyDictionary<long, List<EvaluateAppend>>> GetAppendsBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default);

    /// <summary>批量取一批评价的回复。</summary>
    /// <param name="evaluateIds">评价 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 Id → 回复列表（按时间升序）。</returns>
    Task<IReadOnlyDictionary<long, List<EvaluateReply>>> GetRepliesBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default);

    /// <summary>插入回复。</summary>
    /// <param name="reply">回复。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回复 Id。</returns>
    Task<long> InsertReplyAsync(EvaluateReply reply, CancellationToken ct = default);

    /// <summary>取指定 SPU 的首评聚合（用于重算均分）。</summary>
    /// <param name="spuIds">SPU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// SPU Id → 均分与评价数。
    /// <b>请求了但没有可见评价的 SPU 会以 0 分 0 条出现</b>，
    /// 让调用方能把旧评分清零，而不是「查不到就跳过」。
    /// </returns>
    Task<IReadOnlyDictionary<long, SpuRating>> AggregateBySpuAsync(
        IReadOnlyCollection<long> spuIds, CancellationToken ct = default);

    /// <summary>取全部有评价的 SPU Id 及其商户归属（每日重算用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SPU Id → 商户 Id。</returns>
    Task<IReadOnlyDictionary<long, long>> GetAllRatedSpuOwnersAsync(CancellationToken ct = default);

    /// <summary>取**有过任何评价**的 SPU Id（含已被隐藏的），每日重算用。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SPU Id 集合。</returns>
    /// <remarks>
    /// <para><b>刻意与 <see cref="GetAllRatedSpuOwnersAsync"/> 分开</b>：
    /// 那一个只算「还有可见评价」的 SPU，是给<b>店铺评分</b>用的 ——
    /// 把零评价商品的默认 5.0 算进去会让店铺评分虚高（规格 14.5）。</para>
    ///
    /// <para>这一个要把「评价全被隐藏」的 SPU 也算进来，因为它们的<b>商品评分必须被清零</b>。
    /// 只用前者的话，管理员把某商品的最后一条评价藏起来之后，
    /// 商品表里的评分就永远停在旧值上 —— 而商品列表照常显示那个分数。
    /// 症状是「评价列表里一条都没有，商品却还挂着 4.8 星」，且没有任何地方能解释。</para>
    /// </remarks>
    Task<IReadOnlyCollection<long>> GetAllEvaluatedSpuIdsAsync(CancellationToken ct = default);

    /// <summary>按商户聚合店铺评分（只统计有评价的商品均分）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户 Id → 店铺评分。</returns>
    Task<IReadOnlyDictionary<long, decimal>> AggregateMerchantRatingsAsync(CancellationToken ct = default);
}
