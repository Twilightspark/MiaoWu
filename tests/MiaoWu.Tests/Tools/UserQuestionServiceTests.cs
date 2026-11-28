using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class UserQuestionServiceTests
    {
        [Fact]
        public async Task AnswerReturnsSelectedLabels()
        {
            var service = new UserQuestionService();
            Task<UserQuestionAnswer> pending = service.AskAsync(Request(false, "甲", "乙"), CancellationToken.None);

            Assert.Equal(1, service.GetPendingCount());
            Assert.True(service.TryAnswer("req-1", new[] { "q1o2" }));

            UserQuestionAnswer answer = await pending;
            Assert.False(answer.IsCancelled);
            Assert.Equal(new[] { "q1o2" }, answer.SelectedOptionIds);
            Assert.Equal(new[] { "乙" }, answer.SelectedLabels);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public async Task UnknownOrMultiOptionAnswerIsRejectedAndStaysPending()
        {
            var service = new UserQuestionService();
            Task<UserQuestionAnswer> pending = service.AskAsync(Request(false, "甲", "乙"), CancellationToken.None);

            Assert.False(service.TryAnswer("req-1", new[] { "q1o9" }));
            Assert.False(service.TryAnswer("req-1", new[] { "q1o1", "q1o2" }));
            Assert.False(service.TryAnswer("missing", new[] { "q1o1" }));
            Assert.Equal(1, service.GetPendingCount());

            Assert.True(service.TryAnswer("req-1", new[] { "q1o1" }));
            Assert.Equal(new[] { "甲" }, (await pending).SelectedLabels);
        }

        [Fact]
        public async Task MultiSelectAcceptsSeveralOptions()
        {
            var service = new UserQuestionService();
            Task<UserQuestionAnswer> pending = service.AskAsync(Request(true, "甲", "乙", "丙"), CancellationToken.None);

            Assert.True(service.TryAnswer("req-1", new[] { "q1o1", "q1o3" }));

            UserQuestionAnswer answer = await pending;
            Assert.Equal(new[] { "甲", "丙" }, answer.SelectedLabels);
        }

        [Fact]
        public async Task CancelProducesCancelledAnswerAndEvent()
        {
            var service = new UserQuestionService();
            var events = new List<UserQuestionChangeKind>();
            service.Changed += (sender, args) => events.Add(args.Kind);

            Task<UserQuestionAnswer> pending = service.AskAsync(Request(false, "甲", "乙"), CancellationToken.None);
            Assert.True(service.TryCancel("req-1"));
            Assert.False(service.TryCancel("req-1"));

            UserQuestionAnswer answer = await pending;
            Assert.True(answer.IsCancelled);
            Assert.Empty(answer.SelectedLabels);
            Assert.Equal(
                new[] { UserQuestionChangeKind.Requested, UserQuestionChangeKind.Cancelled },
                events);
        }

        [Fact]
        public async Task CancellationTokenCancelsPendingQuestion()
        {
            var service = new UserQuestionService();
            using (var cts = new CancellationTokenSource())
            {
                Task<UserQuestionAnswer> pending = service.AskAsync(Request(false, "甲"), cts.Token);
                Assert.Equal(1, service.GetPendingCount());

                cts.Cancel();

                UserQuestionAnswer answer = await pending;
                Assert.True(answer.IsCancelled);
                Assert.Equal(0, service.GetPendingCount());
            }
        }

        [Fact]
        public async Task DuplicateRequestIdReusesPendingTask()
        {
            var service = new UserQuestionService();
            UserQuestionRequest request = Request(false, "甲");
            Task<UserQuestionAnswer> first = service.AskAsync(request, CancellationToken.None);
            Task<UserQuestionAnswer> second = service.AskAsync(request, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(1, service.GetPendingCount());

            service.TryAnswer("req-1", new[] { "q1o1" });
            UserQuestionAnswer answer = await first;
            Assert.Equal(new[] { "甲" }, answer.SelectedLabels);
        }

        [Fact]
        public async Task ClearSessionCancelsOnlyThatSession()
        {
            var service = new UserQuestionService();
            UserQuestionRequest mine = Request(false, "甲");
            UserQuestionRequest other = Request(false, "乙");
            other.RequestId = "req-2";
            other.SessionId = "s2";

            Task<UserQuestionAnswer> first = service.AskAsync(mine, CancellationToken.None);
            Task<UserQuestionAnswer> second = service.AskAsync(other, CancellationToken.None);

            service.ClearSession("s1");

            Assert.True((await first).IsCancelled);
            Assert.Equal(1, service.GetPendingCount());
            Assert.Equal("s2", service.GetPending()[0].SessionId);

            Assert.True(service.TryAnswer("req-2", new[] { "q1o1" }));
            Assert.False((await second).IsCancelled);
        }

        [Fact]
        public void MissingSessionIdIsRejected()
        {
            var service = new UserQuestionService();
            var request = Request(false, "甲");
            request.SessionId = null;

            Assert.Throws<ArgumentException>(() => AskIgnoringResult(service, request));
        }

        private static void AskIgnoringResult(UserQuestionService service, UserQuestionRequest request)
        {
            // 校验在返回 Task 之前完成，属于同步抛错。
            service.AskAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }

        [Fact]
        public async Task FreeTextAnswerIsAcceptedWithoutFlag()
        {
            var service = new UserQuestionService();
            UserQuestionRequest request = Request(false, "甲", "乙");
            Task<UserQuestionAnswer> pending = service.AskAsync(request, CancellationToken.None);

            Assert.True(service.TryAnswerText("req-1", "  我自己写一个答案  "));

            UserQuestionAnswer answer = await pending;
            Assert.Equal("我自己写一个答案", answer.Text);
            Assert.Empty(answer.SelectedLabels);
            Assert.False(answer.IsCancelled);
        }

        [Fact]
        public async Task FreeTextRejectedWhenBlankMissingOrTooLong()
        {
            var service = new UserQuestionService();
            Task<UserQuestionAnswer> pending = service.AskAsync(Request(false, "甲"), CancellationToken.None);

            Assert.False(service.TryAnswerText("req-1", "   "));
            Assert.False(service.TryAnswerText("req-missing", "答案"));
            Assert.Equal(1, service.GetPendingCount());

            UserQuestionRequest secondRequest = Request(false, "甲");
            secondRequest.RequestId = "req-2";
            Task<UserQuestionAnswer> second = service.AskAsync(secondRequest, CancellationToken.None);
            Assert.False(service.TryAnswerText("req-2", new string('x', UserQuestionRequest.MaxAnswerTextLength + 1)));
            Assert.True(service.TryAnswerText("req-2", "短答案"));
            Assert.Equal("短答案", (await second).Text);

            service.TryCancel("req-1");
            Assert.True((await pending).IsCancelled);
        }

        private static UserQuestionRequest Request(bool multiSelect, params string[] labels)
        {
            var options = new List<UserQuestionOption>();
            for (int i = 0; i < labels.Length; i++)
            {
                options.Add(new UserQuestionOption
                {
                    Id = "q1o" + (i + 1),
                    Label = labels[i]
                });
            }

            return new UserQuestionRequest
            {
                RequestId = "req-1",
                SessionId = "s1",
                ToolName = "ask_user_question",
                Question = "选一个",
                Options = options,
                MultiSelect = multiSelect,
                CreatedAt = DateTimeOffset.Now
            };
        }
    }
}
