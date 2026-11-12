using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Files;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>文本文件预览。实现负责编码识别、二进制拒绝、大小/行数截断。</summary>
    public interface IFilePreviewService
    {
        Task<FilePreview> LoadAsync(string filePath, CancellationToken cancellationToken);
    }
}
