using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.Exceptions;

namespace MerchantPlatformService.Domain.IRepository;

/// <summary>平台仓储。</summary>
public interface IPlatformRepository
{
    /// <summary>插入平台。</summary>
    /// <param name="platform">平台实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台 Id。</returns>
    /// <exception cref="PlatformCodeTakenException">平台名或编码已被占用。</exception>
    Task<long> InsertAsync(Platform platform, CancellationToken ct = default);

    /// <summary>按 Id 取平台。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<Platform?> GetByIdAsync(long platformId, CancellationToken ct = default);

    /// <summary>按平台编码取平台。</summary>
    /// <param name="platformCode">平台编码（忽略大小写）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<Platform?> GetByCodeAsync(string platformCode, CancellationToken ct = default);

    /// <summary>更新平台。</summary>
    /// <param name="platform">平台实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> UpdateAsync(Platform platform, CancellationToken ct = default);

    /// <summary>软删平台。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> SoftDeleteAsync(long platformId, CancellationToken ct = default);

    /// <summary>分页查平台。</summary>
    /// <param name="keyword">名称 / 编码模糊匹配，空表示不限。</param>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedPlatforms> PageAsync(string? keyword, int status, int page, int pageSize,
        CancellationToken ct = default);

    /// <summary>取全部启用平台（供下拉框使用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用平台列表。</returns>
    IReadOnlyList<Platform> ListEnabled(CancellationToken ct = default);
}

/// <summary>商户仓储。</summary>
public interface IMerchantRepository
{
    /// <summary>插入商户。</summary>
    /// <param name="merchant">商户实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户 Id。</returns>
    /// <exception cref="MerchantNameTakenException">同平台内商户名已被占用。</exception>
    Task<long> InsertAsync(Merchant merchant, CancellationToken ct = default);

    /// <summary>按 Id 取商户。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<Merchant?> GetByIdAsync(long merchantId, CancellationToken ct = default);

    /// <summary>更新商户。</summary>
    /// <param name="merchant">商户实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> UpdateAsync(Merchant merchant, CancellationToken ct = default);

    /// <summary>软删商户。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> SoftDeleteAsync(long merchantId, CancellationToken ct = default);

    /// <summary>分页查商户。</summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedMerchants> PageAsync(MerchantFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 按状态条件更新商户状态（启停 / 审核）。
    /// </summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="expectedAuditStatus">当前审核状态必须等于它（乐观条件）。</param>
    /// <param name="newAuditStatus">写入的审核状态。</param>
    /// <param name="auditRemark">审核意见 / 拒绝原因。</param>
    /// <param name="auditorId">审核人 Id。</param>
    /// <param name="auditorName">审核人姓名快照。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>影响行数。返回 0 表示商户已被别人改过。</returns>
    /// <remarks>
    /// 带上「当前审核状态」作为条件：两个管理员同时点审核时，
    /// 只有一个能把状态从 10 改成 20，另一个拿到 0 就该直接返回，
    /// 而不是把审核人 / 审核时间覆盖掉。
    /// </remarks>
    Task<int> TryUpdateAuditAsync(long merchantId, int expectedAuditStatus, int newAuditStatus,
        string auditRemark, long auditorId, string auditorName, CancellationToken ct = default);

    /// <summary>按商户 Id 集合回写店铺评分。</summary>
    /// <param name="ratings">商户 Id → 评分。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功回写条数。</returns>
    Task<int> SyncRatingsAsync(IReadOnlyDictionary<long, decimal> ratings, CancellationToken ct = default);
}

/// <summary>平台配置仓储（地区地址）。</summary>
public interface IPlatformConfigRepository
{
    /// <summary>取平台配置，不存在返回 null。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置实体或 null。</returns>
    Task<PlatformConfig?> GetByPlatformAsync(long platformId, CancellationToken ct = default);

    /// <summary>保存平台配置（不存在则插入）。</summary>
    /// <param name="config">配置实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置 Id。</returns>
    Task<long> SaveAsync(PlatformConfig config, CancellationToken ct = default);
}

/// <summary>平台分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedPlatforms(IReadOnlyList<Platform> Items, long Total, int Page, int PageSize);

/// <summary>商户分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedMerchants(IReadOnlyList<Merchant> Items, long Total, int Page, int PageSize);

/// <summary>商户列表过滤条件。</summary>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <param name="Keyword">名称模糊匹配，空表示不限。</param>
/// <param name="AuditStatus">审核状态，0 表示不限。</param>
/// <param name="Status">启停状态，0 表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record MerchantFilter(
    long PlatformId = 0,
    string? Keyword = null,
    int AuditStatus = 0,
    int Status = 0,
    int Page = 1,
    int PageSize = 20);
