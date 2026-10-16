using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Api;
using YaMus.Core.Auth;
using YaMus.Core.Concurrency;
using YaMus.Core.Diagnostics;
using YaMus.Core.Models;
using YaMus.Core.Playback;
using YaMus.Core.Transport;

namespace YaMus.UI
{
    /// <summary>Главное окно: сайдбар + список/карточки + плеер. Разнесено по partial-файлам:
    /// Navigation (сайдбар и загрузка вьюх), Wave (ротор), TrackList (список и контекстное меню),
    /// Player (воспроизведение).</summary>
    public sealed partial class MainForm : Form
    {
        private readonly RestClient _rest;
        private readonly HttpClient _http;
        private readonly YandexApi _api;
        private readonly TrackDownloader _downloader;
        private readonly Player _player;
        private readonly Core.Media.CoverLoader _coverLoader;

        private long _uid;
        private List<Track> _tracks = new List<Track>();
        private HashSet<string> _likedIds = new HashSet<string>(StringComparer.Ordinal);
        private int _currentIndex = -1;
        // Контекст воспроизведения: список, из которого запущен текущий трек, и позиция
        // в нём. Next/Prev/повтор ходят по нему, а не по _tracks: переход в другой раздел
        // не должен переписывать плейлист на тот, который сейчас открыт на экране.
        private List<Track> _playTracks;
        private int _playIndex = -1;
        private string _playingTrackId;
        private Track _currentTrack;
        private bool _shuffle;
        private bool _repeat;
        private bool _repeatOne;
        private bool _suppressSelect;
        private bool _waveMode;
        private bool _playbackIntended;
        /// <summary>Идёт переключение трека: пока true, авто-переход к концу файла не
        /// дергает AdvanceAfterEnd повторно (в логе было: SWITCH wait → 1,2,3,4).</summary>
        private bool _switching;
        private string _currentView = "";
        private readonly List<string> _tempFiles = new List<string>();

        /// <summary>Сколько раз подряд провалился Initialize по 401: после автологаута
        /// не должны зациклиться, если сервер отвечает 401 и свежесохранённому токену.</summary>
        private int _authRetries;

        /// <summary>Предыдущий режим окна: чтобы ловить maximize/restore и чинить список.</summary>
        private FormWindowState _lastWindowState;

        /// <summary>История реально сыгранных треков (для Prev в случайном порядке).
        /// Сквозной _playIndex бесполезен при shuffle: он двигается по списку, а не по
        /// тем трекам, которые действительно прозвучали. Ограничена PlayHistoryLimit,
        /// чтобы история не росла бесконечно на радио-станции.</summary>
        private readonly List<Track> _playHistory = new List<Track>();
        private int _historyIndex = -1;
        private const int PlayHistoryLimit = 200;

        /// <summary>Следующий трек в случайном порядке. Считается один раз (вперёд) и
        /// переиспользуется: иначе Next(), автопереход по окончании файла и префетч
        /// каждый раз выбирали бы свою точку — трек «перепрыгивался».</summary>
        private int _shuffleNext = -1;

        /// <summary>Позиция в секундах, на которую нужно перейти после старта трека
        /// (восстановление после замены звукового устройства).</summary>
        private int _pendingSeek = -1;

        /// <summary>Станция волны/радио текущего контекста воспроизведения; пусто — обычный
        /// список. Отдельно от _waveMode, т.к. пользователь может уйти со страницы волны,
        /// пока музыка ещё играет, — тогда новые треки надо подкидывать в играющий
        /// контекст, а не в чужой открытый раздел.</summary>
        private string _playStation = "";

        /// <summary>Tick последнего восстановления после пропажи звукового устройства:
        /// серия падений одного и того же устройства не должна запускать рестарты цепью.</summary>
        private int _lastDeviceLostTick;

        // сессионный кэш скачанных файлов: повтор/перезапись трека не качает файл заново
        private readonly Dictionary<string, Tuple<string, string>> _trackFileCache =
            new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
        private readonly Queue<string> _trackFileOrder = new Queue<string>();
        private readonly object _trackCacheLock = new object();
        private const int TrackFileCacheLimit = 24;

        private readonly object _prefetchLock = new object();
        private int _prefetchIndex = -1;
        private string _prefetchFile;
        private string _prefetchCodec;
        private PreparedTrack _prefetchedPrepared;
        /// <summary>Префетч, ещё выполняющийся (качается/готовится). Next не должен качать
        /// тот же трек заново — ждём эту задачу.</summary>
        private Task _prefetchTask;
        private int _prefetchTaskIndex = -1;
        /// <summary>Отмена выполняющегося префетча (переключились и качаем сами).</summary>
        private CancellationTokenSource _prefetchCts;

        private readonly Dictionary<int, int> _rowToImage = new Dictionary<int, int>();
        private readonly Dictionary<int, long> _coverRequests = new Dictionary<int, long>();
        private const int CoverRetryMs = 20000;

        private readonly object _likesLock = new object();
        private Task<List<Track>> _likesTask;
        /// <summary>Загруженный список «Мне нравится». Держим его в памяти и правим
        /// точечно при лайке/анлайке: перечитывать ~2000 треков (это GetLikeIds плюс
        /// пачка запросов по 200 id) при каждом входе в раздел слишком долго.
        /// Поле обновляется только под _likesLock.</summary>
        private List<Track> _likesCache;
        /// <summary>Треки с лайком в полёте: защита от двойного клика, который иначе
        /// отправлял бы add+remove по одному треку и оставлял состояние вразнобой.</summary>
        private readonly HashSet<string> _likeBusy = new HashSet<string>(StringComparer.Ordinal);

        private ArtistPagePanel _artistPage;

        // Состояние страницы исполнителя: популярные треки из профиля плюс лениво
        // загруженный полный список. _artistAllTracksFor — для какого исполнителя
        // этот кэш действителен, чтобы не показать чужие треки.
        private List<Track> _artistPopularTracks = new List<Track>();
        private List<Album> _artistAlbums;
        private List<Track> _artistAllTracks;
        private long _artistAllTracksFor;
        private bool _artistAllLoaded;
        private HomePagePanel _homePage;
        private SettingsPanel _settingsPage;
        private SearchPagePanel _searchPage;
        private int _searchSeq;
        private readonly ContextMenuStrip _trackMenu = new ContextMenuStrip();

        private TreeView _nav;
        private YaMusScrollBar _navScroll;
        private DarkListView _trackList;
        private YaMusScrollBar _trackScroll;
        private YaMusScrollBar _artistScroll;
        private YaMusScrollBar _settingsScroll;
        private YaMusScrollBar _searchScroll;
        private ImageList _covers;
        private StatusStrip _status;
        private ToolStripStatusLabel _statusLabel;
        private GlyphButton _prevButton;
        private RoundButton _playButton;
        private GlyphButton _nextButton;
        private GlyphButton _shuffleButton;
        private MirrorGlyphButton _repeatButton;
        private GlyphButton _likeButton;
        private Button _moreButton;
        private Button _refreshButton;
        private Button _backButton;
        private Panel _nowTitleHost;
        private NowPlayingOverlay _nowPlaying;
        private LinkLabel _nowTitle;
        private Label _nowVersion;
        private LinkLabel _nowSub;
        private CoverBox _coverBox;
        private SeekBar _seekBar;
        private Label _timeLabel;
        private System.Windows.Forms.Timer _tick;
        private int _hoverRow = -1;
        private readonly Dictionary<long, string> _playlistTitles = new Dictionary<long, string>();
        private readonly HashSet<string> _waveSeenIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _waveSeenLock = new object();
        private readonly List<string> _waveSeenOrder = new List<string>();
        private List<Track> _waveCache;
        private int _waveCacheIndex = -1;
        private string _waveCacheStation = "user:onyourwave";
        private string _waveStation = "user:onyourwave";
        private const int WaveSeenLimit = 500;
        private SplitContainer _rootSplit;
        private bool _waveFetchBusy;
        private const int DwmwaUseImmersiveDarkMode = 20;

        private const int WmHotkey = 0x0312;
        private const int WmAppCommand = 0x0319;
        private const int FAppCommandMask = 0xF000;
        private const int VkMediaPlayPause = 0xB3;
        private const int VkMediaNextTrack = 0xB0;
        private const int VkMediaPrevTrack = 0xB1;
        private const uint ModNoRepeat = 0x4000;
        private long _lastMediaCmdTicks;

        // боковые кнопки мыши: 1 = назад, 2 = вперёд (значение в HIGHWORD wParam)
        private const int WmXButtonDown = 0x020B;
        private const int WmXButtonDblClk = 0x0200;
        private const int XButtonBack = 1;
        private const int XButtonForward = 2;
        // APPCOMMAND_BROWSERBACKWARD / APPCOMMAND_BROWSERFORWARD
        private const int AppCommandBrowserBack = 1;
        private const int AppCommandBrowserForward = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        // GetFocus: HWND контрола с реальным фокусом в текущем потоке (для проверки ввода).
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // LVM_GETTOPINDEX: чистый native-запрос верхней видимой строки
        private const int LVM_FIRST = 0x1000;
        private const int LVM_GETTOPINDEX = LVM_FIRST + 39;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, int lParam);

        public MainForm()
        {
            WheelRouter.Install();
            Text = "YaMus — Яндекс Музыка";
            Icon = LoadAppIcon();
            Width = 1100;
            Height = 650;
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = YmPalette.BgMain;
            Font = new Font("Segoe UI", 9F);
            KeyPreview = true;
            KeyDown += FormOnKeyDown;
            TryDarkTitleBar();

            // На XP..8.1 (major < 10) системный store не знает современных корней, поэтому
            // построение цепочки там ослабляем; hostname и срок всё равно проверяются всегда.
            var validator = new CertificateValidator(allowUntrusted: IsLegacyTrustOs());
            _http = new HttpClient(validator, timeoutMs: 60000);
            _rest = new RestClient(_http);
            _api = new YandexApi(_rest);
            MusicQualityCatalog.Current = MusicQualityCatalog.Parse(TokenStorage.ReadAudioQuality());
            _downloader = new TrackDownloader(_rest, _http);
            _player = new Player();
            // Bluetooth/смена звукового устройства: кнопка Play и Next «зависали», потому что
            // воспроизведение молча умирало. Прерываем трек и запускаем его заново.
            _player.DeviceLost += OnPlayerDeviceLost;
            _coverLoader = new Core.Media.CoverLoader(_http);

            BuildLayout();
            Shown += (s, e) =>
            {
                try { if (_trackList.IsHandleCreated) ShowScrollBar(_trackList.Handle, 0, false); }
                catch { }
                RegisterMediaHotkeys();
                FitSidebarWidth();
                FillHeaderWidths();
                Startup();
                StartUiProbe();
            };
            FormClosing += OnFormClosing;
            // maximize/restore «ломает» список треков — чиним при смене режима окна
            _lastWindowState = WindowState;
            Resize += OnFormResize;
        }

        /// <summary>Смена режима окна (обычный → maximize → обратно).</summary>
        private void OnFormResize(object sender, EventArgs e)
        {
            if (WindowState == _lastWindowState) return;
            _lastWindowState = WindowState;
            RepairListAfterWindowModeChange();
        }

        /// <summary>После maximize/restore список треков мог «сломаться»: нативный диапазон
        /// прокрутки ещё от старого размера, оверлей скроллбара показывает чужую позицию,
        /// а строки OwnerDraw-ListView остаются с прошлого кадра. Возвращаем верхнюю строку
        /// на место, прячем горизонтальную полосу, пересчитываем колонки и скроллбар.</summary>
        private void RepairListAfterWindowModeChange()
        {
            if (_trackList == null || !_trackList.IsHandleCreated) return;

            int top = 0;
            try { top = SendMessage(_trackList.Handle, LVM_GETTOPINDEX, IntPtr.Zero, 0); }
            catch { }
            if (top < 0) top = 0;

            try { ShowScrollBar(_trackList.Handle, 0 /* SB_HORZ */, false); }
            catch { }

            // FillHeaderWidths меняет ширину последней колонки и может сбросить
            // видимый диапазон — поэтому позицию возвращаем уже после него
            FillHeaderWidths();
            try { if (top > 0 && top < _trackList.Items.Count) _trackList.TopItem = _trackList.Items[top]; }
            catch { }

            if (_trackScroll != null) _trackScroll.Sync();
            if (_navScroll != null) _navScroll.Sync();
            _trackList.Invalidate();
            Diag.Log("LIST repair after window mode=" + WindowState + " top=" + top);
        }

        private static bool IsLegacyTrustOs()
        {
            // На XP/7/8 хранилище корней часто устарело, и корректная цепочка GlobalSign там
            // не строится — жёсткая проверка цепочки ломала бы связь на этих ОС. Поэтому
            // цепочку требуем только на Win10+; hostname и срок проверяются всегда.
            try { return Environment.OSVersion.Version.Major < 10; }
            catch { return true; }
        }

        /// <summary>Иконка приложения из ресурса (для окна и панели задач). Падает обратно
        /// на извлечение из exe, если ресурс недоступен.</summary>
        internal static Icon LoadAppIcon()
        {
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("YaMus.YaMus.ico"))
                {
                    if (s != null) return new Icon(s, new Size(32, 32));
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            return null;
        }

        private void StartUiProbe()
        {
            var probe = new System.Threading.Thread(() =>
            {
                while (!IsDisposed)
                {
                    System.Threading.Thread.Sleep(300);
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Invoke(new MethodInvoker(() => { }));
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 200)
                        {
                            Diag.LogStall("ui-pump", sw.ElapsedMilliseconds);
                        }
                    }
                    catch { }
                }
            });
            probe.IsBackground = true;
            probe.Start();
        }

        private void BuildLayout()
        {
            // ==== корень: левая навигация + правая область ====
            var root = new SplitContainer();
            _rootSplit = root;
            root.Dock = DockStyle.Fill;
            root.SplitterDistance = 420;
            root.FixedPanel = FixedPanel.Panel1;
            root.IsSplitterFixed = true;
            root.SplitterWidth = 1;
            root.BackColor = YmPalette.Line;
            root.Panel1.BackColor = YmPalette.BgSide;
            root.Panel2.BackColor = YmPalette.BgMain;

            // ==== левая панель: шапка (назад/вперёд + логотип) + меню ====
            var sideHeader = new Panel();
            sideHeader.Dock = DockStyle.Top;
            sideHeader.Height = 42;
            sideHeader.BackColor = YmPalette.BgSide;

            // кнопки истории добавляются последними: docking обрабатывается
            // с конца коллекции, поэтому они занимают левый край первыми,
            // а логотип (Dock=Fill) получает остаток
            _navBackButton = IconButton(UiGlyphs.NavBack, 30, "Назад", 12F);
            _navBackButton.Click += (s, e) => GoBack();
            _navForwardButton = IconButton(UiGlyphs.NavForward, 30, "Вперёд", 12F);
            _navForwardButton.Click += (s, e) => GoForward();

            var navBtns = new FlowLayoutPanel();
            navBtns.Dock = DockStyle.Left;
            navBtns.Width = 88;
            navBtns.FlowDirection = FlowDirection.LeftToRight;
            navBtns.WrapContents = false;
            navBtns.BackColor = Color.Transparent;
            navBtns.Padding = new Padding(12, 6, 0, 0);
            navBtns.Controls.Add(_navBackButton);
            navBtns.Controls.Add(_navForwardButton);

            var logo = new Label();
            logo.Text = "Я.Музыка";
            logo.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            logo.ForeColor = YmPalette.TextMain;
            logo.Dock = DockStyle.Fill;
            logo.TextAlign = ContentAlignment.MiddleLeft;
            logo.Padding = new Padding(6, 0, 0, 0);

            sideHeader.Controls.Add(logo);
            sideHeader.Controls.Add(navBtns);
            UpdateNavButtons();

            _nav = new TreeView();
            _nav.Dock = DockStyle.Fill;
            _nav.HideSelection = false;
            _nav.FullRowSelect = true;
            _nav.ShowLines = false;
            _nav.ShowPlusMinus = false;
            _nav.ItemHeight = 34;
            _nav.BackColor = YmPalette.BgSide;
            _nav.ForeColor = YmPalette.TextMain;
            _nav.BorderStyle = BorderStyle.None;
            _nav.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _nav.DrawNode += NavOnDrawNode;
            _nav.AfterSelect += NavOnAfterSelect;
            _nav.MouseClick += NavOnMouseClick;
            NativeTheme.EnableDarkScrollbars(_nav);

            root.Panel1.Controls.Add(_nav);
            root.Panel1.Controls.Add(sideHeader);
            _navScroll = new YaMusScrollBar();
            _navScroll.Attach(_nav);

            // ==== правая панель: список + карточки + плеер ====
            var right = new Panel();
            right.Dock = DockStyle.Fill;
            right.BackColor = YmPalette.BgMain;

            _trackList = new DarkListView();
            _trackList.Dock = DockStyle.Fill;
            _trackList.View = View.Details;
            _trackList.FullRowSelect = true;
            _trackList.GridLines = false;
            _trackList.MultiSelect = false;
            _trackList.HideSelection = false;
            _trackList.BorderStyle = BorderStyle.None;
            _trackList.BackColor = YmPalette.BgMain;
            _trackList.ForeColor = YmPalette.TextMain;
            _trackList.OwnerDraw = true;
            _trackList.DrawColumnHeader += TrackListOnDrawColumnHeader;
            _trackList.DrawItem += TrackListOnDrawItem;
            _trackList.DrawSubItem += TrackListOnDrawSubItem;
            _trackList.MouseMove += TrackListOnMouseMove;
            _trackList.MouseLeave += TrackListOnMouseLeave;
            _trackList.MouseUp += TrackListOnMouseUp;
            // ВАЖНО: НЕ включать DoubleBuffered для виртуального ListView через SetStyle —
            // на этой платформе (WinForms 4.0) строки перестают отрисовываться вовсе.
            // Раньше здесь была рефлексия Control.DoubleBuffered = true — убрана.

            _covers = new ImageList();
            _covers.ColorDepth = ColorDepth.Depth32Bit;
            _covers.ImageSize = new Size(40, 40);
            _trackList.SmallImageList = _covers; // задаёт высоту строки под обложку; обложки рисуем вручную
            _trackList.DoubleClick += TrackListOnDoubleClick;
            _trackList.Columns.Add("№", 36);
            _trackList.Columns.Add("ТРЕК", 240);
            _trackList.Columns.Add("ИСПОЛНИТЕЛЬ", 130);
            _trackList.Columns.Add("АЛЬБОМ", 110);
            _trackList.Columns.Add("ДЛИТ.", 64);
            // последняя колонка — мусорная: забирает остаток ширины, чтобы шапка
            // не отдавала белый кусок правее последнего заголовка
            _trackList.Columns.Add("", 8);
            _trackList.Resize += (s, e) => FillHeaderWidths();
            // при переключении списков ListView сам меняет состояние скроллбаров
            // (появляется/исчезает вертикальная полоса) — пересчитываем ширины колонок,
            // иначе мигает горизонтальная полоса
            _trackList.ScrollStateChanged += FillHeaderWidths;

            var listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.BackColor = YmPalette.BgMain;

            var listTop = new Panel();
            listTop.Dock = DockStyle.Top;
            listTop.Height = 60;
            listTop.BackColor = YmPalette.BgMain;

            var listTitle = new Label();
            listTitle.Text = "";
            listTitle.Dock = DockStyle.Fill;
            listTitle.Name = "listTitle";
            listTitle.TextAlign = ContentAlignment.MiddleCenter;
            listTitle.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            listTitle.ForeColor = YmPalette.TextMain;

            _moreButton = new Button();
            _moreButton.Text = "Ещё";
            _moreButton.Width = 72;
            _moreButton.Height = 24;
            _moreButton.Visible = false;
            _moreButton.FlatStyle = FlatStyle.Flat;
            _moreButton.FlatAppearance.BorderSize = 0;
            _moreButton.BackColor = YmPalette.Accent;
            _moreButton.ForeColor = YmPalette.TextMain;
            _moreButton.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _moreButton.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            _moreButton.Click += (s, e) => FetchWaveMore(false);

            _refreshButton = new Button();
            _refreshButton.Text = "Обновить";
            _refreshButton.Width = 88;
            _refreshButton.Height = 24;
            _refreshButton.Visible = false;
            _refreshButton.FlatStyle = FlatStyle.Flat;
            _refreshButton.FlatAppearance.BorderSize = 0;
            _refreshButton.BackColor = YmPalette.Accent;
            _refreshButton.ForeColor = YmPalette.TextMain;
            _refreshButton.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _refreshButton.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            _refreshButton.Click += (s, e) => RefreshWave();

            _backButton = new Button();
            _backButton.Text = "Вернуть волну";
            _backButton.Width = 112;
            _backButton.Height = 24;
            _backButton.Visible = false;
            _backButton.FlatStyle = FlatStyle.Flat;
            _backButton.FlatAppearance.BorderSize = 0;
            _backButton.BackColor = YmPalette.BgCard;
            _backButton.ForeColor = YmPalette.TextMain;
            _backButton.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _backButton.FlatAppearance.MouseOverBackColor = YmPalette.BgHover;
            _backButton.Click += (s, e) => ResetToPersonalWave();

            listTop.Resize += (s, e) =>
            {
                _moreButton.Top = (listTop.ClientSize.Height - _moreButton.Height) / 2;
                _moreButton.Left = listTop.ClientSize.Width - _moreButton.Width - 10;
                _refreshButton.Top = (listTop.ClientSize.Height - _refreshButton.Height) / 2;
                _refreshButton.Left = _moreButton.Left - _refreshButton.Width - 6;
                _backButton.Top = (listTop.ClientSize.Height - _backButton.Height) / 2;
                _backButton.Left = _refreshButton.Left - _backButton.Width - 6;
            };

            listTop.Controls.Add(listTitle);
            listTop.Controls.Add(_moreButton);
            listTop.Controls.Add(_refreshButton);
            listTop.Controls.Add(_backButton);
            _moreButton.BringToFront();
            _refreshButton.BringToFront();
            _backButton.BringToFront();
            _moreButton.Top = (listTop.Height - _moreButton.Height) / 2;
            _moreButton.Left = listTop.Width - _moreButton.Width - 10;
            _refreshButton.Top = (listTop.Height - _refreshButton.Height) / 2;
            _refreshButton.Left = _moreButton.Left - _refreshButton.Width - 6;
            _backButton.Top = (listTop.Height - _backButton.Height) / 2;
            _backButton.Left = _refreshButton.Left - _backButton.Width - 6;

            listHost.Controls.Add(_trackList);
            listHost.Controls.Add(listTop);
            _trackScroll = new YaMusScrollBar();
            _trackScroll.Attach(_trackList);

            // страница исполнителя (скрыта по умолчанию; включается из NavigateToArtist)
            _artistPage = new ArtistPagePanel(
                id => _likedIds != null && _likedIds.Contains(id));
            _artistPage.Dock = DockStyle.Fill;
            _artistPage.Visible = false;
            _artistPage.TrackActivated += ArtistTrackActivated;
            _artistPage.LikeRequested += (t) => ApplyLike(t, t != null && !_likedIds.Contains(t.Id));
            _artistPage.AlbumClicked += OnArtistAlbumClicked;
            _artistPage.TrackContextRequested += TrackContextMenuRequested;
            _artistPage.AllTracksToggled += ArtistAllTracksToggled;
            listHost.Controls.Add(_artistPage);
            _artistScroll = new YaMusScrollBar();
            _artistScroll.Attach(_artistPage.ScrollFlow);

            _homePage = new HomePagePanel();
            _homePage.Dock = DockStyle.Fill;
            _homePage.Visible = true;
            listHost.Controls.Add(_homePage);
            _trackList.Visible = false;

            _settingsPage = new SettingsPanel();
            _settingsPage.Dock = DockStyle.Fill;
            _settingsPage.Visible = false;
            _settingsPage.LogoutRequested += Logout;
            listHost.Controls.Add(_settingsPage);
            _settingsScroll = new YaMusScrollBar();
            _settingsScroll.Attach(_settingsPage.ScrollHost);

            // страница поиска (скрыта по умолчанию; включается из сайдбара)
            _searchPage = new SearchPagePanel(
                id => _likedIds != null && _likedIds.Contains(id));
            _searchPage.Dock = DockStyle.Fill;
            _searchPage.Visible = false;
            _searchPage.TrackActivated += SearchTrackActivated;
            _searchPage.LikeRequested += (t) => ApplyLike(t, t != null && !_likedIds.Contains(t.Id));
            _searchPage.AlbumClicked += OnArtistAlbumClicked;
            _searchPage.ArtistClicked += NavigateToArtist;
            _searchPage.SearchRequested += OnSearchRequested;
            _searchPage.TrackContextRequested += TrackContextMenuRequested;
            listHost.Controls.Add(_searchPage);
            _searchScroll = new YaMusScrollBar();
            _searchScroll.Attach(_searchPage.ScrollFlow);
            _searchPage.ShowIdle();

            // ==== плеер-бар ====
            var bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.BackColor = YmPalette.BgSide;

            var tr = new TableLayoutPanel();
            tr.Dock = DockStyle.Fill;
            tr.ColumnCount = 4;
            tr.RowCount = 2;
            tr.BackColor = YmPalette.BgSide;
            tr.Padding = new Padding(12, 6, 14, 5);
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            tr.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            tr.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // обложка
            _coverBox = new CoverBox();
            _coverBox.Dock = DockStyle.Fill;
            _coverBox.BackColor = YmPalette.BgCard;
            _coverBox.Image = BuildCoverPlaceholder();
            _coverPlaceholder = _coverBox.Image;
            _coverBox.Click += (s, e) => OpenNowPlaying();
            // у обложки нет подсказки: режим открывается кликом, а ToolTip перехватывал бы мышь
            _coverBox.Cursor = Cursors.Hand;
            tr.Controls.Add(_coverBox, 0, 0);

            // заголовок
            var nowHost = new Panel();
            nowHost.Dock = DockStyle.Fill;
            nowHost.BackColor = YmPalette.BgSide;
            nowHost.Padding = new Padding(12, 3, 6, 2);

            _nowTitleHost = new Panel();
            _nowTitleHost.Dock = DockStyle.Fill;
            _nowTitleHost.BackColor = YmPalette.BgSide;
            _nowTitleHost.Resize += (s, e) => LayoutNowTitleRow();

            _nowTitle = new LinkLabel();
            _nowTitle.Text = "";
            SetNowLinkText(_nowTitle, "Ничего не играет");
            _nowTitle.AutoSize = true;
            _nowTitle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nowTitle.LinkColor = YmPalette.TextMain;
            _nowTitle.ActiveLinkColor = YmPalette.TextMain;
            _nowTitle.VisitedLinkColor = YmPalette.TextMain;
            _nowTitle.LinkBehavior = LinkBehavior.HoverUnderline;
            _nowTitle.TextAlign = ContentAlignment.MiddleLeft;
            _nowTitle.BackColor = YmPalette.BgSide;
            _nowTitle.Click += NowTitleClicked;

            _nowVersion = new Label();
            _nowVersion.Text = "";
            _nowVersion.AutoSize = true;
            _nowVersion.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nowVersion.ForeColor = YmPalette.TextFaint;
            _nowVersion.TextAlign = ContentAlignment.MiddleLeft;
            _nowVersion.BackColor = YmPalette.BgSide;

            _nowTitleHost.Controls.Add(_nowTitle);
            _nowTitleHost.Controls.Add(_nowVersion);

            _nowSub = new LinkLabel();
            _nowSub.Text = "";
            SetNowLinkText(_nowSub, "");
            _nowSub.Dock = DockStyle.Bottom;
            _nowSub.AutoSize = false;
            _nowSub.Height = 16;
            _nowSub.Font = new Font("Segoe UI", 8F);
            _nowSub.LinkColor = YmPalette.TextSub;
            _nowSub.ActiveLinkColor = YmPalette.TextMain;
            _nowSub.VisitedLinkColor = YmPalette.TextSub;
            _nowSub.LinkBehavior = LinkBehavior.HoverUnderline;
            _nowSub.TextAlign = ContentAlignment.MiddleLeft;
            _nowSub.Click += NowSubClicked;

            nowHost.Controls.Add(_nowTitleHost);
            nowHost.Controls.Add(_nowSub);
            tr.Controls.Add(nowHost, 1, 0);

            // кнопки управления
            var ctl = new FlowLayoutPanel();
            ctl.Dock = DockStyle.Fill;
            ctl.BackColor = YmPalette.BgSide;
            ctl.FlowDirection = FlowDirection.LeftToRight;
            ctl.WrapContents = false;

            _prevButton = IconButton(UiGlyphs.Previous, 34, "Назад", 15F);
            _prevButton.Click += (s, e) => Prev();
            _playButton = IconPlayButton();
            _playButton.Click += (s, e) => PlayPause();
            _nextButton = IconButton(UiGlyphs.Next, 34, "Вперёд", 15F);
            _nextButton.Click += (s, e) => Next();
            ctl.Controls.Add(_prevButton);
            ctl.Controls.Add(_playButton);
            ctl.Controls.Add(_nextButton);
            AlignPlayerButtons(ctl, 34);
            tr.Controls.Add(ctl, 2, 0);

            var side = new FlowLayoutPanel();
            side.Dock = DockStyle.Fill;
            side.BackColor = YmPalette.BgSide;
            side.FlowDirection = FlowDirection.RightToLeft;
            side.WrapContents = false;

            _likeButton = IconButton(UiGlyphs.Unlike, 30, "Мне нравится (F)", 13F);
            _likeButton.Click += (s, e) => ToggleLike();
            _repeatButton = IconRepeatButton();
            _repeatButton.Click += (s, e) => ToggleRepeat();
            _shuffleButton = IconButton(UiGlyphs.Shuffle, 30, "Перемешать", 12F);
            _shuffleButton.Click += (s, e) => ToggleShuffle();
            side.Controls.Add(_likeButton);
            side.Controls.Add(_repeatButton);
            side.Controls.Add(_shuffleButton);
            AlignPlayerButtons(side, 30);
            tr.Controls.Add(side, 3, 0);

            // полоса прогресса + время
            _seekBar = new SeekBar();
            _seekBar.Dock = DockStyle.Fill;
            _seekBar.SeekTo += seconds => { _player.Seek(TimeSpan.FromSeconds(seconds)); };
            tr.Controls.Add(_seekBar, 0, 1);
            tr.SetColumnSpan(_seekBar, 3);
            tr.SetRowSpan(_seekBar, 1);

            _timeLabel = new Label();
            _timeLabel.Text = "0:00 / 0:00";
            _timeLabel.Dock = DockStyle.Fill;
            _timeLabel.AutoSize = false;
            _timeLabel.TextAlign = ContentAlignment.MiddleRight;
            _timeLabel.Font = new Font("Segoe UI", 8F);
            _timeLabel.ForeColor = YmPalette.TextSub;
            tr.Controls.Add(_timeLabel, 3, 1);

            bar.Controls.Add(tr);
            bar.Height = 72;
            bar.Dock = DockStyle.Bottom;

            // порядок важен: bar добавляется ПОСЛЕДНИМ (выше по z-индексу) — докируется
            // к низу первым, списку достаётся оставшаяся область без наложения
            right.Controls.Add(listHost);
            right.Controls.Add(bar);

            root.Panel2.Controls.Add(right);
            Controls.Add(root);

            // ==== статус-строка ====
            _status = new StatusStrip();
            _status.BackColor = YmPalette.BgSide;
            _status.ForeColor = YmPalette.TextSub;
            _status.SizingGrip = false;
            _statusLabel = new ToolStripStatusLabel("Готов.");
            _statusLabel.ForeColor = YmPalette.TextSub;
            _statusLabel.Font = new Font("Segoe UI", 8F);
            _status.Items.Add(_statusLabel);
            Controls.Add(_status);
            _status.Dock = DockStyle.Bottom;

            // ==== замещающий режим «сейчас играет» (поверх всего окна) ====
            _nowPlaying = new NowPlayingOverlay();
            Controls.Add(_nowPlaying);
            _nowPlaying.CloseRequested += CloseNowPlaying;
            _nowPlaying.PlayPauseRequested += PlayPause;
            _nowPlaying.PrevRequested += Prev;
            _nowPlaying.NextRequested += Next;
            _nowPlaying.LikeRequested += ToggleLike;
            _nowPlaying.ShuffleRequested += ToggleShuffle;
            _nowPlaying.RepeatRequested += ToggleRepeat;

            // ==== таймер прогресса ====
            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 250;
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();

            // ==== контекстное меню трека ====
            _trackMenu.BackColor = YmPalette.BgSide;
            _trackMenu.ForeColor = YmPalette.TextMain;
            _trackMenu.Renderer = new DarkMenuRenderer();

            // смена темы в настройках: перекрашиваем контрольное дерево
            ThemeManager.Changed += OnThemeChanged;
        }

        private void OnThemeChanged(Theme from, Theme to)
        {
            ThemeManager.RefitControlTree(this, from);
            if (_trackList != null) _trackList.Invalidate();
            if (_nav != null) _nav.Invalidate();
            // оверлей «сейчас играет» красит фон цветом обложки, а не палитрой темы —
            // возвращаем его фирменные цвета, иначе он останется со старой темой
            if (_nowPlaying != null && _nowPlaying.Visible) _nowPlaying.RestorePalette();
            if (_nowPlaying != null) _nowPlaying.BringToFront();
        }

        // ==================== замещающий режим «сейчас играет» ====================

        private void OpenNowPlaying()
        {
            if (_nowPlaying == null) return;
            Track track = _currentTrack;
            if (track == null)
            {
                SetStatus("Сейчас ничего не играет");
                return;
            }

            string title = track.Title;
            if (!string.IsNullOrEmpty(track.Version) && track.Version != "original")
            {
                title = title + " (" + track.Version + ")";
            }

            // сначала показываем, потом синхронизируем: SyncNowPlayingState работает
            // только по видимому оверлею, иначе состояние кнопок осталось бы прежним
            _nowPlaying.Visible = true;
            _nowPlaying.BringToFront();
            _nowPlaying.ShowTrack(TrackCoverKey(track), title, track.ArtistsText,
                                  (int)track.Duration.TotalSeconds, _coverLoader);
            _nowPlaying.SeekRequested -= OnOverlaySeek;
            _nowPlaying.SeekRequested += OnOverlaySeek;
            SyncNowPlayingState();
            _nowPlaying.Focus();
        }

        private void OnOverlaySeek(int seconds)
        {
            try { _player.Seek(TimeSpan.FromSeconds(seconds)); }
            catch { }
        }

        private void CloseNowPlaying()
        {
            if (_nowPlaying == null) return;
            _nowPlaying.Visible = false;
            // возвращаем фокус тому, что было под оверлеем
            try { _trackList.Focus(); } catch { }
        }

        /// <summary>Держит кнопки оверлея в том же состоянии, что и основной плеер.</summary>
        private void SyncNowPlayingState()
        {
            if (_nowPlaying == null) return;
            Track track = _currentTrack;
            bool liked = track != null && !string.IsNullOrEmpty(track.Id) && _likedIds.Contains(track.Id);
            bool playing = _player.IsPlaying || _playbackIntended;
            _nowPlaying.SyncState(playing, liked, _shuffle, _repeat, _repeatOne);
        }

        private void TryDarkTitleBar()
        {
            try
            {
                int value = 1;
                DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            }
            catch { }
        }

        /// <summary>Состояние кнопки истории. Когда шага нет, кнопку не прячем и не
        /// отключаем (неактивный Button WinForms перекрашивает текст системным серым),
        /// а держим глиф на месте и просто приглушаем его цвет — раскладка не прыгает.</summary>
        private static void SetNavButton(GlyphButton b, string glyph, bool on)
        {
            b.Glyph = glyph;
            b.Enabled = true;
            b.ForeColor = on ? YmPalette.TextMain : YmPalette.TextFaint;
            // рамку рисует сам GlyphButton (FlatAppearance на нём не действует,
            // потому что фон и рамка идут из OnPaint)
            b.BorderSize = on ? 1 : 0;
            b.BorderColor = YmPalette.TextFaint;
        }

        private static GlyphButton IconButton(string glyph, int size, string tip)
        {
            return IconButton(glyph, size, tip, 10F);
        }

        /// <summary>Кнопка-иконка. Рисуем глиф сами (GlyphButton), а не через Button:
        /// WinForms рисует текст кнопки сам и обрезает глиф по краям — двойной
        /// треугольник ◀◀ выходил наполовину, а у ‹/› срезались кончики. GlyphButton
        /// рисует глиф через TextRenderer в ClientRectangle, поэтому он целый.
        /// fontSize задаётся отдельно: символы вроде ⇄/↻/♡ заметно мельче
        /// треугольников, в общий размер 10pt они читаются плохо.</summary>
        private static GlyphButton IconButton(string glyph, int size, string tip, float fontSize)
        {
            var b = new GlyphButton();
            b.Glyph = glyph;
            b.BaseFill = YmPalette.BgSide;
            b.HoverFill = YmPalette.BgHover;
            b.PressFill = YmPalette.BgHover;
            b.BackColor = YmPalette.BgSide;
            b.ForeColor = YmPalette.TextMain;
            b.Margin = new Padding(1, 0, 1, 0);
            // кнопка растягивается под глиф: ◀◀ при 15pt — 37px и в 34px срезался
            Tuple<Font, Size> fit = UiGlyphs.FitGlyphFont(glyph, size, fontSize, FontStyle.Bold);
            b.Font = fit.Item1;
            b.Width = Math.Max(size, fit.Item2.Width);
            b.Height = Math.Max(size, fit.Item2.Height);
            b.TabStop = false;
            b.Tag = tip;
            return b;
        }

        private static MirrorGlyphButton IconRepeatButton()
        {
            var b = new MirrorGlyphButton();
            b.Glyph = UiGlyphs.Repeat;
            b.Width = 30;
            b.Height = 30;
            b.Margin = new Padding(1, 0, 1, 0);
            b.BackColor = YmPalette.BgSide;
            b.FlatAppearance.MouseOverBackColor = YmPalette.BgHover;
            b.ForeColor = YmPalette.TextMain;
            Tuple<Font, Size> fitRep = UiGlyphs.FitGlyphFont(UiGlyphs.Repeat, 30, 13F, FontStyle.Bold);
            b.Font = fitRep.Item1;
            b.Width = Math.Max(30, fitRep.Item2.Width);
            b.Height = Math.Max(30, fitRep.Item2.Height);
            b.TabStop = false;
            b.Tag = "Повтор: выкл → все → один";
            return b;
        }

        private static RoundButton IconPlayButton()
        {
            var b = new RoundButton();
            b.Text = UiGlyphs.Play;
            b.Width = 34;
            b.Height = 34;
            b.Margin = new Padding(1, 0, 1, 0);
            b.BackColor = YmPalette.Accent;
            b.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            b.ForeColor = YmPalette.TextMain;
            Tuple<Font, Size> fitPlay = UiGlyphs.FitGlyphFont(UiGlyphs.Play, 34, 11F, FontStyle.Bold);
            b.Font = fitPlay.Item1;
            b.Width = Math.Max(34, fitPlay.Item2.Width);
            b.Height = Math.Max(34, fitPlay.Item2.Height);
            b.TabStop = false;
            b.Tag = "Играть/пауза (Пробел)";
            return b;
        }

        /// <summary>Все кнопки плеера одной высоты — центрируем их по вертикали ряда,
        /// чтобы верхние и нижние края совпадали независимо от высоты панели.</summary>
        private static void AlignPlayerButtons(FlowLayoutPanel host, int buttonHeight)
        {
            Action apply = () =>
            {
                int top = Math.Max(0, (host.ClientSize.Height - buttonHeight) / 2);
                host.Padding = new Padding(host.Padding.Left, top, host.Padding.Right, 0);
            };
            apply();
            host.Resize += (s, e) => apply();
        }

        private Image _coverPlaceholder;

        private static Image BuildCoverPlaceholder()
        {
            var bmp = new Bitmap(48, 48);
            using (var g = Graphics.FromImage(bmp))
            using (var f = UiGlyphs.GlyphFont(20F, FontStyle.Regular))
            {
                g.Clear(YmPalette.BgCard);
                g.DrawString(UiGlyphs.Note, f, new SolidBrush(YmPalette.TextFaint),
                             new Rectangle(0, 0, 48, 48),
                             new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            return bmp;
        }

        // ==================== lifecycle ====================

        private void Startup()
        {
            var token = TokenStorage.Load();
            if (token == null || string.IsNullOrEmpty(token.OAuthToken))
            {
                DoLogin();
            }
            else
            {
                _rest.Token = token.OAuthToken;
                Initialize();
            }
        }

        private void DoLogin()
        {
            using (var login = new LoginForm())
            {
                if (login.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(login.Token))
                {
                    var info = new TokenInfo { OAuthToken = login.Token };
                    info.DeviceId = TokenStorage.GetOrCreateDeviceId();
                    TokenStorage.Save(info);
                    _rest.Token = login.Token;
                    Initialize();
                }
                else
                {
                    Application.Exit();
                }
            }
        }

        private void Initialize()
        {
            SetStatus("Загрузка данных аккаунта…");
            DisableNavigation();

            Bg.Run(() =>
            {
                var account = _api.GetAccount();
                var playlists = _api.GetPlaylists(account.Uid);
                var liked = _api.GetLikeIds(account.Uid);
                return new Tuple<AccountInfo, List<PlaylistSummary>, List<LikeItem>>(account, playlists, liked);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Exception inner = t.Exception != null ? t.Exception.GetBaseException() : null;
                    Diag.Log("INIT fail: " + Describe(t.Exception));
                    _trackList.Items.Clear();
                    // неверный/отозванный токен: не «зависаем» в ошибке навсегда, а
                    // чистим аккаунт и возвращаем пользователя в окно входа
                    if (inner is UnauthorizedAccessException)
                    {
                        if (_authRetries++ < 2)
                        {
                            SetStatus("Токен недействителен — нужен вход заново.");
                            LogoutToLogin();
                            return;
                        }
                        SetStatus("Токен недействителен. Выйдите и войдите в настройках.");
                        return;
                    }
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    return;
                }
                _authRetries = 0;
                var data = t.Result;
                _uid = data.Item1.Uid;
                _likedIds = new HashSet<string>(data.Item3.Select(l => l.Id), StringComparer.Ordinal);
                string displayName = string.IsNullOrEmpty(data.Item1.PublicName) ? data.Item1.Login : data.Item1.PublicName;
                if (_homePage != null) _homePage.SetAccount(displayName, data.Item1.Email, data.Item1.AvatarUri, _coverLoader);
                PopulateNav(data.Item1, data.Item2);
                lock (_likesLock)
                {
                    if (_likesTask == null)
                    {
                        // фоновая подгрузка списка лайков: к моменту, когда пользователь
                        // откроет «Мне нравится», данные уже будут на руках
                        Task<List<Track>> prefetch = Bg.Run(() => _api.GetLikedTracks(_uid));
                        _likesTask = prefetch;
                        prefetch.ContinueWith(done =>
                        {
                            lock (_likesLock)
                            {
                                if (ReferenceEquals(_likesTask, prefetch)) _likesTask = null;
                                if (!done.IsFaulted && done.Result != null && done.Result.Count > 0) _likesCache = done.Result;
                            }
                        }, TaskScheduler.Default);
                    }
                }
                EnableNavigation();
                SetStatus("Аккаунт: " + displayName);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ==================== утилиты ====================

        private static string FormatTime(TimeSpan ts)
        {
            return ((int)ts.TotalMinutes).ToString("0") + ":" + ts.Seconds.ToString("00");
        }

        /// <summary>Фокус сейчас в текстовом поле (поиск): пробел/буквы там — это ввод
        /// символа, а не игровые команды. Без проверки печатать «space» в поиске
        /// ставило трек на паузу, а «f» — лайковал его.
        /// ВАЖНО: ActiveControl у формы — это верхний контейнер (SearchBox), а не
        /// вложенный TextBox, поэтому берём HWND с реальным фокусом через GetFocus.</summary>
        private bool IsTextEntryFocused()
        {
            try
            {
                IntPtr h = GetFocus();
                if (h == IntPtr.Zero) return false;
                Control c = Control.FromHandle(h);
                while (c != null)
                {
                    if (c is TextBoxBase || c is MaskedTextBox || c is NumericUpDown || c is ComboBox) return true;
                    c = c.Parent;
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>Горячие клавиши: Esc/пробел/F в оверлее, иначе пробел — пауза, F — лайк.
        /// Во время ввода в текстовом поле игровые клавиши не перехватываем.</summary>
        private void FormOnKeyDown(object sender, KeyEventArgs e)
        {
            bool typing = IsTextEntryFocused();

            // открыт оверлей «сейчас играет»: Esc сворачивает, пробел — пауза,
            // F — лайк, а прочие клавиши не должны уезжать в плеер/список под оверлеем
            if (_nowPlaying != null && _nowPlaying.Visible)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseNowPlaying();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (typing) return;   // ввод в текстовом поле поверх оверлея не трогаем
                if (e.KeyCode == Keys.Space)
                {
                    PlayPause();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode == Keys.F)
                {
                    ToggleLike();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            if (typing) return;

            if (e.KeyCode == Keys.Space)
            {
                PlayPause();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F)
            {
                ToggleLike();
                e.Handled = true;
            }
        }

        private readonly Dictionary<int, long> _msgWinTicks = new Dictionary<int, long>();
        private readonly System.Diagnostics.Stopwatch _msgWinWatch = System.Diagnostics.Stopwatch.StartNew();
        private int _msgWinCount;

        protected override void WndProc(ref Message m)
        {
            var wsw = System.Diagnostics.Stopwatch.StartNew();
            if (m.Msg == WmHotkey)
            {
                if (HandleMediaCommand(WmHotkey, m.WParam.ToInt32(), IntPtr.Zero))
                {
                    m.Result = IntPtr.Zero;
                    wsw.Stop();
                    if (wsw.ElapsedMilliseconds > 250) Diag.LogStall("msg 0x" + m.Msg.ToString("X4"), wsw.ElapsedMilliseconds);
                    return;
                }
            }
            else if (m.Msg == WmXButtonDown || m.Msg == WmXButtonDblClk)
            {
                int xb = (int)(((long)m.WParam >> 16) & 0xFFFFL);
                if (HandleNavCommand(xb))
                {
                    m.Result = IntPtr.Zero;
                    wsw.Stop();
                    if (wsw.ElapsedMilliseconds > 250) Diag.LogStall("msg 0x" + m.Msg.ToString("X4"), wsw.ElapsedMilliseconds);
                    return;
                }
            }
            else if (m.Msg == WmAppCommand)
            {
                int cmd = (int)(((long)m.LParam >> 16) & 0xFFFFL) & ~FAppCommandMask;
                // назад/вперёд браузера обрабатываем первыми: ниже whitelist
                // медиакоманд их отбрасывает
                if (HandleNavCommand(cmd))
                {
                    m.Result = new IntPtr(1);
                    wsw.Stop();
                    if (wsw.ElapsedMilliseconds > 250) Diag.LogStall("msg 0x" + m.Msg.ToString("X4"), wsw.ElapsedMilliseconds);
                    return;
                }
                if (HandleMediaCommand(WmAppCommand, 0, new IntPtr(cmd)))
                {
                    m.Result = new IntPtr(1);
                    wsw.Stop();
                    if (wsw.ElapsedMilliseconds > 250) Diag.LogStall("msg 0x" + m.Msg.ToString("X4"), wsw.ElapsedMilliseconds);
                    return;
                }
            }
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                wsw.Stop();
                if (wsw.ElapsedMilliseconds > 250) Diag.LogStall("msg 0x" + m.Msg.ToString("X4"), wsw.ElapsedMilliseconds);
                var ticks = wsw.ElapsedTicks;
                if (ticks > 0)
                {
                    long mt;
                    _msgWinTicks.TryGetValue(m.Msg, out mt);
                    _msgWinTicks[m.Msg] = mt + ticks;
                    _msgWinCount++;
                    if (_msgWinWatch.ElapsedMilliseconds >= 250)
                    {
                        long total = 0;
                        foreach (var kv in _msgWinTicks) total += kv.Value;
                        if (total > TimeSpan.TicksPerMillisecond * 200)
                        {
                            string parts = "";
                            foreach (var kv in _msgWinTicks)
                            {
                                parts += " 0x" + kv.Key.ToString("X4") + "=" + (kv.Value / TimeSpan.TicksPerMillisecond) + "ms";
                            }
                            Diag.Log("BATCH " + (total / TimeSpan.TicksPerMillisecond) + "ms over " + _msgWinCount + " msgs:" + parts);
                        }
                        _msgWinTicks.Clear();
                        _msgWinCount = 0;
                        _msgWinWatch.Restart();
                    }
                }
            }
        }

        private bool HandleMediaCommand(int kind, int id, IntPtr data)
        {
            int cmd = 0;
            if (kind == WmHotkey)
            {
                switch (id)
                {
                    case 1: cmd = 0x0E; break;
                    case 2: cmd = 0x0B; break;
                    case 3: cmd = 0x0C; break;
                    default: return false;
                }
            }
            else
            {
                cmd = data.ToInt32();
                switch (cmd)
                {
                    case 0x0B: case 0x0C: case 0x0D: case 0x0E: case 0x2E: case 0x2F: break;
                    default: return false;
                }
            }

            long now = Environment.TickCount;
            // мышь шлёт «назад» и как XButton1, и как APPCOMMAND_MEDIA_PREVTRACK:
            // без этой проверки страница возвращалась и следом трек с 0:00
            if ((cmd == 0x0B || cmd == 0x0C) && now - _lastNavCmdTicks < 500) return true;
            if (now - _lastMediaCmdTicks < 120) return true;
            _lastMediaCmdTicks = now;

            switch (cmd)
            {
                case 0x0B: Next(); break;
                case 0x0C: Prev(); break;
                case 0x0E: PlayPause(); break;
                case 0x2E: PlayOnly(); break;
                case 0x2F: PauseOnly(); break;
                case 0x0D: PauseOnly(); break;
            }
            return true;
        }

        private void RegisterMediaHotkeys()
        {
            try
            {
                RegisterHotKey(Handle, 1, ModNoRepeat, VkMediaPlayPause);
                RegisterHotKey(Handle, 2, ModNoRepeat, VkMediaNextTrack);
                RegisterHotKey(Handle, 3, ModNoRepeat, VkMediaPrevTrack);
            }
            catch { }
        }

        private void UnregisterMediaHotkeys()
        {
            try
            {
                UnregisterHotKey(Handle, 1);
                UnregisterHotKey(Handle, 2);
                UnregisterHotKey(Handle, 3);
            }
            catch { }
        }

        private void PlayOnly()
        {
            if (_player.IsPlaying) return;
            if (_player.IsActive)
            {
                _player.Resume();
                _playbackIntended = true;
                UpdatePlayButton();
            }
            else if (_playbackIntended)
            {
                return; // трек ещё грузится
            }
            else
            {
                if (_currentIndex >= 0 && _currentIndex < _tracks.Count) PlayTrack(_currentIndex);
                else if (_tracks.Count > 0) PlayTrack(0);
            }
        }

        private void PauseOnly()
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _playbackIntended = false;
                UpdatePlayButton();
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "неизвестная ошибка";
            return ex.GetBaseException().Message;
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text;
        }

        private void DisableNavigation()
        {
            _nav.Enabled = false;
            // список НЕ дизейблим: в отключённом состоянии WinForms красит его системно (белым)
        }

        private void EnableNavigation()
        {
            _nav.Enabled = true;
            _trackList.Enabled = true;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _tick.Stop();
            UnregisterMediaHotkeys();
            try { _player.Dispose(); }
            catch { }
            ClearPrefetch();
            lock (_tempFiles)
            {
                foreach (string f in _tempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch { }
                }
                _tempFiles.Clear();
            }
            lock (_trackCacheLock)
            {
                _trackFileCache.Clear();
                _trackFileOrder.Clear();
            }
        }
    }
}