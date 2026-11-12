using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Models;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// 模型注册表：由 providers.json + 凭据存储构建不可变快照。
    /// 配置变更时整体重建，运行中的请求继续使用旧 ModelDescriptor。
    /// </summary>
    public sealed class ModelRegistry : IModelRegistry
    {
        private readonly object _gate = new object();
        private readonly ICredentialStore _credentials;
        private readonly Dictionary<string, IModelApi> _apis =
            new Dictionary<string, IModelApi>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<ModelDescriptor> _models = new List<ModelDescriptor>();
        private IReadOnlyList<ProviderDefinition> _providers = new List<ProviderDefinition>();
        private ProviderCatalog _catalog = new ProviderCatalog();
        private string _activeProviderId;

        public ModelRegistry(
            ProviderCatalog catalog,
            ICredentialStore credentials,
            IEnumerable<IModelApi> apis)
        {
            _credentials = credentials;
            if (apis != null)
            {
                foreach (IModelApi api in apis)
                {
                    if (api != null && !string.IsNullOrWhiteSpace(api.Id))
                    {
                        _apis[api.Id] = api;
                    }
                }
            }

            Rebuild(catalog ?? new ProviderCatalog());
        }

        public event EventHandler Changed;

        public IReadOnlyList<ModelDescriptor> Models
        {
            get
            {
                lock (_gate)
                {
                    return _models;
                }
            }
        }

        public IReadOnlyList<ProviderDefinition> Providers
        {
            get
            {
                lock (_gate)
                {
                    return _providers;
                }
            }
        }

        public ProviderCatalog Catalog
        {
            get
            {
                lock (_gate)
                {
                    return _catalog.Clone();
                }
            }
        }

        public ModelDescriptor Resolve(string providerId, string modelId)
        {
            lock (_gate)
            {
                string providerKey = string.IsNullOrWhiteSpace(providerId)
                    ? _activeProviderId
                    : providerId;

                foreach (ModelDescriptor model in _models)
                {
                    if (!string.IsNullOrWhiteSpace(providerKey)
                        && !string.Equals(model.ProviderId, providerKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(modelId)
                        || string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        return model;
                    }
                }

                return null;
            }
        }

        public ModelDescriptor GetDefault()
        {
            lock (_gate)
            {
                if (_models.Count == 0)
                {
                    return null;
                }

                foreach (ModelDescriptor model in _models)
                {
                    if (string.Equals(model.ProviderId, _activeProviderId, StringComparison.OrdinalIgnoreCase))
                    {
                        return model;
                    }
                }

                return _models[0];
            }
        }

        public IModelApi GetApi(string apiId)
        {
            if (string.IsNullOrWhiteSpace(apiId))
            {
                return null;
            }

            IModelApi api;
            return _apis.TryGetValue(apiId, out api) ? api : null;
        }

        public Task<ProviderCredential> ResolveCredentialAsync(
            string providerId,
            CancellationToken cancellationToken)
        {
            ProviderDefinition provider = FindProvider(providerId);
            string key = _credentials == null ? null : _credentials.GetKey(providerId);
            string source = "stored";

            if (string.IsNullOrWhiteSpace(key))
            {
                key = ReadEnvironmentKey(providerId);
                source = string.IsNullOrWhiteSpace(key) ? "none" : "environment";
            }

            var credential = new ProviderCredential
            {
                ProviderId = providerId,
                ApiKey = key,
                BaseUrl = provider == null ? null : provider.BaseUrl,
                Source = source,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            return Task.FromResult(credential);
        }

        public void Rebuild(ProviderCatalog catalog)
        {
            ProviderCatalog normalized = catalog == null ? new ProviderCatalog() : catalog.Clone();
            var providers = new List<ProviderDefinition>();
            var models = new List<ModelDescriptor>();

            foreach (ProviderDefinition provider in normalized.Providers)
            {
                if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
                {
                    continue;
                }

                providers.Add(provider);

                if (provider.Models == null)
                {
                    continue;
                }

                foreach (ModelDefinition model in provider.Models)
                {
                    if (model == null || string.IsNullOrWhiteSpace(model.Id))
                    {
                        continue;
                    }

                    models.Add(new ModelDescriptor
                    {
                        ProviderId = provider.Id,
                        ModelId = model.Id,
                        DisplayName = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                        Api = string.IsNullOrWhiteSpace(provider.Api) ? "openai-completions" : provider.Api,
                        BaseUrl = provider.BaseUrl,
                        ContextWindow = model.ContextWindow <= 0 ? 8192 : model.ContextWindow,
                        MaxOutputTokens = model.MaxOutputTokens <= 0 ? 2048 : model.MaxOutputTokens,
                        SupportsImages = model.SupportsImages,
                        SupportsReasoning = model.SupportsReasoning,
                        Compat = provider.Compat == null ? new OpenAiCompat() : provider.Compat.Clone()
                    });
                }
            }

            string activeProviderId = normalized.ActiveProviderId;
            if (string.IsNullOrWhiteSpace(activeProviderId) && providers.Count > 0)
            {
                activeProviderId = providers[0].Id;
            }

            lock (_gate)
            {
                _catalog = normalized;
                _providers = providers;
                _models = models;
                _activeProviderId = activeProviderId;
            }

            EventHandler handler = Changed;
            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                }
            }
        }

        private ProviderDefinition FindProvider(string providerId)
        {
            lock (_gate)
            {
                foreach (ProviderDefinition provider in _providers)
                {
                    if (string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))
                    {
                        return provider;
                    }
                }
            }

            return null;
        }

        private static string ReadEnvironmentKey(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            string specific = Environment.GetEnvironmentVariable(
                "MIAOWU_API_KEY_" + providerId.ToUpperInvariant().Replace('-', '_'));

            if (!string.IsNullOrWhiteSpace(specific))
            {
                return specific;
            }

            string shared = Environment.GetEnvironmentVariable("MIAOWU_API_KEY");
            if (!string.IsNullOrWhiteSpace(shared))
            {
                return shared;
            }

            switch (providerId.ToLowerInvariant())
            {
                case "openai":
                    return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                case "deepseek":
                    return Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                case "anthropic":
                    return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                default:
                    return null;
            }
        }
    }
}
