using System;
using System.Drawing;
using System.Windows.Forms;
using YaMus.Core.Auth;

namespace YaMus.UI
{
    internal enum ThemeKind
    {
        Dark,
        Black,
        Light
    }

    /// <summary>Палитра темы приложения.</summary>
    internal sealed class Theme
    {
        public readonly bool IsDark;
        public readonly Color BgSide;
        public readonly Color BgMain;
        public readonly Color BgCard;
        public readonly Color BgHover;
        public readonly Color BgSel;
        public readonly Color Line;
        public readonly Color TextMain;
        public readonly Color TextSub;
        public readonly Color TextFaint;
        public readonly Color Accent;
        public readonly Color AccentHot;
        public readonly Color LikeGold;
        public readonly Color WaveAccent;

        public Theme(bool isDark,
                     Color bgSide, Color bgMain, Color bgCard, Color bgHover, Color bgSel,
                     Color line, Color textMain, Color textSub, Color textFaint,
                     Color accent, Color accentHot, Color likeGold, Color waveAccent)
        {
            IsDark = isDark;
            BgSide = bgSide;
            BgMain = bgMain;
            BgCard = bgCard;
            BgHover = bgHover;
            BgSel = bgSel;
            Line = line;
            TextMain = textMain;
            TextSub = textSub;
            TextFaint = textFaint;
            Accent = accent;
            AccentHot = accentHot;
            LikeGold = likeGold;
            WaveAccent = waveAccent;
        }
    }

    /// <summary>Переключение тем + сквозное перекрашивание дерева контролов.</summary>
    internal static class ThemeManager
    {
        public static readonly Theme Dark = new Theme(
            true,
            Color.FromArgb(29, 31, 42),     // BgSide
            Color.FromArgb(36, 38, 50),     // BgMain
            Color.FromArgb(46, 48, 60),     // BgCard
            Color.FromArgb(52, 55, 68),     // BgHover
            Color.FromArgb(60, 63, 78),     // BgSel
            Color.FromArgb(55, 57, 71),     // Line
            Color.FromArgb(230, 230, 238),  // TextMain
            Color.FromArgb(160, 162, 176),  // TextSub
            Color.FromArgb(122, 124, 137),  // TextFaint
            Color.FromArgb(240, 64, 0),     // Accent
            Color.FromArgb(255, 90, 31),    // AccentHot
            Color.FromArgb(240, 171, 0),    // LikeGold
            Color.FromArgb(176, 93, 255));  // WaveAccent

        public static readonly Theme Black = new Theme(
            true,
            Color.FromArgb(8, 8, 10),      // BgSide
            Color.FromArgb(0, 0, 0),       // BgMain
            Color.FromArgb(17, 17, 20),    // BgCard
            Color.FromArgb(24, 24, 28),    // BgHover
            Color.FromArgb(33, 33, 38),    // BgSel
            Color.FromArgb(36, 38, 42),    // Line
            Color.FromArgb(240, 240, 244), // TextMain
            Color.FromArgb(168, 170, 180), // TextSub
            Color.FromArgb(130, 132, 142), // TextFaint
            Color.FromArgb(240, 64, 0),    // Accent
            Color.FromArgb(255, 90, 31),   // AccentHot
            Color.FromArgb(240, 171, 0),   // LikeGold
            Color.FromArgb(176, 93, 255)); // WaveAccent

        public static readonly Theme Light = new Theme(
            false,
            Color.FromArgb(242, 242, 246),   // BgSide
            Color.FromArgb(255, 255, 255),   // BgMain
            Color.FromArgb(255, 255, 255),   // BgCard
            Color.FromArgb(238, 238, 243),   // BgHover
            Color.FromArgb(226, 228, 236),   // BgSel
            Color.FromArgb(220, 222, 230),   // Line
            Color.FromArgb(30, 32, 40),      // TextMain
            Color.FromArgb(100, 104, 118),   // TextSub
            Color.FromArgb(152, 155, 168),   // TextFaint
            Color.FromArgb(240, 64, 0),      // Accent
            Color.FromArgb(255, 90, 31),     // AccentHot
            Color.FromArgb(205, 142, 0),     // LikeGold
            Color.FromArgb(176, 93, 255));   // WaveAccent

        private static readonly Theme _loaded = LoadSaved();
        private static Theme _current = _loaded;

        public static Theme Current
        {
            get { return _current; }
        }

        public static ThemeKind CurrentKind
        {
            get { return KindOf(_current); }
        }

        /// <summary>Старая и новая палитра — слушатели перекрашивают контролы по ролям.</summary>
        public static event Action<Theme, Theme> Changed;

        public static void Apply(ThemeKind kind)
        {
            Theme target = ThemeOf(kind);
            if (ReferenceEquals(_current, target)) return;
            Theme from = _current;
            _current = target;
            try
            {
                TokenStorage.SaveTheme(NameOf(kind));
            }
            catch
            {
                // сохранение темы не критично
            }
            Action<Theme, Theme> h = Changed;
            if (h != null) h(from, target);
        }

        private static Theme ThemeOf(ThemeKind kind)
        {
            switch (kind)
            {
                case ThemeKind.Black: return Black;
                case ThemeKind.Light: return Light;
                default: return Dark;
            }
        }

        private static ThemeKind KindOf(Theme theme)
        {
            if (ReferenceEquals(theme, Black)) return ThemeKind.Black;
            if (ReferenceEquals(theme, Light)) return ThemeKind.Light;
            return ThemeKind.Dark;
        }

        private static string NameOf(ThemeKind kind)
        {
            switch (kind)
            {
                case ThemeKind.Black: return "black";
                case ThemeKind.Light: return "light";
                default: return "dark";
            }
        }

        private static Theme LoadSaved()
        {
            try
            {
                string name = TokenStorage.ReadThemeName();
                if (name == "black") return Black;
                if (name == "light") return Light;
                return Dark;
            }
            catch
            {
                return Dark;
            }
        }

        /// <summary>Перекрашивает дерево контролов: любой цвет, совпадающий с полем старой
        /// палитры, заменяется одноимённым полем новой (по роли, не по значению).</summary>
        public static void RefitControlTree(Control root, Theme oldPalette)
        {
            if (root == null) return;
            var stack = new System.Collections.Generic.Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control c = stack.Pop();
                ApplyControlColors(c, oldPalette);

                // элементы ToolStrip (StatusStrip/меню) — это не Control, красим отдельно
                var ts = c as ToolStrip;
                if (ts != null)
                {
                    foreach (ToolStripItem item in ts.Items)
                    {
                        Color ib = Map(oldPalette, item.BackColor);
                        if (ib != item.BackColor) item.BackColor = ib;
                        Color ifo = Map(oldPalette, item.ForeColor);
                        if (ifo != item.ForeColor) item.ForeColor = ifo;
                    }
                }

                foreach (Control child in c.Controls) stack.Push(child);
            }
            root.Invalidate(true);
        }

        private static void ApplyControlColors(Control c, Theme oldPalette)
        {
            Color back = Map(oldPalette, c.BackColor);
            if (back != c.BackColor) c.BackColor = back;

            Color fore = Map(oldPalette, c.ForeColor);
            if (fore != c.ForeColor) c.ForeColor = fore;

            // LinkLabel хранит цвета отдельно — иначе после смены темы текст ссылки
            // остаётся цветом из прежней палитры (в плеере белый на светлом фоне)
            var ll = c as LinkLabel;
            if (ll != null)
            {
                Color lc = Map(oldPalette, ll.LinkColor);
                if (lc != ll.LinkColor) ll.LinkColor = lc;
                Color ac = Map(oldPalette, ll.ActiveLinkColor);
                if (ac != ll.ActiveLinkColor) ll.ActiveLinkColor = ac;
                Color vc = Map(oldPalette, ll.VisitedLinkColor);
                if (vc != ll.VisitedLinkColor) ll.VisitedLinkColor = vc;
            }

            var baseBtn = c as ButtonBase;
            if (baseBtn != null)
            {
                var fa = baseBtn.FlatAppearance;
                Color hover = Map(oldPalette, fa.MouseOverBackColor);
                Color down = Map(oldPalette, fa.MouseDownBackColor);
                Color border = Map(oldPalette, fa.BorderColor);
                if (hover != fa.MouseOverBackColor) fa.MouseOverBackColor = hover;
                if (down != fa.MouseDownBackColor) fa.MouseDownBackColor = down;
                if (border != fa.BorderColor) fa.BorderColor = border;
            }

            // GlyphButton рисует заливку, подложку и рамку сам в OnPaint, а эти цвета не
            // свойства Control — смена темы их не трогала, и в светлой теме кнопки
            // оставались тёмными пятнами с тёмным глифом внутри
            var glyphBtn = c as GlyphButton;
            if (glyphBtn != null)
            {
                Color baseFill = Map(oldPalette, glyphBtn.BaseFill);
                if (baseFill != glyphBtn.BaseFill) glyphBtn.BaseFill = baseFill;
                Color gbHover = Map(oldPalette, glyphBtn.HoverFill);
                if (gbHover != glyphBtn.HoverFill) glyphBtn.HoverFill = gbHover;
                Color gbPress = Map(oldPalette, glyphBtn.PressFill);
                if (gbPress != glyphBtn.PressFill) glyphBtn.PressFill = gbPress;
                Color gbBorder = Map(oldPalette, glyphBtn.BorderColor);
                if (gbBorder != glyphBtn.BorderColor) glyphBtn.BorderColor = gbBorder;
            }
        }

        private static Color Map(Theme oldPalette, Color c)
        {
            if (c == oldPalette.BgSide) return Current.BgSide;
            if (c == oldPalette.BgMain) return Current.BgMain;
            if (c == oldPalette.BgCard) return Current.BgCard;
            if (c == oldPalette.BgHover) return Current.BgHover;
            if (c == oldPalette.BgSel) return Current.BgSel;
            if (c == oldPalette.Line) return Current.Line;
            if (c == oldPalette.TextMain) return Current.TextMain;
            if (c == oldPalette.TextSub) return Current.TextSub;
            if (c == oldPalette.TextFaint) return Current.TextFaint;
            if (c == oldPalette.Accent) return Current.Accent;
            if (c == oldPalette.AccentHot) return Current.AccentHot;
            if (c == oldPalette.LikeGold) return Current.LikeGold;
            if (c == oldPalette.WaveAccent) return Current.WaveAccent;
            return c;
        }
    }

    /// <summary>Динамическая палитра: все обращения из отрисовки читают текущую тему.</summary>
    internal static class YmPalette
    {
        public static bool IsDark
        {
            get { return ThemeManager.Current.IsDark; }
        }

        public static Color BgSide { get { return ThemeManager.Current.BgSide; } }
        public static Color BgMain { get { return ThemeManager.Current.BgMain; } }
        public static Color BgCard { get { return ThemeManager.Current.BgCard; } }
        public static Color BgHover { get { return ThemeManager.Current.BgHover; } }
        public static Color BgSel { get { return ThemeManager.Current.BgSel; } }
        public static Color Line { get { return ThemeManager.Current.Line; } }
        public static Color TextMain { get { return ThemeManager.Current.TextMain; } }
        public static Color TextSub { get { return ThemeManager.Current.TextSub; } }
        public static Color TextFaint { get { return ThemeManager.Current.TextFaint; } }
        public static Color Accent { get { return ThemeManager.Current.Accent; } }
        public static Color AccentHot { get { return ThemeManager.Current.AccentHot; } }
        public static Color LikeGold { get { return ThemeManager.Current.LikeGold; } }
        public static Color WaveAccent { get { return ThemeManager.Current.WaveAccent; } }
    }
}