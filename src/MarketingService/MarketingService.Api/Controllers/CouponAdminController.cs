using Collaboration.Domain.Common;
using Collaboration.Domain.Infrastructure;
using FreeSql;
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
    [HttpPost("coupon-activities/Create")]
    public ActionResult<ApiResponse<long>> CreateActivity([FromBody] CouponActivity request)
    {
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