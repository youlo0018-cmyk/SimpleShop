using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ToolService.Application.Features.Manage;
using ToolService.Application.Features.Upload;
using ToolService.Domain.Services;

namespace ToolService.Api.Controllers;

/// <summary>统一文件上传。所有端（后台 / 小程序）都走这一个入口（DATA_SPEC 3.4）。</summary>
[ApiController]
[Route("files")]
public sealed class FileController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IFileStorage _storage;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    /// <param name="storage">文件存储后端，用于本地回源。</param>
    public FileController(IMediator mediator, IFileStorage storage)
    {
        _mediator = mediator;
        _storage = storage;
    }

    /// <summary>上传文件。</summary>
    /// <param name="file">文件，表单字段名必须是 file。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回文件 Id、对象键与绝对访问地址。</returns>
    /// <remarks>校验顺序固定：扩展名白名单 → 分类大小 → 魔数（DATA_SPEC 3.4）。</remarks>
    [HttpPost("Upload")]
    [RequestSizeLimit(210_000_000)]
    public async Task<ApiResponse<UploadedFileDto>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return ApiResults.Fail<UploadedFileDto>(BaseApiResponseCode.BadRequest, "文件为空");
        }

        await using var stream = file.OpenReadStream();
        var result = await _mediator.Send(
            new UploadFileCommand(file.FileName, stream, file.Length, file.ContentType), ct);

        return result;
    }

    /// <summary>本地存储回源。云存储场景该接口不会被调用（文件直接访问对象存储地址）。</summary>
    /// <param name="objectKey">对象键，即上传时返回的 objectKey。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中返回文件流，404 表示对象不存在。</returns>
    [HttpGet("Content/{**objectKey}")]
    public async Task<IActionResult> Content([FromRoute] string objectKey, CancellationToken ct)
    {
        var stream = await _storage.OpenReadAsync(objectKey, ct);
        if (stream is null) return NotFound();
        return File(stream, "application/octet-stream");
    }

    /// <summary>分页查文件（后台「文件管理」页）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>文件分页结果，含可读的文件大小与分类中文名。</returns>
    /// <remarks>
    /// 之前只有 Upload 与 Content 两个端点 —— 能传、不能查，
    /// 于是「这个文件传错了 / 传大了」没有任何自助手段，只能找开发。
    /// 文件大小与分类文案在服务端就转好（DESIGN_SPEC 7.1「界面不出现原始数据」）。
    /// </remarks>
    [HttpPost("List")]
    public Task<ApiResponse<PagedResult<FileItem>>> List(
        [FromBody] QueryFilesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除文件（从文件库移除）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>只删元数据，不删存储中的内容</b>：元数据被商品主图、评价图等业务引用着，
    /// 删内容会让那些页面集体裂图。物理清理需要先统计引用，属于存储治理。
    /// </remarks>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete(
        [FromBody] DeleteFileCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
