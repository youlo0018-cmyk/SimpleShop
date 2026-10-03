using EvaluateService.Domain.Entities;
using EvaluateService.Domain.IRepository;
// `Features/Evaluate` 命名空间段遮蔽同名实体 `Evaluate`，见 CODING_STANDARD §6 第 1 条
using EvaluateEntity = EvaluateService.Domain.Entities.Evaluate;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>把一批评价实体装配成 DTO 列表（批量取配套数据）。</summary>
internal static class EvaluateAssembler
{
    /// <summary>批量装配评价列表。</summary>
    /// <param name="repo">评价仓储。</param>
    /// <param name="evaluates">评价实体列表。</param>
    /// <param name="forAdmin">是否给后台看。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 DTO 列表，顺序与入参一致。</returns>
    /// <remarks>
    /// 三次批量查询（SKU 标记 / 追评 / 回复）而不是逐条查：
    /// 一页 10 条评价，逐条查要 1 + 10×3 = 31 次数据库往返；
    /// 批量后固定 4 次，页大小翻十倍也不增加往返次数。
    /// </remarks>
    public static async Task<IReadOnlyList<EvaluateDto>> BuildListAsync(
        IEvaluateRepository repo,
        IReadOnlyList<EvaluateEntity> evaluates,
        bool forAdmin,
        CancellationToken ct)
    {
        if (evaluates.Count == 0) return [];

        var ids = evaluates.Select(a => a.Id).ToList();

        var skuRefs = await repo.GetSkuRefsBatchAsync(ids, ct).ConfigureAwait(false);
        var appends = await repo.GetAppendsBatchAsync(ids, ct).ConfigureAwait(false);
        var replies = await repo.GetRepliesBatchAsync(ids, ct).ConfigureAwait(false);

        var result = new List<EvaluateDto>(evaluates.Count);

        foreach (var evaluate in evaluates)
        {
            var refList = skuRefs.TryGetValue(evaluate.Id, out var r) ? r : [];
            var appendList = appends.TryGetValue(evaluate.Id, out var a) ? a : [];
            var replyList = replies.TryGetValue(evaluate.Id, out var p) ? p : [];

            // 排序让同一页内的展示顺序稳定：按 SKU Id 升序，
            // 否则 PostgreSQL 对 IN 查询返回的行序不保证，前端按 SpuId 过滤时可能出现列表跳动
            var skuIds = refList.Select(x => x.SkuId).OrderBy(x => x).ToList();

            result.Add(EvaluateDtoFactory.Build(evaluate, skuIds, appendList, replyList, forAdmin));
        }

        return result;
    }
}
