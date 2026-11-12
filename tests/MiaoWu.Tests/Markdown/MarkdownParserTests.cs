using System.Text;
using MiaoWu.Core.Markdown;
using Xunit;

namespace MiaoWu.Tests.Markdown
{
    public sealed class MarkdownParserTests
    {
        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        [InlineData("\r")]
        public void EveryLineEndingBecomesSoftBreak(string newLine)
        {
            MarkdownDocument document = MarkdownParser.Parse("第一行" + newLine + "第二行");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            MarkdownInline inline = Assert.Single(paragraph.Inlines);
            Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
            Assert.Equal("第一行\n第二行", inline.Text);
        }

        [Fact]
        public void MixedLineEndingsAreSupported()
        {
            MarkdownDocument document = MarkdownParser.Parse("一\r\n二\n三\r四");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Equal("一\n二\n三\n四", Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void HeadingsClampToLevelThree()
        {
            MarkdownDocument document = MarkdownParser.Parse("# 一\n## 二\n### 三\n#### 四");

            Assert.Collection(
                document.Blocks,
                block => Assert.Equal(1, Assert.IsType<MarkdownHeading>(block).Level),
                block => Assert.Equal(2, Assert.IsType<MarkdownHeading>(block).Level),
                block => Assert.Equal(3, Assert.IsType<MarkdownHeading>(block).Level),
                block => Assert.Equal(3, Assert.IsType<MarkdownHeading>(block).Level));

            MarkdownHeading fourth = Assert.IsType<MarkdownHeading>(document.Blocks[3]);
            Assert.Equal("四", Assert.Single(fourth.Inlines).Text);
        }

        [Fact]
        public void HashWithoutSpaceIsParagraph()
        {
            MarkdownDocument document = MarkdownParser.Parse("#foo");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Equal("#foo", Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void UnorderedListKeepsNestingLevels()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "- 一级\n  - 二级\n    - 三级\n- 回到一级");

            MarkdownList list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));
            Assert.False(list.IsOrdered);
            Assert.Equal(1, list.Start);
            Assert.Collection(
                list.Items,
                item =>
                {
                    Assert.Equal(0, item.Level);
                    Assert.Equal("•", item.Marker);
                    Assert.Equal("一级", Assert.Single(item.Inlines).Text);
                },
                item => Assert.Equal(1, item.Level),
                item => Assert.Equal(2, item.Level),
                item => Assert.Equal(0, item.Level));
        }

        [Theory]
        [InlineData("-")]
        [InlineData("*")]
        [InlineData("+")]
        public void UnorderedListMarkersAreNormalized(string marker)
        {
            MarkdownDocument document = MarkdownParser.Parse(marker + " 项目");

            MarkdownList list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));
            MarkdownListItem item = Assert.Single(list.Items);
            Assert.Equal("•", item.Marker);
            Assert.Equal("项目", Assert.Single(item.Inlines).Text);
        }

        [Fact]
        public void OrderedListUsesFirstNumberAndSequentialMarkers()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "3. 三\n4. 四\n   1. 四甲\n   2. 四乙");

            MarkdownList list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));
            Assert.True(list.IsOrdered);
            Assert.Equal(3, list.Start);
            Assert.Collection(
                list.Items,
                item => Assert.Equal("3.", item.Marker),
                item => Assert.Equal("4.", item.Marker),
                item =>
                {
                    Assert.Equal(1, item.Level);
                    Assert.Equal("1.", item.Marker);
                },
                item =>
                {
                    Assert.Equal(1, item.Level);
                    Assert.Equal("2.", item.Marker);
                });
        }

        [Fact]
        public void OrderedListAcceptsParenthesisMarker()
        {
            MarkdownDocument document = MarkdownParser.Parse("1) 一\n2) 二");

            MarkdownList list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));
            Assert.Equal("1.", list.Items[0].Marker);
            Assert.Equal("2.", list.Items[1].Marker);
        }

        [Fact]
        public void ListContinuationLinesJoinPreviousItem()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "- 第一行\n  续行一\n  续行二\n- 第二项");

            MarkdownList list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(
                "第一行\n续行一\n续行二",
                Assert.Single(list.Items[0].Inlines).Text);
            Assert.Equal("第二项", Assert.Single(list.Items[1].Inlines).Text);
        }

        [Fact]
        public void BlankLineEndsList()
        {
            MarkdownDocument document = MarkdownParser.Parse("- 一\n\n二");

            Assert.Collection(
                document.Blocks,
                block => Assert.IsType<MarkdownList>(block),
                block => Assert.IsType<MarkdownParagraph>(block));
        }

        [Fact]
        public void QuoteSupportsListsAndNestedQuotes()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "> - 项目\n>   - 子项\n>\n> > 内层");

            MarkdownQuote quote = Assert.IsType<MarkdownQuote>(Assert.Single(document.Blocks));
            Assert.Equal(2, quote.Blocks.Count);

            MarkdownList list = Assert.IsType<MarkdownList>(quote.Blocks[0]);
            Assert.Equal(2, list.Items.Count);

            MarkdownQuote nested = Assert.IsType<MarkdownQuote>(quote.Blocks[1]);
            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(nested.Blocks));
            Assert.Equal("内层", Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void TablePadsMissingCellsAndDropsExtraCells()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "| 名称 | 数量 |\n| --- | :---: |\n| 苹果 |\n| 梨 | 2 | 多余 |");

            MarkdownTable table = Assert.IsType<MarkdownTable>(Assert.Single(document.Blocks));
            Assert.Equal(new[] { "名称", "数量" }, table.Headers);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "苹果", string.Empty }, table.Rows[0]);
            Assert.Equal(new[] { "梨", "2" }, table.Rows[1]);
        }

        [Fact]
        public void TableRequiresMatchingSeparatorCells()
        {
            MarkdownDocument document = MarkdownParser.Parse("a | b\n---");

            Assert.Collection(
                document.Blocks,
                block => Assert.IsType<MarkdownParagraph>(block),
                block => Assert.IsType<MarkdownRule>(block));
        }

        [Fact]
        public void RulesAreNotConfusedWithTableSeparators()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "| 甲 | 乙 |\n| --- | --- |\n| 1 | 2 |\n\n---\n\n***\n\n___");

            Assert.Collection(
                document.Blocks,
                block => Assert.IsType<MarkdownTable>(block),
                block => Assert.IsType<MarkdownRule>(block),
                block => Assert.IsType<MarkdownRule>(block),
                block => Assert.IsType<MarkdownRule>(block));
        }

        [Fact]
        public void FencedCodeKeepsLanguageAndTrailingNewline()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "```csharp\nvar value = 1;\n```");

            MarkdownCodeBlock code = Assert.IsType<MarkdownCodeBlock>(
                Assert.Single(document.Blocks));
            Assert.Equal("csharp", code.Language);
            Assert.Equal("var value = 1;\n", code.Text);
        }

        [Fact]
        public void FencedCodeContentIsNotParsed()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "```\n**不是加粗** 与 [不是链接](u)\n\n```");

            MarkdownCodeBlock code = Assert.IsType<MarkdownCodeBlock>(
                Assert.Single(document.Blocks));
            Assert.Null(code.Language);
            Assert.Equal("**不是加粗** 与 [不是链接](u)\n\n", code.Text);
        }

        [Fact]
        public void UnclosedFenceRunsToEndOfDocument()
        {
            MarkdownDocument document = MarkdownParser.Parse("~~~\n第一行\n第二行");

            MarkdownCodeBlock code = Assert.IsType<MarkdownCodeBlock>(
                Assert.Single(document.Blocks));
            Assert.Null(code.Language);
            Assert.Equal("第一行\n第二行\n", code.Text);
        }

        [Fact]
        public void EmptyFencedCodeHasEmptyText()
        {
            MarkdownDocument document = MarkdownParser.Parse("```\n```");

            MarkdownCodeBlock code = Assert.IsType<MarkdownCodeBlock>(
                Assert.Single(document.Blocks));
            Assert.Null(code.Language);
            Assert.Equal(string.Empty, code.Text);
        }

        [Fact]
        public void InlineCodeKeepsMarkersAndBackticks()
        {
            MarkdownDocument document = MarkdownParser.Parse("`a*b` 和 ``x`y``");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Collection(
                paragraph.Inlines,
                inline =>
                {
                    Assert.Equal(MarkdownInlineKind.Code, inline.Kind);
                    Assert.Equal("a*b", inline.Text);
                },
                inline =>
                {
                    Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
                    Assert.Equal(" 和 ", inline.Text);
                },
                inline =>
                {
                    Assert.Equal(MarkdownInlineKind.Code, inline.Kind);
                    Assert.Equal("x`y", inline.Text);
                });
        }

        [Theory]
        [InlineData("**粗体**", MarkdownInlineKind.Strong, "粗体")]
        [InlineData("__粗体__", MarkdownInlineKind.Strong, "粗体")]
        [InlineData("*斜体*", MarkdownInlineKind.Emphasis, "斜体")]
        [InlineData("_斜体_", MarkdownInlineKind.Emphasis, "斜体")]
        [InlineData("***两者***", MarkdownInlineKind.Strong, "两者")]
        public void StrongAndEmphasisAreRecognized(
            string source,
            MarkdownInlineKind expectedKind,
            string expectedText)
        {
            MarkdownDocument document = MarkdownParser.Parse(source);

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            MarkdownInline inline = Assert.Single(paragraph.Inlines);
            Assert.Equal(expectedKind, inline.Kind);
            Assert.Equal(expectedText, inline.Text);
        }

        [Fact]
        public void SnakeCaseIsNotSplitIntoEmphasis()
        {
            MarkdownDocument document = MarkdownParser.Parse("snake_case_name 和 a_b_c");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            MarkdownInline inline = Assert.Single(paragraph.Inlines);
            Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
            Assert.Equal("snake_case_name 和 a_b_c", inline.Text);
        }

        [Fact]
        public void LinkKeepsLabelAndUrl()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "见 [示例](https://example.com/a?b=1) 结束");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Collection(
                paragraph.Inlines,
                inline => Assert.Equal("见 ", inline.Text),
                inline =>
                {
                    Assert.Equal(MarkdownInlineKind.Link, inline.Kind);
                    Assert.Equal("示例", inline.Text);
                    Assert.Equal("https://example.com/a?b=1", inline.Url);
                },
                inline => Assert.Equal(" 结束", inline.Text));
        }

        [Fact]
        public void ImageSyntaxStaysPlainText()
        {
            MarkdownDocument document = MarkdownParser.Parse("![替代文字](image.png)");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            MarkdownInline inline = Assert.Single(paragraph.Inlines);
            Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
            Assert.Equal("![替代文字](image.png)", inline.Text);
            Assert.DoesNotContain(
                paragraph.Inlines,
                candidate => candidate.Kind == MarkdownInlineKind.Link);
        }

        [Fact]
        public void HtmlTagsStayPlainText()
        {
            const string Source = "<b>粗</b> <script>alert(1)</script>";

            MarkdownDocument document = MarkdownParser.Parse(Source);

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Equal(Source, Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void EntitiesAreDecoded()
        {
            MarkdownDocument document = MarkdownParser.Parse(
                "&amp; &lt; &gt; &quot; &#39;");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Equal("& < > \" '", Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void BackslashEscapesAsciiPunctuation()
        {
            MarkdownDocument document = MarkdownParser.Parse("\\*非斜体\\* 与 \\`代码\\`");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            MarkdownInline inline = Assert.Single(paragraph.Inlines);
            Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
            Assert.Equal("*非斜体* 与 `代码`", inline.Text);
        }

        [Fact]
        public void UnclosedMarkersStayLiteral()
        {
            MarkdownDocument document = MarkdownParser.Parse("**未闭合 和 `反引号");

            MarkdownParagraph paragraph = Assert.IsType<MarkdownParagraph>(
                Assert.Single(document.Blocks));
            Assert.Equal("**未闭合 和 `反引号", Assert.Single(paragraph.Inlines).Text);
        }

        [Fact]
        public void EmptyInputProducesEmptyDocument()
        {
            MarkdownDocument document = MarkdownParser.Parse(null);

            Assert.True(document.IsEmpty);
            Assert.Empty(document.Blocks);
        }

        [Fact]
        public void WhitespaceOnlyInputProducesEmptyDocument()
        {
            Assert.True(MarkdownParser.Parse(string.Empty).IsEmpty);
            Assert.True(MarkdownParser.Parse("   \r\n\t\n").IsEmpty);
        }

        [Fact]
        public void LeadingByteOrderMarkIsIgnored()
        {
            MarkdownDocument document = MarkdownParser.Parse("\uFEFF# 标题");

            MarkdownHeading heading = Assert.IsType<MarkdownHeading>(
                Assert.Single(document.Blocks));
            Assert.Equal(1, heading.Level);
            Assert.Equal("标题", Assert.Single(heading.Inlines).Text);
        }

        [Fact]
        public void PlainFactoryCreatesTextInline()
        {
            MarkdownInline inline = MarkdownInline.Plain("文本");

            Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
            Assert.Equal("文本", inline.Text);
            Assert.Null(inline.Url);
        }

        [Fact]
        public void LargeDocumentParsesWithoutThrowing()
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < 2000; index++)
            {
                builder.Append("## 标题 ").Append(index).Append("\r\n");
                builder.Append("- 项目 **加粗** 与 `code` [链接](https://example.com/")
                    .Append(index)
                    .Append(")\r\n");
                builder.Append("普通段落 &amp; 文本\r\n\r\n");
                builder.Append("```csharp\r\nvar value = ").Append(index).Append(";\r\n```\r\n\r\n");
            }

            string source = builder.ToString();
            Assert.True(source.Length >= 100000);

            MarkdownDocument document = MarkdownParser.Parse(source);

            Assert.False(document.IsEmpty);
            Assert.True(document.Blocks.Count > 0);
        }
    }
}
