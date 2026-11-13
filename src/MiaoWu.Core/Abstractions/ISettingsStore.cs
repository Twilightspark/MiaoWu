using MiaoWu.Core.Settings;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>config.json 的持久化契约。未知字段由实现保留。</summary>
    public interface ISettingsStore
    {
        AppSettings Load();

        void Save(AppSettings settings);
    }
}
