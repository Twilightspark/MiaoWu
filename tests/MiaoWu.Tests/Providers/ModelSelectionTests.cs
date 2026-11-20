using System;
using MiaoWu.Core.Models;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    /// <summary>共享模型选择：同值不触发事件，变化时通知一次。</summary>
    public sealed class ModelSelectionTests
    {
        [Fact]
        public void SetReturnsFalseForSameValue()
        {
            var selection = new ModelSelection();

            Assert.True(selection.Set("p", "m"));
            Assert.False(selection.Set("p", "m"));
            Assert.Equal("p", selection.ProviderId);
            Assert.Equal("m", selection.ModelId);
        }

        [Fact]
        public void ChangedFiresOnlyOnRealChange()
        {
            var selection = new ModelSelection();
            int changes = 0;
            selection.Changed += (sender, args) => changes++;

            selection.Set("p", "m");
            selection.Set("p", "m");
            selection.Set("q", "n");

            Assert.Equal(2, changes);
            Assert.Equal("q", selection.ProviderId);
            Assert.Equal("n", selection.ModelId);
        }

        [Fact]
        public void HasValueReflectsProviderOrModel()
        {
            var selection = new ModelSelection();
            Assert.False(selection.HasValue);

            selection.Set("p", null);
            Assert.True(selection.HasValue);
            Assert.Equal("p", selection.ProviderId);
        }

        [Fact]
        public void SubscriberFailureDoesNotBreakSwitch()
        {
            var selection = new ModelSelection();
            selection.Changed += (sender, args) => throw new InvalidOperationException("订阅者炸了");

            Assert.True(selection.Set("q", "n"));
            Assert.Equal("q", selection.ProviderId);
        }
    }
}
