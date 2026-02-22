using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 程序设置（与 Localization 共用 BigBoxSettings.json）。MediaPath 为空或路径不存在时，主窗口使用程序目录下的 Media。
    /// </summary>
    public static class BigBoxSettings
    {
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BigBoxSettings.json");

        private static string _mediaPath = "";
        private static bool _enableDebugLog;

        /// <summary>自定义 Media 根路径（封面、视频等）。留空或目录不存在时使用程序目录。</summary>
        public static string MediaPath
        {
            get => _mediaPath ?? "";
            set => _mediaPath = value ?? "";
        }

        /// <summary>是否将调试日志写入文件（程序目录 BigBoxDebug.log）。</summary>
        public static bool EnableDebugLog
        {
            get => _enableDebugLog;
            set => _enableDebugLog = value;
        }

        /// <summary>加载设置，返回完整键值对供 Localization 使用。</summary>
        public static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(SettingsPath);
                var o = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (o != null)
                {
                    if (o.TryGetValue("MediaPath", out var mp) && mp != null)
                        _mediaPath = mp.Trim();
                    if (o.TryGetValue("EnableDebugLog", out var log) && log != null)
                        _enableDebugLog = log.Trim() == "1" || string.Equals(log.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    return o;
                }
            }
            catch
            {
                // 忽略
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>保存设置（同时写入 Language、MediaPath、EnableDebugLog）。</summary>
        public static void Save(string language)
        {
            try
            {
                var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Language"] = language ?? Localization.LangZh,
                    ["MediaPath"] = _mediaPath ?? "",
                    ["EnableDebugLog"] = _enableDebugLog ? "1" : "0"
                };
                var json = JsonConvert.SerializeObject(o, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
