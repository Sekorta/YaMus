using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Diagnostics;
using YaMus.Core.Media;
using YaMus.Core.Models;

namespace YaMus.UI
{
    /// <summary>Страница исполнителя по образцу Яндекс Музыки: сверху блок «Популярные треки»,
    /// ниже компактная сетка «Альбомы». Вся страница скроллится целиком.</summary>
    internal sealed class ArtistPagePanel : Panel
    {
        public event Action<Track, int> TrackActivated;
        public event Action<Track> LikeRequested;
        public event Action<Track, Point> TrackContextRequested;
        public event Action<long> AlbumClicked;
        /// <summary>Клик по заголовку списка треков: показать все треки исполнителя
        /// (и обратно — популярные). Загрузкой занимается MainForm, здесь только сигнал.</summary>
        public event Action<long> AllTracksToggled;

        private readonly FlowLayoutPanel _flow;
        private readonly Func<string, bool> _likedResolver;
        private readonly List<TrackRowControl> _rows = new List<TrackRowControl>();
        private readonly List<MediaCardCell> _cells = new List<MediaCardCell>();
        private FlowLayoutPanel _albumsBox;
        private long _artistId;
        private CoverLoader _loader;
        private Func<int, bool> _isCurrent;
        private Label _tracksHeader;

        /// <summary>Внутренний скроллируемый контейнер — на него вешается ползунок прокрутки.</summary>
        public FlowLayoutPanel ScrollFlow
        {
            get { return _flow; }
        }

        public List<Track> Tracks { get; private set; }

        /// <summary>Сейчас показан полный список, а не популярные треки — по этому
        /// флагу заголовок работает как переключатель «Все треки» ↔ «Популярные треки».</summary>
        public bool ShowsAllTracks
        {
            get { return _tracksHeader != null && _tracksHeader.Text == "Все треки"; }
        }

        public ArtistPagePanel(Func<string, bool> likedResolver)
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
            _flow.Layout += OnFlowLayout;
            Controls.Add(_flow);
        }

        public void ShowArtist(long artistId, List<Track> tracks, List<Album> albums,
                            CoverLoader loader, Func<int, bool> isCurrent)
        {
            _artistId = artistId;
            _loader = loader;
            _isCurrent = isCurrent;
            ShowTracks(tracks, "Популярные треки", albums, true);
        }

        /// <summary>Перестроить страницу под новое состояние заголовка.
        /// showAlbums = false для полного списка треков: сетка альбомов там лишняя,
        /// каждый трек и так ведёт к своему альбому, и список уходит далеко вниз.</summary>
        public void ShowTracks(List<Track> tracks, string headerText, List<Album> albums,
                               bool showAlbums)
        {
            _flow.SuspendLayout();
            foreach (Control c in _flow.Controls)
            {
                var cell = c as MediaCardCell;
                if (cell != null) cell.ReleaseImage();
            }
            _flow.Controls.Clear();
            _rows.Clear();
            _cells.Clear();

            _tracksHeader = TracksHeader(headerText);
            _flow.Controls.Add(_tracksHeader);

            Tracks = tracks ?? new List<Track>();
            for (int i = 0; i < Tracks.Count; i++)
            {
                Track t = Tracks[i];
                if (t == null) continue;
                var row = new TrackRowControl(t, i, _likedResolver, _isCurrent, _loader);
                int idx = i;
                row.Activated += (s, e) => RaiseTrackActivated(Tracks[idx], idx);
                row.LikeClicked += (s, e) => RaiseLikeRequested(Tracks[idx]);
                row.ContextRequested += (s, pt) => RaiseTrackContextRequested(Tracks[idx], pt);
                row.RowClicked += SelectOnlyRow;
                _flow.Controls.Add(row);
                _rows.Add(row);
            }

            if (!showAlbums)
            {
                _albumsBox = null;
                _flow.ResumeLayout(true);
                Relayout();
                foreach (var r in _rows) r.BeginCoverLoad();
                return;
            }

            _flow.Controls.Add(SectionHeader("Альбомы"));

            _albumsBox = new FlowLayoutPanel();
            _albumsBox.BackColor = YmPalette.BgMain;
            _albumsBox.FlowDirection = FlowDirection.LeftToRight;
            _albumsBox.WrapContents = true;
            _albumsBox.AutoSize = false;
            _albumsBox.Margin = new Padding(4, 0, 4, 8);
            if (albums != null)
            {
                foreach (Album a in albums)
                {
                    if (a == null || a.Id == 0) continue;
                    var cell = new MediaCardCell(a.Id, a.Title, AlbumSubtitle(a), a.CoverUri, _loader);
                    cell.Clicked += RaiseAlbumClicked;
                    _albumsBox.Controls.Add(cell);
                    _cells.Add(cell);
                }
            }
            _flow.Controls.Add(_albumsBox);

            _flow.ResumeLayout(true);
            Relayout();
            foreach (var c in _cells) c.BeginCoverLoad();
            foreach (var r in _rows) r.BeginCoverLoad();
        }

        private void RaiseTrackActivated(Track track, int index)
        {
            if (TrackActivated != null) TrackActivated(track, index);
        }

        /// <summary>Выделена одна строка: клик по новой снимает выделение с прежней.</summary>
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

        /// <summary>Перерисовывает ряды: после изменения «мне нравится» или смены играющего трека.
        /// Строки — отдельные контроля, поэтому инвалидировать нужно каждую.</summary>
        public void RefreshLikes()
        {
            RefreshRows();
        }

        /// <summary>Перерисовывает все ряды «Популярные треки».</summary>
        public void RefreshRows()
        {
            foreach (var r in _rows) r.Invalidate();
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

        /// <summary>Заголовок списка треков — кликабельный: LinkLabel подчёркивает
        /// текст при наведении, а по клику мы показываем все треки исполнителя
        /// вместо популярных (и обратно). Внешне выглядит как обычный заголовок:
        /// цвет ссылки задан тем же, что и у Label.</summary>
        private Label TracksHeader(string text)
        {
            var l = new LinkLabel();
            l.Text = text;
            l.AutoSize = false;
            l.Height = 34;
            l.Width = Math.Max(220, TextRenderer.MeasureText(text, l.Font).Width + 8);
            l.Margin = new Padding(4, 8, 4, 2);
            l.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            l.BackColor = YmPalette.BgMain;
            l.LinkColor = YmPalette.TextMain;
            l.ActiveLinkColor = YmPalette.TextMain;
            l.VisitedLinkColor = YmPalette.TextMain;
            l.LinkBehavior = LinkBehavior.HoverUnderline;
            l.TextAlign = ContentAlignment.BottomLeft;
            l.TabStop = false;
            l.Cursor = Cursors.Hand;
            l.LinkClicked += (s, e) =>
            {
                if (AllTracksToggled != null) AllTracksToggled(_artistId);
            };
            return l;
        }

        /// <summary>Пересчёт при перевёрстке, а не только по событию Resize: появление
        /// системной вертикальной полосы уменьшает ClientSize, но Resize при этом не
        /// приходит — ряды оставались шире клиента и WinForms показывал горизонтальную
        /// прокрутку.</summary>
        private void OnFlowLayout(object sender, LayoutEventArgs e)
        {
            if (_relayouting) return;
            if (_flow.ClientSize.Width == _lastLayoutWidth) return;
            Relayout();
        }

        private bool _relayouting;
        private int _lastLayoutWidth = -1;

        private void Relayout()
        {
            if (_relayouting) return;
            _relayouting = true;
            try
            {
                RelayoutCore();
            }
            finally
            {
                _relayouting = false;
            }
        }

        private void RelayoutCore()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _lastLayoutWidth = _flow.ClientSize.Width;
            int w = _flow.ClientSize.Width;
            if (w <= 0) return;
            int rowW = Math.Max(120, w - _flow.Padding.Horizontal - 4);

            foreach (var r in _rows)
            {
                r.Width = rowW;
                r.Margin = new Padding(2, 1, 2, 1);
            }

            if (_albumsBox != null)
            {
                // на 4px уже поля (_albumsBox.Margin): иначе сетка вылезает за клиентскую
                // область и WinForms включает горизонтальную прокрутку у всей страницы
                _albumsBox.Width = Math.Max(60, rowW - 4);
                const int gutter = 10;
                int cardW = Math.Max(120, (_albumsBox.Width - gutter * 4) / 3);
                int cardH = cardW - 24 + 66;
                int perRow = Math.Max(1, (_albumsBox.Width + gutter) / (cardW + gutter));
                int rowsCount = Math.Max(1, (int)Math.Ceiling((double)_cells.Count / perRow));
                _albumsBox.Height = rowsCount * (cardH + gutter) + 6;
                foreach (var c in _cells)
                {
                    c.Width = cardW;
                    c.Height = cardH;
                    c.Margin = new Padding(gutter / 2);
                }
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 20) Diag.UiProfile.Add("relayout", sw.ElapsedMilliseconds);
        }

        private static string AlbumSubtitle(Album a)
        {
            var parts = new List<string>();
            if (a.Year > 0) parts.Add(a.Year.ToString());
            if (a.TrackCount > 0) parts.Add(a.TrackCount + " треков");
            return string.Join(" · ", parts.ToArray());
        }
    }

    /// <summary>Одна строка «Популярные треки» на странице исполнителя:
    /// номер/▶, обложка, название + исполнитель, длительность, сердечко лайка.</summary>
    internal sealed class TrackRowControl : Control
    {
        private static readonly Font FNum = new Font("Segoe UI", 8.5F);
        private static readonly Font FTitle = new Font("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font FSub = new Font("Segoe UI", 8F);
        private static readonly Font FDur = new Font("Segoe UI", 8F);
        private static readonly Font FMark = UiGlyphs.GlyphFont(9F, FontStyle.Bold);
        // сердце лайка намеренно крупнее остального текста строки: бледный 10pt-глиф
        // в правом углу было почти невозможно нажать мышью
        private static readonly Font FLike = UiGlyphs.GlyphFont(15F, FontStyle.Bold);

        public event EventHandler Activated;
        public event EventHandler LikeClicked;
        /// <summary>ПКМ по строке: трек + экранная точка для контекстного меню.</summary>
        public event Action<Track, Point> ContextRequested;
        /// <summary>ЛКМ по строке вне зоны лайка: выделить строку (единственную на странице).
        /// Раньше клик по строке не делал ничего, и подсветка держалась только при
        /// наведении.</summary>
        public event Action<TrackRowControl> RowClicked;

        /// <summary>Строка выбрана кликом: держится после ухода мыши.</summary>
        public bool IsSelected
        {
            get { return _selected; }
            set { if (_selected == value) return; _selected = value; Invalidate(); }
        }

        private readonly Track _track;
        private readonly int _index;
        private readonly Func<string, bool> _liked;
        private readonly Func<int, bool> _isCurrent;
        private readonly CoverLoader _loader;
        private readonly string _coverUri;
        private Image _cover;
        private bool _coverRequested;
        private bool _hover;
        private bool _hoverLike;
        private bool _selected;

        public TrackRowControl(Track track, int index, Func<string, bool> liked,
                               Func<int, bool> isCurrent, CoverLoader loader)
        {
            _track = track;
            _index = index;
            _liked = liked;
            _isCurrent = isCurrent;
            _loader = loader;
            _coverUri = TrackCoverKey(track);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = YmPalette.BgMain;
            Cursor = Cursors.Hand;
            TabStop = false;
            Height = 44;
        }

        private static string TrackCoverKey(Track track)
        {
            if (track.Albums != null && track.Albums.Count > 0 && track.Albums[0] != null &&
                !string.IsNullOrEmpty(track.Albums[0].CoverUri))
            {
                return track.Albums[0].CoverUri;
            }
            return track.CoverUri;
        }

        public void BeginCoverLoad()
        {
            if (_coverRequested || string.IsNullOrEmpty(_coverUri) || _loader == null) return;
            _coverRequested = true;
            CoverLoader.BeginLoad(() => _loader.Fetch(_coverUri, "100x100"), 40, img =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (IsDisposed) return;
                    var old = _cover;
                    _cover = img;
                    if (old != null) old.Dispose();
                    Invalidate();
                }
                catch { }
                finally
                {
                    sw.Stop();
                    if (!IsDisposed) Diag.UiProfile.Add("cover-apply", sw.ElapsedMilliseconds);
                }
            });
        }

        private bool IsInLikeZone(int x)
        {
            return x >= Width - LikeZone;
        }

        /// <summary>Ширина кликабельной зоны лайка справа (px).</summary>
        private const int LikeZone = 48;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _hoverLike = false; Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hl = IsInLikeZone(e.X);
            if (hl != _hoverLike) { _hoverLike = hl; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (IsInLikeZone(e.X))
            {
                if (LikeClicked != null) LikeClicked(this, EventArgs.Empty);
                return;
            }
            if (RowClicked != null) RowClicked(this);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && ContextRequested != null)
            {
                ContextRequested(_track, PointToScreen(e.Location));
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (Activated != null) Activated(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Graphics g = e.Graphics;
            bool current = _isCurrent != null && _isCurrent(_index);
            bool liked = _liked != null && !string.IsNullOrEmpty(_track.Id) && _liked(_track.Id);

            // Выделение кликом держится после ухода мыши, наведение — второе состояние.
            Color rowBg = _selected ? YmPalette.BgSel : (_hover ? YmPalette.BgHover : YmPalette.BgMain);
            using (var bg = new SolidBrush(rowBg))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            // номер / маркер проигрывания
            int x = 8;
            if (current)
            {
                TextRenderer.DrawText(g, "▶", FMark,
                                      new Rectangle(x, 0, 26, Height), YmPalette.Accent,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            else
            {
                TextRenderer.DrawText(g, (_index + 1).ToString(), FNum,
                                      new Rectangle(x, 0, 26, Height), YmPalette.TextFaint,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            // обложка
            int box = 40;
            int cx = x + 26 + 6;
            int cy = (Height - box) / 2;
            if (_cover != null)
            {
                g.DrawImage(_cover, cx, cy, box, box);
            }
            else
            {
                using (var bb = new SolidBrush(YmPalette.BgCard))
                {
                    g.FillRectangle(bb, cx, cy, box, box);
                }
                TextRenderer.DrawText(g, UiGlyphs.Note, FMark,
                                      new Rectangle(cx, cy, box, box), YmPalette.TextFaint,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // название + исполнитель
            int tx = cx + box + 10;
            int rightX = Math.Max(tx + 40, Width - LikeZone - 48);
            int tw = Math.Max(40, rightX - tx);
            Color titleColor = _track.Available ? (current ? YmPalette.LikeGold : YmPalette.TextMain) : YmPalette.TextFaint;
            string trackTitle = string.IsNullOrEmpty(_track.Title) ? "—" : _track.Title;
            TextRenderer.DrawText(g, trackTitle, FTitle,
                                  new Rectangle(tx, 2, tw, 22), titleColor,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(_track.Version) && _track.Version != "original" && _track.Available)
            {
                int ttW = TextRenderer.MeasureText(g, trackTitle, FTitle).Width;
                if (ttW < tw - 30)
                {
                    TextRenderer.DrawText(g, _track.Version, FSub,
                                          new Rectangle(tx + ttW + 4, 5, Math.Max(1, tw - ttW - 4), 18), YmPalette.TextFaint,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
            TextRenderer.DrawText(g, _track.ArtistsText, FSub,
                                  new Rectangle(tx, 22, tw, 18), YmPalette.TextSub,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // длительность + лайк
            TextRenderer.DrawText(g, _track.DurationText, FDur,
                                  new Rectangle(Width - LikeZone - 44, 0, 44, Height), YmPalette.TextFaint,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            bool overLike = _hoverLike;
            Color likeColor = liked ? YmPalette.LikeGold : (overLike ? YmPalette.Accent : YmPalette.TextSub);
            if (overLike)
            {
                // подсветка зоны при наведении — иначе непонятно, что сердце кликабельно
                using (var hb = new SolidBrush(YmPalette.BgCard))
                {
                    g.FillRectangle(hb, Width - LikeZone, 3, LikeZone - 2, Height - 6);
                }
            }
            TextRenderer.DrawText(g, liked ? UiGlyphs.Like : UiGlyphs.Unlike, FLike,
                                  new Rectangle(Width - LikeZone, 0, LikeZone - 4, Height), likeColor,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            sw.Stop();
            Diag.UiProfile.Add("row-paint", sw.ElapsedMilliseconds);
        }
    }
}