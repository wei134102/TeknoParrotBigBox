using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace TeknoParrotBigBox
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var parrotPath = Path.Combine(baseDir, "TeknoParrotUi.exe");

            if (!File.Exists(parrotPath))
            {
                MessageBox.Show(
                    "未找到 TeknoParrotUi.exe。\n\nBigBox 需与官方 TeknoParrotUi 同目录运行。",
                    "无法启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            Version selfVersion = Assembly.GetExecutingAssembly().GetName().Version;
            Version parrotVersion = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(parrotPath);
                parrotVersion = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
            }
            catch
            {
                parrotVersion = null;
            }

            if (parrotVersion == null || parrotVersion != selfVersion)
            {
                var parrotStr = parrotVersion != null ? parrotVersion.ToString() : "未知";
                var selfStr = selfVersion != null ? selfVersion.ToString() : "未知";
                MessageBox.Show(
                    "BigBox 版本（" + selfStr + "）与 TeknoParrotUi.exe 版本（" + parrotStr + "）不一致。\n\n请保持二者版本相同后再启动。",
                    "版本不同无法启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var main = new MainWindow();
            main.Show();
        }
    }
}

