using System;
using System.Collections.Generic;

namespace MiaoWu.Infrastructure.Json
{
    /// <summary>
    /// JSON 值的种类。
    /// </summary>
    public enum JsonKind
    {
        /// <summary>空值 <c>null</c>。</summary>
        Null,

        /// <summary>布尔值 <c>true</c> / <c>false</c>。</summary>
        Boolean,

        /// <summary>数字，统一以 <see cref="double"/> 保存。</summary>
        Number,

        /// <summary>字符串。</summary>
        String,

        /// <summary>数组。</summary>
        Array,

        /// <summary>对象，成员保持插入顺序。</summary>
        Object
    }

    /// <summary>
    /// 手写 JSON DOM 节点，供 settings.json / workspaces.json 等配置文件读写使用。
    /// </summary>
    /// <remarks>
    /// <para>解析后的对象保留全部字段（包括当前版本不认识的字段），写回时不会静默丢弃。</para>
    /// <para>标量节点不可变；数组与对象支持增删成员，成员顺序即插入顺序。</para>
    /// <para>容器的子节点不允许为 <c>null</c> 引用：向容器写入 <c>null</c> 会按 JSON
    /// <c>null</c>（<see cref="JsonValue.Null"/>）保存。读取不存在的成员时
    /// <see cref="Get(string)"/> 返回 <c>null</c> 引用，调用方需要自行判空。</para>
    /// </remarks>
    public sealed class JsonValue
    {
        private static readonly JsonValue NullInstance = new JsonValue(JsonKind.Null);
        private static readonly JsonValue TrueInstance = new JsonValue(true);
        private static readonly JsonValue FalseInstance = new JsonValue(false);
        private static readonly IReadOnlyList<JsonValue> EmptyItems = new JsonValue[0];
        private static readonly IReadOnlyList<KeyValuePair<string, JsonValue>> EmptyMembers =
            new KeyValuePair<string, JsonValue>[0];

        private readonly JsonKind _kind;
        private readonly bool _boolean;
        private readonly double _number;
        private readonly string _string;
        private readonly List<JsonValue> _items;
        private readonly List<KeyValuePair<string, JsonValue>> _members;
        private readonly Dictionary<string, int> _memberIndex;
        private readonly IReadOnlyList<JsonValue> _itemsView;
        private readonly IReadOnlyList<KeyValuePair<string, JsonValue>> _membersView;

        private JsonValue(JsonKind kind)
        {
            _kind = kind;
        }

        private JsonValue(bool value)
        {
            _kind = JsonKind.Boolean;
            _boolean = value;
        }

        private JsonValue(double value)
        {
            _kind = JsonKind.Number;
            _number = value;
        }

        private JsonValue(string value)
        {
            _kind = JsonKind.String;
            _string = value;
        }

        private JsonValue(List<JsonValue> items)
        {
            _kind = JsonKind.Array;
            _items = items;
            _itemsView = items.AsReadOnly();
        }

        private JsonValue(List<KeyValuePair<string, JsonValue>> members, Dictionary<string, int> memberIndex)
        {
            _kind = JsonKind.Object;
            _members = members;
            _memberIndex = memberIndex;
            _membersView = members.AsReadOnly();
        }

        /// <summary>
        /// 当前节点所属的 JSON 种类。
        /// </summary>
        public JsonKind Kind
        {
            get { return _kind; }
        }

        /// <summary>
        /// 是否为 JSON <c>null</c>。
        /// </summary>
        public bool IsNull
        {
            get { return _kind == JsonKind.Null; }
        }

        /// <summary>
        /// JSON <c>null</c> 的单例。
        /// </summary>
        public static JsonValue Null
        {
            get { return NullInstance; }
        }

        /// <summary>
        /// 创建布尔值节点。
        /// </summary>
        public static JsonValue From(bool value)
        {
            return value ? TrueInstance : FalseInstance;
        }

        /// <summary>
        /// 创建数字节点；<see cref="double.NaN"/> 与无穷大只在写出时被拒绝。
        /// </summary>
        public static JsonValue From(double value)
        {
            return new JsonValue(value);
        }

        /// <summary>
        /// 创建字符串节点；<paramref name="value"/> 为 <c>null</c> 时返回 <see cref="Null"/>。
        /// </summary>
        public static JsonValue From(string value)
        {
            return value == null ? NullInstance : new JsonValue(value);
        }

        /// <summary>
        /// 创建空数组节点。
        /// </summary>
        public static JsonValue NewArray()
        {
            return new JsonValue(new List<JsonValue>());
        }

        /// <summary>
        /// 创建空对象节点。
        /// </summary>
        public static JsonValue NewObject()
        {
            return new JsonValue(
                new List<KeyValuePair<string, JsonValue>>(),
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        /// <summary>
        /// 按布尔值读取；非布尔值时返回 <paramref name="fallback"/>。
        /// </summary>
        public bool AsBoolean(bool fallback = false)
        {
            return _kind == JsonKind.Boolean ? _boolean : fallback;
        }

        /// <summary>
        /// 按数字读取；非数字时返回 <paramref name="fallback"/>。
        /// </summary>
        public double AsDouble(double fallback = 0)
        {
            return _kind == JsonKind.Number ? _number : fallback;
        }

        /// <summary>
        /// 按整数读取（向零截断）；非数字或超出 <see cref="int"/> 范围时返回
        /// <paramref name="fallback"/>。
        /// </summary>
        public int AsInt(int fallback = 0)
        {
            if (_kind != JsonKind.Number || double.IsNaN(_number) || double.IsInfinity(_number))
            {
                return fallback;
            }

            if (_number < int.MinValue || _number > int.MaxValue)
            {
                return fallback;
            }

            return (int)_number;
        }

        /// <summary>
        /// 按长整数读取（向零截断）；非数字或超出 <see cref="long"/> 范围时返回
        /// <paramref name="fallback"/>。
        /// </summary>
        public long AsLong(long fallback = 0)
        {
            if (_kind != JsonKind.Number || double.IsNaN(_number) || double.IsInfinity(_number))
            {
                return fallback;
            }

            // long.MaxValue 无法用 double 精确表示（会进位到 2^63），因此上界使用 2^63 的排他比较。
            if (_number < -9223372036854775808.0 || _number >= 9223372036854775808.0)
            {
                return fallback;
            }

            return (long)_number;
        }

        /// <summary>
        /// 按字符串读取；非字符串时返回 <paramref name="fallback"/>。
        /// </summary>
        public string AsString(string fallback = null)
        {
            return _kind == JsonKind.String ? _string : fallback;
        }

        /// <summary>
        /// 数组元素；非数组时返回空列表。
        /// </summary>
        public IReadOnlyList<JsonValue> Items
        {
            get { return _itemsView ?? EmptyItems; }
        }

        /// <summary>
        /// 对象成员，保持插入顺序；非对象时返回空列表。
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members
        {
            get { return _membersView ?? EmptyMembers; }
        }

        /// <summary>
        /// 数组元素数或对象成员数；标量返回 0。
        /// </summary>
        public int Count
        {
            get
            {
                if (_items != null)
                {
                    return _items.Count;
                }

                if (_members != null)
                {
                    return _members.Count;
                }

                return 0;
            }
        }

        /// <summary>
        /// 对象是否包含指定键；非对象或 <paramref name="key"/> 为 <c>null</c> 时返回
        /// <c>false</c>。
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _memberIndex != null && key != null && _memberIndex.ContainsKey(key);
        }

        /// <summary>
        /// 按名称读取对象成员；不存在或 <paramref name="key"/> 为 <c>null</c> 时返回
        /// <c>null</c>。
        /// </summary>
        public JsonValue Get(string key)
        {
            if (_memberIndex == null || key == null)
            {
                return null;
            }

            int index;
            if (!_memberIndex.TryGetValue(key, out index))
            {
                return null;
            }

            return _members[index].Value;
        }

        /// <summary>
        /// 按索引读取数组元素；越界或非数组时返回 <c>null</c>。
        /// </summary>
        public JsonValue Get(int index)
        {
            if (_items == null || index < 0 || index >= _items.Count)
            {
                return null;
            }

            return _items[index];
        }

        /// <summary>
        /// 按名称读写对象成员；写入不存在的键时追加到末尾，已存在的键原位替换。
        /// </summary>
        /// <exception cref="InvalidOperationException">当前节点不是对象。</exception>
        public JsonValue this[string key]
        {
            get { return Get(key); }
            set { Add(key, value); }
        }

        /// <summary>
        /// 按索引读写数组元素。
        /// </summary>
        /// <exception cref="InvalidOperationException">当前节点不是数组。</exception>
        /// <exception cref="ArgumentOutOfRangeException">索引越界。</exception>
        public JsonValue this[int index]
        {
            get
            {
                if (_items == null)
                {
                    throw new InvalidOperationException("只有 JSON 数组支持按索引访问。");
                }

                if (index < 0 || index >= _items.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        "index", index, "索引超出 JSON 数组的范围。");
                }

                return _items[index];
            }

            set
            {
                if (_items == null)
                {
                    throw new InvalidOperationException("只有 JSON 数组支持按索引访问。");
                }

                if (index < 0 || index >= _items.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        "index", index, "索引超出 JSON 数组的范围。");
                }

                _items[index] = value ?? NullInstance;
            }
        }

        /// <summary>
        /// 向数组末尾追加元素，<c>null</c> 按 JSON <c>null</c> 保存。
        /// </summary>
        /// <exception cref="InvalidOperationException">当前节点不是数组。</exception>
        public void Add(JsonValue value)
        {
            if (_items == null)
            {
                throw new InvalidOperationException("只有 JSON 数组支持追加元素。");
            }

            _items.Add(value ?? NullInstance);
        }

        /// <summary>
        /// 追加对象成员，或在键已存在时原位替换其值。
        /// </summary>
        /// <exception cref="InvalidOperationException">当前节点不是对象。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <c>null</c>。</exception>
        public void Add(string key, JsonValue value)
        {
            if (_members == null)
            {
                throw new InvalidOperationException("只有 JSON 对象支持追加成员。");
            }

            if (key == null)
            {
                throw new ArgumentNullException("key", "JSON 对象的键不能为空。");
            }

            JsonValue memberValue = value ?? NullInstance;

            int index;
            if (_memberIndex.TryGetValue(key, out index))
            {
                // 原位替换，位置保持在第一次出现处。
                _members[index] = new KeyValuePair<string, JsonValue>(_members[index].Key, memberValue);
                return;
            }

            _memberIndex.Add(key, _members.Count);
            _members.Add(new KeyValuePair<string, JsonValue>(key, memberValue));
        }

        /// <summary>
        /// 移除对象成员；不存在（或当前不是对象）时返回 <c>false</c>。
        /// </summary>
        public bool Remove(string key)
        {
            if (_members == null || key == null)
            {
                return false;
            }

            int index;
            if (!_memberIndex.TryGetValue(key, out index))
            {
                return false;
            }

            _members.RemoveAt(index);
            _memberIndex.Remove(key);

            for (int i = index; i < _members.Count; i++)
            {
                _memberIndex[_members[i].Key] = i;
            }

            return true;
        }

        /// <summary>
        /// 清空数组元素或对象成员；标量节点不做任何事。
        /// </summary>
        public void Clear()
        {
            if (_items != null)
            {
                _items.Clear();
            }

            if (_members != null)
            {
                _members.Clear();
                _memberIndex.Clear();
            }
        }
    }
}
