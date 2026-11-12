using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class StreamMapperTests
    {
        private static LlmStreamEvent[] Map(ILlmStreamMapper mapper, params string[] payloads)
        {
            var events = new List<LlmStreamEvent>();
            foreach (string payload in payloads)
            {
                events.AddRange(mapper.Map(new SseFrame { Data = payload }));
            }

            return events.ToArray();
        }

        [Fact]
        public void OpenAiMapsTextUsageAndStop()
        {
            var mapper = new OpenAiStreamMapper();
            LlmStreamEvent[] events = Map(
                mapper,
                "{\"id\":\"resp_1\",\"model\":\"gpt-test\",\"choices\":[{\"delta\":{\"content\":\"你\"}}]}",
                "{\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2,\"total_tokens\":11}}");

            var deltas = events.OfType<LlmTextDelta>().ToList();
            Assert.Equal(2, deltas.Count);
            Assert.Equal("你", deltas[0].Delta);

            var completed = mapper.Finish() as LlmCompleted;
            Assert.NotNull(completed);
            Assert.Equal("你好", completed.Response.Content[0].Text);
            Assert.Equal(StopReason.Stop, completed.Response.StopReason);
            Assert.Equal(9, completed.Response.Usage.Input);
            Assert.Equal(2, completed.Response.Usage.Output);
            Assert.Equal("resp_1", completed.Response.ResponseId);
            Assert.Equal("gpt-test", completed.Response.ModelId);
        }

        [Fact]
        public void OpenAiMapsReasoningAndToolCalls()
        {
            var mapper = new OpenAiStreamMapper();
            LlmStreamEvent[] events = Map(
                mapper,
                "{\"choices\":[{\"delta\":{\"reasoning_content\":\"想\"}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}");

            Assert.Single(events.OfType<LlmThinkingDelta>());
            Assert.Equal(2, events.OfType<LlmToolCallDelta>().Count());

            var completed = mapper.Finish() as LlmCompleted;
            Assert.NotNull(completed);
            Assert.Equal(StopReason.ToolUse, completed.Response.StopReason);
            Assert.Equal(2, completed.Response.Content.Count);
            Assert.Equal("thinking", completed.Response.Content[0].Type);
            Assert.Equal("toolCall", completed.Response.Content[1].Type);
            Assert.Equal("read_file", completed.Response.Content[1].ToolName);
            Assert.Equal("{\"path\":\"a.txt\"}", completed.Response.Content[1].ArgumentsJson);
        }

        [Fact]
        public void AnthropicMapsTextAndStop()
        {
            var mapper = new AnthropicStreamMapper();
            LlmStreamEvent[] events = Map(
                mapper,
                "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-test\",\"usage\":{\"input_tokens\":7}}}",
                "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}",
                "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}");

            Assert.Single(events.OfType<LlmTextDelta>());

            var completed = mapper.Finish() as LlmCompleted;
            Assert.NotNull(completed);
            Assert.Equal("你好", completed.Response.Content[0].Text);
            Assert.Equal(StopReason.Stop, completed.Response.StopReason);
            Assert.Equal(7, completed.Response.Usage.Input);
            Assert.Equal(3, completed.Response.Usage.Output);
            Assert.Equal("msg_1", completed.Response.ResponseId);
        }

        [Fact]
        public void AnthropicMapsToolUse()
        {
            var mapper = new AnthropicStreamMapper();
            Map(
                mapper,
                "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"list_dir\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\".\\\"}\"}}",
                "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":5}}");

            var completed = mapper.Finish() as LlmCompleted;
            Assert.NotNull(completed);
            Assert.Equal(StopReason.ToolUse, completed.Response.StopReason);
            Assert.Single(completed.Response.Content);
            Assert.Equal("list_dir", completed.Response.Content[0].ToolName);
            Assert.Equal("{\"path\":\".\"}", completed.Response.Content[0].ArgumentsJson);
        }

        [Fact]
        public void AnthropicMapsErrorFrame()
        {
            var mapper = new AnthropicStreamMapper();
            LlmStreamEvent[] events = Map(
                mapper,
                "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}");

            var failed = Assert.Single(events.OfType<LlmFailed>());
            Assert.Equal(MiaoWu.Core.Errors.AgentErrorKind.RateLimited, failed.Error.Kind);
            Assert.True(failed.Error.Retryable);
        }
    }
}
