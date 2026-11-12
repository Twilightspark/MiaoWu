using System;
using System.Globalization;

namespace MiaoWu.Infrastructure.Json
{
    /// <summary>
    /// JSON 文本格式错误，消息为中文并携带出错位置。
    /// </summary>
    /// <remarks>
    /// <see cref="Position"/> 是从 0 开始的字符索引，指向检测到问题的字符；
    /// 当文本在值中途意外结束时，通常等于文本长度（未闭合的字符串例外，
    /// 位置指向其起始引号）。<see cref="Exception.Message"/> 末尾统一附带
    /// “（位置 N）”，便于日志和弹窗直接展示。
    /// </remarks>
    public sealed class JsonParseException : Exception
    {
        /// <summary>
        /// 使用中文错误消息和出错位置创建异常。
        /// </summary>
        /// <param name="message">中文错误消息。</param>
        /// <param name="position">出错位置，从 0 开始的字符索引。</param>
        public JsonParseException(string message, int position)
            : base(FormatMessage(message, position))
        {
            Position = position;
        }

        /// <summary>
        /// 出错位置，从 0 开始的字符索引。
        /// </summary>
        public int Position { get; }

        private static string FormatMessage(string message, int position)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}（位置 {1}）",
                message ?? string.Empty,
                position);
        }
    }
}
