using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class AskUserQuestionToolTests
    {
        [Fact]
        public async Task SingleQuestionReturnsSelectedLabel()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(Question("选哪个目录？", false, "源码目录", "文档目录"))),
                Context(),
                CancellationToken.None);

            UserQuestionRequest pending = WaitForPending(service);
            Assert.Equal("选哪个目录？", pending.Question);
            Assert.Equal(2, pending.Options.Count);
            Assert.False(pending.MultiSelect);

            Assert.True(service.TryAnswer(pending.RequestId, new[] { "q1o2" }));

            ToolResult result = await task;
            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("问题 1：选哪个目录？", result.Content);
            Assert.Contains("回答：文档目录", result.Content);
            Assert.Equal("提问 1 个，已答复", result.Summary);
        }

        [Fact]
        public async Task MultipleQuestionsAreAskedInOrder()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(
                    Question("第一步？", false, "开始"),
                    Question("第二步？", false, "继续", "停止"))),
                Context(),
                CancellationToken.None);

            UserQuestionRequest first = WaitForPending(service);
            Assert.Equal("第一步？", first.Question);
            service.TryAnswer(first.RequestId, new[] { "q1o1" });

            UserQuestionRequest second = WaitForPending(service);
            Assert.Equal("第二步？", second.Question);
            service.TryAnswer(second.RequestId, new[] { "q2o2" });

            ToolResult result = await task;
            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("回答：开始", result.Content);
            Assert.Contains("回答：停止", result.Content);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public async Task MultiSelectReturnsJoinedLabels()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(Question("要检查哪些？", true, "编码", "换行", "路径"))),
                Context(),
                CancellationToken.None);

            UserQuestionRequest pending = WaitForPending(service);
            Assert.True(pending.MultiSelect);
            service.TryAnswer(pending.RequestId, new[] { "q1o1", "q1o3" });

            ToolResult result = await task;
            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("回答：编码；路径", result.Content);
        }

        [Fact]
        public async Task FreeTextAnswerIsReturnedWithoutAnyFlag()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(Question("叫什么名字？", false, "默认"))),
                Context(),
                CancellationToken.None);

            // 选项是快捷项：用户不点选项、直接写一句，也算回答。
            UserQuestionRequest pending = WaitForPending(service);
            Assert.True(service.TryAnswerText(pending.RequestId, "阿喵"));

            ToolResult result = await task;
            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("回答：阿喵", result.Content);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public async Task SkippedQuestionEndsWithCancelledStatus()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(Question("继续吗？", false, "继续", "停"))),
                Context(),
                CancellationToken.None);

            UserQuestionRequest pending = WaitForPending(service);
            Assert.True(service.TryCancel(pending.RequestId));

            ToolResult result = await task;
            Assert.Equal(ToolResultStatus.Cancelled, result.Status);
            Assert.Equal("question_cancelled", result.ErrorCode);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public async Task PartialAnswersAreKeptWhenUserSkipsLaterQuestion()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);
            Task<ToolResult> task = tool.ExecuteAsync(
                Call(Questions(
                    Question("第一题？", false, "好"),
                    Question("第二题？", false, "好"))),
                Context(),
                CancellationToken.None);

            UserQuestionRequest first = WaitForPending(service);
            service.TryAnswer(first.RequestId, new[] { "q1o1" });

            UserQuestionRequest second = WaitForPending(service);
            service.TryCancel(second.RequestId);

            ToolResult result = await task;
            Assert.Equal(ToolResultStatus.Cancelled, result.Status);
            Assert.Contains("回答：好", result.Content);
            Assert.Contains("取消了后续提问", result.Content);
        }

        [Fact]
        public async Task MissingServiceReportsUnavailable()
        {
            var tool = new AskUserQuestionTool(null);

            ToolResult result = await tool.ExecuteAsync(
                Call(Questions(Question("问？", false, "好"))),
                Context(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("question_unavailable", result.ErrorCode);
        }

        [Fact]
        public async Task InvalidArgumentsAreReportedWithoutAsking()
        {
            var service = new UserQuestionService();
            var tool = new AskUserQuestionTool(service);

            ToolResult empty = await tool.ExecuteAsync(Call(Object()), Context(), CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, empty.Status);

            ToolResult noOptions = await tool.ExecuteAsync(
                Call(Questions(Question("问？", false))),
                Context(),
                CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, noOptions.Status);
            Assert.Contains("至少要有一个选项", noOptions.Content);

            ToolResult duplicate = await tool.ExecuteAsync(
                Call(Questions(Question("问？", false, "同样", "同样"))),
                Context(),
                CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, duplicate.Status);
            Assert.Contains("不能重复", duplicate.Content);

            ToolResult tooMany = await tool.ExecuteAsync(
                Call(Questions(
                    Question("一", false, "好"),
                    Question("二", false, "好"),
                    Question("三", false, "好"),
                    Question("四", false, "好"),
                    Question("五", false, "好"))),
                Context(),
                CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, tooMany.Status);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public void AuditRecordsCountsNotQuestionText()
        {
            var tool = new AskUserQuestionTool(new UserQuestionService());
            ToolCall call = Call(Questions(
                Question("机密问题正文？", false, "甲", "乙"),
                Question("第二个问题？", true, "丙")));

            string audit = ((IToolAuditRedactor)tool).GetAuditArguments(call, Context());

            Assert.Contains("questions=2", audit);
            Assert.Contains("options=3", audit);
            Assert.Contains("multi_select=true", audit);
            Assert.DoesNotContain("机密问题正文", audit);
        }

        private static UserQuestionRequest WaitForPending(UserQuestionService service)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                IReadOnlyList<UserQuestionRequest> pending = service.GetPending();
                if (pending.Count > 0)
                {
                    return pending[0];
                }

                Thread.Sleep(10);
            }

            Assert.Fail("等待提问挂起超时。");
            return null;
        }

        private static ToolContext Context()
        {
            return new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                CallId = "c1",
                Now = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero)
            };
        }

        private static ToolCall Call(JsonValue arguments)
        {
            return new ToolCall
            {
                Id = "c1",
                Name = ToolNames.AskUserQuestion,
                ArgumentsJson = JsonWriter.Write(arguments)
            };
        }

        private static JsonValue Questions(params JsonValue[] questions)
        {
            JsonValue root = JsonValue.NewObject();
            JsonValue array = JsonValue.NewArray();
            for (int i = 0; i < questions.Length; i++)
            {
                array.Add(questions[i]);
            }

            root["questions"] = array;
            return root;
        }

        private static JsonValue Question(string text, bool multiSelect, params string[] labels)
        {
            JsonValue question = JsonValue.NewObject();
            question["question"] = JsonValue.From(text);
            if (multiSelect)
            {
                question["multi_select"] = JsonValue.From(true);
            }

            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < labels.Length; i++)
            {
                JsonValue option = JsonValue.NewObject();
                option["label"] = JsonValue.From(labels[i]);
                options.Add(option);
            }

            question["options"] = options;
            return question;
        }

        private static JsonValue Object()
        {
            return JsonValue.NewObject();
        }
    }
}
