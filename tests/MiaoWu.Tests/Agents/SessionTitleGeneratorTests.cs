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
        public void NormalizeTakesFirstNonEmptyLine()
        {
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("\n\n  修复构建失败  \n第二行"));
        }

        [Fact]
        public void NormalizeStripsQuotesEmphasisAndPrefix()
        {
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("“修复构建失败”"));
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("**修复构建失败**"));
            Assert.Equal("修复构建失败", SessionTitleGenerator.Normalize("标题：修复构建失败"));
            Assert.Equal("Fix build", SessionTitleGenerator.Normalize("Title: `Fix build`"));
        }

        [Fact]
        public void NormalizeRemovesControlsAndCollapsesWhitespace()
        {
            Assert.Equal("hello world", SessionTitleGenerator.Normalize("hello\u0007 \u200B  world\u202E"));
        }

        [Fact]
        public void NormalizeReturnsNullForBlankText()
        {
            Assert.Null(SessionTitleGenerator.Normalize(null));
            Assert.Null(SessionTitleGenerator.Normalize("   \n\t "));
            Assert.Null(SessionTitleGenerator.Normalize("\u200B\u200B"));
        }

        [Fact]
        public void NormalizeTruncatesByCharacterLimit()
        {
            string text = new string('中', SessionTitleGenerator.MaxTitleChars + 8);
            string title = SessionTitleGenerator.Normalize(text);

            Assert.EndsWith("…", title);
            Assert.True(title.Length <= SessionTitleGenerator.MaxTitleChars + 1);
            Assert.True(Encoding.UTF8.GetByteCount(title) <= SessionTitleGenerator.MaxTitleBytes);
        }

        [Fact]
        public void NormalizeTruncatesEmojiByByteLimit()
        {
            // 每个 emoji 占 4 字节；96 字节预算放不下 32 个，也不会切出半个代理对。
            var builder = new StringBuilder();
            for (int i = 0; i < 30; i++)
            {
                builder.Append("\U0001F600");
            }

            string title = SessionTitleGenerator.Normalize(builder.ToString());

            Assert.EndsWith("…", title);
            Assert.True(Encoding.UTF8.GetByteCount(title) <= SessionTitleGenerator.MaxTitleBytes);
            int lastCodeUnit = title.Length - 2;
            Assert.False(char.IsHighSurrogate(title[lastCodeUnit]) && !char.IsLowSurrogate(title[lastCodeUnit + 1]));
        }

        [Fact]
        public void BuildRequestFramesFirstMessageAsJson()
        {
            LlmRequest request = SessionTitleGenerator.BuildRequest("他说：\"你好\"\n第二行", "s1");

            Assert.NotNull(request);
            Assert.Equal(SessionTitleGenerator.SystemPrompt, request.SystemPrompt);
            Assert.Equal("s1", request.SessionId);
            Assert.Equal(SessionTitleGenerator.MaxOutputTokens, request.MaxOutputTokens);
            Assert.Single(request.Messages);
            Assert.Equal("user", request.Messages[0].Role);

            string text = request.Messages[0].GetPlainText();
            Assert.Contains("\"他说：\\\"你好\\\"\\n第二行\"", text);
        }

        [Fact]
        public void BuildRequestReturnsNullWithoutInput()
        {
            Assert.Null(SessionTitleGenerator.BuildRequest(null, "s1"));
            Assert.Null(SessionTitleGenerator.BuildRequest("   ", "s1"));
        }

        [Fact]
        public void BuildRequestCapsInputBytes()
        {
            string text = new string('长', 3000);
            LlmRequest request = SessionTitleGenerator.BuildRequest(text, "s1");

            string plain = request.Messages[0].GetPlainText();
            Assert.True(Encoding.UTF8.GetByteCount(plain) < SessionTitleGenerator.MaxInputBytes + 200);
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
