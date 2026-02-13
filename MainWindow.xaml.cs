using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using System.Windows.Threading;
using TeknoParrotBigBox.Models;

namespace TeknoParrotBigBox
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> Games { get; } = new ObservableCollection<GameEntry>();
        public ObservableCollection<GameCategory> Categories { get; } = new ObservableCollection<GameCategory>();

        private GameCategory _favoritesCategory;

        private readonly DispatcherTimer _descriptionScrollTimer;
        private readonly DispatcherTimer _gamepadTimer;
        private GamepadInput.GamepadState _lastGamepadState;
        private double _descriptionScrollOffset;
        private bool _isDescriptionHovered;
        private bool _isMuted = false;
        private bool _autoMutedForGame;
        private Process _currentGameProcess;

        private int _totalGameCount;
        /// <summary>总游戏数量（不含收藏分类内的重复计数）。</summary>
        public int TotalGameCount
        {
            get => _totalGameCount;
            private set
            {
                if (_totalGameCount != value)
                {
                    _totalGameCount = value;
                    OnPropertyChanged(nameof(TotalGameCount));
                }
            }
        }

        private GameCategory _selectedCategory;
        public GameCategory SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (!Equals(_selectedCategory, value))
                {
                    _selectedCategory = value;
                    OnPropertyChanged(nameof(SelectedCategory));
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadGamesFromFolders();

            // 自动滚动游戏介绍
            _descriptionScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _descriptionScrollTimer.Tick += DescriptionScrollTimer_Tick;
            _descriptionScrollTimer.Start();

            // 手柄轮询（XInput + DINPUT/winmm 摇杆）
            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _gamepadTimer.Tick += GamepadTimer_Tick;
            _gamepadTimer.Start();
        }

        private void GamepadTimer_Tick(object sender, EventArgs e)
        {
            var now = GamepadInput.Poll();
            if (!now.HasInput)
                return;

            // 边沿检测：仅在手柄“刚按下”时触发，避免连发
            if (now.Left && !_lastGamepadState.Left)
                MoveCategoryLeft();
            if (now.Right && !_lastGamepadState.Right)
                MoveCategoryRight();
            if (now.Up && !_lastGamepadState.Up)
                MoveGameUp();
            if (now.Down && !_lastGamepadState.Down)
                MoveGameDown();
            if (now.A && !_lastGamepadState.A)
                LaunchSelectedGame();
            if (now.B && !_lastGamepadState.B)
                TryCloseWithConfirm();

            _lastGamepadState = now;
        }

        private void MoveCategoryLeft()
        {
            if (CategoriesList == null || Categories.Count == 0) return;
            int idx = CategoriesList.SelectedIndex;
            if (idx <= 0) return;
            CategoriesList.SelectedIndex = idx - 1;
            CategoriesList.Focus();
        }

        private void MoveCategoryRight()
        {
            if (CategoriesList == null || Categories.Count == 0) return;
            int idx = CategoriesList.SelectedIndex;
            if (idx < 0 || idx >= Categories.Count - 1) return;
            CategoriesList.SelectedIndex = idx + 1;
            CategoriesList.Focus();
        }

        private void MoveGameUp()
        {
            if (SelectedCategory?.Games == null || GamesList == null || SelectedCategory.Games.Count == 0) return;
            int idx = GamesList.SelectedIndex;
            if (idx <= 0) return;
            GamesList.SelectedIndex = idx - 1;
            if (GamesList.SelectedItem != null)
                GamesList.ScrollIntoView(GamesList.SelectedItem);
            GamesList.Focus();
            _descriptionScrollOffset = 0;
            DescriptionScrollViewer?.ScrollToVerticalOffset(0);
        }

        private void MoveGameDown()
        {
            if (SelectedCategory?.Games == null || GamesList == null || SelectedCategory.Games.Count == 0) return;
            int idx = GamesList.SelectedIndex;
            if (idx < 0) idx = 0;
            if (idx >= SelectedCategory.Games.Count - 1) return;
            GamesList.SelectedIndex = idx + 1;
            if (GamesList.SelectedItem != null)
                GamesList.ScrollIntoView(GamesList.SelectedItem);
            GamesList.Focus();
            _descriptionScrollOffset = 0;
            DescriptionScrollViewer?.ScrollToVerticalOffset(0);
        }

        /// <summary>
        /// 从 bat / Metadata / Icons / Media\Covers / Media\Videos / launchbox_descriptions.json 加载游戏与分类。
        /// </summary>
        private void LoadGamesFromFolders()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var batDir = Path.Combine(baseDir, "bat");
            var metadataDir = Path.Combine(baseDir, "Metadata");
            var iconsDir = Path.Combine(baseDir, "Icons");
            var coversDir = Path.Combine(baseDir, "Media", "Covers");
            var videosDir = Path.Combine(baseDir, "Media", "Videos");
            var launchboxJsonPath = Path.Combine(baseDir, "launchbox_descriptions.json");

            if (!Directory.Exists(batDir))
            {
                MessageBox.Show("未找到 bat 目录，当前没有可用的游戏启动脚本。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 1) 预扫描 bat，按 profileId 建立索引（--profile=XXXX.xml）
            var batByProfileId = new Dictionary<string, BatInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var batPath in Directory.GetFiles(batDir, "*.bat", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lines = File.ReadAllLines(batPath);
                    if (lines.Length == 0) continue;

                    var line = lines[0];
                    var marker = "--profile=";
                    var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    string profileId = null;
                    if (idx >= 0)
                    {
                        var start = idx + marker.Length;
                        var end = line.IndexOf(".xml", start, StringComparison.OrdinalIgnoreCase);
                        if (end > start)
                        {
                            profileId = line.Substring(start, end - start);
                        }
                    }

                    var displayName = Path.GetFileNameWithoutExtension(batPath);

                    batByProfileId[profileId ?? displayName] = new BatInfo
                    {
                        ProfileId = profileId,
                        BatPath = batPath,
                        DisplayName = displayName
                    };
                }
                catch
                {
                    // 忽略单个 bat 解析错误
                }
            }

            // 2) 预加载 Metadata（按文件名 = profileId）
            var metadataByProfileId = new Dictionary<string, GameMetadata>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(metadataDir))
            {
                foreach (var jsonPath in Directory.GetFiles(metadataDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var profileId = Path.GetFileNameWithoutExtension(jsonPath);
                        var json = File.ReadAllText(jsonPath);
                        var meta = JsonConvert.DeserializeObject<GameMetadata>(json);
                        if (meta != null)
                        {
                            metadataByProfileId[profileId] = meta;
                        }
                    }
                    catch
                    {
                        // 忽略单个 metadata 解析错误
                    }
                }
            }

            // 3) 预加载 LaunchBox 描述（按 profileId）
            var launchboxByProfileId = new Dictionary<string, LaunchboxDescription>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(launchboxJsonPath))
            {
                try
                {
                    var json = File.ReadAllText(launchboxJsonPath);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, LaunchboxDescription>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            launchboxByProfileId[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch
                {
                    // 忽略 launchbox_descriptions.json 解析错误
                }
            }

            // 4) 合并：优先使用 profileId 匹配到的 LaunchBox 描述 + metadata + bat
            var groups = new Dictionary<string, List<GameEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in batByProfileId)
            {
                var batInfo = kv.Value;
                var profileId = batInfo.ProfileId ?? kv.Key;

                metadataByProfileId.TryGetValue(profileId, out var meta);
                launchboxByProfileId.TryGetValue(profileId, out var lb);

                var title =
                    !string.IsNullOrWhiteSpace(lb?.Title) ? lb.Title :
                    meta != null ? SanitizeGameName(meta.GameName) :
                    batInfo.DisplayName;

                // 描述优先使用 LaunchBox 的 Notes，其次使用 Metadata 中的简要信息
                var description =
                    !string.IsNullOrWhiteSpace(lb?.Notes) ? lb.Notes :
                    BuildDescription(meta);
                var coverPath = ResolveCoverPath(coversDir, iconsDir, profileId, batInfo.DisplayName, meta);
                var videoPath = ResolveVideoPath(videosDir, profileId, batInfo.DisplayName);

                var entry = new GameEntry
                {
                    ProfileId = profileId,
                    Title = title,
                    Description = description,
                    CoverImagePath = coverPath,
                    VideoPath = videoPath,
                    LaunchExecutable = batInfo.BatPath,
                    LaunchArguments = string.Empty
                };

                // 分类 key：优先使用 metadata 的 game_genre，其次 LaunchBox 的 genre
                var categoryKey = GetLocalizedCategory(meta?.GameGenre, lb?.Genre);

                if (!groups.TryGetValue(categoryKey, out var list))
                {
                    list = new List<GameEntry>();
                    groups[categoryKey] = list;
                }
                list.Add(entry);
            }

            // 5) 把分组结果转换为 Category 集合
            Categories.Clear();

            // 收藏列表固定放在最上方
            _favoritesCategory = new GameCategory
            {
                Key = "__favorites",
                Name = "★ 收藏 (0)",
                Games = new ObservableCollection<GameEntry>()
            };
            Categories.Add(_favoritesCategory);

            // 先尝试加载历史收藏（按 profileId）
            var favoritesPath = Path.Combine(baseDir, "favorites.json");
            var favoriteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(favoritesPath))
            {
                try
                {
                    var jsonFav = File.ReadAllText(favoritesPath);
                    var favWrapper = JsonConvert.DeserializeObject<FavoritesFile>(jsonFav);
                    if (favWrapper?.Favorites != null)
                    {
                        foreach (var id in favWrapper.Favorites.Where(id => !string.IsNullOrWhiteSpace(id)))
                        {
                            favoriteIds.Add(id.Trim());
                        }
                    }
                }
                catch
                {
                    // 忽略收藏文件解析错误
                }
            }

            foreach (var kv in groups)
            {
                var cat = new GameCategory
                {
                    Key = kv.Key,
                    Name = $"{kv.Key} ({kv.Value.Count})",
                    Games = new ObservableCollection<GameEntry>(kv.Value)
                };
                Categories.Add(cat);
            }

            // 把属于收藏列表的游戏加入到收藏分类
            if (favoriteIds.Count > 0)
            {
                foreach (var cat in Categories)
                {
                    if (cat == _favoritesCategory) continue;
                    foreach (var game in cat.Games)
                    {
                        if (!string.IsNullOrWhiteSpace(game.ProfileId) &&
                            favoriteIds.Contains(game.ProfileId) &&
                            !_favoritesCategory.Games.Contains(game))
                        {
                            game.IsFavorite = true;
                            _favoritesCategory.Games.Add(game);
                        }
                    }
                }

                _favoritesCategory.Name = $"★ 收藏 ({_favoritesCategory.Games.Count})";
            }

            // 统计总游戏数（不含收藏，避免重复计数）
            int total = 0;
            foreach (var c in Categories)
            {
                if (c.Key == "__favorites") continue;
                total += c.Games?.Count ?? 0;
            }
            TotalGameCount = total;

            // 默认选中第一个分类和第一个游戏
            if (Categories.Count > 0)
            {
                SelectedCategory = Categories[0];
                if (CategoriesList != null)
                {
                    CategoriesList.SelectedIndex = 0;
                }
                if (GamesList != null && SelectedCategory.Games.Count > 0)
                {
                    GamesList.SelectedIndex = 0;
                }
            }

            if (Categories.Count == 0)
            {
                MessageBox.Show("未在 bat 目录中找到任何可分组的游戏脚本。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>切换左侧分类后，将右侧游戏列表定位到本分类的第一个游戏并滚动到可见。</summary>
        private void CategoriesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SelectedCategory?.Games == null || GamesList == null)
                return;
            // 等绑定更新完再选中第一项并滚动
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (SelectedCategory.Games.Count > 0)
                {
                    GamesList.SelectedIndex = 0;
                    if (GamesList.SelectedItem != null)
                        GamesList.ScrollIntoView(GamesList.SelectedItem);
                }
                GamesList.Focus();
            }), DispatcherPriority.Loaded);
        }

        private void LaunchSelectedGame()
        {
            var selected = GamesList.SelectedItem as GameEntry;
            if (selected == null)
            {
                MessageBox.Show("尚未选择游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(selected.LaunchExecutable))
            {
                MessageBox.Show("当前游戏尚未配置启动命令行参数，稍后可在 GameEntry 中补充。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = selected.LaunchExecutable,
                    Arguments = selected.LaunchArguments ?? string.Empty
                };

                // 如果是 bat，用 cmd /c 启动更安全
                if (string.Equals(Path.GetExtension(startInfo.FileName), ".bat", StringComparison.OrdinalIgnoreCase))
                {
                    var batPath = startInfo.FileName;
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + batPath + "\" " + (selected.LaunchArguments ?? string.Empty),
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(batPath) ?? AppDomain.CurrentDomain.BaseDirectory
                    };
                }
                else
                {
                    startInfo.UseShellExecute = false;
                    if (!Path.IsPathRooted(startInfo.FileName))
                    {
                        startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    }
                    else
                    {
                        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName) ?? AppDomain.CurrentDomain.BaseDirectory;
                    }
                }

                // 启动游戏进程
                _currentGameProcess?.Dispose();
                _currentGameProcess = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动游戏失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 启动游戏后，直接停止预览视频（不再在后台播放）
            if (PreviewMedia != null)
            {
                try
                {
                    PreviewMedia.Stop();
                }
                catch
                {
                    // 忽略 MediaElement 状态异常
                }
            }
        }

        private void CurrentGameProcess_Exited(object sender, EventArgs e)
        {
            // 回到 UI 线程，仅清理进程句柄（静音恢复交给用户手动控制）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentGameProcess != null)
                {
                    _currentGameProcess.Exited -= CurrentGameProcess_Exited;
                    _currentGameProcess.Dispose();
                    _currentGameProcess = null;
                }
            }));
        }

        private void StartGameButton_Click(object sender, RoutedEventArgs e)
        {
            LaunchSelectedGame();
        }

        private void FavoriteGameButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GamesList.SelectedItem as GameEntry;
            if (selected == null)
            {
                MessageBox.Show("尚未选择游戏。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_favoritesCategory == null)
            {
                MessageBox.Show("收藏列表尚未初始化。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 仅当当前不是收藏状态时才添加
            if (!_favoritesCategory.Games.Contains(selected))
            {
                _favoritesCategory.Games.Add(selected);
            }
            selected.IsFavorite = true;

            SaveFavoritesToFile();
        }

        private void UnfavoriteGameButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GamesList.SelectedItem as GameEntry;
            if (selected == null || _favoritesCategory == null)
                return;

            if (_favoritesCategory.Games.Contains(selected))
            {
                _favoritesCategory.Games.Remove(selected);
            }
            selected.IsFavorite = false;

            SaveFavoritesToFile();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("设置界面暂未实现，可以在后续版本中添加。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BackToParrotButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var parrotPath = System.IO.Path.Combine(baseDir, "TeknoParrotUi.exe");

                if (!System.IO.File.Exists(parrotPath))
                {
                    MessageBox.Show("未找到 TeknoParrotUi.exe。\n\n请确认它与 TeknoParrotBigBox.exe 位于同一目录。", "无法返回鹦鹉 UI",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = parrotPath,
                    WorkingDirectory = baseDir,
                    UseShellExecute = false
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 TeknoParrotUi 失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 成功启动鹦鹉 UI 后退出 BigBox
            Application.Current.Shutdown();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = version != null ? version.ToString() : "未知版本";

            MessageBox.Show(
                "TeknoParrot BigBox 前端" +
                "\n\n版本：" + versionText +
                "\n作者：B站：86年复古游戏厅" +
                "\n用途：为 TeknoParrot 提供封面 + 视频风格启动界面。",
                "关于本程序",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 鼠标滚轮在游戏列表上滚动时，以滚轮作为“上一游戏/下一游戏”切换，而不是只滚动滚动条。
        /// </summary>
        private void GamesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (SelectedCategory == null || SelectedCategory.Games == null || SelectedCategory.Games.Count == 0)
                return;

            var index = GamesList.SelectedIndex;
            if (index < 0) index = 0;

            if (e.Delta < 0)
            {
                // 向下滚动：下一个
                if (index < SelectedCategory.Games.Count - 1)
                    GamesList.SelectedIndex = index + 1;
            }
            else if (e.Delta > 0)
            {
                // 向上滚动：上一个
                if (index > 0)
                    GamesList.SelectedIndex = index - 1;
            }

            // 确保选中项滚动到可见区域，并将键盘焦点保持在列表上
            if (GamesList.SelectedItem != null)
            {
                GamesList.ScrollIntoView(GamesList.SelectedItem);
            }
            GamesList.Focus();

            e.Handled = true;

            // 手动滚动游戏时，重置介绍的自动滚动位置
            _descriptionScrollOffset = 0;
            DescriptionScrollViewer?.ScrollToVerticalOffset(0);
        }

        private void GamesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PreviewMedia == null)
                return;

            try
            {
                var selected = GamesList.SelectedItem as GameEntry;
                if (selected != null && !string.IsNullOrWhiteSpace(selected.VideoPath))
                {
                    // 当选中有视频的游戏时，重新播放预览
                    PreviewMedia.Volume = _isMuted ? 0.0 : 0.5;
                    PreviewMedia.Position = TimeSpan.Zero;
                    PreviewMedia.Play();
                }
                else
                {
                    // 没有视频时停止预览
                    PreviewMedia.Stop();
                }
            }
            catch
            {
                // 忽略 MediaElement 的状态异常
            }
        }

        private void DescriptionScrollViewer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isDescriptionHovered = true;
        }

        private void DescriptionScrollViewer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isDescriptionHovered = false;
        }

        /// <summary>弹出确认框，仅在用户确认后关闭主界面，防止误退出。</summary>
        private void TryCloseWithConfirm()
        {
            var result = MessageBox.Show(
                "确定要退出 TeknoParrot BigBox 吗？",
                "退出确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result == MessageBoxResult.OK)
                Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            var result = MessageBox.Show(
                "确定要退出 TeknoParrot BigBox 吗？",
                "退出确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result != MessageBoxResult.OK)
                e.Cancel = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TryCloseWithConfirm();
                return;
            }

            if (e.Key == Key.Enter)
            {
                LaunchSelectedGame();
                e.Handled = true;
            }
        }

        private static string SanitizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            // 把换行转成空格，并去掉多余空白
            var normalized = name.Replace("\r", " ").Replace("\n", " ");
            return normalized.Trim();
        }

        private static string BuildDescription(GameMetadata meta)
        {
            if (meta == null) return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(meta.GameGenre))
                parts.Add("类型: " + meta.GameGenre);
            if (!string.IsNullOrWhiteSpace(meta.Platform))
                parts.Add("平台: " + meta.Platform);
            if (!string.IsNullOrWhiteSpace(meta.ReleaseYear))
                parts.Add("年份: " + meta.ReleaseYear);

            return string.Join("  /  ", parts);
        }

        private static string ResolveCoverPath(string coversDir, string iconsDir, string profileId, string displayName, GameMetadata meta)
        {
            try
            {
                // 1) 优先使用 Media\Covers 下的封面（按 profileId / bat 名）
                if (Directory.Exists(coversDir))
                {
                    string TryCover(string baseName)
                    {
                        if (string.IsNullOrWhiteSpace(baseName)) return null;
                        var png = Path.Combine(coversDir, baseName + ".png");
                        if (File.Exists(png)) return png;
                        var jpg = Path.Combine(coversDir, baseName + ".jpg");
                        if (File.Exists(jpg)) return jpg;
                        return null;
                    }

                    var byProfile = TryCover(profileId);
                    if (!string.IsNullOrEmpty(byProfile)) return byProfile;

                    var byDisplay = TryCover(displayName);
                    if (!string.IsNullOrEmpty(byDisplay)) return byDisplay;
                }

                // 2) 如果没有 cover，则尝试 Icons 目录 + metadata.icon_name
                if (meta != null && !string.IsNullOrWhiteSpace(meta.IconName) && Directory.Exists(iconsDir))
                {
                    var iconPath = Path.Combine(iconsDir, meta.IconName);
                    if (File.Exists(iconPath))
                    {
                        return iconPath;
                    }
                }
            }
            catch
            {
                // 忽略封面解析错误
            }

            return null;
        }

        private static string ResolveVideoPath(string videosDir, string profileId, string displayName)
        {
            try
            {
                if (!Directory.Exists(videosDir))
                    return null;

                string TryVideo(string baseName)
                {
                    if (string.IsNullOrWhiteSpace(baseName)) return null;
                    var mp4 = Path.Combine(videosDir, baseName + ".mp4");
                    if (File.Exists(mp4)) return mp4;
                    return null;
                }

                // 1) 优先 profileId.mp4
                var byProfile = TryVideo(profileId);
                if (!string.IsNullOrEmpty(byProfile)) return byProfile;

                // 2) 其次按 bat 文件名.mp4
                var byDisplay = TryVideo(displayName);
                if (!string.IsNullOrEmpty(byDisplay)) return byDisplay;

                // 3) 最后使用默认预览视频 TeknoParrot.mp4（放在 Media\Videos 下）
                var defaultPath = Path.Combine(videosDir, "TeknoParrot.mp4");
                if (File.Exists(defaultPath)) return defaultPath;
            }
            catch
            {
                // 忽略视频路径解析错误
            }

            return null;
        }

        private void DescriptionScrollTimer_Tick(object sender, EventArgs e)
        {
            if (DescriptionScrollViewer == null)
                return;

            // 鼠标悬停在介绍区域时暂停自动滚动，允许用户用滚轮自由浏览
            if (_isDescriptionHovered)
                return;

            // 没有内容或内容不足以滚动时，不动
            if (DescriptionScrollViewer.ExtentHeight <= DescriptionScrollViewer.ViewportHeight + 1)
                return;

            // 计算下一个偏移
            _descriptionScrollOffset += 0.8; // 每次轻微移动一点

            if (_descriptionScrollOffset >= DescriptionScrollViewer.ExtentHeight - DescriptionScrollViewer.ViewportHeight)
            {
                // 到底后稍作停顿再回到顶部
                _descriptionScrollOffset = 0;
                DescriptionScrollViewer.ScrollToVerticalOffset(0);
            }
            else
            {
                DescriptionScrollViewer.ScrollToVerticalOffset(_descriptionScrollOffset);
            }
        }

        private void ToggleMuteButton_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            if (PreviewMedia != null)
            {
                PreviewMedia.Volume = _isMuted ? 0.0 : 0.5;
            }

            if (MuteIcon != null)
            {
                MuteIcon.Text = _isMuted ? "🔇" : "🔈";
            }
        }

        private class BatInfo
        {
            public string ProfileId { get; set; }
            public string BatPath { get; set; }
            public string DisplayName { get; set; }
        }

        private class GameMetadata
        {
            [JsonProperty("game_name")]
            public string GameName { get; set; }

            [JsonProperty("game_genre")]
            public string GameGenre { get; set; }

            [JsonProperty("icon_name")]
            public string IconName { get; set; }

            [JsonProperty("platform")]
            public string Platform { get; set; }

            [JsonProperty("release_year")]
            public string ReleaseYear { get; set; }
        }

        private class LaunchboxDescription
        {
            [JsonProperty("profile_id")]
            public string ProfileId { get; set; }

            [JsonProperty("bat_name")]
            public string BatName { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("notes")]
            public string Notes { get; set; }

            [JsonProperty("genre")]
            public string Genre { get; set; }

            [JsonProperty("developer")]
            public string Developer { get; set; }

            [JsonProperty("publisher")]
            public string Publisher { get; set; }

            [JsonProperty("release_date")]
            public string ReleaseDate { get; set; }
        }

        public class GameCategory : INotifyPropertyChanged
        {
            public string Key { get; set; }
            private string _name;
            public string Name
            {
                get => _name;
                set
                {
                    if (_name != value)
                    {
                        _name = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                    }
                }
            }
            public ObservableCollection<GameEntry> Games { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private class FavoritesFile
        {
            [JsonProperty("favorites")]
            public List<string> Favorites { get; set; }
        }

        /// <summary>
        /// 根据元数据/LaunchBox 的 genre 生成“中文分类名称”。
        /// </summary>
        private static string GetLocalizedCategory(string metaGenre, string lbGenre)
        {
            string raw = null;

            if (!string.IsNullOrWhiteSpace(metaGenre))
                raw = metaGenre.Trim();
            else if (!string.IsNullOrWhiteSpace(lbGenre))
                raw = lbGenre.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "未分类";

            // 如果本身已经包含中文字符，就直接用。
            if (raw.Any(c => c >= 0x4e00 && c <= 0x9fff))
                return raw;

            // 常见 LaunchBox 英文类型 → 中文
            switch (raw.ToLowerInvariant())
            {
                case "action":
                    return "动作";
                case "fighting":
                    return "格斗";
                case "racing":
                    return "竞速";
                case "shooter":
                case "light gun":
                    return "射击";
                case "music":
                    return "音乐";
                case "sports":
                    return "体育";
                case "platform":
                case "platformer":
                    return "平台";
                case "puzzle":
                    return "益智";
                case "rhythm":
                    return "节奏";
                case "beat 'em up":
                case "beat'em up":
                    return "横版过关";
                default:
                    return raw;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 将当前收藏列表保存到 favorites.json（仅保存 profileId 列表）。
        /// </summary>
        private void SaveFavoritesToFile()
        {
            if (_favoritesCategory == null) return;

            var ids = _favoritesCategory.Games
                .Select(g => g.ProfileId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fav = new FavoritesFile { Favorites = ids };

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var favoritesPath = Path.Combine(baseDir, "favorites.json");
                var json = JsonConvert.SerializeObject(fav, Formatting.Indented);
                File.WriteAllText(favoritesPath, json);

                // 更新收藏分类名称中的数量
                _favoritesCategory.Name = $"★ 收藏 ({_favoritesCategory.Games.Count})";
            }
            catch
            {
                // 忽略写入错误（不影响运行）
            }
        }
    }
}

