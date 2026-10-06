using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Domain.Services;

namespace MerchantPlatformService.Application.Features.Region;

/// <summary>读取地区数据处理器。</summary>
public sealed class QueryRegionsHandler
    : IRequestHandler<QueryRegionsCommand, ApiResponse<RegionsResult>>
{
    private readonly IPlatformConfigRepository _configs;

    /// <summary>构造处理器。</summary>
    /// <param name="configs">平台配置仓储。</param>
    public QueryRegionsHandler(IPlatformConfigRepository configs) => _configs = configs;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地区数据；平台没自定义时回落到内置默认。</returns>
    public async Task<ApiResponse<RegionsResult>> Handle(
        QueryRegionsCommand request, CancellationToken ct)
    {
        var config = await _configs.GetByPlatformAsync(request.PlatformId, ct);

        // 空串 = 回落内置默认。这不是「没配过」，而是运营主动点了「恢复默认」
        var json = string.IsNullOrWhiteSpace(config?.RegionsJson)
            ? BuiltInRegions.Json
            : config.RegionsJson;

        return ApiResults.Ok(new RegionsResult(
            json,
            IsCustom: !string.IsNullOrWhiteSpace(config?.RegionsJson),
            ByteSize: System.Text.Encoding.UTF8.GetByteCount(json)));
    }
}

/// <summary>小程序按平台编码读取地区数据处理器。</summary>
/// <remarks>
/// 地区库是收货地址三级联动的数据源；小程序没有后台令牌，
/// 不能调 <c>/regions/Get</c>，所以按固定平台编码提供一个只读入口。
/// </remarks>
public sealed class QueryPublicRegionsHandler
    : IRequestHandler<QueryPublicRegionsCommand, ApiResponse<RegionsResult>>
{
    private readonly IPlatformRepository _platforms;
    private readonly IPlatformConfigRepository _configs;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    /// <param name="configs">平台配置仓储。</param>
    public QueryPublicRegionsHandler(
        IPlatformRepository platforms, IPlatformConfigRepository configs)
    {
        _platforms = platforms;
        _configs = configs;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台地区数据；平台不存在返回 404。</returns>
    public async Task<ApiResponse<RegionsResult>> Handle(
        QueryPublicRegionsCommand request, CancellationToken ct)
    {
        var code = (request.PlatformCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return ApiResults.Fail<RegionsResult>(
                BaseApiResponseCode.BadRequest, "平台编码不能为空");
        }

        var platform = await _platforms.GetByCodeAsync(code, ct).ConfigureAwait(false);
        if (platform is null)
        {
            return ApiResults.Fail<RegionsResult>(
                BaseApiResponseCode.NotFound, "平台不存在或已停用");
        }

        var config = await _configs.GetByPlatformAsync(platform.Id, ct).ConfigureAwait(false);
        var json = string.IsNullOrWhiteSpace(config?.RegionsJson)
            ? BuiltInRegions.Json
            : config.RegionsJson;

        return ApiResults.Ok(new RegionsResult(
            json,
            IsCustom: !string.IsNullOrWhiteSpace(config?.RegionsJson),
            ByteSize: System.Text.Encoding.UTF8.GetByteCount(json)));
    }
}

/// <summary>保存地区数据处理器。</summary>
public sealed class SaveRegionsHandler : IRequestHandler<SaveRegionsCommand, ApiResponse>
{
    private readonly IPlatformConfigRepository _configs;

    /// <summary>构造处理器。</summary>
    /// <param name="configs">平台配置仓储。</param>
    public SaveRegionsHandler(IPlatformConfigRepository configs) => _configs = configs;

    /// <summary>执行保存。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SaveRegionsCommand request, CancellationToken ct)
    {
        var json = request.RegionsJson ?? string.Empty;

        var error = MerchantRules.ValidateRegionsJson(json);
        if (error is not null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, error);
        }

        var existing = await _configs.GetByPlatformAsync(request.PlatformId, ct);
        var config = existing ?? new PlatformConfig { PlatformId = request.PlatformId };

        config.RegionsJson = json.Trim();
        await _configs.SaveAsync(config, ct);

        return ApiResponseFactory.Ok(
            config.RegionsJson.Length == 0 ? "已恢复内置默认地区" : "保存成功");
    }
}
