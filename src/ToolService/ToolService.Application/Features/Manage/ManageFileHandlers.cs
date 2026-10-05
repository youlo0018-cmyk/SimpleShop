using Collaboration.Domain.Common;
using MediatR;
using ToolService.Domain.IRepository;

namespace ToolService.Application.Features.Manage;

/// <summary>后台分页查文件处理器。</summary>
public sealed class QueryFilesHandler
    : IRequestHandler<QueryFilesCommand, ApiResponse<PagedResult<FileItem>>>
{
    private readonly IStoredFileRepository _files;

    /// <summary>构造处理器。</summary>
    /// <param name="files">文件元数据仓储。</param>
    public QueryFilesHandler(IStoredFileRepository files) => _files = files;

    /// <inheritdoc />
    public async Task<ApiResponse<PagedResult<FileItem>>> Handle(
        QueryFilesCommand request, CancellationToken ct)
    {
        var page = await _files.PageAsync(
            request.Page, request.PageSize, request.Keyword, request.Category, ct).ConfigureAwait(false);

        var items = page.Items.Select(a => new FileItem(
            a.Id, a.OriginalName, a.Extension,
            a.Category, CategoryNames.Of(a.Category),
            a.Provider,
            a.SizeBytes, FileSizeFormatter.Format(a.SizeBytes),
            a.PublicUrl,
            a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<FileItem>(items, page.Total, request.Page, request.PageSize));
    }
}

/// <summary>删除文件处理器。</summary>
public sealed class DeleteFileHandler : IRequestHandler<DeleteFileCommand, ApiResponse>
{
    private readonly IStoredFileRepository _files;

    /// <summary>构造处理器。</summary>
    /// <param name="files">文件元数据仓储。</param>
    public DeleteFileHandler(IStoredFileRepository files) => _files = files;

    /// <inheritdoc />
    /// <remarks>
    /// <b>不调用存储后端的 DeleteAsync</b>：元数据被商品主图、评价图等业务引用着，
    /// 删了内容会让那些页面集体裂图。而「这个文件我不用了」与「这个文件可以物理销毁」
    /// 是两件事，后者需要先统计引用数，属于存储治理，不塞进后台管理页。
    ///
    /// <para>提示语里必须写明这一点：否则运营会以为文件已经彻底没了，
    /// 之后磁盘占用涨上来时找不到人。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteFileCommand request, CancellationToken ct)
    {
        var file = await _files.GetByIdAsync(request.FileId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "文件不存在");
        }

        await _files.DeleteAsync(request.FileId, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("已从文件库移除（存储中的文件保留，如需清理请联系管理员）");
    }
}

/// <summary>文件大小的可读化。</summary>
public static class FileSizeFormatter
{
    /// <summary>单位表，按 KB / MB / GB / TB 顺序。</summary>
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>把字节数转成可读文本。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>小于 1KB 显示字节数，否则一位小数加单位。</returns>
    /// <remarks>
    /// <b>放后端而不是前端</b>：前端做这个换算要自己维护一份单位表，
    /// 而「文件多大」是服务端才知道的事实（DESIGN_SPEC 7.1「界面不出现原始数据」）。
    /// 用 InvariantCulture 是为了不受服务器区域设置影响——
    /// 德语区会把小数点显示成逗号，那在 JSON 里就成了另一种格式。
    /// </remarks>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return string.Concat(bytes.ToString(), " B");

        var value = (double)bytes;
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < Units.Length - 1);

        return string.Concat(
            value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            " ",
            Units[unit]);
    }
}

/// <summary>文件分类的中文名。</summary>
public static class CategoryNames
{
    /// <summary>取分类中文名。</summary>
    /// <param name="category">分类值。</param>
    /// <returns>中文名；未知分类原样返回。</returns>
    /// <remarks>
    /// 未知分类<b>原样返回</b>而不是回「未知」：后台管理页遇到没配过的分类时，
    /// 显示原始值能立刻让人看出「库里存了个 xxx」，而「未知」会把这个线索抹掉。
    /// 面向客户的页面才需要「未知」兜底（那里不能暴露内部枚举值）。
    /// </remarks>
    public static string Of(string category) => category switch
    {
        "image" => "图片",
        "document" => "文档",
        "audio" => "音频",
        "video" => "视频",
        "default" => "其他",
        _ => category
    };
}
