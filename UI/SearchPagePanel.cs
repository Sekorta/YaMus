using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YaMus.Core.Media;
using YaMus.Core.Models;

namespace YaMus.UI
{
    /// <summary>Страница поиска: поле ввода и результаты секциями «Треки» / «Альбомы» /
    /// «Исполнители». Ряды и карточки — те же, что на странице исполнителя.</summary>
    internal sealed class SearchPagePanel : Panel
    {
        public event Action<Track, int> TrackActivated;
        public event Action<Track> LikeRequested;
        public event Action<Track, System.Drawing.Point> TrackContextRequested;
        public event Action<long> AlbumClicked;
        public event Action<long> ArtistClicked;
        /// <summary>Запрос поиска: текст + категория ("track" | "album" | "artist").</summary>
        public event Action<string, string> SearchRequested;

        private readonly Func<string, bool> _likedResolver;
        private readonly SearchBox _searchBox;
        private readonly FlowLayoutPanel _filters;
        private readonly FlowLayoutPanel _flow;
        private readonly List<TrackRowControl> _rows = new List<TrackRowControl>();
        private readonly List<MediaCardCell> _cells = new List<MediaCardCell>();
        private FlowLayoutPanel _albumsBox;
        private FlowLayoutPanel _artistsBox;
        private string _category = "track";

        public List<Track> Tracks { get; private set; }

        /// <summary>Внутренний скроллируемый контейнер — на него вешается ползунок прокрутки.</summary>
        public FlowLayoutPanel ScrollFlow
        {
            get { return _flow; }
        }

        public SearchPagePanel(Func<string, bool> likedResolver)
        {
            _likedResolver = likedResolver;
            BackColor = YmPalette.BgMain;

            _flow = new FlowLayoutPanel();
            _flow.Dock = DockStyle.Fill;
            _flow.BackColor = YmPalette.BgMain;
            _flow.FlowDirection = FlowDirection.TopDown;
            _flow.WrapContents = false;
            _flow.AutoScroll = true;
            _flow.Padding = new Padding(10, 6, 10, 12);
            _flow.Resize += (s, e) => Relayout();

            // шапка: сверху поле поиска, под ним переключатель категории
            var header = new TableLayoutPanel();
            header.Dock = DockStyle.Top;
            header.Height = 116;
            header.ColumnCount = 1;
            header.RowCount = 2;
            header.BackColor = YmPalette.BgMain;
            header.Padding = new Padding(14, 12, 22, 6);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            _searchBox = new SearchBox();
            _searchBox.Dock = DockStyle.Fill;
            _searchBox.Margin = new Padding(0);
            _searchBox.QueryChanged += q => RaiseSearchRequested();
            header.Controls.Add(_searchBox, 0, 0);

            _filters = new FlowLayoutPanel();
            _filters.Dock = DockStyle.Fill;
            _filters.BackColor = YmPalette.BgMain;
            _filters.FlowDirection = FlowDirection.LeftToRight;
            _filters.WrapContents = false;
            _filters.Margin = new Padding(0);
            _filters.Padding = new Padding(0, 8, 0, 0);
            _filters.Controls.Add(MakeFilterButton("Треки", "track"));
            _filters.Controls.Add(MakeFilterButton("Альбомы", "album"));
            _filters.Controls.Add(MakeFilterButton("Артисты", "artist"));
            header.Controls.Add(_filters, 0, 1);

            Controls.Add(_flow);
            Controls.Add(header);

            RefreshFilters();
            ThemeManager.Changed += (from, to) => RefreshFilters();
        }

        public string Query
        {
            get { return _searchBox != null ? _searchBox.Query : ""; }
        }

        public void FocusInput()
        {
            if (_searchBox != null) _searchBox.FocusInput();
        }

        /// <summary>Показывает подсказку для текущей категории (когда запрос пуст).</summary>
        public void ShowIdle()
        {
            ShowStatus(HintForCategory());
        }

        private string HintForCategory()
        {
            if (_category == "album") return "Введите запрос — найдём альбомы.";
            if (_category == "artist") return "Введите запрос — найдём артистов.";
            return "Введите запрос — найдём треки.";
        }

        private Button MakeFilterButton(string text, string type)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Width = 96;
            b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
            b.Margin = new Padding(0, 0, 8, 0);
            b.Tag = type;
            b.Click += (s, e) => SelectCategory(type);
            return b;
        }

        private void SelectCategory(string type)
        {
            _category = type;
            RefreshFilters();
            RaiseSearchRequested();
        }

        /// <summary>Активная категория — акцентная кнопка, остальные — нейтральные.</summary>
        private void RefreshFilters()
        {
            if (_filters == null) return;
            foreach (Control c in _filters.Controls)
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = Equals(b.Tag, _category);
                if (active)
                {
                    b.BackColor = YmPalette.Accent;
                    b.ForeColor = Color.White;
                    b.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
                }
                else
                {
                    b.BackColor = YmPalette.BgHover;
                    b.ForeColor = YmPalette.TextMain;
                    b.FlatAppearance.MouseOverBackColor = YmPalette.BgSel;
                }
            }
        }

        private void RaiseSearchRequested()
        {
            if (SearchRequested != null) SearchRequested(Query, _category);
        }

        /// <summary>Очищает результаты и показывает подсказку/ошибку.</summary>
        public void ShowStatus(string text)
        {
            _flow.SuspendLayout();
            ResetContent();
            Tracks = new List<Track>();
            if (!string.IsNullOrEmpty(text)) _flow.Controls.Add(HintLabel(text));
            _flow.ResumeLayout(true);
            Relayout();
        }

        /// <summary>Рисует результаты поиска: секции треков, альбомов и исполнителей.</summary>
        public void ShowResults(SearchResults r, CoverLoader loader, Func<int, bool> isCurrent)
        {
            _flow.SuspendLayout();
            ResetContent();

            Tracks = r != null && r.Tracks != null ? r.Tracks : new List<Track>();

            bool noAlbums = r == null || r.Albums == null || r.Albums.Count == 0;
            bool noArtists = r == null || r.Artists == null || r.Artists.Count == 0;
            if (Tracks.Count == 0 && noAlbums && noArtists)
            {
                _flow.Controls.Add(HintLabel("Ничего не найдено"));
                _flow.ResumeLayout(true);
                Relayout();
                return;
            }

            if (Tracks.Count > 0)
            {
                _flow.Controls.Add(SectionHeader("Треки"));
                for (int i = 0; i < Tracks.Count; i++)
                {
                    Track t = Tracks[i];
                    if (t == null) continue;
                    var row = new TrackRowControl(t, i, _likedResolver, isCurrent, loader);
                    int idx = i;
                    row.Activated += (s, e) => RaiseTrackActivated(Tracks[idx], idx);
                    row.LikeClicked += (s, e) => RaiseLikeRequested(Tracks[idx]);
                    row.ContextRequested += (s, pt) => RaiseTrackContextRequested(Tracks[idx], pt);
                    row.RowClicked += SelectOnlyRow;
                    _flow.Controls.Add(row);
                    _rows.Add(row);
                }
            }

            if (!noAlbums)
            {
                _flow.Controls.Add(SectionHeader("Альбомы"));
                _albumsBox = MakeGridBox();
                foreach (Album a in r.Albums)
                {
                    if (a == null || a.Id == 0) continue;
                    var cell = new MediaCardCell(a.Id, a.Title, AlbumSubtitle(a), a.CoverUri, loader);
                    cell.Clicked += RaiseAlbumClicked;
                    _albumsBox.Controls.Add(cell);
                    _cells.Add(cell);
                }
                _flow.Controls.Add(_albumsBox);
            }

            if (!noArtists)
            {
                _flow.Controls.Add(SectionHeader("Артисты"));
                _artistsBox = MakeGridBox();
                foreach (Artist ar in r.Artists)
                {
                    if (ar == null || ar.Id == 0) continue;
                    var cell = new MediaCardCell(ar.Id, ar.Name, "", ar.CoverUri, loader);
                    cell.Clicked += RaiseArtistClicked;
                    _artistsBox.Controls.Add(cell);
                    _cells.Add(cell);
                }
                _flow.Controls.Add(_artistsBox);
            }

            _flow.ResumeLayout(true);
            Relayout();
            ScheduleRelayout();
            foreach (var c in _cells) c.BeginCoverLoad();
            foreach (var row in _rows) row.BeginCoverLoad();
        }

/// <summary>Повторная раскладка после того, как внешний flow определит ширину и полосы
    /// прокрутки: при первом показе результатов они ещё не пересчитаны, и высота сетки
    /// расходится с числом переносок (низ списка обрезался).</summary>
        private void ScheduleRelayout()
        {
            if (!_flow.IsHandleCreated) return;
            _flow.BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || _flow.IsDisposed) return;
                Relayout();
                _flow.PerformLayout();
                _flow.Invalidate();
            });
        }

        public void RefreshRows()
        {
            foreach (var r in _rows) r.Invalidate();
        }

        public void RefreshLikes()
        {
            RefreshRows();
        }

        private void ResetContent()
        {
            foreach (var c in _cells) c.ReleaseImage();
            _flow.Controls.Clear();
            _rows.Clear();
            _cells.Clear();
            _albumsBox = null;
            _artistsBox = null;
        }

        private void RaiseTrackActivated(Track track, int index)
        {
            if (TrackActivated != null) TrackActivated(track, index);
        }

        /// <summary>Выделена одна строка: клик по новой снимает выделение с прежней,
        /// иначе подсветка «залипала» бы на всех ранее кликнутых.</summary>
        private void SelectOnlyRow(TrackRowControl row)
        {
            if (row == null) return;
            foreach (var r in _rows) r.IsSelected = (r == row);
        }

        private void RaiseLikeRequested(Track track)
        {
            if (LikeRequested != null) LikeRequested(track);
        }

        private void RaiseTrackContextRequested(Track track, Point screenPoint)
        {
            if (TrackContextRequested != null) TrackContextRequested(track, screenPoint);
        }

        private void RaiseAlbumClicked(long id)
        {
            if (AlbumClicked != null) AlbumClicked(id);
        }

        private void RaiseArtistClicked(long id)
        {
            if (ArtistClicked != null) ArtistClicked(id);
        }

        private static FlowLayoutPanel MakeGridBox()
        {
            var box = new FlowLayoutPanel();
            box.BackColor = YmPalette.BgMain;
            box.FlowDirection = FlowDirection.LeftToRight;
            box.WrapContents = true;
            box.AutoSize = false;
            box.Margin = new Padding(4, 0, 4, 8);
            // внешний flow может сузить коробку (например, при появлении полосы
            // прокрутки) — тогда пересчитываем карточки и высоту по факту ширины,
            // иначе карточки переносок не совпадут с высотой и низ обрежется
            box.Resize += (s, e) => LayoutGridChildren(box);
            return box;
        }

        private void Relayout()
        {
            int w = _flow.ClientSize.Width;
            if (w <= 0) return;
            // запас на горизонтальные отступы строк/коробки — иначе появляется
            // нативная горизонтальная полоса на пару пикселей
            int rowW = Math.Max(120, w - _flow.Padding.Horizontal - 10);

            foreach (var r in _rows)
            {
                r.Width = rowW;
                r.Margin = new Padding(2, 1, 2, 1);
            }

            LayoutGrid(_albumsBox, rowW);
            LayoutGrid(_artistsBox, rowW);
        }

        private static void LayoutGrid(FlowLayoutPanel box, int width)
        {
            if (box == null || box.Controls.Count == 0) return;
            box.Width = width;
            LayoutGridChildren(box);
        }

        /// <summary>Раскладка карточек строго по фактической ширине коробки: 3 в ряд,
        /// высота — по реальному числу переносок. Вызывается и при Resize коробки,
        /// поэтому не расходится с тем, как FlowLayoutPanel реально переносит карточки.</summary>
        private static void LayoutGridChildren(FlowLayoutPanel box)
        {
            if (box == null || box.Controls.Count == 0 || box.IsDisposed) return;
            int width = box.ClientSize.Width;
            if (width <= 0) return;

            const int gutter = 10;
            const int cols = 3;
            int cardW = Math.Max(120, (width - gutter * (cols + 1)) / cols);
            int cardH = cardW + 42;
            int perRow = Math.Max(1, (width + gutter) / (cardW + gutter));
            int rows = Math.Max(1, (int)Math.Ceiling(box.Controls.Count / (double)perRow));
            int newH = rows * (cardH + gutter) + 6;
            if (box.Height != newH) box.Height = newH;

            foreach (Control c in box.Controls)
            {
                c.Width = cardW;
                c.Height = cardH;
                c.Margin = new Padding(gutter / 2);
            }
        }

        private static Label SectionHeader(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Height = 34;
            l.Margin = new Padding(4, 8, 4, 2);
            l.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            l.ForeColor = YmPalette.TextMain;
            l.TextAlign = ContentAlignment.BottomLeft;
            l.TabStop = false;
            return l;
        }

        private static Label HintLabel(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(8, 14, 8, 2);
            l.Font = new Font("Segoe UI", 10F);
            l.ForeColor = YmPalette.TextFaint;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.TabStop = false;
            return l;
        }

        private static string AlbumSubtitle(Album a)
        {
            var parts = new List<string>();
            if (a.Year > 0) parts.Add(a.Year.ToString());
            if (a.TrackCount > 0) parts.Add(a.TrackCount + " треков");
            return string.Join(" · ", parts.ToArray());
        }
    }

    /// <summary>Поле поиска: скруглённая рамка в цвет темы, лупа, нативный
    /// плейсхолдер (cue banner) и debounce при вводе. Enter ищет сразу.</summary>
    internal sealed class SearchBox : Control
    {
        private const int EmSetCueBanner = 0x1501;
        private const int PadLeft = 38;

        private readonly TextBox _input;
        private readonly Timer _debounce;
        private bool _focused;

        public event Action<string> QueryChanged;

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = YmPalette.BgMain;
            Height = 40;

            _debounce = new Timer();
            _debounce.Interval = 350;
            _debounce.Tick += (s, e) => { _debounce.Stop(); RaiseQuery(); };

            _input = new TextBox();
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = YmPalette.BgMain;
            _input.ForeColor = YmPalette.TextMain;
            _input.Font = new Font("Segoe UI", 11F);
            _input.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); Invalidate(); };
            _input.KeyDown += InputOnKeyDown;
            _input.GotFocus += (s, e) => { _focused = true; Invalidate(); };
            _input.LostFocus += (s, e) => { _focused = false; Invalidate(); };
            _input.HandleCreated += (s, e) => SetCue();
            Controls.Add(_input);
        }

        public string Query
        {
            get { return _input.Text == null ? "" : _input.Text.Trim(); }
        }

        public void FocusInput()
        {
            try { _input.Focus(); } catch { }
        }

        private void InputOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _debounce.Stop();
                RaiseQuery();
            }
            else if (e.KeyCode == Keys.Escape && _input.Text.Length > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _input.Text = "";
            }
        }

        private void RaiseQuery()
        {
            if (QueryChanged != null) QueryChanged(Query);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutInput();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutInput();
        }

        private void LayoutInput()
        {
            if (_input == null) return;
            int h = _input.PreferredHeight;
            int top = Math.Max(2, (Height - h) / 2);
            _input.SetBounds(PadLeft, top, Math.Max(10, Width - PadLeft - 14), h);
        }

        private void SetCue()
        {
            try { SendMessage(_input.Handle, EmSetCueBanner, new IntPtr(1), "Поиск в Яндекс Музыке"); }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = RoundedRect(r, 8))
            {
                using (var bg = new SolidBrush(YmPalette.BgMain))
                {
                    g.FillPath(bg, path);
                }
                using (var pen = new Pen(_focused ? YmPalette.Accent : YmPalette.Line))
                {
                    g.DrawPath(pen, path);
                }
            }

            Color fg = _focused ? YmPalette.TextMain : YmPalette.TextFaint;
            using (var pen = new Pen(fg, 2f))
            {
                int cx = 20;
                int cy = Height / 2;
                int rad = 6;
                g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                g.DrawLine(pen, cx + rad - 1, cy + rad - 1, cx + rad + 3, cy + rad + 3);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _debounce != null) _debounce.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
