using Collaboration.Domain.Repository;
using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToolService.Application.Configuration;
using ToolService.Domain.Entities;
using ToolService.Domain.IRepository;
using ToolService.Domain.Services;
using ToolService.Infrastructure.Storage;

namespace ToolService.Infrastructure.Repository;

/// <summary>文件元数据仓储实现。InsertAsync 继承 CrudRepository（雪花 Id 由基类填）。</summary>
public sealed class StoredFileRepository : CrudRepository<StoredFile>, IStoredFileRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public StoredFileRepository(IFreeSql freeSql) : base(freeSql)
    {
    }
}

/// <summary>ToolService 的基础设施注册入口。必须在 Build() 之前调用。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储与存储后端。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于绑定 FileStorage 节。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.AddScoped<IStoredFileRepository, StoredFileRepository>();

        // 存储后端按配置选一次；云厂商适配器未接入时落到本地磁盘
        services.AddSingleton<IFileStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;
            return new LocalFileStorage(options.LocalRoot, options.PublicBase);
        });

        return services;
    }
}
