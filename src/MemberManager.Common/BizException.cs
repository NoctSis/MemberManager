using System;

namespace MemberManager.Common
{
    /// <summary>业务异常：消息一律简体中文，UI 层可直接展示。任何层不得吞异常。</summary>
    public class BizException : Exception
    {
        public BizException(string message) : base(message) { }
        public BizException(string message, Exception inner) : base(message, inner) { }
    }
}
