namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 定义应用在本机的数据目录布局。
    /// 具体路径由基础设施层提供，应用层不直接拼接本地路径。
    /// </summary>
    public interface IAppPaths
    {
        string RootDirectory { get; }

        string DataDirectory { get; }

        string AttachmentsDirectory { get; }

        string BackupsDirectory { get; }

        string LogsDirectory { get; }

        string CacheDirectory { get; }

        string ExportsDirectory { get; }

        string SettingsFilePath { get; }

        string DatabaseFilePath { get; }

        /// <summary>data\providers.json：Provider 与模型目录。</summary>
        string ProvidersFilePath { get; }

        /// <summary>data\secrets.dat：DPAPI 加密的 API Key。</summary>
        string SecretsFilePath { get; }

        /// <summary>用户级技能目录（跨工作区）。</summary>
        string UserSkillsDirectory { get; }

        /// <summary>用户全局指令 AGENTS.md。</summary>
        string UserInstructionsFilePath { get; }

        /// <summary>工具溢出正文缓存目录。</summary>
        string ToolOutputCacheDirectory { get; }

        /// <summary>创建缺失的应用目录。重复调用应为幂等操作。</summary>
        void EnsureCreated();
    }
}
