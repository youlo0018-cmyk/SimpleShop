using Collaboration.Domain.Common;
using Collaboration.Domain.Infrastructure;
using FreeSql;
using MarketingService.Application.Features.Coupon;
using MarketingService.Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>券模板与券活动（后台）。</summary>
[ApiController]
[Route("marketing")]
public sealed class CouponAdminController : ControllerBase
{
    private readonly IFreeSql _db;
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="mediator">MediatR 入口。</param>
    public CouponAdminController(IFreeSql db, IMediator mediator)
    {
        _db = db;
        _mediator = mediator;
    }

    /// <summary>新建券模板。</summary>
    /// <param name="request">模板。</param>
    /// <returns>新模板 Id。</returns>
    /// <remarks>
    /// 改模板不影响已发出的券——已发的券按自己的快照算（DATA_SPEC 5.12），
    /// 所以这里不做任何「同步到已发券」的动作。
    /// </remarks>
    [HttpPost("coupon-templates/Create")]
    public ActionResult<ApiResponse<long>> CreateTemplate([FromBody] CouponTemplate request)
    {
        if (request.ValidDays is < 1 or > 3650) return BadRequest(Envelope("有效期必须为 1 ~ 3650 天"));
        if (request.CouponType is < 1 or > 4) return BadRequest(Envelope("券类型必须是 1 满减 / 2 折扣 / 3 代金 / 4 满赠"));

        request.Id = SnowflakeId.NewId();
        request.CreatedAt = DateTime.UtcNow;
        _db.Insert(request).ExecuteAffrows();
        return Ok(ApiResults.Ok(request.Id, "创建成功"));
    }

    /// <summary>分页查询券模板。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>券模板分页结果。</returns>
    /// <remarks>
    /// 列表里带出 <c>IssuedQuantity</c>（已发放数）与模板规则，
    /// 运营才能在列表上直接判断「这个券还剩多少、门槛是多少」，
    /// 不必逐个点进详情。
    /// </remarks>
    [HttpPost("coupon-templates/List")]
    public Task<ApiResponse<PagedResult<CouponTemplateItem>>> ListTemplates(
        [FromBody] QueryCouponTemplatesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑券模板。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>改模板不影响已发出的券</b>（DATA_SPEC 5.12 快照机制），
    /// 所以这里不做任何「同步到已发券」的动作，返回消息里也明确提示了这一点。
    /// </remarks>
    [HttpPost("coupon-templates/Update")]
    public Task<ApiResponse> UpdateTemplate(
        [FromBody] UpdateCouponTemplateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除券模板（软删）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>已发出的券有快照，删模板不影响它们计算与展示。停发请改状态为「停用」。</remarks>
    [HttpPost("coupon-templates/Delete")]
    public Task<ApiResponse> DeleteTemplate(
        [FromBody] DeleteCouponTemplateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查询券模板。</summary>
    /// <param name="templateId">模板 Id。</param>
    /// <returns>模板。</returns>
    [HttpGet("coupon-templates/Get")]
    public ActionResult<ApiResponse<CouponTemplate>> GetTemplate([FromQuery] long templateId)
    {
        var row = _db.Select<CouponTemplate>().Where(a => a.Id == templateId).First();
        return row is null ? NotFound(Envelope("券模板不存在")) : Ok(ApiResults.Ok(row));
    }

    /// <summary>新建券活动。</summary>
    /// <param name="request">券活动。</param>
    /// <returns>新活动 Id。</returns>
    /// <remarks>
    /// 时间先归一到 UTC 再落库，原因写在 <see cref="ToUtc"/>。
    /// </remarks>
    [HttpPost("coupon-activities/Create")]
    public ActionResult<ApiResponse<long>> CreateActivity([FromBody] CouponActivity request)
    {
        request.ClaimStartTime = ToUtc(request.ClaimStartTime);
        request.ClaimEndTime = ToUtc(request.ClaimEndTime);

        if (request.ClaimEndTime <= request.ClaimStartTime)
        {
            return BadRequest(Envelope("领取结束时间必须晚于开始时间"));
        }

        if (request.ClaimQuantity < 1) return BadRequest(Envelope("发放量必须大于等于 1"));

        request.Id = SnowflakeId.NewId();
        request.CreatedAt = DateTime.UtcNow;
        _db.Insert(request).ExecuteAffrows();
        return Ok(ApiResults.Ok(request.Id, "创建成功"));
    }

    /// <summary>分页查询券活动。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>券活动分页结果，含关联模板名。</returns>
    /// <remarks>
    /// 列表冗余了 <c>TemplateName</c>（DATA_SPEC 4.1「下拉框显示 name，绝不显示 id」）：
    /// 券活动名字常年起得很随意，只显示模板 Id 的列表等于让人去猜。
    /// </remarks>
    [HttpPost("coupon-activities/List")]
    public Task<ApiResponse<PagedResult<CouponActivityItem>>> ListActivities(
        [FromBody] QueryCouponActivitiesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑券活动。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 🔴 <b>发放量不许改到小于已领取数</b>：活动池子是独立计数，
    /// 调到比已领取还小，报表上就会凭空多出一堆不存在的券。
    /// 这是唯一一条后台不能自由修改的字段。
    /// </remarks>
    [HttpPost("coupon-activities/Update")]
    public Task<ApiResponse> UpdateActivity(
        [FromBody] UpdateCouponActivityCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>分页查询券核销记录。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>用户券分页结果。</returns>
    /// <remarks>
    /// 数据源是 <c>user_coupon</c>（一行 = 一张已发出的券），
    /// 不另建记录表——两张表并存只会带来「两处数据对不上」的老问题。
    /// </remarks>
    [HttpPost("coupon-records/List")]
    public Task<ApiResponse<PagedResult<CouponRecordItem>>> ListCouponRecords(
        [FromBody] QueryCouponRecordsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>把客户端传来的时间归一到 UTC。</summary>
    /// <param name="value">原始时间。</param>
    /// <returns>UTC 时间。</returns>
    /// <remarks>
    /// <b>踩过的坑</b>：本项目所有时间列都是 UTC（实体注释里写死了），领取窗口也拿
    /// <c>DateTime.UtcNow</c> 比。但 <see cref="DateTime"/> 从 JSON 反序列化时，
    /// 带偏移量的字符串（<c>2026-10-03T19:00:00+08:00</c>）会得到
    /// <b>Kind=Local 且时钟值已是本地 19:00</b>——直接存进 <c>timestamp</c> 列就是错的，
    /// 服务器按 UTC 一比就差了一个时区（本项目所在时区是 +08，也就是整整 8 小时）。
    /// 症状是「刚建的活动立刻提示不在领取时间内」，而代码看起来完全没问题。
    /// <para>Kind 三种取值都要处理：Local 要转换；Utc 原样；Unspecified 按本项目约定当 UTC。</para>
    /// </remarks>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>查询券活动。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <returns>券活动。</returns>
    [HttpGet("coupon-activities/Get")]
    public ActionResult<ApiResponse<CouponActivity>> GetActivity([FromQuery] long activityId)
    {
        var row = _db.Select<CouponActivity>().Where(a => a.Id == activityId).First();
        return row is null ? NotFound(Envelope("券活动不存在")) : Ok(ApiResults.Ok(row));
    }

    /// <summary>读取平台优惠优先级。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <returns>1 活动优先 / 2 券优先。</returns>
    [HttpGet("marketing-config/Get")]
    public ActionResult<ApiResponse<MarketingConfig>> GetConfig([FromQuery] long platformId)
    {
        var row = _db.Select<MarketingConfig>().Where(a => a.PlatformId == platformId).First();
        // 没配过就是默认「券优先」，返回默认行而不是 404
        return Ok(ApiResults.Ok(row ?? new MarketingConfig { PlatformId = platformId, Priority = MarketingPriorities.CouponFirst }));
    }

    /// <summary>保存平台优惠优先级。</summary>
    /// <param name="config">配置。</param>
    /// <returns>保存结果。</returns>
    [HttpPost("marketing-config/Save")]
    public ActionResult<ApiResponse> SaveConfig([FromBody] MarketingConfig config)
    {
        if (config.Priority is not (MarketingPriorities.ActivityFirst or MarketingPriorities.CouponFirst))
        {
            return BadRequest(Envelope("优先级只能是 1 活动优先 或 2 券优先"));
        }

        var row = _db.Select<MarketingConfig>().Where(a => a.PlatformId == config.PlatformId).First();
        if (row is null)
        {
            config.Id = SnowflakeId.NewId();
            config.CreatedAt = DateTime.UtcNow;
            _db.Insert(config).ExecuteAffrows();
        }
        else
        {
            // 用 Where + Set 而不是 Update<T>(entity)：后者在本项目已确认会生成空 SET 子句、
            // 一条 SQL 都不发就返回 0（见 CrudRepository 的 P0 修复说明）
            _db.Update<MarketingConfig>()
                .Where(a => a.Id == row.Id)
                .Set(a => new MarketingConfig { Priority = config.Priority, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrows();
        }

        return Ok(ApiResults.Ok());
    }

    private static Dictionary<string, object> Envelope(string message)
        => new() { ["error"] = "invalid_request", ["error_description"] = message };
}
