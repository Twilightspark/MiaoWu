using System;
using System.Collections.Generic;
using MiaoWu.Core.Input;
using Xunit;

namespace MiaoWu.Tests.Input
{
    /// <summary>触发检测的边界用例：直接对应 docs/08 §9.6 的验收清单。</summary>
    public class TriggerDetectorTests
    {
        private static TriggerHit Detect(string draft, int? caret = null, TriggerGuardTier guard = TriggerGuardTier.Plain)
        {
            return TriggerDetector.Detect(draft, caret ?? draft.Length, guard);
        }

        [Fact]
        public void SlashAtLineStart_Triggers()
        {
            TriggerHit hit = Detect("/pl");

            Assert.NotNull(hit);
            Assert.Equal(TriggerChar.Slash, hit.Trigger);
            Assert.Equal("pl", hit.Query);
            Assert.Equal(0, hit.Start);
            Assert.Equal(3, hit.End);
            Assert.True(hit.IsLeading);
        }

        [Theory]
        [InlineData("https://a/b")]
        [InlineData("a/b")]
        [InlineData("see http:/x")]
        public void SlashInsideUrlOrPath_DoesNotTrigger(string draft)
        {
            Assert.Null(Detect(draft));
        }

        [Fact]
        public void SlashInProse_DoesNotTrigger()
        {
            Assert.Null(Detect("晚上好/明天见"));
        }

        [Fact]
        public void SlashAfterWhitespace_TriggersInline()
        {
            TriggerHit hit = Detect("先看一下 /comp");

            Assert.NotNull(hit);
            Assert.Equal("comp", hit.Query);
            Assert.False(hit.IsLeading);
        }

        [Fact]
        public void SlashUntilWhitespace_StopsAtSpace()
        {
            Assert.Null(Detect("/plan args"));
        }

        [Fact]
        public void ClaimedGuard_SuppressesSlash()
        {
            Assert.Null(Detect("/pl", 3, TriggerGuardTier.Claimed));
        }

        [Fact]
        public void FrozenGuard_SuppressesEverything()
        {
            Assert.Null(Detect("@src/", 5, TriggerGuardTier.Frozen));
        }

        [Fact]
        public void AtAfterWhitespace_TriggersWithQuery()
        {
            TriggerHit hit = Detect("@src/main.cs");

            Assert.NotNull(hit);
            Assert.Equal(TriggerChar.At, hit.Trigger);
            Assert.Equal("src/main.cs", hit.Query);
            Assert.False(hit.Quoted);
            Assert.True(hit.IsLeading);
        }

        [Fact]
        public void AtInEmail_DoesNotTrigger()
        {
            Assert.Null(Detect("user@host"));
        }

        [Fact]
        public void AtFollowedBySpace_DoesNotTrigger()
        {
            Assert.Null(Detect("@ hello"));
        }

        [Fact]
        public void QuotedAt_AllowsSpacesAndReportsQuoted()
        {
            TriggerHit hit = Detect("@\"a b");

            Assert.NotNull(hit);
            Assert.True(hit.Quoted);
            Assert.Equal("a b", hit.Query);
        }

        [Fact]
        public void ClosedQuote_IsNotActive()
        {
            Assert.Null(Detect("@\"a b\" tail"));
        }

        [Fact]
        public void QuotedAtAfterText_Triggers()
        {
            TriggerHit hit = Detect("看一下 @\"dir/my file");

            Assert.NotNull(hit);
            Assert.True(hit.Quoted);
            Assert.Equal("dir/my file", hit.Query);
            Assert.False(hit.IsLeading);
        }

        [Fact]
        public void AtEmptyQuery_StillTriggers()
        {
            TriggerHit hit = Detect("hello @");

            Assert.NotNull(hit);
            Assert.Equal(string.Empty, hit.Query);
            Assert.Equal(6, hit.Start);
        }

        [Fact]
        public void CaretInMiddle_OnlySeesTextBeforeCaret()
        {
            TriggerHit hit = Detect("@a b", 2);

            Assert.NotNull(hit);
            Assert.Equal("a", hit.Query);
            Assert.Equal(2, hit.End);
        }

        [Fact]
        public void SignatureTracksQueryAndSpan()
        {
            TriggerHit first = Detect("@src/");
            TriggerHit second = Detect("@src/m");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first.Signature, second.Signature);
        }

        [Fact]
        public void LeadingIsCharacterizedByWhitespaceOnlyPrefix()
        {
            Assert.True(Detect("   /p").IsLeading);
            Assert.False(Detect("x /p").IsLeading);
        }
    }
}
