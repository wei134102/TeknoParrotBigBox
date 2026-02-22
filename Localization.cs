using System;
using System.Collections.Generic;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 中英文界面字符串与语言切换。设置保存在程序目录 BigBoxSettings.json。
    /// </summary>
    public static class Localization
    {
        public const string LangZh = "zh";
        public const string LangEn = "en";

        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TitleMain"] = "TeknoParrot BigBox",
            ["ButtonStartGame"] = "开始游戏",
            ["ButtonFavorite"] = "收藏游戏",
            ["ButtonUnfavorite"] = "取消收藏",
            ["ButtonBackToParrot"] = "返回鹦鹉",
            ["ButtonSettings"] = "设置",
            ["ButtonAbout"] = "关于本程序",
            ["HintBottom"] = "←→ 切换游戏   ↑↓ 切换分类    Enter/A 启动    Esc/B 退出    支持 XInput / DINPUT 手柄",
            ["GamesCountPrefix"] = "共 ",
            ["GamesCountSuffix"] = " 款游戏",
            ["CategoryFavorites"] = "★ 收藏",
            ["ExitConfirmMessage"] = "确定要退出 TeknoParrot BigBox 吗？",
            ["ExitConfirmTitle"] = "退出确认",
            ["MsgNoGameSelected"] = "尚未选择游戏。",
            ["MsgLaunchNotConfigured"] = "当前游戏尚未配置启动命令行参数，稍后可在 GameEntry 中补充。",
            ["MsgNoBatFolder"] = "未找到 bat 目录，当前没有可用的游戏启动脚本。",
            ["MsgNoGameScripts"] = "未在 bat 目录中找到任何可分组的游戏脚本。",
            ["MsgLaunchFailed"] = "启动游戏失败：\n{0}",
            ["MsgParrotNotFound"] = "未找到 TeknoParrotUi.exe。\n\n请确认它与 TeknoParrotBigBox.exe 位于同一目录。",
            ["MsgParrotNotFoundTitle"] = "无法返回鹦鹉 UI",
            ["MsgParrotStartFailed"] = "启动 TeknoParrotUi 失败：\n{0}",
            ["CaptionTip"] = "提示",
            ["CaptionError"] = "错误",
            ["AboutMessage"] = "TeknoParrot BigBox 前端\n\n版本：{0}\n作者：B站：86年复古游戏厅\n用途：为 TeknoParrot 提供封面 + 视频风格启动界面。",
            ["AboutTitle"] = "关于本程序",
            ["SettingsTitle"] = "设置",
            ["SettingsLanguageLabel"] = "界面语言",
            ["SettingsLangZh"] = "中文",
            ["SettingsLangEn"] = "English",
            ["ButtonOk"] = "确定",
            ["ButtonCancel"] = "取消",
            ["VersionUnknown"] = "未知版本",
            ["SettingsMediaPathLabel"] = "Media 路径（调试用，留空则使用程序目录）",
            ["SettingsEnableDebugLogLabel"] = "写入调试日志到文件（BigBoxDebug.log）",
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TitleMain"] = "TeknoParrot BigBox",
            ["ButtonStartGame"] = "Start Game",
            ["ButtonFavorite"] = "Add to Favorites",
            ["ButtonUnfavorite"] = "Remove from Favorites",
            ["ButtonBackToParrot"] = "Back to Parrot",
            ["ButtonSettings"] = "Settings",
            ["ButtonAbout"] = "About",
            ["HintBottom"] = "←→ Change game   ↑↓ Change category    Enter/A Launch    Esc/B Exit    XInput / DINPUT gamepad",
            ["GamesCountPrefix"] = "",
            ["GamesCountSuffix"] = " games",
            ["CategoryFavorites"] = "★ Favorites",
            ["ExitConfirmMessage"] = "Are you sure you want to exit TeknoParrot BigBox?",
            ["ExitConfirmTitle"] = "Exit",
            ["MsgNoGameSelected"] = "No game selected.",
            ["MsgLaunchNotConfigured"] = "Launch command not configured for this game.",
            ["MsgNoBatFolder"] = "bat folder not found. No game launch scripts available.",
            ["MsgNoGameScripts"] = "No game scripts found in bat folder.",
            ["MsgLaunchFailed"] = "Failed to launch game:\n{0}",
            ["MsgParrotNotFound"] = "TeknoParrotUi.exe not found.\n\nPlease ensure it is in the same folder as TeknoParrotBigBox.exe.",
            ["MsgParrotNotFoundTitle"] = "Cannot open Parrot UI",
            ["MsgParrotStartFailed"] = "Failed to start TeknoParrotUi:\n{0}",
            ["CaptionTip"] = "Info",
            ["CaptionError"] = "Error",
            ["AboutMessage"] = "TeknoParrot BigBox\n\nVersion: {0}\nAuthor: Bilibili 86年复古游戏厅\nA cover + video style launcher for TeknoParrot.",
            ["AboutTitle"] = "About",
            ["SettingsTitle"] = "Settings",
            ["SettingsLanguageLabel"] = "Language",
            ["SettingsLangZh"] = "中文",
            ["SettingsLangEn"] = "English",
            ["ButtonOk"] = "OK",
            ["ButtonCancel"] = "Cancel",
            ["VersionUnknown"] = "Unknown",
            ["SettingsMediaPathLabel"] = "Media path (for debugging; leave empty to use app directory)",
            ["SettingsEnableDebugLogLabel"] = "Write debug log to file (BigBoxDebug.log)",
        };

        private static string _language = LangZh;

        public static string Language
        {
            get => _language;
            set
            {
                if (string.IsNullOrEmpty(value)) value = LangZh;
                if (string.Equals(_language, value, StringComparison.OrdinalIgnoreCase)) return;
                _language = value.Equals(LangEn, StringComparison.OrdinalIgnoreCase) ? LangEn : LangZh;
                Save();
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static bool IsEnglish => string.Equals(_language, LangEn, StringComparison.OrdinalIgnoreCase);

        public static event EventHandler LanguageChanged;

        public static string Get(string key)
        {
            var dict = IsEnglish ? En : Zh;
            return dict.TryGetValue(key, out var s) ? s : key;
        }

        public static string Get(string key, params object[] args)
        {
            var format = Get(key);
            return args != null && args.Length > 0 ? string.Format(format, args) : format;
        }

        public static void Load()
        {
            try
            {
                var o = BigBoxSettings.Load();
                if (o != null && o.TryGetValue("Language", out var lang) && !string.IsNullOrWhiteSpace(lang))
                    _language = lang.Equals(LangEn, StringComparison.OrdinalIgnoreCase) ? LangEn : LangZh;
            }
            catch
            {
                // 忽略
            }
        }

        public static void Save()
        {
            BigBoxSettings.Save(_language);
        }
    }
}
