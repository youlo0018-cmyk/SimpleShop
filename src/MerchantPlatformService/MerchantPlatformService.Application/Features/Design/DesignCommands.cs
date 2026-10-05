using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Domain.Services;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>读平台装修：返回草稿，没有草稿则返回已发布版本。</summary>
/// <param name="PlatformId">平台 Id。</param>
public record QueryPlatformDesignCommand(long PlatformId) : IRequest<ApiResponse<DesignResult>>;

/// <summary>读商户装修：返回草稿，没有草稿则返回已发布版本。</summary>
/// <param name="MerchantId">商户 Id。</param>
public record QueryMerchantDesignCommand(long MerchantId) : IRequest<ApiResponse<DesignResult>>;

/// <summary>小程序读商户已发布店铺装修。只返回 published_json，不返回草稿。</summary>
/// <param name="MerchantId">商户 Id。</param>
public record QueryPublicMerchantDesignCommand(long MerchantId) : IRequest<ApiResponse<DesignResult>>;

/// <summary>小程序按平台编码读已发布平台装修。只返回 published_json。</summary>
/// <param name="PlatformCode">平台编码。</param>
public record QueryPublicPlatformDesignCommand(string PlatformCode) : IRequest<ApiResponse<DesignResult>>;

/// <summary>保存平台装修草稿（不影响线上）。</summary>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="ConfigJson">配置 JSON（BUSINESS.md 16.5 的结构）。</param>
public record SavePlatformDraftCommand(long PlatformId, string ConfigJson) : IRequest<ApiResponse<DesignResult>>;

/// <summary>保存商户装修草稿（不影响线上）。</summary>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="ConfigJson">配置 JSON，<c>pages</c> 只含 <c>store</c>。</param>
public record SaveMerchantDraftCommand(long MerchantId, string ConfigJson) : IRequest<ApiResponse<DesignResult>>;

/// <summary>发布平台装修：草稿转已发布，版本 +1。</summary>
/// <param name="PlatformId">平台 Id。</param>
public record PublishPlatformDesignCommand(long PlatformId) : IRequest<ApiResponse<int>>;

/// <summary>发布商户装修：草稿转已发布，版本 +1。</summary>
/// <param name="MerchantId">商户 Id。</param>
public record PublishMerchantDesignCommand(long MerchantId) : IRequest<ApiResponse<int>>;

/// <summary>取组件库（左侧面板）。</summary>
/// <param name="ForMerchant">true 取商户可用组件，false 取平台指定页面的可用组件。</param>
/// <param name="Page">页面标识；forMerchant 时忽略。</param>
public record QueryComponentLibraryCommand(bool ForMerchant = false, string Page = DesignPages.Index) : IRequest<ApiResponse<List<ComponentDefDto>>>;
