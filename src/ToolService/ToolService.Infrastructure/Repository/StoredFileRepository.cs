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

    /// <inheritdoc />
    public async Task<(List<StoredFile> Items, long Total)> PageAsync(
        int page, int pageSize, string keyword, string category, CancellationToken ct = default)
    {
        var select = Db.Select<StoredFile>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.OriginalName.Contains(kw));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim();
            select = select.Where(a => a.Category == cat);
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 新文件在前：文件管理页是「刚传错了赶紧找出来删掉」的场景，
        // 按时间倒序才符合这个动线。Id 兜底是为了同一毫秒上传的多个文件顺序稳定。
        var items = await select
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public Task<int> DeleteAsync(long id, CancellationToken ct = default)
        => Db.Update<StoredFile>()
            .Where(a => a.Id == id)
            .Set(a => new StoredFile { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);
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
