using System.Collections.Generic;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    /// <summary>
    /// 推理等级：只有模型声明了思考通道才把 <c>reasoning_effort</c> 写进请求体；
    /// 默认档（null）不写字段，让 Provider 用自己的思考预算。
    /// </summary>
    public sealed class ReasoningEffortRequestTests
    {
        [Fact]
        public void OpenAiBodyCarriesReasoningEffortForReasoningModel()
        {
            string body = LlmRequestJson.BuildOpenAiBody(
                Model(),
                new LlmRequest
                {
                    Messages = new List<LlmMessage>(),
                    ReasoningEffort = "high"
                });

            JsonValue root = JsonParser.Parse(body);
            Assert.Equal("high", root.Get("reasoning_effort").AsString());
        }

        [Fact]
        public void OpenAiBodyDropsReasoningEffortForPlainModel()
        {
            string body = LlmRequestJson.BuildOpenAiBody(
                Model(supportsReasoning: false),
                new LlmRequest
                {
                    Messages = new List<LlmMessage>(),
                    ReasoningEffort = "high"
                });

            JsonValue root = JsonParser.Parse(body);
            Assert.Null(root.Get("reasoning_effort"));
        }

        [Fact]
        public void OpenAiBodyOmitsDefaultReasoningLevel()
        {
            string body = LlmRequestJson.BuildOpenAiBody(
                Model(),
                new LlmRequest
                {
                    Messages = new List<LlmMessage>(),
                    ReasoningEffort = null
                });

            JsonValue root = JsonParser.Parse(body);
            Assert.Null(root.Get("reasoning_effort"));
        }

        [Theory]
        [InlineData("high", "high")]
        [InlineData(" Medium ", "medium")]
        [InlineData("LOW", "low")]
        [InlineData("", null)]
        [InlineData("turbo", null)]
        public void NormalizeKeepsOnlyKnownLevels(string input, string expected)
        {
            Assert.Equal(expected, MiaoWu.Core.Settings.AgentSettings.NormalizeReasoningEffort(input));
        }

        private static ModelDescriptor Model(bool supportsReasoning = true)
        {
            return new ModelDescriptor
            {
                ProviderId = "p",
                ModelId = "m",
                DisplayName = "测试模型",
                SupportsReasoning = supportsReasoning
            };
        }
    }
}
