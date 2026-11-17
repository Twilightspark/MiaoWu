namespace MiaoWu.App.ViewModels
{
    /// <summary>标题栏工作区菜单项；IsCurrent 用于菜单里的勾选。</summary>
    public sealed class WorkspaceOption
    {
        public WorkspaceOption(string id, string name, bool isCurrent)
        {
            Id = id;
            Name = name;
            IsCurrent = isCurrent;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsCurrent { get; set; }
    }
}
