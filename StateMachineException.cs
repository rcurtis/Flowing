using System;
using System.Collections.Generic;
using System.Text;

namespace Flowing.Library
{
    public class StateMachineException : Exception
    {
        public StateMachineException()
        {
        }

        public StateMachineException(string message) : base(message)
        {
        }

        public StateMachineException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
