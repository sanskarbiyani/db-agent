using DbAgent.Common.Messages;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;

namespace DbAgent.DbExecutor
{
    public class RetryChannel
    {
        public Channel<RetryMessage> RetryQueryChannel { get; } = Channel.CreateUnbounded<RetryMessage>();
    }
}
