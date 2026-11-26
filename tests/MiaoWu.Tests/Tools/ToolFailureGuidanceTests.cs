using MiaoWu.Core.Agents.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>
    /// 失败建议：自测暴露的浪费是“超时后原样重试”和“不知道换哪个工具”，
    /// 这里固定住每种已知失败模式的下一步动作与连续失败提醒。
    /// </summary>
    public sealed class ToolFailureGuidanceTests
    {
        [Fact]
        public void SuccessfulResultHasNoGuidance()
        {
            ToolResult result = ToolResult.Success(Call("read"), "ok", "ok", false, false);
            Assert.Null(ToolFailureGuidance.For(result, 0));
            Assert.Null(ToolFailureGuidance.For(result, 5));
        }

        [Fact]
        public void KnownFailuresPointAtTheNextStep()
        {
            string timeout = ToolFailureGuidance.For(
                Failure("web_search", "timeout", "抓取超时（20 秒）。"),
                1);
            Assert.Contains("web_fetch", timeout);
            Assert.Contains("不要用相同查询重试", timeout);

            string fetch = ToolFailureGuidance.For(
                Failure("web_fetch", "http_status", "服务器返回 503。"),
                1);
            Assert.Contains("换一个来源", fetch);

            string missingPath = ToolFailureGuidance.For(Failure("read", "not_found", "路径不存在：a.txt"), 1);
            Assert.Contains("glob", missingPath);

            string denied = ToolFailureGuidance.For(
                ToolResult.Failure(Call("write"), ToolResultStatus.Denied, "approval_denied", "用户拒绝。"),
                1);
            Assert.Contains("用户拒绝", denied);
            Assert.Contains("不要用同样参数重试", denied);

            string invalid = ToolFailureGuidance.For(
                ToolResult.Failure(Call("write"), ToolResultStatus.InvalidArguments, "missing_argument", "缺少 content。"),
                1);
            Assert.Contains("参数", invalid);
        }

        [Fact]
        public void RepeatedFailureEscalatesOnTheSecondAttempt()
        {
            ToolResult timeout = Failure("web_search", "timeout", "抓取超时（20 秒）。");
            Assert.DoesNotContain("连续失败", ToolFailureGuidance.For(timeout, 1));
            Assert.Contains("连续失败 2 次", ToolFailureGuidance.For(timeout, 2));
            Assert.Contains("停止原样重试", ToolFailureGuidance.For(timeout, 2));

            // 没有专门建议的失败也要在第 2 次提醒停手。
            ToolResult unknown = Failure("read", "mystery", "未知失败。");
            Assert.Null(ToolFailureGuidance.For(unknown, 1));
            Assert.Contains("连续失败 2 次", ToolFailureGuidance.For(unknown, 2));
        }

        [Fact]
        public void StripRemovesTheGuidanceBlockOnly()
        {
            string content = "抓取超时（20 秒）。"
                + ToolFailureGuidance.Marker
                + " web_search 后端不可达时改用 web_fetch。";

            Assert.Equal("抓取超时（20 秒）。", ToolFailureGuidance.Strip(content));
            Assert.Equal("已读取 3 行。", ToolFailureGuidance.Strip("已读取 3 行。"));
            Assert.Equal(string.Empty, ToolFailureGuidance.Strip(null));
        }

        private static ToolResult Failure(string tool, string code, string message)
        {
            return ToolResult.Failure(Call(tool), ToolResultStatus.Failed, code, message);
        }

        private static ToolCall Call(string tool)
        {
            return new ToolCall { Id = "call_1", Name = tool, ArgumentsJson = "{}" };
        }
    }
}
