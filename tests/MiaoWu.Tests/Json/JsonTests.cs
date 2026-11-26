using System;
using System.Collections.Generic;
using MiaoWu.Infrastructure.Json;
using Xunit;

namespace MiaoWu.Tests.Json
{
    public sealed class JsonTests
    {
        // ---------- 解析：基本类型与结构 ----------

        [Fact]
        public void ParsesScalarKindsAndTopLevelNull()
        {
            JsonValue nullValue = JsonParser.Parse("null");
            Assert.Equal(JsonKind.Null, nullValue.Kind);
            Assert.True(nullValue.IsNull);
            Assert.Equal(0, nullValue.Count);
            Assert.Equal("null", JsonWriter.Write(nullValue));

            JsonValue trueValue = JsonParser.Parse("true");
            Assert.Equal(JsonKind.Boolean, trueValue.Kind);
            Assert.True(trueValue.AsBoolean());

            JsonValue falseValue = JsonParser.Parse("false");
            Assert.False(falseValue.AsBoolean());

            Assert.Equal(JsonKind.Number, JsonParser.Parse("42").Kind);
            Assert.Equal(JsonKind.String, JsonParser.Parse("\"text\"").Kind);
            Assert.Equal(JsonKind.Array, JsonParser.Parse("[]").Kind);
            Assert.Equal(JsonKind.Object, JsonParser.Parse("{}").Kind);
        }

        [Fact]
        public void ParsesNestedObjectsAndArrays()
        {
            JsonValue root = JsonParser.Parse("{\"a\":{\"b\":[1,2,{\"c\":true}]},\"d\":null}");

            Assert.Equal(2, root.Count);
            JsonValue inner = root.Get("a").Get("b");
            Assert.Equal(3, inner.Count);
            Assert.Equal(2, inner.Get(1).AsInt());
            Assert.True(inner.Get(2).Get("c").AsBoolean());
            Assert.True(root.Get("d").IsNull);
            Assert.Null(root.Get("missing"));
        }

        [Fact]
        public void ParsesChineseKeysAndValues()
        {
            JsonValue root = JsonParser.Parse("{\"窗口布局\":\"左中右\",\"缩放\":1.5}");

            Assert.Equal("左中右", root.Get("窗口布局").AsString());
            Assert.Equal(1.5, root.Get("缩放").AsDouble());

            // 非 ASCII 字符原样输出，不做 \u 转义。
            string text = JsonWriter.Write(root);
            Assert.Contains("窗口布局", text);
            Assert.Contains("左中右", text);
            Assert.Equal("左中右", JsonParser.Parse(text).Get("窗口布局").AsString());
        }

        [Fact]
        public void AcceptsWhitespaceAndLeadingBom()
        {
            JsonValue root = JsonParser.Parse("\uFEFF \n{\"a\": [ 1 , 2 ]}\t ");

            Assert.Equal(2, root.Get("a").Count);
            Assert.Equal(2, root.Get("a").Get(1).AsInt());
        }

        // ---------- 解析：字符串转义 ----------

        [Fact]
        public void ParsesEscapedCharacters()
        {
            JsonValue value = JsonParser.Parse("\"a\\\"b\\\\c\\/d\\be\\ff\\ng\\rh\\ti\"");

            Assert.Equal("a\"b\\c/d\be\ff\ng\rh\ti", value.AsString());
        }

        [Fact]
        public void ParsesUnicodeEscapesAndPreservesSurrogatePairs()
        {
            // U+20000 𠀀，UTF-16 代理对 D840 DC00。
            const string Supplement = "\uD840\uDC00";

            JsonValue parsed = JsonParser.Parse("\"\\uD840\\uDC00\"");
            Assert.Equal(Supplement, parsed.AsString());
            Assert.Equal(2, parsed.AsString().Length);

            Assert.Equal(Supplement, JsonParser.Parse(JsonWriter.Write(parsed)).AsString());
            Assert.Equal(Supplement, JsonParser.Parse(JsonWriter.WriteIndented(parsed)).AsString());
        }

        // ---------- 解析：数字 ----------

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.5)]
        [InlineData(1e10)]
        [InlineData(1e300)]
        public void NumbersRoundTrip(double expected)
        {
            JsonValue value = JsonValue.From(expected);

            JsonValue compact = JsonParser.Parse(JsonWriter.Write(value));
            JsonValue indented = JsonParser.Parse(JsonWriter.WriteIndented(value));

            Assert.Equal(JsonKind.Number, compact.Kind);
            Assert.Equal(expected, compact.AsDouble());
            Assert.Equal(expected, indented.AsDouble());
        }

        [Fact]
        public void WritesIntegralNumbersWithoutDecimalPoint()
        {
            Assert.Equal("0", JsonWriter.Write(JsonValue.From(0.0)));
            Assert.Equal("1", JsonWriter.Write(JsonValue.From(1.0)));
            Assert.Equal("-1", JsonWriter.Write(JsonValue.From(-1.0)));
            Assert.Equal("1.5", JsonWriter.Write(JsonValue.From(1.5)));
            Assert.Equal("0.001", JsonWriter.Write(JsonValue.From(0.001)));

            // 文本形式允许被规范化，数值必须保持不变。
            Assert.Equal("1", JsonWriter.Write(JsonParser.Parse("1.0")));
        }

        [Fact]
        public void WriteRejectsNaNAndInfinity()
        {
            Assert.Throws<ArgumentException>(() => { JsonWriter.Write(JsonValue.From(double.NaN)); });
            Assert.Throws<ArgumentException>(() => { JsonWriter.Write(JsonValue.From(double.PositiveInfinity)); });
            Assert.Throws<ArgumentException>(() => { JsonWriter.WriteIndented(JsonValue.From(double.NegativeInfinity)); });
        }

        // ---------- 写出：紧凑 / 缩进 ----------

        [Fact]
        public void WritesCompactJson()
        {
            JsonValue items = JsonValue.NewArray();
            items.Add(JsonValue.From("x"));

            JsonValue tail = JsonValue.NewObject();
            tail.Add("d", JsonValue.Null);

            JsonValue root = JsonValue.NewObject();
            root.Add("a", JsonValue.From(1));
            root.Add("b", items);
            root.Add("c", tail);

            Assert.Equal("{\"a\":1,\"b\":[\"x\"],\"c\":{\"d\":null}}", JsonWriter.Write(root));
        }

        [Fact]
        public void WritesIndentedJsonWithTwoSpaces()
        {
            JsonValue items = JsonValue.NewArray();
            items.Add(JsonValue.From(1));
            items.Add(JsonValue.From(2));

            JsonValue root = JsonValue.NewObject();
            root.Add("a", JsonValue.From(1));
            root.Add("b", items);

            const string Expected = "{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ]\n}";
            Assert.Equal(Expected, JsonWriter.WriteIndented(root));

            Assert.Equal("{}", JsonWriter.WriteIndented(JsonValue.NewObject()));
            Assert.Equal("[]", JsonWriter.WriteIndented(JsonValue.NewArray()));
        }

        [Fact]
        public void EscapesControlCharactersOnWrite()
        {
            JsonValue value = JsonValue.From("a\u0001b\nc\td\"e\\f");

            string text = JsonWriter.Write(value);

            Assert.Equal("\"a\\u0001b\\nc\\td\\\"e\\\\f\"", text);
            Assert.Equal(value.AsString(), JsonParser.Parse(text).AsString());
        }

        [Fact]
        public void IndentedOutputReparsesToTheSameDocument()
        {
            JsonValue root = JsonParser.Parse(
                "{\"中文\":[\"\\uD840\\uDC00\",{\"n\":1.5}],\"ok\":true,\"空\":null}");

            string compact = JsonWriter.Write(root);
            string indented = JsonWriter.WriteIndented(root);

            JsonValue reparsed = JsonParser.Parse(indented);
            Assert.Equal("\uD840\uDC00", reparsed.Get("中文").Get(0).AsString());
            Assert.Equal(1.5, reparsed.Get("中文").Get(1).Get("n").AsDouble());
            Assert.True(reparsed.Get("ok").AsBoolean());
            Assert.True(reparsed.Get("空").IsNull);

            Assert.Equal(compact, JsonWriter.Write(reparsed));
        }

        [Fact]
        public void UnknownFieldsSurviveRoundTrip()
        {
            JsonValue root = JsonParser.Parse("{\"known\":1,\"futureField\":{\"x\":[true,null]}}");
            root["known"] = JsonValue.From(2);

            string text = JsonWriter.WriteIndented(root);
            JsonValue reparsed = JsonParser.Parse(text);

            Assert.Equal(2, reparsed.Get("known").AsInt());
            Assert.True(reparsed.Get("futureField").Get("x").Get(0).AsBoolean());
            Assert.True(reparsed.Get("futureField").Get("x").Get(1).IsNull);
        }

        // ---------- 对象成员顺序与重复键 ----------

        [Fact]
        public void KeepsMemberInsertionOrder()
        {
            JsonValue root = JsonParser.Parse("{\"z\":1,\"a\":2,\"m\":3}");

            Assert.Equal("z,a,m", JoinKeys(root));

            root["b"] = JsonValue.From(4);
            Assert.Equal("z,a,m,b", JoinKeys(root));

            root["a"] = JsonValue.From(9);
            Assert.Equal("z,a,m,b", JoinKeys(root));
            Assert.Equal(9, root.Get("a").AsInt());
            Assert.Equal(4, root.Count);
        }

        [Fact]
        public void DuplicateKeysKeepFirstPositionAndLastValue()
        {
            JsonValue root = JsonParser.Parse("{\"a\":1,\"b\":2,\"a\":3}");

            Assert.Equal(2, root.Count);
            Assert.Equal("a,b", JoinKeys(root));
            Assert.Equal(3, root.Get("a").AsInt());
        }

        // ---------- 索引器与查找 ----------

        [Fact]
        public void MissingLookupsAndScalarCollectionsStayEmpty()
        {
            JsonValue root = JsonParser.Parse("{\"a\":[1,2]}");

            Assert.Null(root["missing"]);
            Assert.Null(root.Get("missing"));
            Assert.Null(root.Get(5));
            Assert.Null(root.Get("a").Get(2));
            Assert.False(root.ContainsKey("missing"));
            Assert.True(root.ContainsKey("a"));

            JsonValue text = JsonValue.From("x");
            Assert.Empty(text.Items);
            Assert.Empty(text.Members);
            Assert.Equal(0, text.Count);
            Assert.Null(text.Get("a"));
            Assert.Null(text.Get(0));
            Assert.False(text.ContainsKey("a"));
        }

        [Fact]
        public void ArrayIndexerOutOfRangeThrows()
        {
            JsonValue array = JsonParser.Parse("[1,2]");

            JsonValue outOfRange = null;
            Assert.Throws<ArgumentOutOfRangeException>(() => { outOfRange = array[2]; });
            Assert.Null(outOfRange);
            Assert.Throws<ArgumentOutOfRangeException>(() => { array[2] = JsonValue.From(3); });
        }

        // ---------- 修改行为 ----------

        [Fact]
        public void AddAndRemoveMutateContainers()
        {
            JsonValue objectValue = JsonValue.NewObject();
            objectValue.Add("a", JsonValue.From(1));
            objectValue.Add("b", JsonValue.From("x"));
            objectValue.Add("a", JsonValue.From(2)); // 原位替换

            Assert.Equal(2, objectValue.Count);
            Assert.Equal("a,b", JoinKeys(objectValue));
            Assert.Equal(2, objectValue.Get("a").AsInt());

            Assert.True(objectValue.Remove("a"));
            Assert.False(objectValue.Remove("a"));
            Assert.False(objectValue.Remove(null));
            Assert.Equal(1, objectValue.Count);
            Assert.Equal("b", JoinKeys(objectValue));

            objectValue.Clear();
            Assert.Equal(0, objectValue.Count);
            Assert.Equal("{}", JsonWriter.Write(objectValue));

            JsonValue array = JsonValue.NewArray();
            array.Add(JsonValue.From(1));
            array.Add(JsonValue.From(2));
            array.Add(null); // 按 JSON null 保存
            Assert.Equal(3, array.Count);
            Assert.Same(JsonValue.Null, array.Get(2));

            array[0] = JsonValue.From(7);
            Assert.Equal(7, array[0].AsInt());

            array.Clear();
            Assert.Equal(0, array.Count);
            Assert.Equal("[]", JsonWriter.Write(array));

            Assert.Throws<InvalidOperationException>(() => { array.Add("k", JsonValue.From(1)); });
            Assert.Throws<InvalidOperationException>(() => { objectValue.Add(JsonValue.From(1)); });
        }

        [Fact]
        public void RemoveKeepsRemainingMemberOrder()
        {
            JsonValue root = JsonParser.Parse("{\"z\":1,\"a\":2,\"m\":3}");

            Assert.True(root.Remove("a"));

            Assert.Equal("z,m", JoinKeys(root));
            Assert.Equal(3, root.Get("m").AsInt());
        }

        [Fact]
        public void FromNullStringProducesTheSharedJsonNull()
        {
            Assert.Same(JsonValue.Null, JsonValue.From((string)null));
        }

        [Fact]
        public void AccessorsUseFallbacksForWrongKinds()
        {
            JsonValue text = JsonValue.From("12");
            Assert.Equal(3, text.AsInt(3));
            Assert.Equal(0, text.AsDouble());
            Assert.Equal("fallback", JsonValue.From(1.0).AsString("fallback"));

            JsonValue root = JsonParser.Parse(
                "{\"i\":12,\"big\":9007199254740992,\"flag\":true,\"s\":\"12\"}");

            Assert.Equal(12, root.Get("i").AsInt());
            Assert.Equal(12L, root.Get("i").AsLong());
            Assert.Equal(9007199254740992L, root.Get("big").AsLong());
            Assert.True(root.Get("flag").AsBoolean());
            Assert.False(root.Get("i").AsBoolean());
            Assert.Equal("12", root.Get("s").AsString());

            // 超出范围的整数值回退到默认值，而不是溢出成 int.MinValue。
            Assert.Equal(-1, JsonParser.Parse("1e30").AsInt(-1));
            Assert.Equal(-1L, JsonParser.Parse("1e30").AsLong(-1));
        }

        // ---------- 严格拒绝非法 JSON ----------

        [Theory]
        [InlineData("[1 2]")]                // 数组缺逗号
        [InlineData("[,1]")]                 // 数组空元素
        [InlineData("[")]                    // 数组未闭合
        [InlineData("{\"a\":+1}")]           // 数字带正号
        [InlineData("{\"a\":.5}")]           // 数字缺整数部分
        [InlineData("{\"a\":1e}")]           // 指数不完整
        [InlineData("{\"a\" 1}")]            // 缺冒号
        [InlineData("{\"a\":1 \"b\":2}")]    // 对象缺逗号
        [InlineData("{\"a\":}")]             // 缺值
        [InlineData("{\"a\":1}}")]           // 多余右括号
        [InlineData("{'a':1}")]              // 单引号键
        [InlineData("{a:1}")]                // 未加引号的键
        [InlineData("NaN")]                  // 非标准字面量
        [InlineData("tru")]                  // 截断字面量
        [InlineData("\"a\nb\"")]             // 字符串内裸换行
        [InlineData("\"bad\\qescape\"")]     // 非法转义
        [InlineData("\"\\u12\"")]            // 不完整 Unicode 转义
        [InlineData("{\"a\":1} // 注释")]    // 注释
        [InlineData("")]                     // 空输入
        public void RejectsInvalidJson(string text)
        {
            Assert.Throws<JsonParseException>(() => { JsonParser.Parse(text); });
        }

        [Fact]
        public void RejectsNullText()
        {
            Assert.Throws<ArgumentNullException>(() => { JsonParser.Parse(null); });
        }

        [Fact]
        public void ReportsPositionOfTrailingComma()
        {
            JsonParseException error = Assert.Throws<JsonParseException>(
                () => { JsonParser.Parse("{\"a\":1,}"); });

            Assert.Equal(7, error.Position);
            Assert.Contains("逗号", error.Message);
        }

        [Fact]
        public void ReportsPositionOfLeadingZero()
        {
            JsonParseException error = Assert.Throws<JsonParseException>(
                () => { JsonParser.Parse("{\"a\":01}"); });

            Assert.Equal(6, error.Position);
            Assert.Contains("0 开头", error.Message);
        }

        [Fact]
        public void ReportsPositionOfExtraCharacterAfterRoot()
        {
            JsonParseException error = Assert.Throws<JsonParseException>(
                () => { JsonParser.Parse("true false"); });

            Assert.Equal(5, error.Position);
        }

        [Fact]
        public void ReportsPositionOfUnclosedString()
        {
            JsonParseException error = Assert.Throws<JsonParseException>(
                () => { JsonParser.Parse("{\"a\":\"x"); });

            Assert.Equal(5, error.Position);
            Assert.Contains("未闭合", error.Message);
        }

        // ---------- 嵌套深度上限 ----------

        [Fact]
        public void AcceptsNestingUpTo64Levels()
        {
            string text = new string('[', 64) + new string(']', 64);

            JsonValue current = JsonParser.Parse(text);
            int depth = 0;
            while (current != null)
            {
                Assert.Equal(JsonKind.Array, current.Kind);
                depth++;
                current = current.Get(0);
            }

            Assert.Equal(64, depth);
        }

        [Fact]
        public void RejectsNestingDeeperThan64Levels()
        {
            string text = new string('[', 65) + new string(']', 65);

            JsonParseException error = Assert.Throws<JsonParseException>(
                () => { JsonParser.Parse(text); });

            Assert.Contains("嵌套", error.Message);
        }

        private static string JoinKeys(JsonValue value)
        {
            List<string> keys = new List<string>();
            for (int i = 0; i < value.Members.Count; i++)
            {
                keys.Add(value.Members[i].Key);
            }

            return string.Join(",", keys.ToArray());
        }
    }
}
