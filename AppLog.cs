using System;
using System.IO;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 调试日志：当设置中开启“写入调试日志”时，将消息追加到程序目录的 BigBoxDebug.log。
    /// </summary>
    public static class AppLog
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BigBoxDebug.log");
        private static readonly object Lock = new object();

        public static void WriteLine(string message)
        {
            if (!BigBoxSettings.EnableDebugLog || string.IsNullOrEmpty(message)) return;
            try
            {
                lock (Lock)
                {
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
                    File.AppendAllText(LogPath, line);
                }
            }
            catch
            {
                // 忽略写入失败
            }
        }
    }
}
