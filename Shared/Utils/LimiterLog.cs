using System;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>Logs through the host, so shared code stays free of NLog and PluginSdk.</summary>
    internal readonly struct LimiterLog
    {
        private readonly string _prefix;

        public LimiterLog(string source)
        {
            _prefix = source + " => ";
        }

        public void Trace(string message) => LimiterCore.Host.LogTrace(_prefix + message);
        public void Info(string message) => LimiterCore.Host.LogInfo(_prefix + message);
        public void Warn(string message) => LimiterCore.Host.LogWarn(_prefix + message);
        public void Error(string message) => LimiterCore.Host.LogError(_prefix + message);
        public void Error(Exception exception, string message) => LimiterCore.Host.LogError(_prefix + message, exception);
    }
}
