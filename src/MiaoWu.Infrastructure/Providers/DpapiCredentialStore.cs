using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MiaoWu.Core.Abstractions;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Storage;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// DPAPI（CurrentUser）加密的 API Key 存储。文件是二进制，不是明文 JSON；
    /// 换用户/换机器后解密失败视为未配置，不崩溃。
    /// </summary>
    public sealed class DpapiCredentialStore : ICredentialStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("MiaoWu.Providers.v1");

        private readonly string _filePath;
        private readonly object _gate = new object();
        private Dictionary<string, string> _keys;

        public DpapiCredentialStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("secrets.dat 路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public string GetKey(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            lock (_gate)
            {
                Dictionary<string, string> keys = LoadUnsafe();
                string value;
                return keys.TryGetValue(providerId, out value) ? value : null;
            }
        }

        public void SetKey(string providerId, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException("Provider ID 不能为空。", nameof(providerId));
            }

            lock (_gate)
            {
                Dictionary<string, string> keys = LoadUnsafe();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    keys.Remove(providerId);
                }
                else
                {
                    keys[providerId] = apiKey.Trim();
                }

                SaveUnsafe(keys);
            }
        }

        public void Remove(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            lock (_gate)
            {
                Dictionary<string, string> keys = LoadUnsafe();
                if (keys.Remove(providerId))
                {
                    SaveUnsafe(keys);
                }
            }
        }

        public IReadOnlyDictionary<string, string> LoadAll()
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(LoadUnsafe(), StringComparer.OrdinalIgnoreCase);
            }
        }

        private Dictionary<string, string> LoadUnsafe()
        {
            if (_keys != null)
            {
                return _keys;
            }

            _keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_filePath))
            {
                return _keys;
            }

            try
            {
                byte[] protectedBytes = File.ReadAllBytes(_filePath);
                byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                JsonValue root = JsonParser.Parse(Encoding.UTF8.GetString(plain));
                if (root != null && root.Kind == JsonKind.Object)
                {
                    foreach (KeyValuePair<string, JsonValue> member in root.Members)
                    {
                        if (member.Value != null && member.Value.Kind == JsonKind.String)
                        {
                            _keys[member.Key] = member.Value.AsString();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 换用户/换机器/文件损坏：视为未配置，不阻断启动。
                _keys.Clear();
            }

            return _keys;
        }

        private void SaveUnsafe(Dictionary<string, string> keys)
        {
            JsonValue root = JsonValue.NewObject();
            foreach (KeyValuePair<string, string> pair in keys)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    root[pair.Key] = JsonValue.From(pair.Value);
                }
            }

            byte[] plain = Encoding.UTF8.GetBytes(JsonWriter.Write(root));
            byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            string directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AtomicFile.WriteAllBytes(_filePath, protectedBytes);
        }
    }
}
