namespace MiaoWu.App.Services
{
    /// <summary>一次自动刷新模型目录的结果，用于日志与调用方观察。</summary>
    public sealed class ProviderModelRefreshResult
    {
        /// <summary>参与刷新的供应商数量（有 Base URL 的条目，含没有密钥而被跳过的）。</summary>
        public int AttemptedProviders { get; set; }

        /// <summary>成功从 <c>/models</c> 取到列表的供应商数量。</summary>
        public int FetchedProviders { get; set; }

        /// <summary>实际追加进 providers.json 的模型数量。</summary>
        public int AddedModels { get; set; }

        /// <summary>请求失败或超时的供应商数量。</summary>
        public int FailedProviders { get; set; }

        /// <summary>是否因为已有一次刷新正在进行而跳过本次。</summary>
        public bool Skipped { get; set; }
    }
}
