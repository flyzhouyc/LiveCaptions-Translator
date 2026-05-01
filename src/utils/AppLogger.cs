using System.IO;

namespace LiveCaptionsTranslator.utils
{
    public static class AppLogger
    {
        private static readonly object LockObject = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveCaptionsTranslator",
            "logs");

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warning(string message, Exception? exception = null)
        {
            Write("WARN", message, exception);
        }

        public static void Error(string message, Exception? exception = null)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception? exception = null)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception != null)
                    line += $" | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}";

                lock (LockObject)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the caption or translation loops.
            }
        }
    }
}
