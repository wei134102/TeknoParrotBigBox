using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.Windows.Threading;
using TeknoParrotBigBox.Models;
using LibVLCSharp.Shared;

namespace TeknoParrotBigBox
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> Games { get; } = new ObservableCollection<GameEntry>();
        public ObservableCollection<GameCategory> Categories { get; } = new ObservableCollection<GameCategory>();

        private GameCategory _favoritesCategory;

        private readonly DispatcherTimer _descriptionScrollTimer;
        private readonly DispatcherTimer _gamepadTimer;
        private readonly DispatcherTimer _previewDelayTimer;
        private DispatcherTimer _showPreviewTimer;
        private GamepadInput.GamepadState _lastGamepadState;
        private double _descriptionScrollOffset;
        private bool _isDescriptionHovered;
        private bool _isMuted = false;
        private bool _autoMutedForGame;
        private Process _currentGameProcess;
        private bool _categoryPreviewRetryScheduled;
        /// <summary>LibVLC 与预览播放器（兼容更多视频格式）。</summary>
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _previewVlcPlayer;
        private Media _currentPreviewMedia;
        /// <summary>上一支已成功开始播放的预览视频文件大小(MB)，用于大文件后延迟加载下一支。</summary>
        private double _lastPlayedPreviewFileSizeMb;
        /// <summary>当前一次 Play() 对应的文件大小(MB)，在 Playing 时写回 _lastPlayedPreviewFileSizeMb。</summary>
        private double _currentPreviewFileSizeMb;

        private string _windowTitle = "TeknoParrot BigBox";
        public string WindowTitle
        {
            get => _windowTitle;
            set { _windowTitle = value; OnPropertyChanged(nameof(WindowTitle)); }
        }

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
                    OnPropertyChanged(nameof(GamesCountFormatted));
                }
            }
        }

        public string GamesCountFormatted => Localization.Get("GamesCountPrefix") + TotalGameCount + Localization.Get("GamesCountSuffix");

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
            Localization.Load();
            LoadGamesFromFolders();
            ApplyLanguage();
            Localization.LanguageChanged += (s, ev) => ApplyLanguage();

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

            // 预览视频延迟播放：只有选中项停留超过此时间才加载视频，快速滚轮切换时不会逐个起播
            const int PreviewDelayMs = 500;
            _previewDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PreviewDelayMs)
            };
            _previewDelayTimer.Tick += PreviewDelayTimer_Tick;
            if (PreviewVideoView != null)
                PreviewVideoView.Loaded += PreviewVideoView_Loaded;
        }

        private void PreviewVideoView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _previewVlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                PreviewVideoView.MediaPlayer = _previewVlcPlayer;
                _previewVlcPlayer.Volume = _isMuted ? 0 : 50;
                _previewVlcPlayer.Playing += PreviewVlc_Playing;
                _previewVlcPlayer.EncounteredError += PreviewVlc_EncounteredError;
                _previewVlcPlayer.EndReached += PreviewVlc_EndReached;
                VideoLog("LibVLC 预览播放器已初始化");
                StartPreviewForCurrentGame();
            }
            catch (Exception ex)
            {
                VideoLog("LibVLC 初始化异常: " + ex.Message);
            }
        }

        private void PreviewVlc_Playing(object sender, EventArgs e)
        {
            _lastPlayedPreviewFileSizeMb = _currentPreviewFileSizeMb;
            VideoLog("VLC Playing: 媒体已开始播放 (记录上一支=" + _lastPlayedPreviewFileSizeMb.ToString("F1") + "MB)");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _showPreviewTimer?.Stop();
                _showPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _showPreviewTimer.Tick += (s, ev) =>
                {
                    _showPreviewTimer.Stop();
                    if (PreviewVideoView != null) PreviewVideoView.Opacity = 1;
                };
                _showPreviewTimer.Start();
            }));
        }

        private void PreviewVlc_EncounteredError(object sender, EventArgs e)
        {
            VideoLog("VLC EncounteredError: 播放失败");
        }

        private void PreviewVlc_EndReached(object sender, EventArgs e)
        {
            if (_previewVlcPlayer == null || _currentPreviewMedia == null) return;
            if (_previewVlcPlayer.Media == _currentPreviewMedia)
                _previewVlcPlayer.Play(_currentPreviewMedia);
        }

        private static void VideoLog(string message)
        {
            var line = "[视频] " + message;
            Debug.WriteLine(line);
            AppLog.WriteLine(line);
        }

        private void DoPreviewLoad(Uri uri, GameEntry selected, double currentFileSizeMb)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_previewVlcPlayer == null)
                {
                    VideoLog("DoPreviewLoad: _previewVlcPlayer 未初始化");
                    return;
                }
                var cur = GamesList?.SelectedItem as GameEntry;
                if (cur != selected || string.IsNullOrWhiteSpace(cur?.VideoPath))
                {
                    VideoLog("选择已变更或无视频，跳过本次加载");
                    return;
                }
                try
                {
                    _showPreviewTimer?.Stop();
                    if (PreviewVideoView != null) PreviewVideoView.Opacity = 0;
                    var path = uri.IsFile ? uri.LocalPath : uri.ToString();
                    _previewVlcPlayer.Stop();
                    _currentPreviewMedia?.Dispose();
                    _currentPreviewMedia = new Media(_libVLC, path, FromType.FromPath);
                    _currentPreviewFileSizeMb = currentFileSizeMb;
                    _previewVlcPlayer.Volume = _isMuted ? 0 : 50;
                    _previewVlcPlayer.Play(_currentPreviewMedia);
                    VideoLog("VLC Play: " + path + " (约" + currentFileSizeMb.ToString("F1") + "MB)");
                }
                catch (Exception ex)
                {
                    VideoLog("DoPreviewLoad 异常: " + ex.Message);
                }
            }), DispatcherPriority.Background);
        }

        private void PreviewDelayTimer_Tick(object sender, EventArgs e)
        {
            _previewDelayTimer.Stop();
            VideoLog("Tick 触发");
            if (_previewVlcPlayer == null) return;
            var selected = GamesList.SelectedItem as GameEntry;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (selected != null && !string.IsNullOrWhiteSpace(selected.VideoPath))
                    {
                        var path = selected.VideoPath.Trim();
                        var fileSizeMb = 0.0;
                        try { if (File.Exists(path)) fileSizeMb = new FileInfo(path).Length / (1024.0 * 1024.0); }
                        catch { }
                        Uri uri = Path.IsPathRooted(path)
                            ? new Uri(path, UriKind.Absolute)
                            : new Uri(Path.GetFullPath(path), UriKind.Absolute);
                        VideoLog("准备加载: " + path + " (ProfileId=" + (selected.ProfileId ?? "") + ", 约" + fileSizeMb.ToString("F1") + "MB)");
                        _previewVlcPlayer.Stop();
                        if (fileSizeMb > 50)
                        {
                            VideoLog("大文件(" + fileSizeMb.ToString("F0") + "MB)，延迟 600ms 后加载");
                            var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                            delayTimer.Tick += (s, ev) =>
                            {
                                delayTimer.Stop();
                                VideoLog("延迟结束，开始加载");
                                DoPreviewLoad(uri, selected, fileSizeMb);
                            };
                            delayTimer.Start();
                        }
                        else
                            DoPreviewLoad(uri, selected, fileSizeMb);
                    }
                    else
                    {
                        VideoLog("无视频路径，Stop");
                        _showPreviewTimer?.Stop();
                        _previewVlcPlayer.Stop();
                        if (PreviewVideoView != null) PreviewVideoView.Opacity = 0;
                    }
                }
                catch (Exception ex) { VideoLog("PreviewDelayTimer_Tick 异常: " + ex.Message); }
            }), DispatcherPriority.Background);
        }

        private void GamepadTimer_Tick(object sender, EventArgs e)
        {
            var now = GamepadInput.Poll();
            if (!now.HasInput)
                return;

            // 边沿检测：左右=切换游戏，上下=切换分类（与键盘一致，和原来一样）
            if (now.Left && !_lastGamepadState.Left)
                MoveGameUp();
            if (now.Right && !_lastGamepadState.Right)
                MoveGameDown();
            if (now.Up && !_lastGamepadState.Up)
                MoveCategoryLeft();
            if (now.Down && !_lastGamepadState.Down)
                MoveCategoryRight();
            if (now.A && !_lastGamepadState.A)
                LaunchSelectedGame();
            if (now.B && !_lastGamepadState.B)
                TryCloseWithConfirm();

            _lastGamepadState = now;
        }

        /// <summary>分类向左切换，光标保持在中间、列表动；到第一个时循环到最后一个。</summary>
        private void MoveCategoryLeft()
        {
            if (CategoriesList == null || Categories.Count == 0) return;
            int idx = CategoriesList.SelectedIndex < 0 ? 0 : CategoriesList.SelectedIndex;
            int next = idx <= 0 ? Categories.Count - 1 : idx - 1;
            CategoriesList.SelectedIndex = next;
            ScrollCategoryIntoView();
            CategoriesList.Focus();
        }

        /// <summary>分类向右切换，光标保持在中间、列表动；到最后一个时循环到第一个。</summary>
        private void MoveCategoryRight()
        {
            if (CategoriesList == null || Categories.Count == 0) return;
            int idx = CategoriesList.SelectedIndex < 0 ? 0 : CategoriesList.SelectedIndex;
            int next = idx >= Categories.Count - 1 ? 0 : idx + 1;
            CategoriesList.SelectedIndex = next;
            ScrollCategoryIntoView();
            CategoriesList.Focus();
        }

        /// <summary>将当前选中的分类居中显示，只保留约 7 个分类可见、选中项不动在中间，左右切换时轮动。</summary>
        private void ScrollCategoryIntoView()
        {
            if (CategoriesList?.SelectedItem == null || CategoriesScrollViewer == null) return;
            CategoriesList.ScrollIntoView(CategoriesList.SelectedItem);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var container = CategoriesList.ItemContainerGenerator.ContainerFromItem(CategoriesList.SelectedItem) as FrameworkElement;
                    var listBox = CategoriesScrollViewer.Content as FrameworkElement;
                    if (container == null || listBox == null || container.ActualWidth <= 0) return;
                    var pt = container.TranslatePoint(new Point(0, 0), listBox);
                    double itemCenter = pt.X + container.ActualWidth / 2.0;
                    double viewportCenter = CategoriesScrollViewer.ViewportWidth / 2.0;
                    double offset = itemCenter - viewportCenter;
                    double maxOffset = Math.Max(0, CategoriesScrollViewer.ExtentWidth - CategoriesScrollViewer.ViewportWidth);
                    offset = Math.Max(0, Math.Min(offset, maxOffset));
                    CategoriesScrollViewer.ScrollToHorizontalOffset(offset);
                }
                catch { }
            }), DispatcherPriority.Loaded);
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
        /// 官方客户端鹦鹉 UI 路径：与 BigBox 同目录的 TeknoParrotUi.exe。
        /// </summary>
        private static string GetTeknoParrotUiPath(string baseDir)
        {
            return Path.Combine(baseDir, "TeknoParrotUi.exe");
        }

        /// <summary>
        /// 解析封面/视频目录。支持两种自定义路径：1) 指向“Media 的上级”（内含 Media\Covers、Media\Videos）；2) 指向 Media 文件夹本身（内含 Covers、Videos）。
        /// </summary>
        private static void ResolveMediaDirs(string baseDir, out string coversDir, out string videosDir)
        {
            var defaultCovers = Path.Combine(baseDir, "Media", "Covers");
            var defaultVideos = Path.Combine(baseDir, "Media", "Videos");
            if (string.IsNullOrWhiteSpace(BigBoxSettings.MediaPath))
            {
                coversDir = defaultCovers;
                videosDir = defaultVideos;
                return;
            }
            var custom = BigBoxSettings.MediaPath.Trim();
            if (!Directory.Exists(custom))
            {
                coversDir = defaultCovers;
                videosDir = defaultVideos;
                return;
            }
            var withMediaCovers = Path.Combine(custom, "Media", "Covers");
            var directCovers = Path.Combine(custom, "Covers");
            if (Directory.Exists(withMediaCovers))
            {
                coversDir = withMediaCovers;
                videosDir = Path.Combine(custom, "Media", "Videos");
            }
            else if (Directory.Exists(directCovers))
            {
                coversDir = directCovers;
                videosDir = Path.Combine(custom, "Videos");
            }
            else
            {
                coversDir = defaultCovers;
                videosDir = defaultVideos;
            }
        }

        /// <summary>
        /// 从 UserProfiles（优先）/ bat / Metadata / Icons / Media\Covers / Media\Videos / launchbox_descriptions.json 加载游戏与分类。
        /// </summary>
        private void LoadGamesFromFolders()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string coversDir, videosDir;
            ResolveMediaDirs(baseDir, out coversDir, out videosDir);
            var userProfilesDir = Path.Combine(baseDir, "UserProfiles");
            var batDir = Path.Combine(baseDir, "bat");
            var metadataDir = Path.Combine(baseDir, "Metadata");
            var iconsDir = Path.Combine(baseDir, "Icons");
            var launchboxJsonPath = Path.Combine(baseDir, "launchbox_descriptions.json");

            // 1) 优先使用官方 UserProfiles 目录（.xml 文件名 = profileId），比 bat 更可靠
            var profileIdsFromUserProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(userProfilesDir))
            {
                foreach (var xmlPath in Directory.GetFiles(userProfilesDir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var profileId = Path.GetFileNameWithoutExtension(xmlPath);
                    if (!string.IsNullOrWhiteSpace(profileId))
                        profileIdsFromUserProfiles[profileId] = profileId;
                }
            }

            // 2) 若无 UserProfiles 或为空，则回退到 bat 目录扫描（--profile=XXXX.xml）
            var batByProfileId = new Dictionary<string, BatInfo>(StringComparer.OrdinalIgnoreCase);
            if (profileIdsFromUserProfiles.Count == 0)
            {
                if (!Directory.Exists(batDir))
                {
                    MessageBox.Show(Localization.Get("MsgNoBatFolder"), Localization.Get("CaptionTip"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
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
                                profileId = line.Substring(start, end - start);
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
            }

            // 3) 预加载 Metadata（按文件名 = profileId）
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
                            metadataByProfileId[profileId] = meta;
                    }
                    catch
                    {
                        // 忽略单个 metadata 解析错误
                    }
                }
            }

            // 4) 预加载 LaunchBox 描述（按 profileId）
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

            // 5) 合并：LaunchBox 描述 + metadata + 来源（UserProfiles 或 bat）
            var groups = new Dictionary<string, List<GameEntry>>(StringComparer.OrdinalIgnoreCase);
            var teknoParrotUiPath = GetTeknoParrotUiPath(baseDir);

            if (profileIdsFromUserProfiles.Count > 0)
            {
                // 来源：UserProfiles，启动方式为 TeknoParrotUi.exe --profile=ID.xml
                foreach (var kv in profileIdsFromUserProfiles)
                {
                    var profileId = kv.Key;
                    var displayName = kv.Value;

                    metadataByProfileId.TryGetValue(profileId, out var meta);
                    launchboxByProfileId.TryGetValue(profileId, out var lb);

                    var title =
                        !string.IsNullOrWhiteSpace(lb?.Title) ? lb.Title :
                        meta != null ? SanitizeGameName(meta.GameName) :
                        displayName;
                    var description =
                        !string.IsNullOrWhiteSpace(lb?.Notes) ? lb.Notes :
                        BuildDescription(meta);
                    var coverPath = ResolveCoverPath(coversDir, iconsDir, profileId, displayName, meta);
                    var videoPath = ResolveVideoPath(videosDir, profileId, displayName);

                    var entry = new GameEntry
                    {
                        ProfileId = profileId,
                        Title = title,
                        Description = description,
                        CoverImagePath = coverPath,
                        VideoPath = videoPath,
                        LaunchExecutable = teknoParrotUiPath,
                        LaunchArguments = "--profile=" + profileId + ".xml"
                    };
                    var categoryKey = GetLocalizedCategory(meta?.GameGenre, lb?.Genre);
                    if (!groups.TryGetValue(categoryKey, out var list))
                    {
                        list = new List<GameEntry>();
                        groups[categoryKey] = list;
                    }
                    list.Add(entry);
                }
            }
            else
            {
                // 回退：bat，启动方式为执行 bat
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
                    var categoryKey = GetLocalizedCategory(meta?.GameGenre, lb?.Genre);
                    if (!groups.TryGetValue(categoryKey, out var list))
                    {
                        list = new List<GameEntry>();
                        groups[categoryKey] = list;
                    }
                    list.Add(entry);
                }
            }

            // 6) 把分组结果转换为 Category 集合
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

                _favoritesCategory.Name = Localization.Get("CategoryFavorites") + " (" + _favoritesCategory.Games.Count + ")";
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
                MessageBox.Show(Localization.Get("MsgNoGameScripts"), Localization.Get("CaptionTip"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>根据当前语言刷新主界面所有文案（含收藏分类名称）。</summary>
        private void ApplyLanguage()
        {
            WindowTitle = Localization.Get("TitleMain");
            if (StartGameButton != null) StartGameButton.Content = Localization.Get("ButtonStartGame");
            if (FavoriteGameButton != null) FavoriteGameButton.Content = Localization.Get("ButtonFavorite");
            if (UnfavoriteGameButton != null) UnfavoriteGameButton.Content = Localization.Get("ButtonUnfavorite");
            if (BackToParrotButton != null) BackToParrotButton.Content = Localization.Get("ButtonBackToParrot");
            if (SettingsButton != null) SettingsButton.Content = Localization.Get("ButtonSettings");
            if (AboutButton != null) AboutButton.Content = Localization.Get("ButtonAbout");
            if (HintText != null) HintText.Text = Localization.Get("HintBottom");
            OnPropertyChanged(nameof(GamesCountFormatted));
            if (_favoritesCategory != null)
                _favoritesCategory.Name = Localization.Get("CategoryFavorites") + " (" + (_favoritesCategory.Games?.Count ?? 0) + ")";
        }

        /// <summary>切换分类后，将游戏列表定位到本分类第一个游戏，并保证分类栏中选中项可见。用 ApplicationIdle 确保绑定已更新后再设选中项和预览，避免“切到最后一类再切回来”无预览。</summary>
        private void CategoriesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SelectedCategory?.Games == null || GamesList == null)
                return;
            ScrollCategoryIntoView();
            _previewDelayTimer.Stop();
            _categoryPreviewRetryScheduled = false;
            Dispatcher.BeginInvoke(new Action(ApplyCategoryChangeAndPreview), DispatcherPriority.ApplicationIdle);
        }

        private void ApplyCategoryChangeAndPreview()
        {
            if (SelectedCategory?.Games == null || GamesList == null) return;
            _showPreviewTimer?.Stop();
            if (_previewVlcPlayer != null) { try { _previewVlcPlayer.Stop(); } catch { } }
            if (PreviewVideoView != null) PreviewVideoView.Opacity = 0;
            if (SelectedCategory.Games.Count > 0)
            {
                GamesList.SelectedIndex = 0;
                if (GamesList.SelectedItem != null)
                    GamesList.ScrollIntoView(GamesList.SelectedItem);
                StartPreviewForCurrentGame();
                // 若绑定尚未生效导致 SelectedItem 仍为 null，再调度一次重试（仅一次）
                if (GamesList.SelectedItem == null && !_categoryPreviewRetryScheduled)
                {
                    _categoryPreviewRetryScheduled = true;
                    Dispatcher.BeginInvoke(new Action(ApplyCategoryChangeAndPreview), DispatcherPriority.ApplicationIdle);
                }
            }
            GamesList.Focus();
        }

        private void LaunchSelectedGame()
        {
            var selected = GamesList.SelectedItem as GameEntry;
            if (selected == null)
            {
                MessageBox.Show(Localization.Get("MsgNoGameSelected"), Localization.Get("CaptionTip"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(selected.LaunchExecutable))
            {
                MessageBox.Show(Localization.Get("MsgLaunchNotConfigured"), Localization.Get("CaptionTip"),
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

                startInfo.UseShellExecute = false;
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
                    startInfo.WorkingDirectory = Path.IsPathRooted(startInfo.FileName)
                        ? Path.GetDirectoryName(startInfo.FileName) ?? AppDomain.CurrentDomain.BaseDirectory
                        : AppDomain.CurrentDomain.BaseDirectory;
                }

                // 启动游戏进程
                _currentGameProcess?.Dispose();
                _currentGameProcess = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Get("MsgLaunchFailed", ex.Message), Localization.Get("CaptionError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_previewVlcPlayer != null)
            {
                try { _previewVlcPlayer.Stop(); }
                catch { }
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
                MessageBox.Show(Localization.Get("MsgNoGameSelected"), Localization.Get("CaptionTip"),
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
            var win = new SettingsWindow { Owner = this };
            if (win.ShowDialog() == true)
                LoadGamesFromFolders();
        }

        private void BackToParrotButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var parrotPath = System.IO.Path.Combine(baseDir, "TeknoParrotUi.exe");

                if (!System.IO.File.Exists(parrotPath))
                {
                    MessageBox.Show(Localization.Get("MsgParrotNotFound"), Localization.Get("MsgParrotNotFoundTitle"),
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
                MessageBox.Show(Localization.Get("MsgParrotStartFailed", ex.Message), Localization.Get("CaptionError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 成功启动鹦鹉 UI 后退出 BigBox
            Application.Current.Shutdown();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = version != null ? version.ToString() : Localization.Get("VersionUnknown");
            MessageBox.Show(
                Localization.Get("AboutMessage", versionText),
                Localization.Get("AboutTitle"),
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
            _previewDelayTimer.Stop();
            if (_previewVlcPlayer == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _previewVlcPlayer.Stop();
                    StartPreviewForCurrentGame();
                }
                catch { }
            }), DispatcherPriority.Background);
        }

        /// <summary>根据当前选中的游戏启动预览（有视频则启动延迟计时器，无则停止）。</summary>
        private void StartPreviewForCurrentGame()
        {
            if (_previewVlcPlayer == null) return;
            try
            {
                var selected = GamesList?.SelectedItem as GameEntry;
                if (selected != null && !string.IsNullOrWhiteSpace(selected.VideoPath))
                {
                    VideoLog("StartPreviewForCurrentGame: 启动延迟计时器, ProfileId=" + (selected.ProfileId ?? ""));
                    _previewDelayTimer.Start();
                }
                else
                {
                    VideoLog("StartPreviewForCurrentGame: 无视频，清空");
                    _showPreviewTimer?.Stop();
                    _previewVlcPlayer.Stop();
                    if (PreviewVideoView != null) PreviewVideoView.Opacity = 0;
                }
            }
            catch (Exception ex) { VideoLog("StartPreviewForCurrentGame 异常: " + ex.Message); }
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
                Localization.Get("ExitConfirmMessage"),
                Localization.Get("ExitConfirmTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result == MessageBoxResult.OK)
                Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            var result = MessageBox.Show(
                Localization.Get("ExitConfirmMessage"),
                Localization.Get("ExitConfirmTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result != MessageBoxResult.OK)
                e.Cancel = true;
        }

        /// <summary>PreviewKeyDown 先于列表收到按键，保证左右=游戏、上下=分类统一生效。</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                MoveGameUp();
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                MoveGameDown();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                MoveCategoryLeft();
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                MoveCategoryRight();
                e.Handled = true;
            }
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

        private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".webm", ".mkv", ".wmv", ".m4v" };

        private static string ResolveVideoPath(string videosDir, string profileId, string displayName)
        {
            try
            {
                if (!Directory.Exists(videosDir))
                    return null;

                string TryVideo(string baseName)
                {
                    if (string.IsNullOrWhiteSpace(baseName)) return null;
                    foreach (var ext in VideoExtensions)
                    {
                        var path = Path.Combine(videosDir, baseName + ext);
                        if (File.Exists(path)) return path;
                    }
                    return null;
                }

                // 1) 优先 profileId
                var byProfile = TryVideo(profileId);
                if (!string.IsNullOrEmpty(byProfile)) return byProfile;

                // 2) 其次按 bat 文件名
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

        private void PreviewVideoContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var selected = GamesList?.SelectedItem as GameEntry;
            AddVideoMenuItem.IsEnabled = selected != null && !string.IsNullOrWhiteSpace(selected.ProfileId) && string.IsNullOrWhiteSpace(selected.VideoPath);
        }

        private void AddVideoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = GamesList?.SelectedItem as GameEntry;
            if (selected == null || string.IsNullOrWhiteSpace(selected.ProfileId))
            {
                MessageBox.Show("请先选中一个游戏。", "添加视频", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!string.IsNullOrWhiteSpace(selected.VideoPath))
            {
                MessageBox.Show("当前游戏已有预览视频。", "添加视频", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string coversDir, videosDir;
            ResolveMediaDirs(baseDir, out coversDir, out videosDir);
            try
            {
                if (!Directory.Exists(videosDir))
                    Directory.CreateDirectory(videosDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法创建视频目录：\n" + ex.Message, "添加视频", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "选择预览视频",
                Filter = "视频文件|*.mp4;*.avi;*.webm;*.mkv;*.wmv;*.m4v|所有文件|*.*",
                FilterIndex = 1
            };
            if (dlg.ShowDialog() != true)
                return;

            var sourcePath = dlg.FileName;
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".mp4";
            var destFileName = selected.ProfileId + ext;
            var destPath = Path.Combine(videosDir, destFileName);

            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制视频失败：\n" + ex.Message, "添加视频", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            selected.VideoPath = destPath;
            StartPreviewForCurrentGame();
            MessageBox.Show("已添加预览视频：\n" + destFileName, "添加视频", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (_previewVlcPlayer != null)
            {
                _previewVlcPlayer.Volume = _isMuted ? 0 : 50;
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

            // 常见 LaunchBox / TeknoParrot 英文类型 → 中文（尽量覆盖未汉化分类）
            switch (raw.ToLowerInvariant())
            {
                case "action":
                    return "动作";
                case "fighting":
                    return "格斗";
                case "racing":
                case "driving":
                    return "竞速";
                case "shooter":
                case "light gun":
                case "first person shooter":
                case "fps":
                    return "射击";
                case "music":
                case "music/rhythm":
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
                case "beat em up":
                    return "横版过关";
                case "adventure":
                case "adventure game":
                    return "冒险";
                case "simulation":
                case "sim":
                    return "模拟";
                case "role-playing":
                case "roleplaying":
                case "rpg":
                    return "角色扮演";
                case "arcade":
                    return "街机";
                case "misc":
                case "miscellaneous":
                case "other":
                    return "其他";
                case "pinball":
                    return "弹珠";
                case "card":
                case "card game":
                    return "卡牌";
                case "board":
                case "board game":
                    return "桌游";
                case "trivia":
                    return "问答";
                case "compilation":
                    return "合集";
                case "party":
                case "party game":
                    return "聚会";
                case "horror":
                    return "恐怖";
                case "strategy":
                    return "策略";
                case "flight":
                case "flight simulation":
                    return "飞行";
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
                _favoritesCategory.Name = Localization.Get("CategoryFavorites") + " (" + _favoritesCategory.Games.Count + ")";
            }
            catch
            {
                // 忽略写入错误（不影响运行）
            }
        }
    }
}

