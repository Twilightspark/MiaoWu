using System;
using System.IO;
using MiaoWu.Core.Abstractions;

namespace MiaoWu.Infrastructure
{
    /// <summary>
    /// 默认本地配置目录：用户目录\.miaowu。
    /// 应用配置（config.json / workspaces.json / providers.json / secrets.dat）
    /// 与日志、缓存、导出都放在这里。
    /// </summary>
    public sealed class AppPaths : IAppPaths
    {
        private const string AttachmentsFolderName = "attachments";
        private const string BackupsFolderName = "backups";
        private const string LogsFolderName = "logs";
        private const string CacheFolderName = "cache";
        private const string ExportsFolderName = "exports";

        public AppPaths(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("应用数据根目录不能为空。", nameof(rootDirectory));
            }

            RootDirectory = Path.GetFullPath(rootDirectory);
            DataDirectory = RootDirectory;
            AttachmentsDirectory = Path.Combine(RootDirectory, AttachmentsFolderName);
            BackupsDirectory = Path.Combine(RootDirectory, BackupsFolderName);
            LogsDirectory = Path.Combine(RootDirectory, LogsFolderName);
            CacheDirectory = Path.Combine(RootDirectory, CacheFolderName);
            ExportsDirectory = Path.Combine(RootDirectory, ExportsFolderName);
            SettingsFilePath = Path.Combine(DataDirectory, "config.json");
            DatabaseFilePath = Path.Combine(DataDirectory, "miaowu.db");
            ProvidersFilePath = Path.Combine(DataDirectory, "providers.json");
            SecretsFilePath = Path.Combine(DataDirectory, "secrets.dat");
            UserSkillsDirectory = Path.Combine(RootDirectory, "skills");
            UserInstructionsFilePath = Path.Combine(RootDirectory, "AGENTS.md");
            ToolOutputCacheDirectory = Path.Combine(CacheDirectory, "tool-output");
        }

        public string RootDirectory { get; }

        public string DataDirectory { get; }

        public string AttachmentsDirectory { get; }

        public string BackupsDirectory { get; }

        public string LogsDirectory { get; }

        public string CacheDirectory { get; }

        public string ExportsDirectory { get; }

        public string SettingsFilePath { get; }

        public string DatabaseFilePath { get; }

        public string ProvidersFilePath { get; }

        public string SecretsFilePath { get; }

        public string UserSkillsDirectory { get; }

        public string UserInstructionsFilePath { get; }

        public string ToolOutputCacheDirectory { get; }

        public static AppPaths CreateDefault()
        {
            // 便于开发调试、自动化测试和便携运行，可显式覆盖数据根目录。
            string overrideDirectory = Environment.GetEnvironmentVariable("MIAOWU_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                return new AppPaths(overrideDirectory);
            }

            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
            }

            return new AppPaths(Path.Combine(userProfile, ".miaowu"));
        }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(AttachmentsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(ExportsDirectory);
            Directory.CreateDirectory(UserSkillsDirectory);
            Directory.CreateDirectory(ToolOutputCacheDirectory);
        }
    }
}
