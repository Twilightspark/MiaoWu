using System.Collections.Generic;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 文件树目录读取。实现必须只枚举一层，并捕获越权/IO 异常，
    /// 以错误节点返回，不向 UI 抛异常。
    /// </summary>
    public interface IFileCatalog
    {
        IReadOnlyList<FileNode> GetChildren(string directoryPath, int depth);
    }
}
