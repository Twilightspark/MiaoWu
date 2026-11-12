using System.Collections.Generic;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// API Key 存储。实现必须使用 DPAPI（CurrentUser），
    /// 并且不得把明文写入 settings.json、日志或导出。
    /// </summary>
    public interface ICredentialStore
    {
        string GetKey(string providerId);

        void SetKey(string providerId, string apiKey);

        void Remove(string providerId);

        IReadOnlyDictionary<string, string> LoadAll();
    }

    /// <summary>网络代理模式。设置页与 Provider 请求共用。</summary>
    public static class ProxyModes
    {
        public const string System = "system";
        public const string Direct = "direct";
        public const string Manual = "manual";
    }
}
