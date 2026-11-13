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
    /// 首次启动与设置页可用的 Provider 预设。只提供目录与端点，不包含任何密钥；
    /// 用户可以在设置页删除或改 baseUrl。
    /// </summary>
    public static class ProviderPresets
    {
        /// <summary>
        /// 首次引导支持的六个供应商，顺序即下拉顺序。
        /// 返回新的可变对象，调用方可以安全地加入 providers.json。
        /// </summary>
        public static IReadOnlyList<ProviderDefinition> CreateOnboardingProviders()
        {
            return new List<ProviderDefinition>
            {
                CreateDeepSeek(),
                CreateOpenCodeGo(),
                CreateMiniMax(),
                CreateZai(),
                CreateXiaomi(),
                CreateKimi()
            };
        }

        public static ProviderCatalog CreateDefault()
        {
            var catalog = new ProviderCatalog { ActiveProviderId = "deepseek" };

            foreach (ProviderDefinition provider in CreateOnboardingProviders())
            {
                catalog.Providers.Add(provider);
            }

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

        private static ProviderDefinition CreateDeepSeek()
        {
            return new ProviderDefinition
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
                        Id = "deepseek-flash",
                        Name = "DeepSeek Flash",
                        ContextWindow = 131072,
                        MaxOutputTokens = 8192
                    },
                    new ModelDefinition
                    {
                        Id = "deepseek-v4-pro",
                        Name = "DeepSeek V4 Pro",
                        ContextWindow = 131072,
                        MaxOutputTokens = 16384,
                        SupportsReasoning = true
                    },
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
            };
        }

        private static ProviderDefinition CreateOpenCodeGo()
        {
            return new ProviderDefinition
            {
                Id = "opencode-go",
                DisplayName = "OpenCode Go",
                Api = "openai-completions",
                BaseUrl = "https://opencode.ai/inference/go/openai/v1",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "kimi-k2.7-code",
                        Name = "Kimi K2.7 Code",
                        ContextWindow = 262144,
                        MaxOutputTokens = 32768
                    },
                    new ModelDefinition
                    {
                        Id = "glm-5.3",
                        Name = "GLM-5.3",
                        ContextWindow = 200000,
                        MaxOutputTokens = 32768,
                        SupportsReasoning = true
                    }
                }
            };
        }

        private static ProviderDefinition CreateMiniMax()
        {
            return new ProviderDefinition
            {
                Id = "minimax",
                DisplayName = "MiniMax",
                Api = "openai-completions",
                BaseUrl = "https://api.minimaxi.com/v1",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "MiniMax-M3",
                        Name = "MiniMax M3",
                        ContextWindow = 204800,
                        MaxOutputTokens = 32768,
                        SupportsReasoning = true
                    }
                }
            };
        }

        private static ProviderDefinition CreateZai()
        {
            return new ProviderDefinition
            {
                Id = "zai",
                DisplayName = "Z.AI",
                Api = "openai-completions",
                BaseUrl = "https://api.z.ai/api/paas/v4",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "glm-5.3",
                        Name = "GLM-5.3",
                        ContextWindow = 200000,
                        MaxOutputTokens = 32768,
                        SupportsReasoning = true
                    },
                    new ModelDefinition
                    {
                        Id = "glm-5.3-flash",
                        Name = "GLM-5.3 Flash",
                        ContextWindow = 128000,
                        MaxOutputTokens = 16384
                    }
                }
            };
        }

        private static ProviderDefinition CreateXiaomi()
        {
            return new ProviderDefinition
            {
                Id = "xiaomi",
                DisplayName = "XiaoMi",
                Api = "openai-completions",
                BaseUrl = "https://api.xiaomimimo.com/v1",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "mimo-v2.6-flash",
                        Name = "MiMo V2.6 Flash",
                        ContextWindow = 131072,
                        MaxOutputTokens = 16384
                    },
                    new ModelDefinition
                    {
                        Id = "mimo-v2.6-pro",
                        Name = "MiMo V2.6 Pro",
                        ContextWindow = 131072,
                        MaxOutputTokens = 32768,
                        SupportsReasoning = true
                    }
                }
            };
        }

        private static ProviderDefinition CreateKimi()
        {
            return new ProviderDefinition
            {
                Id = "kimi",
                DisplayName = "Kimi",
                Api = "openai-completions",
                BaseUrl = "https://api.moonshot.cn/v1",
                Models =
                {
                    new ModelDefinition
                    {
                        Id = "kimi-k3.1-pro",
                        Name = "Kimi K3.1 Pro",
                        ContextWindow = 262144,
                        MaxOutputTokens = 32768,
                        SupportsReasoning = true
                    },
                    new ModelDefinition
                    {
                        Id = "kimi-k3",
                        Name = "Kimi K3",
                        ContextWindow = 262144,
                        MaxOutputTokens = 32768
                    }
                }
            };
        }
    }
}
