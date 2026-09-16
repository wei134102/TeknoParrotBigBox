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
using System.Windows.Media.Animation;
using Microsoft.Win32;
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

        // ====== 视频预览状态机 ======
        private enum VideoPreviewState { Idle, WaitingSelected, WaitingBigFile, LoadingMedia, TransitioningIn }
        private VideoPreviewState _videoState;
        private readonly DispatcherTimer _videoStateTimer;
        private GamepadInput.GamepadState _lastGamepadState;
        private double _descriptionScrollOffset;
        private bool _isDescriptionHovered;
        private bool _isMuted = false;
        private bool _isExitingConfirmed;
        private bool _isCurrentMediaPlaying;
        private Process _currentGameProcess;
        private bool _categoryPreviewRetryScheduled;
        

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

            // 统一视频状态机计时器：根据 _videoState 处理不同阶段的时序
            _videoStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
            _videoStateTimer.Tick += VideoStateTimer_Tick;

            StartPreviewForCurrentGame();
        }

        private void PreviewVideoView_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isCurrentMediaPlaying = true;
            VideoLog("MediaElement 媒体已打开");
            if (VideoPlaceholder != null) VideoPlaceholder.Visibility = Visibility.Collapsed;
            StartVideoFadeIn();
            _videoState = VideoPreviewState.Idle;
        }

        private void PreviewVideoView_MediaEnded(object sender, RoutedEventArgs e)
        {
            // MediaElement 结束：停止而不是重播，避免长时间占用 CPU/内存。
            StopVideoPreviewInternal();
        }

        private void PreviewVideoView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            VideoLog("MediaElement 播放失败: " + (e.ErrorException?.Message ?? ""));
            _isCurrentMediaPlaying = false;
            _videoStateTimer.Stop();
            TransitionVideoState(VideoPreviewState.Idle);
            if (VideoPlaceholder != null) VideoPlaceholder.Visibility = Visibility.Visible;
        }

        private void StartVideoFadeIn()
        {
            if (PreviewVideoView == null) return;
            try
            {
                var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                PreviewVideoView.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { PreviewVideoView.Opacity = 1; }
        }

        private void StartVideoFadeOut()
        {
            if (PreviewVideoView == null || PreviewVideoView.Opacity <= 0.05) return;
            try
            {
                var anim = new DoubleAnimation(PreviewVideoView.Opacity, 0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                PreviewVideoView.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { PreviewVideoView.Opacity = 0; }
        }

        private static void VideoLog(string message)
        {
            var line = "[视频] " + message;
            Debug.WriteLine(line);
            AppLog.WriteLine(line);
        }

        // ====== 视频状态机 ======
        //   Idle               → 空闲，可随时启动新预览
        //   WaitingSelected    → 等待选中项稳定（500ms），避免快速切换时逐个起播
        //   WaitingBigFile     → 大文件(>50MB)额外延迟 600ms 缓冲
        //   LoadingMedia       → 正在加载媒体
        //   TransitioningIn    → 等待播放开始后淡入
        
        private DateTime _videoStateEnterTime;

        private void TransitionVideoState(VideoPreviewState newState)
        {
            _videoState = newState;
            _videoStateEnterTime = DateTime.Now;
            VideoLog("状态机 → " + newState);
        }

        private void VideoStateTimer_Tick(object sender, EventArgs e)
        {
            if (_videoState == VideoPreviewState.Idle || _videoState == VideoPreviewState.LoadingMedia || _videoState == VideoPreviewState.TransitioningIn)
            {
                _videoStateTimer.Stop();
                return;
            }

            var selected = GamesList?.SelectedItem as GameEntry;
            if (selected == null || string.IsNullOrWhiteSpace(selected.VideoPath))
            {
                StopVideoPreviewInternal();
                TransitionVideoState(VideoPreviewState.Idle);
                _videoStateTimer.Stop();
                return;
            }

            var elapsed = (DateTime.Now - _videoStateEnterTime).TotalMilliseconds;
            if (elapsed >= 30000)
            {
                VideoLog("状态机超时，强制回到 Idle");
                StopVideoPreviewInternal();
                TransitionVideoState(VideoPreviewState.Idle);
                _videoStateTimer.Stop();
                return;
            }

            if (_videoState == VideoPreviewState.WaitingSelected && elapsed >= 500)
            {
                var path = selected.VideoPath.Trim();
                var fileSizeMb = 0.0;
                try { if (File.Exists(path)) fileSizeMb = new FileInfo(path).Length / (1024.0 * 1024.0); }
                catch { }

                if (fileSizeMb > 50)
                {
                    VideoLog("大文件(" + fileSizeMb.ToString("F0") + "MB)，缓冲 600ms 再加载");
                    TransitionVideoState(VideoPreviewState.WaitingBigFile);
                }
                else
                {
                    LoadVideoMedia(selected, path, fileSizeMb);
                    TransitionVideoState(VideoPreviewState.LoadingMedia);
                    _videoStateTimer.Stop();
                }
            }
            else if (_videoState == VideoPreviewState.WaitingBigFile && elapsed >= 600)
            {
                var path = selected.VideoPath.Trim();
                var fileSizeMb = 0.0;
                try { if (File.Exists(path)) fileSizeMb = new FileInfo(path).Length / (1024.0 * 1024.0); }
                catch { }
                LoadVideoMedia(selected, path, fileSizeMb);
                TransitionVideoState(VideoPreviewState.LoadingMedia);
                _videoStateTimer.Stop();
            }
        }

        private void LoadVideoMedia(GameEntry selected, string path, double fileSizeMb)
        {
            if (PreviewVideoView == null) return;
            try
            {
                if (_isCurrentMediaPlaying)
                    StartVideoFadeOut();

                _isCurrentMediaPlaying = false;
                StopVideoPreviewInternal();

                Uri uri = Path.IsPathRooted(path)
                    ? new Uri(path, UriKind.Absolute)
                    : new Uri(Path.GetFullPath(path), UriKind.Absolute);

                PreviewVideoView.Source = uri;
                PreviewVideoView.IsMuted = _isMuted;
                PreviewVideoView.Play();
                VideoLog("MediaElement Play: " + path + " (约" + fileSizeMb.ToString("F1") + "MB)");
            }
            catch (Exception ex)
            {
                VideoLog("LoadVideoMedia 异常: " + ex.Message);
                TransitionVideoState(VideoPreviewState.Idle);
            }
        }

        private void StopVideoPreviewInternal()
        {
            _isCurrentMediaPlaying = false;
            if (PreviewVideoView != null)
            {
                PreviewVideoView.Stop();
                PreviewVideoView.Source = null;
                PreviewVideoView.Opacity = 0;
            }
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
            if (FavoriteIndicator != null) FavoriteIndicator.Text = Localization.Get("LabelFavoriteIndicator");
            if (NoPreviewVideoText != null) NoPreviewVideoText.Text = Localization.Get("LabelNoPreviewVideo");
            if (AddVideoMenuItem != null) AddVideoMenuItem.Header = Localization.Get("MenuAddVideo");
            if (GameCategoryLabel != null && (SelectedCategory == null || SelectedCategory.Key == "__favorites"))
                GameCategoryLabel.Text = Localization.Get("LabelArcade");
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
            _videoStateTimer.Stop();
            TransitionVideoState(VideoPreviewState.Idle);
            _categoryPreviewRetryScheduled = false;
            Dispatcher.BeginInvoke(new Action(ApplyCategoryChangeAndPreview), DispatcherPriority.ApplicationIdle);
        }

        private void ApplyCategoryChangeAndPreview()
        {
            if (SelectedCategory?.Games == null || GamesList == null) return;
            StopVideoPreviewInternal();
            TransitionVideoState(VideoPreviewState.Idle);
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
                _currentGameProcess = new Process { EnableRaisingEvents = true, StartInfo = startInfo };
                _currentGameProcess.Exited += CurrentGameProcess_Exited;
                _currentGameProcess.Start();

                // 游戏运行时将主窗口最小化，减少干扰
                WindowState = WindowState.Minimized;
                ShowInTaskbar = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Get("MsgLaunchFailed", ex.Message), Localization.Get("CaptionError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                StopVideoPreviewInternal();
            }
            catch { }
        }

        private void CurrentGameProcess_Exited(object sender, EventArgs e)
        {
            // 回到 UI 线程，清理进程句柄并恢复主窗口
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentGameProcess != null)
                {
                    _currentGameProcess.Exited -= CurrentGameProcess_Exited;
                    _currentGameProcess.Dispose();
                    _currentGameProcess = null;
                }
                // 游戏结束后恢复窗口
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
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
            {
                NullToImageSourceConverter.ClearCache();
                LoadGamesFromFolders();
            }
        }

        private void BackToParrotButton_Click(object sender, RoutedEventArgs e)
        {
            // 先弹三按钮确认框：返回鹦鹉 / 推出程序 / 取消。仅当用户明确选择才执行动作，避免误点。
            switch (ShowBackToParrotConfirm())
            {
                case BackToParrotChoice.BackToParrot:
                    LaunchTeknoParrotUi();
                    break;
                case BackToParrotChoice.ExitProgram:
                    _isExitingConfirmed = true;
                    Application.Current.Shutdown();
                    break;
                case BackToParrotChoice.Cancel:
                    break;
            }
        }

        /// <summary>三按钮确认对话框的用户选择。</summary>
        private enum BackToParrotChoice
        {
            BackToParrot,
            ExitProgram,
            Cancel
        }

        /// <summary>
        /// 弹出三按钮确认框：「返回鹦鹉 / 推出程序 / 取消」。
        /// 使用自定义 Window 承载，因为系统 MessageBox 无法自定义第三个按钮的文本。
        /// 按钮风格与主界面保持一致：返回=街机绿，推出=暗紫警示，取消=暗紫中性。
        /// </summary>
        private BackToParrotChoice ShowBackToParrotConfirm()
        {
            BackToParrotChoice choice = BackToParrotChoice.Cancel;

            var dlg = new Window
            {
                Owner = this,
                Title = Localization.Get("TitleBackToParrotConfirm"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                MinWidth = 420,
                MaxWidth = 600,
                WindowStyle = WindowStyle.SingleBorderWindow
            };
            dlg.Background = (SolidColorBrush)TryFindResource("ThemeBgWindowBrush")
                             ?? new SolidColorBrush(Color.FromRgb(10, 10, 20));
            dlg.Foreground = (SolidColorBrush)TryFindResource("ThemeTextPrimaryBrush")
                             ?? new SolidColorBrush(Color.FromRgb(221, 221, 238));
            dlg.FontFamily = new FontFamily("Segoe UI");
            dlg.FontSize = 13;

            var root = new Grid { Margin = new System.Windows.Thickness(28, 24, 28, 20) };

            // 标题栏：与卡片一致的暗紫底 + 街机绿左侧装饰条
            var header = new Border
            {
                Background = (Brush)TryFindResource("ThemeBgPanelBrush") ?? Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new System.Windows.Thickness(14, 12, 14, 12),
                Margin = new System.Windows.Thickness(0, 0, 0, 18)
            };
            var headerPanel = new Grid();
            var headerText = new TextBlock
            {
                Text = Localization.Get("TitleBackToParrotConfirm"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)TryFindResource("ThemeAccentCyanBrush") ?? Brushes.White
            };
            headerPanel.Children.Add(headerText);
            header.Child = headerPanel;
            root.Children.Add(header);

            var message = new TextBlock
            {
                Text = Localization.Get("MsgBackToParrotConfirm"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
                Margin = new System.Windows.Thickness(0, 0, 0, 22)
            };
            root.Children.Add(message);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            root.Children.Add(panel);

            void SetChoice(BackToParrotChoice value)
            {
                choice = value;
                dlg.Close();
            }

            // 把主界面的「街机绿」主按钮样式注入到对话框，保持视觉一致
            var greenStyle = TryFindResource("GreenPrimaryButtonStyle") as Style;
            var darkStyle = TryFindResource("DarkActionButtonStyle") as Style;

            var backBtn = new Button
            {
                Content = Localization.Get("ButtonBackToParrotConfirm"),
                MinWidth = 120,
                Height = 36,
                Margin = new System.Windows.Thickness(8, 0, 0, 0),
                Style = greenStyle
            };
            backBtn.Click += (s, e) => SetChoice(BackToParrotChoice.BackToParrot);
            panel.Children.Add(backBtn);

            // 推出程序：与「取消」按钮一致使用暗紫中性风格，靠图标 ⏻ 区分含义
            var exitBtn = new Button
            {
                Content = Localization.Get("ButtonExitProgram"),
                MinWidth = 120,
                Height = 36,
                Margin = new System.Windows.Thickness(8, 0, 0, 0),
                Style = darkStyle
            };
            exitBtn.Click += (s, e) => SetChoice(BackToParrotChoice.ExitProgram);
            panel.Children.Add(exitBtn);

            var cancelBtn = new Button
            {
                // 取消按钮：与其它两个按钮保持一致，加图标前缀
                Content = "✕  " + Localization.Get("ButtonCancel"),
                MinWidth = 120,
                Height = 36,
                Margin = new System.Windows.Thickness(8, 0, 0, 0),
                Style = darkStyle,
                IsCancel = true
            };
            cancelBtn.Click += (s, e) => SetChoice(BackToParrotChoice.Cancel);
            panel.Children.Add(cancelBtn);

            dlg.Content = root;
            dlg.ShowDialog();
            return choice;
        }

        /// <summary>启动同目录下的 TeknoParrotUi.exe，成功后退出 BigBox。</summary>
        private void LaunchTeknoParrotUi()
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

            // 成功启动鹦鹉 UI 后退出 BigBox；标记已确认退出，避免 Window_Closing 再次弹确认框
            _isExitingConfirmed = true;
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
            _videoStateTimer.Stop();
            TransitionVideoState(VideoPreviewState.Idle);

            // 更新新 UI 元素：分类标签、收藏指示器、视频占位符
            var selected = GamesList?.SelectedItem as GameEntry;
            if (selected != null)
            {
                if (GameCategoryLabel != null)
                    GameCategoryLabel.Text = SelectedCategory?.Name ?? "街机";

                if (FavoriteIndicator != null)
                    FavoriteIndicator.Visibility = selected.IsFavorite ? Visibility.Visible : Visibility.Collapsed;

                if (VideoPlaceholder != null)
                    VideoPlaceholder.Visibility = string.IsNullOrWhiteSpace(selected.VideoPath)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else
            {
                if (GameCategoryLabel != null) GameCategoryLabel.Text = "街机";
                if (FavoriteIndicator != null) FavoriteIndicator.Visibility = Visibility.Collapsed;
                if (VideoPlaceholder != null) VideoPlaceholder.Visibility = Visibility.Visible;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    StartPreviewForCurrentGame();
                }
                catch { }
            }), DispatcherPriority.Background);
        }

        /// <summary>根据当前选中的游戏启动预览（有视频则启动延迟计时器，无则停止）。</summary>
        private void StartPreviewForCurrentGame()
        {
            if (PreviewVideoView == null) return;
            try
            {
                var selected = GamesList?.SelectedItem as GameEntry;
                if (selected != null && !string.IsNullOrWhiteSpace(selected.VideoPath))
                {
                    VideoLog("StartPreviewForCurrentGame: 进入 WaitingSelected, ProfileId=" + (selected.ProfileId ?? ""));
                    TransitionVideoState(VideoPreviewState.WaitingSelected);
                    _videoStateTimer.Start();
                }
                else
                {
                    VideoLog("StartPreviewForCurrentGame: 无视频，清空");
                    _videoStateTimer.Stop();
                    StopVideoPreviewInternal();
                    if (VideoPlaceholder != null) VideoPlaceholder.Visibility = Visibility.Visible;
                    TransitionVideoState(VideoPreviewState.Idle);
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
            if (_isExitingConfirmed) return;
            var result = MessageBox.Show(
                Localization.Get("ExitConfirmMessage"),
                Localization.Get("ExitConfirmTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result == MessageBoxResult.OK)
            {
                _isExitingConfirmed = true;
                Close();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isExitingConfirmed) return;
            var result = MessageBox.Show(
                Localization.Get("ExitConfirmMessage"),
                Localization.Get("ExitConfirmTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result != MessageBoxResult.OK)
                e.Cancel = true;
            else
                _isExitingConfirmed = true;
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
            if (PreviewVideoView != null)
            {
                PreviewVideoView.IsMuted = _isMuted;
                if (!_isMuted) PreviewVideoView.Volume = 0.5;
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


