using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YaMus.Core.Auth;
using YaMus.Core.Playback;

namespace YaMus.UI
{
    /// <summary>Страница настроек: слева блоки «Аккаунт», «Тема» и «Качество музыки»,
    /// весь контент — в автоскролл-панели, чтобы низкое окно прокручивалось.</summary>
    internal sealed class SettingsPanel : Panel
    {
        public event Action LogoutRequested;

        private static readonly Font FAccount = new Font("Segoe UI", 14F, FontStyle.Bold);
        private static readonly Font FButton = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        private static readonly Font FNote = new Font("Segoe UI", 9.5F);

        private readonly VScrollPanel _scroll;
        private readonly Label _account;
        private bool _relayouting;
        private readonly Button _logout;
        private readonly Label _themeLabel;
        private readonly Button _blackBtn;
        private readonly Button _darkBtn;
        private readonly Button _lightBtn;
        private readonly Label _qualityLabel;
        private readonly Button _maxBtn;
        private readonly Button _mediumBtn;
        private readonly Button _lowBtn;
        private readonly Label _qualityNote;
        private readonly Label _note;

        /// <summary>Хост с прокруткой — на него вешается оверлей полосы прокрутки.</summary>
        public Panel ScrollHost
        {
            get { return _scroll; }
        }

        public SettingsPanel()
        {
            BackColor = YmPalette.BgMain;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            // весь контент лежит в автоскролл-панели: в низком окне настройки
            // прокручиваются, а не обрезаются
            _scroll = new VScrollPanel();
            _scroll.Dock = DockStyle.Fill;
            _scroll.BackColor = YmPalette.BgMain;
            Controls.Add(_scroll);

            _account = new Label();
            _account.Text = "Аккаунт";
            _account.AutoSize = false;
            _account.Font = FAccount;
            _account.ForeColor = YmPalette.TextMain;
            _account.TextAlign = ContentAlignment.MiddleLeft;
            _scroll.Controls.Add(_account);

            _logout = new Button();
            _logout.Text = "Выход из аккаунта";
            _logout.Width = 190;
            _logout.Height = 36;
            _logout.FlatStyle = FlatStyle.Flat;
            _logout.FlatAppearance.BorderSize = 0;
            _logout.BackColor = YmPalette.Accent;
            _logout.ForeColor = Color.White;
            _logout.Font = FButton;
            _logout.FlatAppearance.MouseOverBackColor = YmPalette.AccentHot;
            _logout.Click += (s, e) => { if (LogoutRequested != null) LogoutRequested(); };
            _scroll.Controls.Add(_logout);

            _themeLabel = new Label();
            _themeLabel.Text = "Тема";
            _themeLabel.AutoSize = false;
            _themeLabel.Font = FAccount;
            _themeLabel.ForeColor = YmPalette.TextMain;
            _themeLabel.TextAlign = ContentAlignment.MiddleLeft;
            _scroll.Controls.Add(_themeLabel);

            _blackBtn = MakeThemeButton("Чёрная");
            _darkBtn = MakeThemeButton("Тёмная");
            _lightBtn = MakeThemeButton("Светлая");
            _blackBtn.Click += (s, e) => ThemeManager.Apply(ThemeKind.Black);
            _darkBtn.Click += (s, e) => ThemeManager.Apply(ThemeKind.Dark);
            _lightBtn.Click += (s, e) => ThemeManager.Apply(ThemeKind.Light);
            _scroll.Controls.Add(_blackBtn);
            _scroll.Controls.Add(_darkBtn);
            _scroll.Controls.Add(_lightBtn);

            _qualityLabel = new Label();
            _qualityLabel.Text = "Качество музыки";
            _qualityLabel.AutoSize = false;
            _qualityLabel.Font = FAccount;
            _qualityLabel.ForeColor = YmPalette.TextMain;
            _qualityLabel.TextAlign = ContentAlignment.MiddleLeft;
            _scroll.Controls.Add(_qualityLabel);

            _maxBtn = MakeThemeButton("Максимальное");
            _mediumBtn = MakeThemeButton("Среднее");
            _lowBtn = MakeThemeButton("Низкое");
            _maxBtn.Click += (s, e) => SetQuality(MusicQuality.Max);
            _mediumBtn.Click += (s, e) => SetQuality(MusicQuality.Medium);
            _lowBtn.Click += (s, e) => SetQuality(MusicQuality.Low);
            _scroll.Controls.Add(_maxBtn);
            _scroll.Controls.Add(_mediumBtn);
            _scroll.Controls.Add(_lowBtn);

            _qualityNote = new Label();
            _qualityNote.AutoSize = false;
            _qualityNote.Font = FNote;
            _qualityNote.ForeColor = YmPalette.TextFaint;
            _qualityNote.TextAlign = ContentAlignment.TopLeft;
            _scroll.Controls.Add(_qualityNote);

            _note = new Label();
            _note.Text = "Здесь можно выйти из текущего аккаунта.\n\n" +
                         "После выхода приложение вернётся в окно входа.\n" +
                         "Заново войти можно по коду или по токену.";
            _note.AutoSize = false;
            _note.Font = FNote;
            _note.ForeColor = YmPalette.TextFaint;
            _note.TextAlign = ContentAlignment.TopLeft;
            _scroll.Controls.Add(_note);

            ThemeManager.Changed += (from, to) => { RefreshThemeButtons(); RefreshQualityButtons(); };
            RefreshThemeButtons();
            RefreshQualityButtons();

            _scroll.Resize += (s, e) => RelayoutControls();
            Resize += (s, e) => RelayoutControls();
        }

        /// <summary>Ширина кнопки по её тексту: «Максимальное» не влезает в фиксированные 96 px.</summary>
        private static int TextButtonWidth(Button b)
        {
            return TextRenderer.MeasureText(b.Text, b.Font).Width + 22;
        }

        private static Button MakeThemeButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.Width = 96;
            b.Height = 36;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = FButton;
            return b;
        }

        /// <summary>Активная тема — акцентная кнопка, неактивные — нейтральные.</summary>
        private void RefreshThemeButtons()
        {
            ThemeKind k = ThemeManager.CurrentKind;
            StyleThemeButton(_blackBtn, k == ThemeKind.Black);
            StyleThemeButton(_darkBtn, k == ThemeKind.Dark);
            StyleThemeButton(_lightBtn, k == ThemeKind.Light);
        }

        private static void StyleThemeButton(Button b, bool active)
        {
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

        /// <summary>Применяет уровень качества и сохраняет выбор в конфиг.</summary>
        private void SetQuality(MusicQuality q)
        {
            MusicQualityCatalog.Current = q;
            try { TokenStorage.SaveAudioQuality(MusicQualityCatalog.NameOf(q)); }
            catch { }
            RefreshQualityButtons();
        }

        /// <summary>Активный уровень — акцентная кнопка, рядом — описание формата/битрейта.</summary>
        private void RefreshQualityButtons()
        {
            MusicQuality q = MusicQualityCatalog.Current;
            StyleThemeButton(_maxBtn, q == MusicQuality.Max);
            StyleThemeButton(_mediumBtn, q == MusicQuality.Medium);
            StyleThemeButton(_lowBtn, q == MusicQuality.Low);
            if (_qualityNote != null) _qualityNote.Text = MusicQualityCatalog.Describe(q);
        }

        private void RelayoutControls()
        {
            const int left = 28;
            int w = _scroll.ClientSize.Width;

            _account.Top = 24;
            _account.Left = left;
            _account.Width = w - left - 24;
            _account.Height = 34;

            _logout.Top = _account.Bottom + 20;
            _logout.Left = left;

            _themeLabel.Top = _logout.Bottom + 44;
            _themeLabel.Left = left;
            _themeLabel.Width = w - left - 24;
            _themeLabel.Height = 34;

            _blackBtn.Top = _themeLabel.Bottom + 16;
            _blackBtn.Left = left;
            _darkBtn.Top = _blackBtn.Top;
            _darkBtn.Left = left + _blackBtn.Width + 10;
            _lightBtn.Top = _blackBtn.Top;
            _lightBtn.Left = _darkBtn.Right + 10;

            _qualityLabel.Top = _blackBtn.Bottom + 40;
            _qualityLabel.Left = left;
            _qualityLabel.Width = w - left - 24;
            _qualityLabel.Height = 34;

            _maxBtn.Width = TextButtonWidth(_maxBtn);
            _mediumBtn.Width = TextButtonWidth(_mediumBtn);
            _lowBtn.Width = TextButtonWidth(_lowBtn);
            _maxBtn.Top = _qualityLabel.Bottom + 16;
            _maxBtn.Left = left;
            _mediumBtn.Top = _maxBtn.Top;
            _mediumBtn.Left = _maxBtn.Right + 8;
            _lowBtn.Top = _maxBtn.Top;
            _lowBtn.Left = _mediumBtn.Right + 8;

            // описание качества — справа от кнопок (текст сверху, вровень с кнопками),
            // а если места не хватает, то под ними
            int descLeft = _lowBtn.Right + 16;
            int rightSpace = w - descLeft - 28;
            if (rightSpace >= 240)
            {
                _qualityNote.Left = descLeft;
                _qualityNote.Top = _maxBtn.Top;
                _qualityNote.Width = rightSpace;
                _qualityNote.Height = 60;
            }
            else
            {
                _qualityNote.Left = left;
                _qualityNote.Top = _maxBtn.Bottom + 12;
                _qualityNote.Width = Math.Max(100, w - left - 28);
                _qualityNote.Height = 100;
            }

            int contentBottom = Math.Max(_lowBtn.Bottom, _qualityNote.Bottom);

            // пояснение про аккаунт — снова в правой колонке сверху
            const int leftColumn = 340;
            int noteSpace = w - leftColumn - 28;
            if (noteSpace >= 140)
            {
                _note.Left = leftColumn;
                _note.Top = 30;
                _note.Width = noteSpace;
                _note.Height = 200;
            }
            else
            {
                _note.Left = left;
                _note.Top = contentBottom + 28;
                _note.Width = Math.Max(100, w - left - 28);
                _note.Height = 120;
            }

            // Ручная раскладка выполняется уже после того, как WinForms посчитал
            // DisplayRectangle для AutoScroll, поэтому в кэше остаётся ширина
            // предыдущего (например, maximized) размера — и снизу вылезает
            // горизонтальная полоса. Дополнительный проход layout пересчитывает
            // кэш по уже актуальным границам.
            if (!_relayouting)
            {
                _relayouting = true;
                try
                {
                    _scroll.PerformLayout();
                }
                finally
                {
                    _relayouting = false;
                }
            }
        }

        /// <summary>Панель с прокруткой только по вертикали. WinForms кэширует
        /// DisplayRectangle по границам потомков: после maximize → restore там остаётся
        /// ширина maximized-размера, и снизу всплывает горизонтальная полоса, которая
        /// резервирует 17 px и сдвигает всю раскладку. Поэтому на каждом проходе
        /// layout и при изменении размера прячем нативную полосу (Win32) и обнуляем
        /// горизонтальный диапазон.</summary>
        private sealed class VScrollPanel : Panel
        {
            private const int SB_HORZ = 0;

            [DllImport("user32.dll")]
            private static extern bool ShowScrollBar(IntPtr hWnd, int nBar, bool bShow);

            public VScrollPanel()
            {
                AutoScroll = true;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                FixHorizontalBar();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                FixHorizontalBar();
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                FixHorizontalBar();
            }

            private void FixHorizontalBar()
            {
                if (!IsHandleCreated) return;
                try
                {
                    ShowScrollBar(Handle, SB_HORZ, false);
                }
                catch
                {
                }
                ScrollProperties h = HorizontalScroll;
                if (h.Minimum != 0 || h.Maximum != 0)
                {
                    h.Minimum = 0;
                    h.Value = 0;
                    h.Maximum = 0;
                }
            }
        }
    }
}
