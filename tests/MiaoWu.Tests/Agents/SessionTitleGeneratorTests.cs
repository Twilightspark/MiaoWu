using System;
using System.Collections.Generic;
using System.Text;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Models;
using Xunit;

namespace MiaoWu.Tests.Agents
{
    public sealed class SessionTitleGeneratorTests
    {
        [Fact]
        public void NormalizeKeepsFirstLineAndStripsDecoration()
        {
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("\n\n  修复构建失败  \n第二行"));
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("“修复构建失败”"));
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("**修复构建失败**"));
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("标题：修复构建失败"));
            Assert.Equal("Fix build", SessionTitleGenerator.Normalize("Title: `Fix build`"));

            // 控制字符与零宽字符被移除，连续空白折叠成一个空格。
            Assert.Equal("hello world", SessionTitleGenerator.Normalize("hello\u0007 \u200B  world\u202E"));
        }

        [Fact]
        public void NormalizeReturnsNullForBlankTextAndTruncatesByBothLimits()
        {
            Assert.Null(SessionTitleGenerator.Normalize(null));
            Assert.Null(SessionTitleGenerator.Normalize("   \n\t "));
            Assert.Null(SessionTitleGenerator.Normalize("\u200B\u200B"));

            string chinese = SessionTitleGenerator.Normalize(new string('中', SessionTitleGenerator.MaxTitleChars + 8));
            Assert.EndsWith("…", chinese);
            Assert.True(chinese.Length <= SessionTitleGenerator.MaxTitleChars + 1);
            Assert.True(Encoding.UTF8.GetByteCount(chinese) <= SessionTitleGenerator.MaxTitleBytes);

            // 每个 emoji 占 4 字节；96 字节预算放不下 32 个，也不会切出半个代理对。
            var builder = new StringBuilder();
            for (int i = 0; i < 30; i++)
            {
                builder.Append("\U0001F600");
            }

            string emoji = SessionTitleGenerator.Normalize(builder.ToString());
            Assert.EndsWith("…", emoji);
            Assert.True(Encoding.UTF8.GetByteCount(emoji) <= SessionTitleGenerator.MaxTitleBytes);
            int lastCodeUnit = emoji.Length - 2;
            Assert.False(char.IsHighSurrogate(emoji[lastCodeUnit]) && !char.IsLowSurrogate(emoji[lastCodeUnit + 1]));
        }

        [Fact]
        public void BuildRequestFramesInputCapsBytesAndRejectsBlank()
        {
            LlmRequest request = SessionTitleGenerator.BuildRequest("他说：\"你好\"\n第二行", "s1");

            Assert.NotNull(request);
            Assert.Equal(SessionTitleGenerator.SystemPrompt, request.SystemPrompt);
            Assert.Equal("s1", request.SessionId);
            Assert.Equal(SessionTitleGenerator.MaxOutputTokens, request.MaxOutputTokens);
            Assert.Single(request.Messages);
            Assert.Equal("user", request.Messages[0].Role);
            Assert.Contains("\"他说：\\\"你好\\\"\\n第二行\"", request.Messages[0].GetPlainText());

            string text = new string('长', 3000);
            LlmRequest capped = SessionTitleGenerator.BuildRequest(text, "s1");
            Assert.True(Encoding.UTF8.GetByteCount(capped.Messages[0].GetPlainText())
                < SessionTitleGenerator.MaxInputBytes + 200);

            Assert.Null(SessionTitleGenerator.BuildRequest(null, "s1"));
            Assert.Null(SessionTitleGenerator.BuildRequest("   ", "s1"));
        }

        [Fact]
        public void ExtractTextJoinsTextBlocksAndIgnoresThinking()
        {
            var response = new LlmResponse
            {
                Content = new List<LlmContent>
                {
                    new LlmContent { Type = "thinking", Thinking = "内部推理" },
                    new LlmContent { Type = "text", Text = "修复" },
                    new LlmContent { Type = "text", Text = "构建" }
                }
            };

            Assert.Equal("修复 构建", SessionTitleGenerator.ExtractText(response));
            Assert.Null(SessionTitleGenerator.ExtractText(new LlmResponse()));
        }
    }
}
