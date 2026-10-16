using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using YaMus.Core.Media;

namespace YaMus.UI
{
    /// <summary>Замещающий режим «сейчас играет»: окно заливается цветом обложки,
    /// по центру крупная обложка, под ней название/исполнитель, полоса прогресса
    /// и управление, сверху слева — стрелка вниз для выхода.
    /// Открывается кликом по обложке в плеере.</summary>
    internal sealed class NowPlayingOverlay : Panel
    {
        private static readonly Font FTitle = new Font("Segoe UI", 19F, FontStyle.Bold);
        private static readonly Font FArtist = new Font("Segoe UI", 12F);
        private static readonly Font FTime = new Font("Segoe UI", 9F);

        private readonly List<GlyphButton> _buttons = new List<GlyphButton>();
        private readonly GlyphButton _closeBtn;
        private readonly GlyphButton _shuffleBtn;
        private readonly GlyphButton _prevBtn;
        private readonly GlyphButton _nextBtn;
        private readonly GlyphButton _likeBtn;
        private readonly GlyphButton _repeatBtn;
        private readonly RoundButton _playBtn;
        private readonly ProgressLine _progress;

        private Image _cover;
        private string _coverUri;
        private CoverLoader _loader;
        private string _title = "";
        private string _artist = "";
        private int _durationSeconds;

        // палитра от обложки
        private Color _cTop = Color.FromArgb(38, 40, 52);
        private Color _cBottom = Color.FromArgb(18, 19, 26);
        private Color _cAccent = Color.FromArgb(240, 64, 0);
        private Color _cText = Color.FromArgb(245, 245, 248);
        private Color _cTextSub = Color.FromArgb(190, 192, 202);

        // состояние кнопок держим явно: цвета кнопок — часть палитры обложки,
        // читать состояние обратно из них нельзя
        private bool _statePlaying;
        private bool _stateLiked;
        private bool _stateShuffle;
        private bool _stateRepeat;
        private bool _stateRepeatOne;

        // единая раскладка: её используют и OnPaint, и расстановка контролов,
        // поэтому текст и кнопки не могут наехать друг на друга
        private Rectangle _coverRect;
        private Rectangle _titleRect;
        private Rectangle _artistRect;
        private Rectangle _progressRect;
        private Rectangle _elapsedRect;
        private Rectangle _totalRect;

        public event Action CloseRequested;
        public event Action PlayPauseRequested;
        public event Action PrevRequested;
        public event Action NextRequested;
        public event Action LikeRequested;
        public event Action ShuffleRequested;
        public event Action RepeatRequested;

        public NowPlayingOverlay()
        {
            Dock = DockStyle.Fill;
            BackColor = _cTop;
            Visible = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            _closeBtn = MakeGlyph("▾", "Свернуть (Esc)", 36, 13F);
            _closeBtn.Click += (s, e) => { if (CloseRequested != null) CloseRequested(); };

            _shuffleBtn = MakeGlyph(UiGlyphs.Shuffle, "Перемешать", 40, 17F);
            _shuffleBtn.Click += (s, e) => { if (ShuffleRequested != null) ShuffleRequested(); };

            _prevBtn = MakeGlyph(UiGlyphs.Previous, "Назад", 40, 18F);
            _prevBtn.Click += (s, e) => { if (PrevRequested != null) PrevRequested(); };

            _nextBtn = MakeGlyph(UiGlyphs.Next, "Вперёд", 40, 18F);
            _nextBtn.Click += (s, e) => { if (NextRequested != null) NextRequested(); };

            _likeBtn = MakeGlyph(UiGlyphs.Unlike, "Мне нравится", 40, 17F);
            _likeBtn.Click += (s, e) => { if (LikeRequested != null) LikeRequested(); };

            _repeatBtn = MakeGlyph(UiGlyphs.Repeat, "Повтор", 40, 17F);
            _repeatBtn.Click += (s, e) => { if (RepeatRequested != null) RepeatRequested(); };

            _playBtn = new RoundButton();
            _playBtn.Text = UiGlyphs.Play;
            _playBtn.Width = 56;
            _playBtn.Height = 56;
            _playBtn.BackColor = PlayButtonColor;
            _playBtn.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            _playBtn.ForeColor = Color.White;
            Tuple<Font, Size> fitPlay = UiGlyphs.FitGlyphFont(UiGlyphs.Play, 56, 15F, FontStyle.Bold);
            _playBtn.Font = fitPlay.Item1;
            _playBtn.Width = Math.Max(56, fitPlay.Item2.Width);
            _playBtn.Height = Math.Max(56, fitPlay.Item2.Height);
            _playBtn.TabStop = false;
            _playBtn.Cursor = Cursors.Hand;
            _playBtn.Click += (s, e) => { if (PlayPauseRequested != null) PlayPauseRequested(); };

            _progress = new ProgressLine();
            _progress.BackColor = _cTop;
            _progress.SeekTo += seconds =>
            {
                if (SeekRequested != null) SeekRequested(seconds);
            };

            Controls.Add(_closeBtn);
            Controls.Add(_progress);
            Controls.Add(_shuffleBtn);
            Controls.Add(_prevBtn);
            Controls.Add(_playBtn);
            Controls.Add(_nextBtn);
            Controls.Add(_likeBtn);
            Controls.Add(_repeatBtn);
        }

        public event Action<int> SeekRequested;

        private GlyphButton MakeGlyph(string glyph, string tip, int size, float fontSize)
        {
            var b = new GlyphButton();
            b.Glyph = glyph;
            // глиф подгоняем под кнопку: ◀◀/▶▶ в 40px при 18pt шире кнопки, и край срезался
            Tuple<Font, Size> fit = UiGlyphs.FitGlyphFont(glyph, size, fontSize, FontStyle.Bold);
            int w = Math.Max(size, fit.Item2.Width);
            int h = Math.Max(size, fit.Item2.Height);
            // кнопки оверлея квадратные: если глиф расширил кнопку, расширяем по обеим осям
            if (w != size) h = Math.Max(h, w);
            b.Width = w;
            b.Height = h;
            b.Font = fit.Item1;
            b.ForeColor = _cText;
            // Фон не задаём здесь: BaseFill выставляется в ApplyButtonBackgrounds
            // по фактической позиции кнопки.
            b.TabStop = false;
            b.Tag = tip;
            b.Cursor = Cursors.Hand;
            _buttons.Add(b);
            return b;
        }

        /// <summary>Показать трек: подгружаем большую обложку и красим фон её цветом.</summary>
        public void ShowTrack(string coverUri, string title, string artist, int durationSeconds, CoverLoader loader)
        {
            _title = title ?? "";
            _artist = artist ?? "";
            _coverUri = coverUri;
            _loader = loader;
            _durationSeconds = Math.Max(0, durationSeconds);

            if (_cover != null)
            {
                _cover.Dispose();
                _cover = null;
            }

            _progress.DurationSeconds = _durationSeconds;
            _progress.PositionSeconds = 0;

            if (!string.IsNullOrEmpty(coverUri) && loader != null)
            {
                CoverLoader.BeginLoad(() => loader.Fetch(coverUri, "1000x1000"), 360, img =>
                {
                    if (IsDisposed) return;
                    // пока большая обложка грузилась, оверлей уже показали другой трек —
                    // чужую картинку не применяем (при быстрых Next/Prev они путались)
                    if (!string.Equals(coverUri, _coverUri, StringComparison.Ordinal))
                    {
                        if (img != null) img.Dispose();
                        return;
                    }
                    if (_cover != null) _cover.Dispose();
                    _cover = img;
                    if (img != null)
                    {
                        BuildPalette(img);
                    }
                    else
                    {
                        // обложки нет — остаётся фон темы
                        _cTop = YmPalette.BgCard;
                        _cBottom = YmPalette.BgMain;
                        ApplyPalette();
                    }
                    Invalidate(true);
                });
            }
            else
            {
                _cTop = YmPalette.BgCard;
                _cBottom = YmPalette.BgMain;
                ApplyPalette();
            }

            RecalcLayout();
            Invalidate(true);
        }

        public void SetProgress(int positionSeconds, int durationSeconds)
        {
            if (durationSeconds > 0) _durationSeconds = durationSeconds;
            _progress.DurationSeconds = _durationSeconds;
            _progress.PositionSeconds = Math.Max(0, Math.Min(_durationSeconds, positionSeconds));
            Invalidate();
        }

        /// <summary>Обновить состояние управляющих кнопок из основного плеера.</summary>
        public void SyncState(bool playing, bool liked, bool shuffle, bool repeat, bool repeatOne)
        {
            _statePlaying = playing;
            _stateLiked = liked;
            _stateShuffle = shuffle;
            _stateRepeat = repeat;
            _stateRepeatOne = repeatOne;
            ApplyState();
        }

        /// <summary>Режим «идёт переключение трека»: кнопка Play показывает значок
        /// загрузки вместо глифа — загрузку видно там же, где и в основном плеере.</summary>
        public void SetBusy(bool busy)
        {
            _playBtn.Loading = busy;
        }

        /// <summary>Применяет сохранённое состояние к кнопкам. Каждая кнопка
        /// перерисовывается явно: смена ForeColor/Text сама по себе не всегда
        /// вызывает перерисовку дочернего контрола в оверлее.</summary>
        private void ApplyState()
        {
            string playGlyph = _statePlaying ? UiGlyphs.Pause : UiGlyphs.Play;
            if (_playBtn.Text != playGlyph) _playBtn.Text = playGlyph;
            _playBtn.Invalidate();

            string likeGlyph = _stateLiked ? UiGlyphs.Like : UiGlyphs.Unlike;
            if (_likeBtn.Glyph != likeGlyph) _likeBtn.Glyph = likeGlyph;
            _likeBtn.ForeColor = _stateLiked ? YmPalette.LikeGold : _cText;
            _likeBtn.Invalidate();

            _shuffleBtn.ForeColor = _stateShuffle ? YmPalette.Accent : _cText;
            _shuffleBtn.Invalidate();

            _repeatBtn.Glyph = UiGlyphs.Repeat;
            if (_repeatBtn.Mirrored != _stateRepeatOne) _repeatBtn.Mirrored = _stateRepeatOne;
            _repeatBtn.ForeColor = _stateRepeatOne ? YmPalette.LikeGold
                              : _stateRepeat ? YmPalette.Accent : _cText;
            _repeatBtn.Invalidate();
        }

        /// <summary>Усреднённый цвет обложки → градиент фона и акцент кнопки play.</summary>
        private void BuildPalette(Image cover)
        {
            Color avg;
            try { avg = AverageColor(cover); }
            catch { avg = Color.FromArgb(90, 90, 110); }

            // тёмный, но не чёрный: поднимаем тени, чтобы текст читался
            _cTop = Scale(avg, 0.42f, 8);
            _cBottom = Scale(avg, 0.20f, 4);
            _cText = Color.FromArgb(246, 246, 250);
            _cTextSub = Color.FromArgb(196, 198, 208);
            _cAccent = Scale(avg, 1.18f, 30);

            if (_cAccent.R + _cAccent.G + _cAccent.B < 150) _cAccent = Color.FromArgb(240, 64, 0);

            ApplyPalette();
        }

        /// <summary>Цвет кнопки play фиксирован (фирменный акцент), а не берётся
        /// из цвета обложки — так кнопка не «теряется» на однотонных обложках.</summary>
        private static Color PlayButtonColor
        {
            get { return YmPalette.Accent; }
        }

        private void ApplyPalette()
        {
            BackColor = _cTop;
            // Заливка кнопок зависит от градиента, а градиент меняется здесь же:
            // без этого BaseFill остаётся от предыдущей палитры (например, от темы)
            // и кнопки видны как пятна до переключения трека.
            ApplyButtonBackgrounds();
            _playBtn.BackColor = PlayButtonColor;
            _playBtn.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            _playBtn.ForeColor = Color.White;
            _playBtn.FlatAppearance.MouseDownBackColor = PlayButtonColor;
            _progress.BackColor = _cTop;
            _progress.TrackColor = Scale(_cTop, 1.7f, 18);
            _progress.FillColor = _cAccent;
            _progress.ThumbColor = _cText;

            foreach (GlyphButton b in _buttons)
            {
                // BackColor не задаём намеренно: заливку делает BaseFill, и оверлей
                // подставляет туда цвет градиента в точке кнопки (ApplyButtonBackgrounds).
                b.ForeColor = _cText;
                b.HoverFill = Color.FromArgb(30, 255, 255, 255);
                b.PressFill = Color.FromArgb(60, 255, 255, 255);
                b.Invalidate();
            }
            _playBtn.Invalidate();
            _progress.Invalidate();
            ApplyState();
        }

        /// <summary>Пересчитать палитру под текущую тему (после смены темы приложения),
        /// сохранив обложку, если она уже загружена.</summary>
        public void RestorePalette()
        {
            if (_cover != null) BuildPalette(_cover);
            else
            {
                _cTop = YmPalette.BgCard;
                _cBottom = YmPalette.BgMain;
                ApplyPalette();
            }
            Invalidate(true);
        }

        private static Color Scale(Color c, float f, int lift)
        {
            int r = (int)(c.R * f) + lift;
            int g = (int)(c.G * f) + lift;
            int b = (int)(c.B * f) + lift;
            r = Math.Max(0, Math.Min(255, r));
            g = Math.Max(0, Math.Min(255, g));
            b = Math.Max(0, Math.Min(255, b));
            return Color.FromArgb(r, g, b);
        }

        private static Color AverageColor(Image img)
        {
            using (var small = new Bitmap(24, 24))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(img, 0, 0, 24, 24);
                }
                long r = 0, gg = 0, b = 0;
                for (int y = 0; y < 24; y++)
                {
                    for (int x = 0; x < 24; x++)
                    {
                        Color p = small.GetPixel(x, y);
                        r += p.R; gg += p.G; b += p.B;
                    }
                }
                const int n = 24 * 24;
                return Color.FromArgb((int)(r / n), (int)(gg / n), (int)(b / n));
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RecalcLayout();
        }

        /// <summary>Высота строки шрифта по GDI: учитывает выносные элементы и
        /// интерлиньяж, поэтому текст не обрезается по вертикали.</summary>
        private static int LineHeight(Font font)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                return TextRenderer.MeasureText(g, "Ag", font, new Size(int.MaxValue, int.MaxValue),
                                                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Height;
            }
        }

        /// <summary>Вертикальная раскладка сверху вниз: обложка → текст → прогресс →
        /// основной ряд кнопок → ряд лайк/shuffle/repeat. Всё влезает в окно,
        /// при нехватке места равномерно ужимается.</summary>
        private void RecalcLayout()
        {
            if (Width <= 0 || Height <= 0) return;

            const int padTop = 64;      // зона под стрелку закрытия
            const int padBottom = 20;
            const int gapCoverText = 22;
            const int gapTextProgress = 18;
            const int gapProgressBtns = 20;
            const int gapRows = 10;
            const int progressH = 18;
            const int sideRowH = 40;

            // высоты строк берём из самих шрифтов: у заголовка 19pt есть выносные
            // элементы (g, у, щ), которые обрезаются в фиксированной полосе 28px
            int textH = LineHeight(FTitle) + 6;
            int artistH = LineHeight(FArtist) + 2;
            int timeH = LineHeight(FTime) + 2;

            int cx = Width / 2;
            int avail = Math.Max(160, Height - padTop - padBottom);

            // всё, кроме обложки. Прогресс отдельной строкой не считаем: он стоит
            // внутри строки времени и в высоту блока не добавляется
            int rowsH = textH + artistH + timeH + _playBtn.Height + sideRowH +
                        gapCoverText + gapTextProgress + gapProgressBtns + gapRows;

            // обложка занимает остаток, но в пределах 360 и ширины окна
            int cover = Math.Min(360, Math.Min(Width - 220, avail - rowsH));
            cover = Math.Max(60, cover);

            // Центрируем весь блок по вертикали между зоной закрытия сверху и нижним
            // отступом — на fullscreen-высоте иначе оверлей «прилипает» к верху экрана
            int contentH = cover + rowsH;
            int topGap = (avail - contentH) / 2;
            if (topGap < 0) topGap = 0;

            int y = padTop + topGap;

            _coverRect = new Rectangle(cx - cover / 2, y, cover, cover);
            y += cover + gapCoverText;

            _titleRect = new Rectangle(0, y, Width, textH);
            y += textH;

            _artistRect = new Rectangle(0, y, Width, artistH);
            y += artistH + gapTextProgress;

            int barW = Math.Max(120, Math.Min(520, Width - 220));
            int timeW = 46;
            _elapsedRect = new Rectangle(cx - barW / 2 - timeW - 8, y, timeW, timeH);
            _progressRect = new Rectangle(cx - barW / 2, y + (timeH - progressH) / 2, barW, progressH);
            _totalRect = new Rectangle(cx + barW / 2 + 8, y, timeW, timeH);
            y += timeH + gapProgressBtns;

            // основной ряд: назад — play — вперёд
            int gap = 10;
            int mainW = _prevBtn.Width + gap + _playBtn.Width + gap + _nextBtn.Width;
            int mx = cx - mainW / 2;
            int mainY = y;
            _prevBtn.Location = new Point(mx, mainY + (_playBtn.Height - _prevBtn.Height) / 2);
            _playBtn.Location = new Point(mx + _prevBtn.Width + gap, mainY);
            _nextBtn.Location = new Point(mx + _prevBtn.Width + gap + _playBtn.Width + gap,
                                          mainY + (_playBtn.Height - _nextBtn.Height) / 2);
            y += _playBtn.Height + gapRows;

            // нижний ряд: лайк / перемешать / повтор
            int sideGap = 14;
            int sideW = _likeBtn.Width + sideGap + _shuffleBtn.Width + sideGap + _repeatBtn.Width;
            int sx = cx - sideW / 2;
            int sy = y;
            _likeBtn.Location = new Point(sx, sy);
            _shuffleBtn.Location = new Point(sx + _likeBtn.Width + sideGap, sy);
            _repeatBtn.Location = new Point(sx + _likeBtn.Width + sideGap + _shuffleBtn.Width + sideGap, sy);

            _closeBtn.Location = new Point(18, 16);
            _progress.Bounds = _progressRect;

            // Фон каждой кнопки = цвет градиента оверлея в её центре, иначе кнопка
            // залита одним цветом и на фоне градиента выглядит пятном.
            // Color.Transparent недопустим: буфер двойной буферизации не очищается
            // и в кнопке остаётся мусор от прошлого кадра родителя.
            ApplyButtonBackgrounds();
        }

        /// <summary>Цвет вертикального градиента _cTop→_cBottom в строке y.</summary>
        private Color GradientAt(int y)
        {
            if (Height <= 0) return _cTop;
            float t = Math.Max(0f, Math.Min(1f, y / (float)Height));
            return Color.FromArgb(
                (int)Math.Round(_cTop.R + (_cBottom.R - _cTop.R) * t),
                (int)Math.Round(_cTop.G + (_cBottom.G - _cTop.G) * t),
                (int)Math.Round(_cTop.B + (_cBottom.B - _cTop.B) * t));
        }

        private void ApplyButtonBackgrounds()
        {
            foreach (GlyphButton b in _buttons)
            {
                b.BaseFill = GradientAt(b.Top + b.Height / 2);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_cTop);

            using (var brush = new LinearGradientBrush(
                       new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                       _cTop, _cBottom, LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, 0, 0, Width, Height);
            }

            if (Width <= 0 || Height <= 0) return;

            // обложка с мягкой тенью
            for (int i = 6; i >= 1; i--)
            {
                using (var sh = new SolidBrush(Color.FromArgb(10, 0, 0, 0)))
                {
                    g.FillRectangle(sh, _coverRect.X - i * 3, _coverRect.Y + i * 3,
                                    _coverRect.Width + i * 6, _coverRect.Height + i * 6);
                }
            }

            using (var path = RoundedRect(_coverRect, 10))
            {
                using (var under = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                {
                    g.FillPath(under, path);
                }
                if (_cover != null)
                {
                    var clipState = g.Save();
                    g.SetClip(path);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_cover, _coverRect);
                    g.Restore(clipState);
                }
            }

            if (!string.IsNullOrEmpty(_title))
            {
                TextRenderer.DrawText(g, Ellipsize(g, _title, FTitle, _titleRect.Width), FTitle, _titleRect, _cText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            if (!string.IsNullOrEmpty(_artist))
            {
                TextRenderer.DrawText(g, _artist, FArtist, _artistRect, _cTextSub,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }

            string elapsed = FormatTime(_progress.PositionSeconds);
            string total = FormatTime(_durationSeconds);
            TextRenderer.DrawText(g, elapsed, FTime, _elapsedRect, Color.FromArgb(150, 152, 162),
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, total, FTime, _totalRect, Color.FromArgb(150, 152, 162),
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static string FormatTime(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        private static string Ellipsize(Graphics g, string text, Font font, int maxWidth)
        {
            if (maxWidth <= 0 || string.IsNullOrEmpty(text)) return text;
            if (TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue),
                                         TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= maxWidth)
            {
                return text;
            }
            string cut = text;
            while (cut.Length > 1 &&
                   TextRenderer.MeasureText(g, cut + "…", font).Width > maxWidth)
            {
                cut = cut.Substring(0, cut.Length - 1);
            }
            return cut + "…";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (CloseRequested != null) CloseRequested();
                return true;
            }
            if (keyData == Keys.Left)
            {
                if (PrevRequested != null) PrevRequested();
                return true;
            }
            if (keyData == Keys.Right)
            {
                if (NextRequested != null) NextRequested();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
    }

    /// <summary>Полоса прогресса в оверлее: своя палитра (цвета обложки), перемотка мышью.</summary>
    internal sealed class ProgressLine : Control
    {
        private int _durationSeconds;
        private int _positionSeconds;
        private bool _dragging;
        private bool _hover;

        public int DurationSeconds
        {
            get { return _durationSeconds; }
            set { _durationSeconds = Math.Max(0, value); Invalidate(); }
        }

        public int PositionSeconds
        {
            get { return _positionSeconds; }
            set { _positionSeconds = Math.Max(0, Math.Min(_durationSeconds, value)); Invalidate(); }
        }

        public Color TrackColor { get; set; }
        public Color FillColor { get; set; }
        public Color ThumbColor { get; set; }

        public event Action<int> SeekTo;

        public ProgressLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TrackColor = Color.FromArgb(90, 92, 104);
            FillColor = Color.FromArgb(240, 64, 0);
            ThumbColor = Color.FromArgb(240, 240, 245);
            Cursor = Cursors.Hand;
        }

        private float FractionFromMouse(int x)
        {
            if (Width <= 0) return 0f;
            float f = (x - 6f) / (Width - 12f);
            return Math.Max(0f, Math.Min(1f, f));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_durationSeconds <= 0) return;
            _dragging = true;
            PositionSeconds = (int)(FractionFromMouse(e.X) * _durationSeconds);
            if (SeekTo != null) SeekTo(PositionSeconds);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && _durationSeconds > 0)
            {
                PositionSeconds = (int)(FractionFromMouse(e.X) * _durationSeconds);
                if (SeekTo != null) SeekTo(PositionSeconds);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(BackColor))
            {
                g.FillRectangle(b, 0, 0, Width, Height);
            }

            float cy = Height / 2f;
            float h = (_hover || _dragging) ? 5f : 4f;
            float w = Width - 12f;
            if (w <= 0) return;

            float frac = _durationSeconds > 0
                ? Math.Max(0f, Math.Min(1f, (float)_positionSeconds / _durationSeconds))
                : 0f;
            float fill = w * frac;

            using (var track = new SolidBrush(TrackColor))
            using (var fillB = new SolidBrush(FillColor))
            using (var thumb = new SolidBrush(ThumbColor))
            {
                // скруглённые торцы полосы, как в плеере Яндекс Музыки
                using (var path = new GraphicsPath())
                {
                    path.AddArc(6, cy - h / 2, h, h, 90, 180);
                    path.AddArc(6 + w - h, cy - h / 2, h, h, 270, 180);
                    path.CloseFigure();
                    g.FillPath(track, path);
                }

                if (fill > 0)
                {
                    // заливка обрезается по той же скруглённой форме — иначе левый
                    // торец заполненной части выглядит углом
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(6, cy - h / 2, h, h, 90, 180);
                        path.AddArc(6 + w - h, cy - h / 2, h, h, 270, 180);
                        path.CloseFigure();
                        var state = g.Save();
                        g.SetClip(path);
                        g.FillRectangle(fillB, 6, cy - h / 2, fill, h);
                        g.Restore(state);
                    }
                }
                if (_durationSeconds > 0)
                {
                    float r = (_hover || _dragging) ? 7f : 6f;
                    g.FillEllipse(thumb, 6 + fill - r / 2, cy - r / 2, r, r);
                }
            }
        }
    }
}
