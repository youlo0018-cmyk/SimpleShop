using Collaboration.Domain.Messaging;

namespace LogService.Application;

/// <summary>日志索引写入端口。</summary>
public interface ILogIndexer
{
    /// <summary>写一条页面访问日志。</summary>
    /// <param name="entry">日志内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> IndexPvAsync(PvLogEntry entry, CancellationToken ct = default);

    /// <summary>写一条写操作日志。</summary>
    /// <param name="entry">日志内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> IndexOperationAsync(OperationLogEntry entry, CancellationToken ct = default);

    /// <summary>写一条异常日志。</summary>
    /// <param name="entry">日志内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> IndexExceptionAsync(ExceptionLogEntry entry, CancellationToken ct = default);
}
