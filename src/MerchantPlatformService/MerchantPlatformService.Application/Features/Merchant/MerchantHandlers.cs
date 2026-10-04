using Collaboration.Domain.Common;
using Collaboration.Domain.Infrastructure;
using MediatR;
using MerchantPlatformService.Application.Services;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.Exceptions;
using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Domain.Services;
using Microsoft.Extensions.Logging;
// `Features/Merchant` 这个命名空间段会遮蔽同名实体 `Merchant`（CODING_STANDARD §6 第 1 条）。
// 不加别名的话 `new Merchant { ... }` 报 CS0118「Merchant 是命名空间，但此处被当做类型」。
using MerchantEntity = MerchantPlatformService.Domain.Entities.Merchant;

namespace MerchantPlatformService.Application.Features.Merchant;

/// <summary>创建商户处理器。</summary>
public sealed class CreateMerchantHandler : IRequestHandler<CreateMerchantCommand, ApiResponse<long>>
{
    private readonly IMerchantRepository _merchants;
    private readonly IPlatformRepository _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    /// <param name="platforms">平台仓储。</param>
    public CreateMerchantHandler(IMerchantRepository merchants, IPlatformRepository platforms)
    {
        _merchants = merchants;
        _platforms = platforms;
    }

    /// <summary>执行创建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户 Id。</returns>
    public async Task<ApiResponse<long>> Handle(CreateMerchantCommand request, CancellationToken ct)
    {
        var platform = await _platforms.GetByIdAsync(request.PlatformId, ct);
        if (platform is null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "所属平台不存在");
        }

        // 必须挂在**启用**的平台下：停用平台意味着「小程序不可见、不可交易」，
        // 新建一个挂在停用平台下的商户，等于造出一个永远没人能看到的店铺
        if (platform.Status != PlatformStatuses.Enabled)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError, "所属平台已停用，不能新建商户");
        }

        var merchant = new MerchantEntity
        {
            PlatformId = request.PlatformId,
            MerchantName = request.MerchantName.Trim(),
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            Logo = request.Logo.Trim(),
            Description = request.Description.Trim(),
            Status = request.Status,
            // 固定待审核：商户资质没过审之前不能对外运营（规格 5.2）
            AuditStatus = MerchantAuditStatuses.Pending,
            Remark = request.Remark.Trim()
        };

        // MerchantNo = 平台编码 + 雪花 Id，所以必须先拿到 Id 才能拼
        merchant.Id = SnowflakeId.NewId();
        merchant.MerchantNo = MerchantRules.BuildMerchantNo(platform.PlatformCode, merchant.Id);

        try
        {
            await _merchants.InsertAsync(merchant, ct);
            return ApiResults.Ok(merchant.Id, "商户创建成功，等待平台审核");
        }
        catch (MerchantNameTakenException ex)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BusinessError, ex.Message,
                new Dictionary<string, string[]> { ["MerchantName"] = [ex.Message] });
        }
    }
}

/// <summary>商户审核处理器。</summary>
/// <remarks>
/// 审核结论只有两个副作用，方向相反：
/// <list type="bullet">
/// <item><b>已通过</b>：写入审核信息，商户方可登录运营（不改启停状态）</item>
/// <item><b>已拒绝</b>：除审核信息外，<b>批量下架该商户全部已上架商品并同步搜索索引</b>。
/// 商品资质依赖商户资质——商户没资质了，它的商品就不该继续对外销售。
/// 不同步索引就会出现「商品页看不到但搜索搜得到」（搜索不走 C 端可见性过滤）。</item>
/// </list>
/// </remarks>
public sealed class AuditMerchantHandler
    : IRequestHandler<AuditMerchantCommand, ApiResponse<AuditMerchantResult>>
{
    private readonly IMerchantRepository _merchants;
    private readonly IProductPort _products;
    private readonly ILogger<AuditMerchantHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    /// <param name="products">商品服务端口（连带下架用）。</param>
    /// <param name="logger">日志器。</param>
    public AuditMerchantHandler(
        IMerchantRepository merchants, IProductPort products, ILogger<AuditMerchantHandler> logger)
    {
        _merchants = merchants;
        _products = products;
        _logger = logger;
    }

    /// <summary>执行审核。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>审核结果，含被连带下架的商品数。</returns>
    public async Task<ApiResponse<AuditMerchantResult>> Handle(
        AuditMerchantCommand request, CancellationToken ct)
    {
        if (!MerchantAuditStatuses.IsConclusion(request.AuditStatus))
        {
            return ApiResults.Fail<AuditMerchantResult>(
                BaseApiResponseCode.BadRequest, "审核结论只能是已通过或已拒绝");
        }

        // 拒绝时必须写原因：商户要知道**为什么**被拒，否则只能反复提交碰运气
        var remark = (request.AuditRemark ?? string.Empty).Trim();
        if (request.AuditStatus == MerchantAuditStatuses.Rejected && remark.Length == 0)
        {
            return ApiResults.Fail<AuditMerchantResult>(
                BaseApiResponseCode.BadRequest, "拒绝时必须填写原因");
        }

        var merchant = await _merchants.GetByIdAsync(request.MerchantId, ct);
        if (merchant is null)
        {
            return ApiResults.Fail<AuditMerchantResult>(BaseApiResponseCode.NotFound, "商户不存在");
        }

        // 🔴 只能审核「待审核」的商户。
        // 把当前状态当乐观条件传下去是**不够的**：那等于「拿当前状态去覆盖」，
        // 于是已拒绝的商户可以直接被改成已通过，绕过「拒绝 → 重新提交 → 再审核」这条链路，
        // 而拒绝时下架过的商品也就再也不会被恢复。
        if (merchant.AuditStatus != MerchantAuditStatuses.Pending)
        {
            return ApiResults.Fail<AuditMerchantResult>(
                BaseApiResponseCode.BusinessError,
                merchant.AuditStatus == MerchantAuditStatuses.Rejected
                    ? "该商户审核已被拒绝，请让商户重新提交后再审核"
                    : "该商户已审核通过，无需重复审核");
        }

        // 条件更新带上「当前审核状态」：两个管理员同时点审核时只有一个能改成功，
        // 另一个拿到 0 就该直接返回，而不是把审核人 / 审核时间覆盖掉
        var affected = await _merchants.TryUpdateAuditAsync(
            request.MerchantId,
            MerchantAuditStatuses.Pending,
            request.AuditStatus,
            remark,
            request.AuditorId,
            request.AuditorName,
            ct);

        if (affected == 0)
        {
            return ApiResults.Fail<AuditMerchantResult>(
                BaseApiResponseCode.BusinessError, "该商户已被其他人审核过，请刷新后重试");
        }

        var offShelved = 0;
        var synced = false;

        if (request.AuditStatus == MerchantAuditStatuses.Rejected)
        {
            var outcome = await _products.OffShelfByMerchantAsync(request.MerchantId, ct);

            if (outcome is null)
            {
                // 🔴 下架失败**不回滚审核结论**：审核拒绝是合规决定，必须生效。
                // 商品下架是补救动作，失败只意味着「搜索里可能还搜得到这些已下架商品」，
                // 严重度远低于「无资质商户的商品继续在售」。
                // 回滚审核会让审核员以为操作没生效，再点一次就变成重复审核。
                _logger.LogError(
                    "商户 {MerchantId} 审核被拒，但连带下架商品失败，商品可能仍在搜索结果里，需人工处理",
                    request.MerchantId);
            }
            else
            {
                offShelved = outcome.OffShelved;
                synced = outcome.IndexFailed == 0;

                if (outcome.IndexFailed > 0)
                {
                    _logger.LogError(
                        "商户 {MerchantId} 有 {Failed} 个商品下架后索引同步失败，将由对账任务兜底",
                        request.MerchantId, outcome.IndexFailed);
                }
            }
        }

        var name = MerchantAuditStatuses.NameOf(request.AuditStatus);
        return ApiResults.Ok(
            new AuditMerchantResult(request.MerchantId, name, offShelved, synced),
            offShelved > 0 ? $"审核{name}，已连带下架 {offShelved} 个商品" : $"审核{name}");
    }
}

/// <summary>编辑商户处理器。</summary>
public sealed class UpdateMerchantHandler : IRequestHandler<UpdateMerchantCommand, ApiResponse>
{
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    public UpdateMerchantHandler(IMerchantRepository merchants) => _merchants = merchants;

    /// <summary>执行编辑。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>不重置审核状态</b>（规格 5.2）：已通过商户改个联系电话不需要重新审核；
    /// 已拒绝商户改完资料要用「重新提交」动作打回待审核。
    /// 把重置混进编辑里就会出现「改个电话就要重新审一遍」的荒唐行为。
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdateMerchantCommand request, CancellationToken ct)
    {
        var merchant = await _merchants.GetByIdAsync(request.MerchantId, ct);
        if (merchant is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商户不存在");
        }

        merchant.MerchantName = request.MerchantName.Trim();
        merchant.ContactName = request.ContactName.Trim();
        merchant.ContactPhone = request.ContactPhone.Trim();
        merchant.Logo = request.Logo.Trim();
        merchant.Description = request.Description.Trim();
        merchant.Status = request.Status;
        merchant.Remark = request.Remark.Trim();

        // PlatformId / MerchantNo / 审核状态都**不赋值**：前者是租户归属不能改，
        // 后者是系统生成的（编号含 Id，改了会与订单历史对不上）

        try
        {
            var ok = await _merchants.UpdateAsync(merchant, ct);
            return ok
                ? ApiResponseFactory.Ok("保存成功")
                : ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "保存失败，请重试");
        }
        catch (MerchantNameTakenException ex)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, ex.Message,
                new Dictionary<string, string[]> { ["MerchantName"] = [ex.Message] });
        }
    }
}

/// <summary>删除商户处理器。</summary>
public sealed class DeleteMerchantHandler : IRequestHandler<DeleteMerchantCommand, ApiResponse>
{
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    public DeleteMerchantHandler(IMerchantRepository merchants) => _merchants = merchants;

    /// <summary>执行删除。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(DeleteMerchantCommand request, CancellationToken ct)
    {
        var merchant = await _merchants.GetByIdAsync(request.MerchantId, ct);
        if (merchant is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商户不存在");
        }

        // 规格 5.2「有商品或订单时禁止删除，只能停用」。
        // 这里只挡「已过审」的商户：待审核 / 已拒绝的商户必然还没接单，
        // 用审核状态判断比再跨服务查一次商品订单更省，也更不容易因对方服务不可用而误拦。
        if (merchant.AuditStatus == MerchantAuditStatuses.Approved)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, "已通过审核的商户不能删除，只能停用");
        }

        var ok = await _merchants.SoftDeleteAsync(request.MerchantId, ct);
        return ok
            ? ApiResponseFactory.Ok("删除成功")
            : ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "删除失败，请重试");
    }
}

/// <summary>已拒绝商户重新提交审核处理器。</summary>
public sealed class ResubmitMerchantHandler : IRequestHandler<ResubmitMerchantCommand, ApiResponse>
{
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    public ResubmitMerchantHandler(IMerchantRepository merchants) => _merchants = merchants;

    /// <summary>执行重提交。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ResubmitMerchantCommand request, CancellationToken ct)
    {
        var merchant = await _merchants.GetByIdAsync(request.MerchantId, ct);
        if (merchant is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商户不存在");
        }

        if (merchant.AuditStatus != MerchantAuditStatuses.Rejected)
        {
            // 只有被拒的商户才需要重提交。对已通过的商户重复提交，
            // 等于把「已通过」白送回「待审核」，让商户莫名其妙地失去经营资格
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, "只有审核被拒的商户需要重新提交");
        }

        var affected = await _merchants.TryUpdateAuditAsync(
            request.MerchantId,
            MerchantAuditStatuses.Rejected,
            MerchantAuditStatuses.Pending,
            string.Empty,
            0,
            string.Empty,
            ct);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, "该商户状态已变化，请刷新后重试");
        }

        return ApiResponseFactory.Ok("已重新提交审核");
    }
}

/// <summary>商户列表处理器。</summary>
public sealed class QueryMerchantsHandler
    : IRequestHandler<QueryMerchantsCommand, ApiResponse<PagedMerchantDtos>>
{
    private readonly IMerchantRepository _merchants;
    private readonly IPlatformRepository _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    /// <param name="platforms">平台仓储（取平台名称）。</param>
    public QueryMerchantsHandler(IMerchantRepository merchants, IPlatformRepository platforms)
    {
        _merchants = merchants;
        _platforms = platforms;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户分页。</returns>
    public async Task<ApiResponse<PagedMerchantDtos>> Handle(
        QueryMerchantsCommand request, CancellationToken ct)
    {
        var filter = new MerchantFilter(
            request.PlatformId, request.Keyword, request.AuditStatus,
            request.Status, request.Page, request.PageSize);

        var page = await _merchants.PageAsync(filter, ct);

        // 平台名称一次性取回：列表里要显示平台名而不是 Id，
        // 逐条查平台就是 N+1 次查询
        var platforms = _platforms.ListEnabled().ToDictionary(a => a.Id, a => a.PlatformName);

        var items = page.Items.Select(a => new MerchantListDto(
            a.Id,
            a.MerchantName,
            a.MerchantNo,
            a.PlatformId,
            platforms.TryGetValue(a.PlatformId, out var name) ? name : string.Empty,
            a.ContactName,
            a.ContactPhone,
            a.Logo,
            a.Description,
            a.Status,
            PlatformStatuses.NameOf(a.Status),
            a.AuditStatus,
            MerchantAuditStatuses.NameOf(a.AuditStatus),
            a.AuditRemark,
            a.AuditedAt is null ? string.Empty : MerchantAssembler.FormatTime(a.AuditedAt.Value),
            a.AuditorName,
            a.Rating,
            MerchantAssembler.FormatTime(a.CreatedAt),
            a.CreatedByName,
            a.UpdatedAt is null ? string.Empty : MerchantAssembler.FormatTime(a.UpdatedAt.Value),
            a.OperationName)).ToList();

        return ApiResults.Ok(new PagedMerchantDtos(items, page.Total, page.Page, page.PageSize));
    }
}

/// <summary>商户模块的展示时间格式化。</summary>
public static class MerchantAssembler
{
    /// <summary>展示时间格式：本地时区，不带时区后缀。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>把 UTC 时间转成展示字符串。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>展示用字符串。</returns>
    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(TimeFormat);
}

/// <summary>商户下拉框处理器。</summary>
public sealed class QueryMerchantOptionsHandler
    : IRequestHandler<QueryMerchantOptionsQuery, ApiResponse<List<MerchantOptionDto>>>
{
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="merchants">商户仓储。</param>
    public QueryMerchantOptionsHandler(IMerchantRepository merchants) => _merchants = merchants;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用商户的下拉项。</returns>
    /// <remarks>
    /// 只返回<b>启用</b>的商户：停用商户不可接单、不可新增商品，
    /// 让它出现在下拉里会让运营选到一个「选了也不能用」的店铺。
    /// </remarks>
    public Task<ApiResponse<List<MerchantOptionDto>>> Handle(
        QueryMerchantOptionsQuery request, CancellationToken ct)
    {
        var items = _merchants.ListEnabledByPlatform(request.PlatformId, ct)
            .Select(a => new MerchantOptionDto(a.Id, a.MerchantName, a.PlatformId))
            .ToList();

        return Task.FromResult(ApiResults.Ok(items));
    }
}
