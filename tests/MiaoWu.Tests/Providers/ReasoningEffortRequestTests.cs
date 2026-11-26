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
        public void OpenAiBodyOnlyCarriesReasoningEffortWhenTheModelSupportsIt()
        {
            JsonValue reasoning = ParseBody(Model(), "high");
            Assert.Equal("high", reasoning.Get("reasoning_effort").AsString());

            JsonValue plain = ParseBody(Model(supportsReasoning: false), "high");
            Assert.Null(plain.Get("reasoning_effort"));

            JsonValue defaultLevel = ParseBody(Model(), null);
            Assert.Null(defaultLevel.Get("reasoning_effort"));
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

        private static JsonValue ParseBody(ModelDescriptor model, string effort)
        {
            string body = LlmRequestJson.BuildOpenAiBody(
                model,
                new LlmRequest
                {
                    Messages = new List<LlmMessage>(),
                    ReasoningEffort = effort
                });

            return JsonParser.Parse(body);
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
