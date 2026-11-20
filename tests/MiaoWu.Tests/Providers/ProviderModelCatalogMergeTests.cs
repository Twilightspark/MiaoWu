using System.Collections.Generic;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class ProviderModelCatalogMergeTests
    {
        [Fact]
        public void AddsMissingModelsAndKeepsExistingSettings()
        {
            var provider = new ProviderDefinition
            {
                Id = "custom",
                Models = new List<ModelDefinition>
                {
                    new ModelDefinition
                    {
                        Id = "keep-me",
                        Name = "我改过的名字",
                        ContextWindow = 64000,
                        MaxOutputTokens = 4096,
                        SupportsReasoning = true
                    }
                }
            };

            int added = ProviderModelCatalogMerge.Merge(provider, new[]
            {
                new ModelCatalogEntry("keep-me", "接口返回的名字"),
                new ModelCatalogEntry("new-model", "New Model")
            });

            Assert.Equal(1, added);
            Assert.Equal(2, provider.Models.Count);

            ModelDefinition kept = provider.Models[0];
            Assert.Equal("我改过的名字", kept.Name);
            Assert.Equal(64000, kept.ContextWindow);
            Assert.Equal(4096, kept.MaxOutputTokens);
            Assert.True(kept.SupportsReasoning);

            ModelDefinition appended = provider.Models[1];
            Assert.Equal("new-model", appended.Id);
            Assert.Equal("New Model", appended.Name);
            Assert.Equal(ProviderModelCatalogMerge.DefaultContextWindow, appended.ContextWindow);
            Assert.Equal(ProviderModelCatalogMerge.DefaultMaxOutputTokens, appended.MaxOutputTokens);
        }

        [Fact]
        public void DeduplicatesIdsCaseInsensitivelyAndSkipsBlankEntries()
        {
            var provider = new ProviderDefinition
            {
                Id = "p",
                Models = new List<ModelDefinition>
                {
                    new ModelDefinition { Id = "M1" }
                }
            };

            int added = ProviderModelCatalogMerge.Merge(provider, new[]
            {
                new ModelCatalogEntry("m1", null),
                new ModelCatalogEntry("m2", null),
                new ModelCatalogEntry("M2", null),
                new ModelCatalogEntry("   ", null),
                null
            });

            Assert.Equal(1, added);
            Assert.Equal(2, provider.Models.Count);
            Assert.Equal("m2", provider.Models[1].Id);
        }

        [Fact]
        public void IsIdempotentAcrossRepeatedRefreshes()
        {
            var provider = new ProviderDefinition { Id = "p" };
            var entries = new[] { new ModelCatalogEntry("m1", null), new ModelCatalogEntry("m2", null) };

            Assert.Equal(2, ProviderModelCatalogMerge.Merge(provider, entries));
            Assert.Equal(0, ProviderModelCatalogMerge.Merge(provider, entries));
            Assert.Equal(2, provider.Models.Count);
        }

        [Fact]
        public void CreatesModelsListWhenProviderHasNone()
        {
            var provider = new ProviderDefinition { Id = "p", Models = null };

            int added = ProviderModelCatalogMerge.Merge(
                provider,
                new[] { new ModelCatalogEntry("m1", "Model One") });

            Assert.Equal(1, added);
            Assert.NotNull(provider.Models);
            Assert.Single(provider.Models);
        }

        [Fact]
        public void CreateModelTrimsIdAndFallsBackToIdForDisplayName()
        {
            ModelDefinition model = ProviderModelCatalogMerge.CreateModel(
                new ModelCatalogEntry("  m3  ", "   "));

            Assert.Equal("m3", model.Id);
            Assert.Equal("m3", model.Name);
            Assert.Equal(ProviderModelCatalogMerge.DefaultContextWindow, model.ContextWindow);
            Assert.Equal(ProviderModelCatalogMerge.DefaultMaxOutputTokens, model.MaxOutputTokens);
        }
    }
}
