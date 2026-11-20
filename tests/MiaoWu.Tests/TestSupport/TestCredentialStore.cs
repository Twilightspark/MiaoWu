using System;
using System.Collections.Generic;
using MiaoWu.Core.Abstractions;

namespace MiaoWu.Tests
{
    /// <summary>内存凭据存储：给不需要 DPAPI 的会话 / Provider 用例注入 API Key。</summary>
    public sealed class TestCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _keys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public TestCredentialStore()
        {
        }

        public TestCredentialStore(string providerId, string key)
        {
            _keys[providerId] = key;
        }

        public string GetKey(string providerId)
        {
            string value;
            return _keys.TryGetValue(providerId, out value) ? value : null;
        }

        public void SetKey(string providerId, string apiKey)
        {
            _keys[providerId] = apiKey;
        }

        public void Remove(string providerId)
        {
            _keys.Remove(providerId);
        }

        public IReadOnlyDictionary<string, string> LoadAll()
        {
            return new Dictionary<string, string>(_keys, StringComparer.OrdinalIgnoreCase);
        }
    }
}
