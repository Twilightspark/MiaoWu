namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 文件树重命名与删除。实现必须拒绝目录穿越与非法文件名，
    /// 删除优先放入回收站；失败通过异常交给 UI 显示内联错误。
    /// </summary>
    public interface IFileEditor
    {
        /// <summary>在同一目录内重命名文件，返回新的完整路径。</summary>
        string Rename(string filePath, string newName);

        /// <summary>删除文件；实现应优先移入回收站。</summary>
        void Delete(string filePath);
    }
}
