using System;
using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Input;
using Xunit;

namespace MiaoWu.Tests.Input
{
    /// <summary>菜单归约：换代、过期丢弃、stale-while-revalidate、自动关闭与高亮循环。</summary>
    public class InputTriggerMenuTests
    {
        private static InputTriggerMenuState Seed()
        {
            return InputTriggerMenuReducer.Seed(new[]
            {
                new InputTriggerGroup("command", "指令", 0, true, null),
                new InputTriggerGroup("reference", "文件", 2, false, null)
            });
        }

        private static InputTriggerCandidate Candidate(string id)
        {
            return new InputTriggerCandidate { Id = id, Label = id };
        }

        private static TriggerHit Hit(string query)
        {
            return new TriggerHit
            {
                Trigger = TriggerChar.Slash,
                Query = query,
                Start = 0,
                End = query.Length + 1
            };
        }

        private static InputTriggerMenuState Open(string query)
        {
            return InputTriggerMenuReducer.Reduce(Seed(), new MenuHitEvent(Hit(query)));
        }

        [Fact]
        public void Seed_OrdersGroupsAndStartsClosed()
        {
            InputTriggerMenuState state = Seed();

            Assert.False(state.IsOpen);
            Assert.Equal(2, state.Groups.Count);
            Assert.Equal("command", state.Groups[0].Source);
            Assert.Equal("reference", state.Groups[1].Source);
            Assert.All(state.Groups, group => Assert.Equal(InputTriggerGroupStatus.Pending, group.Status));
        }

        [Fact]
        public void Hit_OpensAndBumpsGeneration()
        {
            InputTriggerMenuState state = Open("p");

            Assert.True(state.IsOpen);
            Assert.Equal(1, state.Generation);
            Assert.NotNull(state.Hit);
            Assert.False(state.HasHighlight);
        }

        [Fact]
        public void SameToken_DoesNotBumpGeneration()
        {
            InputTriggerMenuState first = Open("p");
            InputTriggerMenuState second = InputTriggerMenuReducer.Reduce(first, new MenuHitEvent(Hit("p")));

            Assert.Equal(1, second.Generation);
        }

        [Fact]
        public void QueryChange_BumpsGenerationAndKeepsOldItemsWhilePending()
        {
            InputTriggerMenuState first = Open("p");
            InputTriggerMenuState settled = InputTriggerMenuReducer.Reduce(
                first,
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan") }));

            InputTriggerMenuState changed = InputTriggerMenuReducer.Reduce(settled, new MenuHitEvent(Hit("pl")));

            Assert.Equal(2, changed.Generation);
            Assert.Equal(InputTriggerGroupStatus.Pending, changed.FindGroup("command").Status);
            Assert.Single(changed.FindGroup("command").Items);
        }

        [Fact]
        public void Settled_StaleGenerationIsDropped()
        {
            InputTriggerMenuState first = Open("p");
            InputTriggerMenuState changed = InputTriggerMenuReducer.Reduce(first, new MenuHitEvent(Hit("pl")));

            InputTriggerMenuState stale = InputTriggerMenuReducer.Reduce(
                changed,
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan") }));

            Assert.Same(changed, stale);
            Assert.Empty(stale.FindGroup("command").Items);
        }

        [Fact]
        public void Settled_FirstRowBecomesHighlight()
        {
            InputTriggerMenuState state = InputTriggerMenuReducer.Reduce(
                Open("p"),
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan"), Candidate("/compact") }));

            Assert.True(state.HasHighlight);
            Assert.Equal("command", state.HighlightSource);
            Assert.Equal(0, state.HighlightIndex);
        }

        [Fact]
        public void Move_CyclesAcrossReadyGroupsOnly()
        {
            InputTriggerMenuState state = InputTriggerMenuReducer.Reduce(
                Open("p"),
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan") }));
            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupSettledEvent(1, "reference", new[] { Candidate("a.txt"), Candidate("b.txt") }));

            state = InputTriggerMenuReducer.Reduce(state, new MenuMoveEvent(1));
            Assert.Equal("reference", state.HighlightSource);
            Assert.Equal(0, state.HighlightIndex);

            state = InputTriggerMenuReducer.Reduce(state, new MenuMoveEvent(1));
            Assert.Equal(1, state.HighlightIndex);

            state = InputTriggerMenuReducer.Reduce(state, new MenuMoveEvent(1));
            Assert.Equal("command", state.HighlightSource);
            Assert.Equal(0, state.HighlightIndex);

            state = InputTriggerMenuReducer.Reduce(state, new MenuMoveEvent(-1));
            Assert.Equal("reference", state.HighlightSource);
            Assert.Equal(1, state.HighlightIndex);
        }

        [Fact]
        public void Hover_RejectsPendingRows()
        {
            InputTriggerMenuState state = Open("p");
            InputTriggerMenuState hovered = InputTriggerMenuReducer.Reduce(state, new MenuHoverEvent("command", 0));

            Assert.Same(state, hovered);
        }

        [Fact]
        public void AllSettledAndEmpty_AutoCloses()
        {
            InputTriggerMenuState state = Open("zzz");
            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupSettledEvent(1, "command", Array.Empty<InputTriggerCandidate>()));

            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupSettledEvent(1, "reference", Array.Empty<InputTriggerCandidate>()));

            Assert.False(state.IsOpen);
            Assert.Equal(1, state.Generation);
        }

        [Fact]
        public void FailedGroup_IsRemovedAndClosesWhenNothingLeft()
        {
            InputTriggerMenuState state = Open("p");
            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupFailedEvent(1, "command"));
            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupFailedEvent(1, "reference"));

            Assert.False(state.IsOpen);
            Assert.Empty(state.Groups);
        }

        [Fact]
        public void Close_KeepsGenerationForLateResults()
        {
            InputTriggerMenuState state = Open("p");
            state = InputTriggerMenuReducer.Reduce(state, new MenuCloseEvent());

            Assert.False(state.IsOpen);
            Assert.Equal(1, state.Generation);

            InputTriggerMenuState late = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan") }));

            Assert.False(late.IsOpen);
            Assert.Empty(late.FindGroup("command").Items);
        }

        [Fact]
        public void HighlightFallsBackWhenSettledGroupBecomesEmpty()
        {
            InputTriggerMenuState state = InputTriggerMenuReducer.Reduce(
                Open("p"),
                new MenuGroupSettledEvent(1, "command", new[] { Candidate("/plan") }));
            state = InputTriggerMenuReducer.Reduce(state, new MenuMoveEvent(1));
            Assert.Equal("command", state.HighlightSource);

            state = InputTriggerMenuReducer.Reduce(
                state,
                new MenuGroupSettledEvent(1, "command", Array.Empty<InputTriggerCandidate>()));

            Assert.False(state.HasHighlight);
        }
    }
}
