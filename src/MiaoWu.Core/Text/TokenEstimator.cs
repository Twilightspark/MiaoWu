using System;
using System.Text;

namespace MiaoWu.Core.Text
{
    /// <summary>
    /// 粗略 token 估算：CJK 约 1 字 1 token，拉丁文本约 4 字符 1 token。
    /// 只用于上下文预算与用量提示，不追求与具体分词器一致。
    /// </summary>
    public static class TokenEstimator
    {
        public static int Estimate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double tokens = 0;
            int asciiRun = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 128)
                {
                    asciiRun++;
                    continue;
                }

                if (asciiRun > 0)
                {
                    tokens += (asciiRun + 3.0) / 4.0;
                    asciiRun = 0;
                }

                // CJK 统一表意文字、假名、韩文、全角标点按 1 token/字估算。
                if (c >= 0x2E80)
                {
                    tokens += 1.0;
                }
                else
                {
                    tokens += 0.5;
                }
            }

            if (asciiRun > 0)
            {
                tokens += (asciiRun + 3.0) / 4.0;
            }

            return (int)Math.Ceiling(tokens);
        }

        public static int EstimateMessages(System.Collections.Generic.IEnumerable<string> texts)
        {
            if (texts == null)
            {
                return 0;
            }

            int total = 0;
            foreach (string text in texts)
            {
                total += Estimate(text) + 4;
            }

            return total;
        }

        public static string Format(int tokens)
        {
            if (tokens < 1000)
            {
                return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (tokens < 1000000)
            {
                return (tokens / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
            }

            return (tokens / 1000000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }
    }
}
