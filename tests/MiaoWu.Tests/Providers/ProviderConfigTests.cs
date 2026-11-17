using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class ProviderConfigTests
    {
        [Fact]
        public void RoundTripsCatalog()
        {
            string path = Path.Combine(Path.GetTempPath(), "miaowu-providers-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new ProviderConfigStore(path);
                var catalog = new ProviderCatalog { ActiveProviderId = "p1" };
                var provider = new ProviderDefinition
                {
                    Id = "p1",
                    DisplayName = "Provider 1",
                    Api = "openai-completions",
                    BaseUrl = "https://example.test/v1",
                    Temperature = 0.3,
                    MaxOutputTokens = 2048
                };
                provider.Compat.SystemRole = "developer";
                provider.Models.Add(new ModelDefinition
                {
                    Id = "m1",
                    Name = "Model 1",
                    ContextWindow = 16000,
                    MaxOutputTokens = 4000,
                    SupportsImages = true
                });
                catalog.Providers.Add(provider);

                store.Save(catalog);
                ProviderCatalog loaded = new ProviderConfigStore(path).Load();

                Assert.Equal("p1", loaded.ActiveProviderId);
                Assert.Single(loaded.Providers);
                Assert.Equal("https://example.test/v1", loaded.Providers[0].BaseUrl);
                Assert.Equal("developer", loaded.Providers[0].Compat.SystemRole);
                Assert.Single(loaded.Providers[0].Models);
                Assert.True(loaded.Providers[0].Models[0].SupportsImages);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                }
            }
        }

        [Fact]
        public void MissingFileReturnsEmptyCatalog()
        {
            string path = Path.Combine(Path.GetTempPath(), "miaowu-providers-" + Guid.NewGuid().ToString("N") + ".json");
            ProviderCatalog catalog = new ProviderConfigStore(path).Load();

            Assert.Empty(catalog.Providers);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void CorruptFileFallsBackToEmptyCatalog()
        {
            string path = Path.Combine(Path.GetTempPath(), "miaowu-providers-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ not json", System.Text.Encoding.UTF8);
                ProviderCatalog catalog = new ProviderConfigStore(path).Load();
                Assert.Empty(catalog.Providers);
            }
            finally
            {
                foreach (string file in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + "*"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        [Fact]
        public void ValidatorRejectsBadEndpointAndDuplicateModels()
        {
            var catalog = new ProviderCatalog();
            var provider = new ProviderDefinition
            {
                Id = "p",
                DisplayName = "P",
                Api = "openai-completions",
                BaseUrl = "ftp://example.test"
            };
            provider.Models.Add(new ModelDefinition { Id = "m", ContextWindow = 1000, MaxOutputTokens = 900 });
            provider.Models.Add(new ModelDefinition { Id = "m", ContextWindow = 16000, MaxOutputTokens = 100 });
            catalog.Providers.Add(provider);

            IReadOnlyList<string> errors = ProviderConfigValidator.Validate(catalog);

            Assert.Contains(errors, e => e.Contains("Base URL"));
            Assert.Contains(errors, e => e.Contains("上下文窗口"));
            Assert.Contains(errors, e => e.Contains("模型 ID 重复"));
        }

        [Fact]
        public void ResolvesEnvFallbackAndDefaultModel()
        {
            string catalogPath = Path.Combine(Path.GetTempPath(), "miaowu-providers-" + Guid.NewGuid().ToString("N") + ".json");
            string secretPath = Path.Combine(Path.GetTempPath(), "miaowu-secrets-" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                var catalog = new ProviderCatalog { ActiveProviderId = "p" };
                var provider = new ProviderDefinition
                {
                    Id = "p",
                    DisplayName = "P",
                    Api = "openai-completions",
                    BaseUrl = "https://example.test/v1"
                };
                provider.Models.Add(new ModelDefinition { Id = "m", ContextWindow = 8000, MaxOutputTokens = 1000 });
                catalog.Providers.Add(provider);
                new ProviderConfigStore(catalogPath).Save(catalog);

                var credentials = new DpapiCredentialStore(secretPath);
                credentials.SetKey("p", "secret-key");
                var registry = new ModelRegistry(
                    new ProviderConfigStore(catalogPath).Load(),
                    credentials,
                    new IModelApi[0]);

                ModelDescriptor model = registry.GetDefault();
                Assert.NotNull(model);
                Assert.Equal("m", model.ModelId);

                ProviderCredential credential = registry
                    .ResolveCredentialAsync("p", System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                Assert.Equal("secret-key", credential.ApiKey);
            }
            finally
            {
                foreach (string file in new[] { catalogPath, secretPath, catalogPath + ".bak" })
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
