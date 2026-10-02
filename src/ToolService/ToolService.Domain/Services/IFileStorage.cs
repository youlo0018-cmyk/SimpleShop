namespace ToolService.Domain.Services;

/// <summary>文件存储后端抽象。切换 Provider 由配置决定，业务代码不感知（DATA_SPEC 3.4）。</summary>
public interface IFileStorage
{
    /// <summary>后端标识。</summary>
    string Name { get; }

    /// <summary>把内容写入存储。</summary>
    /// <param name="objectKey">对象键。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="contentType">MIME 类型。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对外可访问的绝对地址。</returns>
    Task<string> SaveAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>读取内容。</summary>
    /// <param name="objectKey">对象键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>文件内容流；对象不存在返回 null。</returns>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken ct = default);

    /// <summary>删除内容。</summary>
    /// <param name="objectKey">对象键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否删除成功。</returns>
    Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default);
}
