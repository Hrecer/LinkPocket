using System;
using System.IO;
using System.Linq;

namespace LinkPocket.Services
{
    public static class Logger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkPocket", "logs");

        private static readonly object Lock = new();

        static Logger()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var maxLogs = 10;
                foreach (var file in new DirectoryInfo(LogDir).GetFiles("*.log").OrderByDescending(f => f.Name))
                {
                    if (--maxLogs <= 0)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception? ex = null)
        {
            Write("ERROR", message);
            if (ex != null)
            {
                Write("ERROR", $"  异常类型: {ex.GetType().FullName}");
                Write("ERROR", $"  异常消息: {ex.Message}");
                Write("ERROR", $"  堆栈: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Write("ERROR", $"  内部异常: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                    Write("ERROR", $"  内部堆栈: {ex.InnerException.StackTrace}");
                }
            }
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    var logFile = Path.Combine(LogDir, $"linkpocket-{DateTime.Now:yyyy-MM-dd}.log");
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                    File.AppendAllText(logFile, line + Environment.NewLine, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
