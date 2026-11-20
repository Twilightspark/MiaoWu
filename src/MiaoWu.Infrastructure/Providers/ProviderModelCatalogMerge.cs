using System;
using System.Collections.Generic;
using MiaoWu.Core.Models;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// 把供应商 <c>/models</c> 返回的条目合并进模型目录。
    /// 只追加缺失的模型 ID：已存在的模型保留用户保存过的上下文窗口、最大输出与能力标记，
    /// 不做删除或覆盖，因此重复执行是幂等的。设置页的“自动获取”和启动 / 引导后的
    /// 后台自动刷新共用这一份规则。
    /// </summary>
    public static class ProviderModelCatalogMerge
    {
        /// <summary>接口不提供参数时，新模型使用的上下文窗口。</summary>
        public const int DefaultContextWindow = 128000;

        /// <summary>接口不提供参数时，新模型使用的最大输出。</summary>
        public const int DefaultMaxOutputTokens = 8192;

        /// <summary>按接口条目生成一张新的模型定义；ID 去空白，缺少展示名时回落到 ID。</summary>
        public static ModelDefinition CreateModel(ModelCatalogEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            string id = (entry.Id ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                throw new ArgumentException("模型 ID 不能为空。", nameof(entry));
            }

            return new ModelDefinition
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName.Trim(),
                ContextWindow = DefaultContextWindow,
                MaxOutputTokens = DefaultMaxOutputTokens
            };
        }

        /// <summary>
        /// 把接口条目合并进 <paramref name="provider"/>，返回新增的模型数量。
        /// ID 按不区分大小写去重；空 ID 与重复条目直接跳过。
        /// </summary>
        public static int Merge(ProviderDefinition provider, IEnumerable<ModelCatalogEntry> entries)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (entries == null)
            {
                return 0;
            }

            if (provider.Models == null)
            {
                provider.Models = new List<ModelDefinition>();
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelDefinition model in provider.Models)
            {
                if (model != null && !string.IsNullOrWhiteSpace(model.Id))
                {
                    ids.Add(model.Id.Trim());
                }
            }

            int added = 0;
            foreach (ModelCatalogEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id.Trim()))
                {
                    continue;
                }

                provider.Models.Add(CreateModel(entry));
                added++;
            }

            return added;
        }
    }
}
