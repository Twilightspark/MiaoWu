using System;

namespace MiaoWu.Core.Models
{
    /// <summary>
    /// 全局共享的模型选择：所有会话共用这一份配置。
    /// 会话每次运行都从这里解析模型，因此在任意一个会话的输入框里换模型后，
    /// 其它会话的下一条消息也跟着用同一个模型；事件只用于让已热起来的会话刷新快照。
    /// </summary>
    public sealed class ModelSelection
    {
        private readonly object _gate = new object();
        private string _providerId;
        private string _modelId;

        /// <summary>模型选择变化；重复设置同一个模型不会触发。</summary>
        public event EventHandler Changed;

        public string ProviderId
        {
            get
            {
                lock (_gate)
                {
                    return _providerId;
                }
            }
        }

        public string ModelId
        {
            get
            {
                lock (_gate)
                {
                    return _modelId;
                }
            }
        }

        public bool HasValue
        {
            get
            {
                lock (_gate)
                {
                    return !string.IsNullOrWhiteSpace(_providerId) || !string.IsNullOrWhiteSpace(_modelId);
                }
            }
        }

        /// <summary>设置共享模型，provider / model 至少给一个；返回是否真的发生了变化。</summary>
        public bool Set(string providerId, string modelId)
        {
            lock (_gate)
            {
                if (string.Equals(_providerId, providerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_modelId, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _providerId = string.IsNullOrWhiteSpace(providerId) ? null : providerId;
                _modelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId;
            }

            EventHandler handler = Changed;
            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // 订阅者异常不影响切换模型本身。
                }
            }

            return true;
        }
    }
}
