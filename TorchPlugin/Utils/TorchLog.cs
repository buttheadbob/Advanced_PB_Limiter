using System;
using System.Globalization;
using NLog;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// The Torch plugin's NLog logger. Messages are written as they are, never read as a format template, so a player or
    /// grid name with braces in it can't break a log line, and numbers already in the text are not formatted again.
    /// </summary>
    internal sealed class TorchLog
    {
        private readonly Logger _logger;

        public TorchLog(string name)
        {
            _logger = LogManager.GetLogger(name);
        }

        public void Trace(string message) => _logger.Trace(CultureInfo.InvariantCulture, message);
        public void Info(string message) => _logger.Info(CultureInfo.InvariantCulture, message);
        public void Warn(string message) => _logger.Warn(CultureInfo.InvariantCulture, message);
        public void Warn(Exception exception) => _logger.Warn(exception, CultureInfo.InvariantCulture, "{0}", exception.Message);
        public void Warn(Exception exception, string message) => _logger.Warn(exception, CultureInfo.InvariantCulture, "{0}", message);
        public void Error(string message) => _logger.Error(CultureInfo.InvariantCulture, message);
        public void Error(Exception exception) => _logger.Error(exception, CultureInfo.InvariantCulture, "{0}", exception.Message);
        public void Error(Exception exception, string message) => _logger.Error(exception, CultureInfo.InvariantCulture, "{0}", message);
        public void Fatal(Exception exception, string message) => _logger.Fatal(exception, CultureInfo.InvariantCulture, "{0}", message);
    }
}
