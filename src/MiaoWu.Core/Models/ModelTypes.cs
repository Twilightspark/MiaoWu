using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Models
{
    /// <summary>指向某个 Provider 下的某个模型。</summary>
    public sealed class ModelRef
    {
        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public override string ToString()
        {
            return (ProviderId ?? string.Empty) + "/" + (ModelId ?? string.Empty);
        }
    }

    /// <summary>
    /// 不可变模型目录项。配置变更时整体重建注册表，不原地修改，
    /// 避免一个请求在旧配置下开始、在新配置下结束。
    /// </summary>
    public sealed class ModelDescriptor
    {
        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public string DisplayName { get; set; }

        /// <summary>"openai-completions" 或 "anthropic-messages"。</summary>
        public string Api { get; set; }

        public string BaseUrl { get; set; }

        public int ContextWindow { get; set; }

        public int MaxOutputTokens { get; set; }

        public bool SupportsImages { get; set; }

        public bool SupportsReasoning { get; set; }

        /// <summary>OpenAI-compatible 方言开关；其他协议忽略。</summary>
        public OpenAiCompat Compat { get; set; }

        public string Key
        {
            get { return (ProviderId ?? string.Empty) + "/" + (ModelId ?? string.Empty); }
        }

        public string GetDisplayName()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? ModelId : DisplayName;
        }
    }

    /// <summary>
    /// OpenAI-compatible 网关的方言开关。默认值面向“最保守的兼容端点”，
    /// 由设置页按 Provider 覆盖。
    /// </summary>
    public sealed class OpenAiCompat
    {
        public OpenAiCompat()
        {
            MaxTokensField = "max_tokens";
            SupportsUsageInStreaming = true;
            ThinkingFormat = "none";
            SystemRole = "system";
        }

        /// <summary>"max_tokens" | "max_completion_tokens"。</summary>
        public string MaxTokensField { get; set; }

        public bool SupportsUsageInStreaming { get; set; }

        /// <summary>"openai" | "deepseek" | "openrouter" | "none"。</summary>
        public string ThinkingFormat { get; set; }

        /// <summary>"system" | "developer"。</summary>
        public string SystemRole { get; set; }

        public OpenAiCompat Clone()
        {
            return new OpenAiCompat
            {
                MaxTokensField = MaxTokensField,
                SupportsUsageInStreaming = SupportsUsageInStreaming,
                ThinkingFormat = ThinkingFormat,
                SystemRole = SystemRole
            };
        }
    }

    /// <summary>providers.json 里的模型定义（可变配置）。</summary>
    public sealed class ModelDefinition
    {
        public ModelDefinition()
        {
            ContextWindow = 128000;
            MaxOutputTokens = 8192;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public int ContextWindow { get; set; }

        public int MaxOutputTokens { get; set; }

        public bool SupportsImages { get; set; }

        public bool SupportsReasoning { get; set; }

        public ModelDefinition Clone()
        {
            return new ModelDefinition
            {
                Id = Id,
                Name = Name,
                ContextWindow = ContextWindow,
                MaxOutputTokens = MaxOutputTokens,
                SupportsImages = SupportsImages,
                SupportsReasoning = SupportsReasoning
            };
        }
    }

    /// <summary>providers.json 里的 Provider 定义（可变配置）。</summary>
    public sealed class ProviderDefinition
    {
        public ProviderDefinition()
        {
            Models = new List<ModelDefinition>();
            Compat = new OpenAiCompat();
            Temperature = 0.7;
            TimeoutSeconds = 120;
            MaxRetries = 2;
        }

        public string Id { get; set; }

        public string DisplayName { get; set; }

        /// <summary>"openai-completions" | "anthropic-messages"。</summary>
        public string Api { get; set; }

        public string BaseUrl { get; set; }

        public double? Temperature { get; set; }

        public int? MaxOutputTokens { get; set; }

        public int TimeoutSeconds { get; set; }

        public int MaxRetries { get; set; }

        public OpenAiCompat Compat { get; set; }

        public List<ModelDefinition> Models { get; set; }

        public ProviderDefinition Clone()
        {
            var copy = new ProviderDefinition
            {
                Id = Id,
                DisplayName = DisplayName,
                Api = Api,
                BaseUrl = BaseUrl,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                TimeoutSeconds = TimeoutSeconds,
                MaxRetries = MaxRetries,
                Compat = Compat == null ? new OpenAiCompat() : Compat.Clone(),
                Models = new List<ModelDefinition>()
            };

            if (Models != null)
            {
                foreach (ModelDefinition model in Models)
                {
                    if (model != null)
                    {
                        copy.Models.Add(model.Clone());
                    }
                }
            }

            return copy;
        }
    }

    /// <summary>Provider 与模型目录。data/providers.json 的强类型形态。</summary>
    public sealed class ProviderCatalog
    {
        public const int CurrentSchemaVersion = 1;

        public ProviderCatalog()
        {
            SchemaVersion = CurrentSchemaVersion;
            Providers = new List<ProviderDefinition>();
        }

        public int SchemaVersion { get; set; }

        public string ActiveProviderId { get; set; }

        public List<ProviderDefinition> Providers { get; set; }

        public ProviderDefinition FindProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId) || Providers == null)
            {
                return null;
            }

            foreach (ProviderDefinition provider in Providers)
            {
                if (provider != null && string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    return provider;
                }
            }

            return null;
        }

        public ProviderCatalog Clone()
        {
            var copy = new ProviderCatalog
            {
                SchemaVersion = SchemaVersion,
                ActiveProviderId = ActiveProviderId,
                Providers = new List<ProviderDefinition>()
            };

            if (Providers != null)
            {
                foreach (ProviderDefinition provider in Providers)
                {
                    if (provider != null)
                    {
                        copy.Providers.Add(provider.Clone());
                    }
                }
            }

            return copy;
        }
    }

    /// <summary>
    /// 首次启动时可用的 Provider 预设。只提供目录与端点，不包含任何密钥；
    /// 用户可以在设置页删除或改 baseUrl。
    /// </summary>
    public static class ProviderPresets
    {
        public static ProviderCatalog CreateDefault()
        {
            var catalog = new ProviderCatalog { ActiveProviderId = "deepseek" };

            catalog.Providers.Add(new ProviderDefinition
            {
                Id = "deepseek",
                DisplayName = "DeepSeek",
                Api = "openai-completions",
                BaseUrl = "https://api.deepseek.com/v1",
                Compat = new OpenAiCompat
                {
                    MaxTokensField = "max_tokens",
                    SupportsUsageInStreaming = true,
                    ThinkingFormat = "deepseek",
                    SystemRole = "system"
                },
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "deepseek-chat",
                        Name = "DeepSeek Chat",
                        ContextWindow = 65536,
                        MaxOutputTokens = 8192
                    },
                    new ModelDefinition
                    {
                        Id = "deepseek-reasoner",
                        Name = "DeepSeek Reasoner",
                        ContextWindow = 65536,
                        MaxOutputTokens = 8192,
                        SupportsReasoning = true
                    }
                }
            });

            catalog.Providers.Add(new ProviderDefinition
            {
                Id = "openai",
                DisplayName = "OpenAI",
                Api = "openai-completions",
                BaseUrl = "https://api.openai.com/v1",
                Compat = new OpenAiCompat
                {
                    MaxTokensField = "max_completion_tokens",
                    SupportsUsageInStreaming = true,
                    ThinkingFormat = "openai",
                    SystemRole = "system"
                },
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "gpt-4o-mini",
                        Name = "GPT-4o mini",
                        ContextWindow = 128000,
                        MaxOutputTokens = 16384,
                        SupportsImages = true
                    },
                    new ModelDefinition
                    {
                        Id = "gpt-4o",
                        Name = "GPT-4o",
                        ContextWindow = 128000,
                        MaxOutputTokens = 16384,
                        SupportsImages = true
                    }
                }
            });

            catalog.Providers.Add(new ProviderDefinition
            {
                Id = "anthropic",
                DisplayName = "Anthropic",
                Api = "anthropic-messages",
                BaseUrl = "https://api.anthropic.com",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "claude-sonnet-4-5",
                        Name = "Claude Sonnet 4.5",
                        ContextWindow = 200000,
                        MaxOutputTokens = 16384,
                        SupportsImages = true,
                        SupportsReasoning = true
                    },
                    new ModelDefinition
                    {
                        Id = "claude-3-5-haiku-latest",
                        Name = "Claude 3.5 Haiku",
                        ContextWindow = 200000,
                        MaxOutputTokens = 8192,
                        SupportsImages = true
                    }
                }
            });

            return catalog;
        }
    }
}
