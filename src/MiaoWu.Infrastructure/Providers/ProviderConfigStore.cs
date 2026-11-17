using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Storage;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// data/providers.json 的读写。文件缺失、损坏或为空时返回空目录，不自动种预置供应商；
    /// 保存时保留未知根字段，避免版本降级丢配置。
    /// </summary>
    public sealed class ProviderConfigStore
    {
        private readonly string _filePath;
        private JsonValue _root;

        public ProviderConfigStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("providers.json 路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public ProviderCatalog Load()
        {
            if (!File.Exists(_filePath))
            {
                _root = null;
                return new ProviderCatalog();
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(AtomicFile.ReadAllText(_filePath));
            }
            catch (Exception)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                _root = null;
                return new ProviderCatalog();
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                _root = null;
                return new ProviderCatalog();
            }

            _root = root;
            var catalog = new ProviderCatalog
            {
                SchemaVersion = GetInt(root, "schemaVersion", ProviderCatalog.CurrentSchemaVersion),
                ActiveProviderId = GetString(root, "activeProviderId")
            };

            JsonValue providers = root.Get("providers");
            if (providers != null && providers.Kind == JsonKind.Array)
            {
                foreach (JsonValue item in providers.Items)
                {
                    ProviderDefinition provider = ReadProvider(item);
                    if (provider != null)
                    {
                        catalog.Providers.Add(provider);
                    }
                }
            }

            return catalog;
        }

        public void Save(ProviderCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            JsonValue root = _root ?? JsonValue.NewObject();
            root["schemaVersion"] = JsonValue.From(ProviderCatalog.CurrentSchemaVersion);
            root["activeProviderId"] = catalog.ActiveProviderId == null
                ? JsonValue.Null
                : JsonValue.From(catalog.ActiveProviderId);

            var providers = JsonValue.NewArray();
            if (catalog.Providers != null)
            {
                foreach (ProviderDefinition provider in catalog.Providers)
                {
                    if (provider != null)
                    {
                        providers.Add(WriteProvider(provider));
                    }
                }
            }

            root["providers"] = providers;
            AtomicFile.WriteAllText(_filePath, JsonWriter.WriteIndented(root));
            _root = root;
        }

        private static ProviderDefinition ReadProvider(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            string id = GetString(node, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var provider = new ProviderDefinition
            {
                Id = id.Trim(),
                DisplayName = GetString(node, "displayName"),
                Api = GetString(node, "api"),
                BaseUrl = GetString(node, "baseUrl"),
                TimeoutSeconds = GetInt(node, "timeoutSeconds", 120),
                MaxRetries = GetInt(node, "maxRetries", 2)
            };

            JsonValue temperature = node.Get("temperature");
            if (temperature != null && temperature.Kind == JsonKind.Number)
            {
                provider.Temperature = temperature.AsDouble(0.7);
            }

            JsonValue maxOutput = node.Get("maxOutputTokens");
            if (maxOutput != null && maxOutput.Kind == JsonKind.Number)
            {
                provider.MaxOutputTokens = maxOutput.AsInt(0);
            }

            JsonValue compat = node.Get("compat");
            if (compat != null && compat.Kind == JsonKind.Object)
            {
                provider.Compat = new OpenAiCompat
                {
                    MaxTokensField = GetString(compat, "maxTokensField") ?? "max_tokens",
                    SupportsUsageInStreaming = GetBool(compat, "supportsUsageInStreaming", true),
                    ThinkingFormat = GetString(compat, "thinkingFormat") ?? "none",
                    SystemRole = GetString(compat, "systemRole") ?? "system"
                };
            }

            JsonValue models = node.Get("models");
            if (models != null && models.Kind == JsonKind.Array)
            {
                foreach (JsonValue modelNode in models.Items)
                {
                    ModelDefinition model = ReadModel(modelNode);
                    if (model != null)
                    {
                        provider.Models.Add(model);
                    }
                }
            }

            return provider;
        }

        private static ModelDefinition ReadModel(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            string id = GetString(node, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new ModelDefinition
            {
                Id = id.Trim(),
                Name = GetString(node, "name"),
                ContextWindow = GetInt(node, "contextWindow", 128000),
                MaxOutputTokens = GetInt(node, "maxOutputTokens", 8192),
                SupportsImages = GetBool(node, "supportsImages", false),
                SupportsReasoning = GetBool(node, "supportsReasoning", false)
            };
        }

        private static JsonValue WriteProvider(ProviderDefinition provider)
        {
            JsonValue node = JsonValue.NewObject();
            node["id"] = JsonValue.From(provider.Id ?? string.Empty);
            node["displayName"] = JsonValue.From(provider.DisplayName ?? provider.Id ?? string.Empty);
            node["api"] = JsonValue.From(provider.Api ?? "openai-completions");
            node["baseUrl"] = JsonValue.From(provider.BaseUrl ?? string.Empty);
            node["temperature"] = provider.Temperature.HasValue
                ? JsonValue.From(provider.Temperature.Value)
                : JsonValue.Null;
            node["maxOutputTokens"] = provider.MaxOutputTokens.HasValue
                ? JsonValue.From(provider.MaxOutputTokens.Value)
                : JsonValue.Null;
            node["timeoutSeconds"] = JsonValue.From(provider.TimeoutSeconds <= 0 ? 120 : provider.TimeoutSeconds);
            node["maxRetries"] = JsonValue.From(provider.MaxRetries < 0 ? 0 : provider.MaxRetries);

            OpenAiCompat compat = provider.Compat ?? new OpenAiCompat();
            JsonValue compatNode = JsonValue.NewObject();
            compatNode["maxTokensField"] = JsonValue.From(compat.MaxTokensField ?? "max_tokens");
            compatNode["supportsUsageInStreaming"] = JsonValue.From(compat.SupportsUsageInStreaming);
            compatNode["thinkingFormat"] = JsonValue.From(compat.ThinkingFormat ?? "none");
            compatNode["systemRole"] = JsonValue.From(compat.SystemRole ?? "system");
            node["compat"] = compatNode;

            var models = JsonValue.NewArray();
            if (provider.Models != null)
            {
                foreach (ModelDefinition model in provider.Models)
                {
                    if (model == null)
                    {
                        continue;
                    }

                    JsonValue modelNode = JsonValue.NewObject();
                    modelNode["id"] = JsonValue.From(model.Id ?? string.Empty);
                    modelNode["name"] = JsonValue.From(model.Name ?? model.Id ?? string.Empty);
                    modelNode["contextWindow"] = JsonValue.From(model.ContextWindow);
                    modelNode["maxOutputTokens"] = JsonValue.From(model.MaxOutputTokens);
                    modelNode["supportsImages"] = JsonValue.From(model.SupportsImages);
                    modelNode["supportsReasoning"] = JsonValue.From(model.SupportsReasoning);
                    models.Add(modelNode);
                }
            }

            node["models"] = models;
            return node;
        }

        private static string GetString(JsonValue parent, string key)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null || value.Kind == JsonKind.Null ? null : value.AsString(null);
        }

        private static int GetInt(JsonValue parent, string key, int fallback)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? fallback : value.AsInt(fallback);
        }

        private static bool GetBool(JsonValue parent, string key, bool fallback)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? fallback : value.AsBoolean(fallback);
        }
    }

    /// <summary>Provider 配置校验：保存前执行，失败返回字段级错误。</summary>
    public static class ProviderConfigValidator
    {
        public static IReadOnlyList<string> Validate(ProviderCatalog catalog)
        {
            var errors = new List<string>();
            if (catalog == null)
            {
                errors.Add("配置为空。");
                return errors;
            }

            var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProviderDefinition provider in catalog.Providers ?? new List<ProviderDefinition>())
            {
                if (provider == null)
                {
                    continue;
                }

                string label = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id : provider.DisplayName;
                if (string.IsNullOrWhiteSpace(provider.Id))
                {
                    errors.Add("Provider ID 不能为空。");
                    continue;
                }

                if (!providerIds.Add(provider.Id))
                {
                    errors.Add("Provider ID 重复：" + provider.Id + "。");
                }

                if (string.IsNullOrWhiteSpace(provider.Api)
                    || (provider.Api != "openai-completions" && provider.Api != "anthropic-messages"))
                {
                    errors.Add(label + "：协议必须是 openai-completions 或 anthropic-messages。");
                }

                Uri uri;
                if (string.IsNullOrWhiteSpace(provider.BaseUrl)
                    || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    errors.Add(label + "：Base URL 必须是绝对 http/https 地址，且不能带查询、锚点或用户信息。");
                }

                if (provider.Models == null || provider.Models.Count == 0)
                {
                    errors.Add(label + "：至少需要一个模型。");
                    continue;
                }

                var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModelDefinition model in provider.Models)
                {
                    if (model == null || string.IsNullOrWhiteSpace(model.Id))
                    {
                        errors.Add(label + "：模型 ID 不能为空。");
                        continue;
                    }

                    if (!modelIds.Add(model.Id))
                    {
                        errors.Add(label + "：模型 ID 重复：" + model.Id + "。");
                    }

                    if (model.ContextWindow < 1024)
                    {
                        errors.Add(label + "/" + model.Id + "：上下文窗口至少 1024。");
                    }

                    if (model.MaxOutputTokens > model.ContextWindow / 2)
                    {
                        errors.Add(label + "/" + model.Id + "：最大输出不能超过上下文窗口的一半。");
                    }
                }
            }

            return errors;
        }
    }
}
