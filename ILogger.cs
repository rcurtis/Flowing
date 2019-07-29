using System;
using System.Collections.Generic;
using System.Text;

namespace Flowing.Library
{
    public interface ILogger
    {
        bool IsDebugEnabled { get; set; }

        void Debug(string msg);
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg);
    }
}
