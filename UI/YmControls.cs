using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YaMus.Core.Diagnostics;

namespace YaMus.UI
{
    /// <summary>Нарисованный скроллбар в цвет темы. Оверлей поверх хоста: нативная полоса
    /// прячется (ShowScrollBar), сдвиг выполняется через WM_VSCROLL — работает на
    /// Windows XP и новее, плюс убирает стандартные белые/серые полосы. Вертикальный.</summary>
    internal sealed class YaMusScrollBar : Control
    {
        private Control _host;
        private bool _dragging;
        private int _dragStartY;
        private int _dragStartPos;
        private int _pos;
        private int _max = 1;
        private int _page = 1;
        private bool _hoverOnThumb;

        private const int BarWidthMin = 17;
        private readonly int _barWidth = Math.Max(BarWidthMin, SystemInformation.VerticalScrollBarWidth);
        private const int WM_VSCROLL = 0x0115;
        private const int SB_THUMBTRACK = 5;
        private const int SB_ENDSCROLL = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollInfo
        {
            public uint cbSize;
            public uint fMask;
            public int nMin;
            public int nMax;
            public uint nPage;
            public int nPos;
            public int nTrackPos;
        }

        [DllImport("user32.dll")]
        private static extern bool GetScrollInfo(IntPtr hwnd, int fnBar, ref ScrollInfo lpsi);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hwnd, int wBar, bool bShow);

        [DllImport("user32.dll")]
        private static extern int SetScrollInfo(IntPtr hwnd, int fnBar, ref ScrollInfo lpsi, bool fRedraw);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int SIF_RANGE = 0x0001;
        private const int SIF_POS = 0x0004;
        private const int SIF_PAGE = 0x0002;

        // ListView: LVM_GETTOPINDEX возвращает первую видимую строку, LVM_SCROLL
        // прокручивает на заданное число пикселей по вертикали
        private const int LVM_FIRST = 0x1000;
        private const int LVM_GETTOPINDEX = LVM_FIRST + 39;
        private const int LVM_SCROLL = LVM_FIRST + 20;

        public YaMusScrollBar()
        {
            Width = _barWidth;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        /// <summary>Навешивается на контрол с прокруткой (ListView/TreeView):
        /// вертикальная нативная полоса остаётся (чтобы не появлялась белая/пустая
        /// полоса в заголовках) и накрывается оверлеем в цвет темы; горизонтальная
        /// нативная полоса прячется совсем, чтобы не мигала.</summary>
        public void Attach(Control host)
        {
            if (_host == host) return;
            if (_host != null)
            {
                _host.Resize -= OnHostResize;
                _host.Paint -= OnHostPaint;
                _host.MouseWheel -= OnHostWheel;
                _host.MouseEnter -= OnHostMouseEnter;
                _host.VisibleChanged -= OnHostVisibleChanged;
                _host.Layout -= OnHostLayout;
            }

            _host = host;
            if (_host == null) return;

            if (!Instances.Contains(this)) Instances.Add(this);

_host.Resize += OnHostResize;
            _host.Paint += OnHostPaint;
            _host.MouseWheel += OnHostWheel;
            _host.MouseEnter += OnHostMouseEnter;
            _host.VisibleChanged += OnHostVisibleChanged;
            _host.Layout += OnHostLayout;

            // Вертикальную полосу рисуем сами. Горизонтальная не нужна нигде, но WinForms
            // возвращает WS_HSCROLL при каждой перевёрстке хоста (FlowLayoutPanel считает
            // диапазон по содержимому), поэтому держим полосу скрытой и в Layout.
            HideHostHorizontalBar();

            if (host.Parent != null)
            {
                Parent = host.Parent;
                BringToFront();
            }

            OnHostResize(host, EventArgs.Empty);
            Sync();
        }

        private void OnHostResize(object sender, EventArgs e)
        {
            if (_host == null || _host.IsDisposed) return;
            Bounds = new Rectangle(_host.Right - _barWidth, _host.Top, _barWidth, _host.Height);
            Sync();
        }

        private void OnHostLayout(object sender, LayoutEventArgs e)
        {
            HideHostHorizontalBar();
        }

        /// <summary>Гасит системную горизонтальную полосу и обнуляет её диапазон: без этого
        /// FlowLayoutPanel возвращает WS_HSCROLL при каждой перевёрстке.</summary>
        private void HideHostHorizontalBar()
        {
            if (_host == null || _host.IsDisposed) return;
            if (!_host.IsHandleCreated) return;
            try
            {
                var scrollable = _host as ScrollableControl;
                if (scrollable != null)
                {
                    // сначала диапазон: SetScrollInfo может вернуть стиль WS_HSCROLL
                    var h = scrollable.HorizontalScroll;
                    if (h.Minimum != 0 || h.Maximum != 0 || h.Value != 0)
                    {
                        h.Value = 0;
                        h.Minimum = 0;
                        h.Maximum = 0;
                    }
                }
                // ...и только потом гасим саму полосу — это должно быть последним действием
                ShowScrollBar(_host.Handle, 0 /* SB_HORZ */, false);
            }
            catch
            {
            }
        }

        private void OnHostMouseEnter(object sender, EventArgs e)
        {
            Sync();
            // Фокус хосту здесь намеренно НЕ забираем. Раньше это делалось, чтобы
            // колесо крутило список: WM_MOUSEWHEEL уходит окну с фокусом, а не под
            // курсором. Теперь колесо маршрутизирует WheelRouter (IMessageFilter) —
            // он смотрит на позицию курсора, и фокус не нужен. А забирание фокуса
            // приводило к побочному эффекту: TreeView сайдбара без выбранного узла
            // (страница исполнителя/альбома) при фокусе сам выделял первый узел,
            // т.е. «Главную», и страница переключалась просто от наведения мыши.
        }

        private void OnHostPaint(object sender, PaintEventArgs e)
        {
            Sync();
        }

        private void OnHostWheel(object sender, MouseEventArgs e)
        {
            Sync();
        }

        // страницы переключаются через Visible: оверлей должен прятаться вместе с хостом
        private void OnHostVisibleChanged(object sender, EventArgs e)
        {
            if (_host == null || _host.IsDisposed) return;
            if (!_host.Visible)
            {
                Visible = false;
                return;
            }
            Sync();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            TryScrollByWheel(e.Delta);
            _host?.Invalidate();
        }

        /// <summary>Шаг прокрутки на один щелчок колеса: у списков/деревьев — строки
        /// (3, как у нативных ListView), у скроллящихся панелей — пиксели.</summary>
        private int WheelStep()
        {
            if (_host is ListView || _host is TreeView) return 3;
            return Math.Max(16, _page / 6);
        }

        /// <summary>Текущие pos/max/page из нативного состояния (работает даже при
        /// скрытой полосе). Вызывается при перерисовке/сообщениях хоста.</summary>
        public void Sync()
        {
            if (_host == null || _host.IsDisposed || _host.Handle == IntPtr.Zero) return;
            try
            {
                var si = new ScrollInfo();
                si.cbSize = (uint)Marshal.SizeOf(typeof(ScrollInfo));
                si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
                if (!GetScrollInfo(_host.Handle, 1 /* SB_VERT */, ref si)) return;

                long range = (long)si.nMax + 1 - si.nMin;
                bool hasScroll = si.nPage > 0 && range > si.nPage;
                Visible = hasScroll;
                if (!hasScroll) return;

                // max позиция = nMax - nPage + 1 (раньше бралось range-1, из-за чего
                // ползунок не доезжал до низа, а зазор рос с размером ползунка)
                _max = (int)Math.Max(1, range - (long)si.nPage);
                _page = Math.Max(1, (int)si.nPage);
                _pos = si.nPos;
                Invalidate();
            }
            catch
            {
            }
        }

        private void ScrollTo(int pos)
        {
            if (_host == null || _host.IsDisposed) return;
            try
            {
                pos = Math.Min(_max, Math.Max(0, pos));

                // ListView: единицы скролла = строки, nPos = первая видимая строка
                var lv = _host as ListView;
                if (lv != null && lv.Items.Count > 0)
                {
                    int idx = Math.Min(lv.Items.Count - 1, Math.Max(0, pos));
                    try { lv.TopItem = lv.Items[idx]; }
                    catch { }

                    // TopItem молча игнорируется, если ListView ещё не пересчитал
                    // диапазон после resize/maximize — тогда «скролл есть, а список
                    // стоит на месте». В этом случае едем явно через LVM_SCROLL.
                    int actual = ListViewTopIndex(lv);
                    if (actual != idx)
                    {
                        try
                        {
                            int rowH = lv.Items.Count > 0 ? Math.Max(1, lv.Items[0].Bounds.Height) : 20;
                            int dy = (idx - actual) * rowH;
                            if (dy != 0) SendMessage(lv.Handle, LVM_SCROLL, IntPtr.Zero, new IntPtr(dy));
                        }
                        catch
                        {
                        }
                    }

                    Sync();
                    return;
                }

                // TreeView: единицы = узлы
                var tv = _host as TreeView;
                if (tv != null)
                {
                    TreeNode target = NodeAt(tv, pos);
                    if (target != null)
                    {
                        // только TopNode: SelectedNode переключил бы страницу через AfterSelect
                        tv.TopNode = target;
                    }
                    Sync();
                    return;
                }

                // скроллящиеся панели (FlowLayoutPanel страницы артиста): единицы = пиксели
                var sc = _host as ScrollableControl;
                if (sc != null)
                {
                    sc.AutoScrollPosition = new Point(0, pos);
                    Sync();
                    return;
                }

                // fallback: нативный скролл
                var si = new ScrollInfo();
                si.cbSize = (uint)Marshal.SizeOf(typeof(ScrollInfo));
                si.fMask = SIF_POS;
                si.nPos = pos;
                SetScrollInfo(_host.Handle, 1 /* SB_VERT */, ref si, false);
                SendMessage(_host.Handle, WM_VSCROLL, new IntPtr((pos << 16) | SB_THUMBTRACK), IntPtr.Zero);
                Sync();
            }
            catch
            {
            }
        }

        /// <summary>Прокрутка хостом по «колесу» в сторону delta. Возвращает false,
        /// если прокручивать нечего (сообщение должно уйти дальше).</summary>
        internal bool TryScrollByWheel(int delta)
        {
            if (_host == null || _host.IsDisposed || _host.Handle == IntPtr.Zero) return false;
            Sync();
            if (!Visible || _max <= 0) return false;
            int step = delta > 0 ? -WheelStep() : WheelStep();
            ScrollTo(_pos + step);
            try { _host.Invalidate(); } catch { }
            return true;
        }

        /// <summary>Все подключённые оверлеи: нужны глобальному маршрутизатору колеса,
        /// чтобы прокручивать список под курсором независимо от того, где фокус.</summary>
        private static readonly List<YaMusScrollBar> Instances = new List<YaMusScrollBar>();

        /// <summary>Хост (список/дерево/панель), находящийся под текущим курсором.</summary>
        internal static YaMusScrollBar HostAtCursor()
        {
            Point cur = Cursor.Position;
            YaMusScrollBar found = null;
            for (int i = Instances.Count - 1; i >= 0; i--)
            {
                YaMusScrollBar sb = Instances[i];
                if (sb == null || sb.IsDisposed || sb._host == null || sb._host.IsDisposed)
                {
                    Instances.RemoveAt(i);
                    continue;
                }
                if (!sb.Visible || !sb._host.Visible) continue;
                if (sb._host.Parent != null && !sb._host.Parent.Visible) continue;
                Rectangle r;
                try { r = sb._host.RectangleToScreen(sb._host.ClientRectangle); }
                catch { continue; }
                if (!r.Contains(cur)) continue;
                if (found == null || sb._host.Height > found._host.Height) found = sb;
            }
            return found;
        }

        private static TreeNode NodeAt(TreeView tv, int target)
        {
            int cur = 0;
            foreach (TreeNode root in tv.Nodes)
            {
                TreeNode found = FindVisible(root, ref cur, target);
                if (found != null) return found;
            }
            return null;
        }

        private static TreeNode FindVisible(TreeNode n, ref int cur, int target)
        {
            if (!n.IsVisible) return null;
            if (cur == target) return n;
            cur++;
            foreach (TreeNode child in n.Nodes)
            {
                TreeNode found = FindVisible(child, ref cur, target);
                if (found != null) return found;
            }
            return null;
        }

        private void FinishScroll()
        {
            if (_host == null || _host.IsDisposed || _host.Handle == IntPtr.Zero) return;
            try
            {
                SendMessage(_host.Handle, WM_VSCROLL, new IntPtr(SB_ENDSCROLL), IntPtr.Zero);
                Sync();
            }
            catch
            {
            }
        }

        /// <summary>Первая видимая строка ListView (LVM_GETTOPINDEX). Нужна, чтобы
        /// проверить, сработал ли TopItem, и при необходимости дослать LVM_SCROLL.</summary>
        private static int ListViewTopIndex(ListView lv)
        {
            try { return SendMessage(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero); }
            catch { return 0; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Visible || e.Button != MouseButtons.Left) return;

            int len = Height - 8;
            int thumbLen = ThumbLength(len);
            int thumbY = ThumbTop(len, thumbLen);

            if (e.Y >= thumbY && e.Y <= thumbY + thumbLen)
            {
                _dragging = true;
                _dragStartY = e.Y;
                _dragStartPos = _pos;
                Capture = true;
                Invalidate();
                return;
            }

            // клик по дорожке — листаем на страницу
            int step = e.Y < thumbY ? -_page : _page;
            ScrollTo(_pos + step);
            _host?.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                int len = Height - 8;
                int thumbLen = ThumbLength(len);
                int usable = Math.Max(1, len - thumbLen);
                int dy = e.Y - _dragStartY;
                int target = _dragStartPos + (int)Math.Round(dy * (_max / (double)usable));
                ScrollTo(target);
                return;
            }

            int tl = ThumbLength(Height - 8);
            int ty = ThumbTop(Height - 8, tl);
            bool over = e.Y >= ty && e.Y <= ty + tl;
            if (over != _hoverOnThumb)
            {
                _hoverOnThumb = over;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                FinishScroll();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverOnThumb = false;
            Invalidate();
        }

        private int ThumbLength(int len)
        {
            double frac = _page / (double)(_page + _max);
            int t = (int)Math.Round(len * frac);
            return Math.Max(24, Math.Min(len, t));
        }

        private int ThumbTop(int len, int thumbLen)
        {
            if (_max <= 0) return 4;
            int usable = len - thumbLen;
            int top = 4 + (int)Math.Round(_pos * (usable / (double)_max));
            return Math.Max(4, Math.Min(4 + usable, top));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (!Visible) return;

            // полностью закрываем нативную полосу под оверлеем фоном контрола
            Color bg = _host?.BackColor ?? YmPalette.BgMain;
            using (var b = new SolidBrush(bg))
            {
                g.FillRectangle(b, 0, 0, Width, Height);
            }

            int len = Height - 8;
            int thumbLen = ThumbLength(len);
            int thumbY = ThumbTop(len, thumbLen);

            int trackX = Width / 2 - 1;
            using (var track = new SolidBrush(YmPalette.Line))
            {
                g.FillRectangle(track, trackX, 4, 3, len);
            }

            // бегунок: акцент при наведении/перетаскивании, иначе чуть светлее дорожки
            Color thumbColor = (_hoverOnThumb || _dragging) ? YmPalette.Accent : YmPalette.BgHover;
            int thumbX = Width / 2 - 2;
            using (var thumb = new SolidBrush(thumbColor))
            {
                using (var path = RoundedRect(new Rectangle(thumbX, thumbY, 5, thumbLen), 3))
                {
                    g.FillPath(thumb, path);
                }
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
            if (disposing && _host != null)
            {
                _host.Resize -= OnHostResize;
                _host.Paint -= OnHostPaint;
                _host.MouseWheel -= OnHostWheel;
                _host.MouseEnter -= OnHostMouseEnter;
                _host.VisibleChanged -= OnHostVisibleChanged;
                _host = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Набор иконок-глифов с проверкой наличия символа в шрифтах.
    /// В сборку вшит открытый шрифт Symbola (Assets\Symbola.ttf) — он гарантирует
    /// корректный вид современных символов (▶, ❙, ♥, ⇄, ↻, ◉…) даже на XP,
    /// где системных символьных шрифтов нет. Если встраивание не удалось,
    /// работает прежний отбор по системным шрифтам (font linking).
    /// </summary>
    internal static class UiGlyphs
    {
        private static readonly PrivateFontCollection EmbeddedFonts = new PrivateFontCollection();

        private static GCHandle _fontMemoryPin;

        /// <summary>Байты шрифта. GDI после AddFontMemResourceEx продолжает читать их
        /// всю жизнь процесса, поэтому ссылку на массив держим в статическом поле
        /// и никогда не отпускаем вместе с закреплённым указателем.</summary>
        private static byte[] _fontBytes;

        /// <summary>Хэнд шрифта в таблице GDI (нужен, чтобы не дать GDI выгрузить его).</summary>
        private static IntPtr _gdiFontHandle;

        /// <summary>Семейство встроенного символьного шрифта (или null, если не загрузился).</summary>
        public static readonly FontFamily SymbolFamily = TryLoadEmbeddedFont();

        /// <summary>Имя семейства, под которым шрифт виден GDI. Именно по имени создаются
        /// шрифты в GlyphFont: контролы и TextRenderer рисуют через GDI, а GDI не видит
        /// шрифты из PrivateFontCollection — на XP он подставлял Tahoma и все глифы,
        /// кроме ♥, превращались в квадратики. AddFontMemResourceEx регистрирует шрифт
        /// в таблице шрифтов процесса, после чего он доступен и GDI, и GDI+.</summary>
        public static readonly string SymbolFamilyName = RegisterWithGdi();

        private static string RegisterWithGdi()
        {
            if (_fontBytes == null) return null;
            try
            {
                IntPtr numFonts;
                _gdiFontHandle = AddFontMemResourceEx(_fontBytes, (uint)_fontBytes.Length, IntPtr.Zero, out numFonts);
                if (_gdiFontHandle == IntPtr.Zero) return null;
                return EmbeddedFonts.Families.Length > 0 ? EmbeddedFonts.Families[0].Name : null;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AddFontMemResourceEx(byte[] fontData, uint size, IntPtr reserved, out IntPtr numFonts);

        private static FontFamily TryLoadEmbeddedFont()
        {
            try
            {
                Assembly asm = typeof(UiGlyphs).Assembly;
                string resName = FindFontResource(asm);
                if (resName == null) return null;
                using (Stream s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    byte[] data;
                    using (var copy = new MemoryStream())
                    {
                        s.CopyTo(copy);
                        data = copy.ToArray();
                    }
                    _fontBytes = data;
                    _fontMemoryPin = GCHandle.Alloc(data, GCHandleType.Pinned);
                    EmbeddedFonts.AddMemoryFont(_fontMemoryPin.AddrOfPinnedObject(), data.Length);
                }
                return EmbeddedFonts.Families.Length > 0 ? EmbeddedFonts.Families[0] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Имя встроенного ресурса задаётся RootNamespace проекта
        /// (YaMus.Assets.Symbola.ttf), а не пространством имён класса
        /// (…UI.Assets.Symbola.ttf) — ищем сначала точное имя, затем по суффиксу.</summary>
        private static string FindFontResource(Assembly asm)
        {
            string[] all = asm.GetManifestResourceNames();
            string direct = typeof(UiGlyphs).Namespace + ".Assets.Symbola.ttf";
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(all[i], direct, StringComparison.Ordinal)) return all[i];
            }
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].EndsWith(".Assets.Symbola.ttf", StringComparison.OrdinalIgnoreCase)) return all[i];
            }
            return null;
        }

        /// <summary>Рисуем глиф через GDI+. TextRenderer работает через GDI, который на XP
        /// не видит шрифты из PrivateFontCollection и подставляет Tahoma — тогда ▶/↻/⇄/❙
        /// превращаются в квадратики (рисовалось только ♥, найденный в MS Gothic).
        /// GDI+ видит встроенный шрифт всегда, поэтому глифы рисуем им — и не зависим
        /// от того, отработала ли регистрация шрифта в GDI. Плюс у GDI+ включён
        /// StringFormatFlags.NoClip: глиф не обрезается границами области, даже если
        /// метрики шрифта на XP отличаются (GDI по метрикам обрезал кончики ‹/›).</summary>
        public static void DrawGlyph(Graphics g, string glyph, Font font, Rectangle bounds, Color color)
        {
            if (g == null || font == null || string.IsNullOrEmpty(glyph)) return;
            GraphicsState state = g.Save();
            try
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var brush = new SolidBrush(color))
                using (var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                })
                {
                    g.DrawString(glyph, font, brush, bounds, sf);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }

        /// <summary>Размер нарисованных пикселей глифа, а не ширина шрифта: часть символов
        /// рисуется шире, чем отведённая под них advance-ширина. Если ничего не
        /// нарисовалось, возвращает (0,0).</summary>
        public static Size MeasureInk(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return Size.Empty;
            using (var bmp = new Bitmap(200, 96))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    TextRenderer.DrawText(g, text, font, new Rectangle(0, 0, 200, 96), Color.White,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
                return InkBounds(bmp);
            }
        }

        /// <summary>Прямоугольник «чернил» глифа внутри бокса, в который его рисует
        /// TextRenderer с центрированием. Symbola отводит символу часть эма — бокс
        /// центрируется по шрифту, а чернила (▶, ❙❙) сидят мимо центра на 1–3px.
        /// Возвращает прямоугольник, чтобы кнопка могла скомпенсировать смещение
        /// и отцентровать именно видимые пиксели.</summary>
        public static Rectangle MeasureInkRect(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return Rectangle.Empty;
            using (var bmp = new Bitmap(200, 96))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    TextRenderer.DrawText(g, text, font, new Rectangle(0, 0, 200, 96), Color.White,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
                return InkRect(bmp);
            }
        }

        private static Rectangle InkRect(Bitmap bmp)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color p = bmp.GetPixel(x, y);
                    if (p.R < 60 && p.G < 60 && p.B < 60) continue;      // чёрный фон — не чернила
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        /// <summary>Сколько места надо кнопке, чтобы глиф не срезался: берём максимум из
        /// advance-ширины (её Button резервирует под текст) и чернил (у глифов вроде
        /// ‹ чернила могут выходить за advance-ширину) + 4px запаса.</summary>
        public static Size GlyphNeeded(string text, Font font)
        {
            Size m = TextRenderer.MeasureText(text, font);
            Size ink = MeasureInk(text, font);
            return new Size(Math.Max(m.Width, ink.Width) + 4, Math.Max(m.Height, ink.Height) + 4);
        }

        /// <summary>Шрифт глифа и требуемый размер кнопки под него. Кегль не трогаем —
        /// иначе двойной треугольник ◀◀ (37px при 15pt) станет мелким; вместо этого
        /// возвращаем размер, до которого кнопка обязана дотянуться. Уменьшаем кегль
        /// только если глиф больше кнопки в разы (явная ошибка разметки).</summary>
        public static Tuple<Font, Size> FitGlyphFont(string glyph, int size, float desiredSize, FontStyle style)
        {
            Font f = GlyphFont(desiredSize, style);
            Size need = GlyphNeeded(glyph, f);
            if (need.Width <= size && need.Height <= size) return new Tuple<Font, Size>(f, need);
            if (need.Width > size * 2 || need.Height > size * 2)
            {
                for (float s = desiredSize - 0.5F; s >= 6F; s -= 0.5F)
                {
                    Font smaller = GlyphFont(s, style);
                    Size n = GlyphNeeded(glyph, smaller);
                    if (n.Width <= size && n.Height <= size) { f.Dispose(); return new Tuple<Font, Size>(smaller, n); }
                    smaller.Dispose();
                }
            }
            return new Tuple<Font, Size>(f, need);
        }

        private static Size InkBounds(Bitmap bmp)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color p = bmp.GetPixel(x, y);
                    if (p.R < 60 && p.G < 60 && p.B < 60) continue;      // чёрный фон — не чернила
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return Size.Empty;
            return new Size(maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>Шрифт для отрисовки глифов. Сначала — по имени, чтобы GDI (TextRenderer,
        /// текст контролов) увидел встроенный Symbola; если имя не разрешилось в
        /// наш шрифт (new Font молча подставил бы системный) — берём приватный
        /// экземпляр для GDI+. new Font не бросает исключение при отсутствии
        /// семейства, поэтому результат обязательно проверяем.</summary>
        public static Font GlyphFont(float size, FontStyle style)
        {
            if (!string.IsNullOrEmpty(SymbolFamilyName))
            {
                try
                {
                    Font byName = new Font(SymbolFamilyName, size, style);
                    if (byName.FontFamily.Name.IndexOf(SymbolFamilyName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return byName;
                    }
                    byName.Dispose();
                }
                catch
                {
                }
            }
            if (SymbolFamily != null)
            {
                try { return new Font(SymbolFamily, size, style); }
                catch { }
            }
            try { return new Font("Segoe UI Symbol", size, style); }
            catch { }
            try { return new Font("Segoe UI", size, style); }
            catch { }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        public static readonly string Play = Pick("▶", "►", ">");
        public static readonly string Pause = Pick("❙❙", "‖", "||", "▮▮");
        // ⏮/⏭ (U+23EE/U+23ED) рисуются с прямоугольной окантовкой и на мелком
        // размере выглядят как «иконка в рамке» — берём чистые двойные треугольники.
        public static readonly string Previous = Pick("◀◀", "◄◄", "<<");
        public static readonly string Next = Pick("▶▶", "►►", ">>");
        public static readonly string Shuffle = Pick("⇄", "↔", "S");
        public static readonly string Repeat = Pick("↻", "↺", "R");
        public static readonly string Like = Pick("♥", "*");
        public static readonly string Unlike = Pick("♡", "♥");
        public static readonly string Note = Pick("♪", "?");
        public static readonly string WaveIcon = Pick("◉", "•");
        public static readonly string HomeIcon = Pick("⌂", "♥", "•");
        public static readonly string SettingsIcon = Pick("⚙", "♥", "•");
        public static readonly string ExpanderOpen = Pick("▾", "-");
        public static readonly string ExpanderClosed = Pick("▸", "+");

        // Навигация по страницам. Отдельные глифы от Previous/Next (⏮/⏭):
        // те заняты переключением треков.
        public static readonly string NavBack = Pick("‹", "◀", "<");
        public static readonly string NavForward = Pick("›", "▶", ">");

        private static string Pick(params string[] candidates)
        {
            // Symbola вшит в сборку и покрывает все используемые символы. Но брать
            // символ «на веру» нельзя: если шрифт по какой-то причине не подхватился
            // (GDI подставит Tahoma, а там нет ▶/↻/⇄/❙) — на XP пользователь увидит
            // квадратики. Поэтому проверяем факт отрисовки тем же шрифтом, которым
            // будет рисовать кнопка.
            if (SymbolFamily != null || !string.IsNullOrEmpty(SymbolFamilyName))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (RendersAsGlyph(candidates[i])) return candidates[i];
                }
            }

            // Ни один вариант не рисуется — берём ASCII: он есть на любой системе.
            foreach (string s in candidates)
            {
                if (IsAscii(s)) return s;
            }
            return candidates[candidates.Length - 1];
        }

        private static bool IsAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 0x7F) return false;
            }
            return true;
        }

        /// <summary>Рисуется ли символ шрифтом как символ, а не как .notdef (квадратик)?
        /// Рисуем сам символ и столько же «заведомо отсутствующих» глифов U+FFFF
        /// и сравниваем картинки: совпали — значит GDI подставил шрифт без этого
        /// символа и на экране будет квадратик.</summary>
        private static bool RendersAsGlyph(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                using (var font = GlyphFont(12F, FontStyle.Regular))
                {
                    using (Bitmap glyph = RenderInk(text, font))
                    using (Bitmap missing = RenderInk(new string('￿', text.Length), font))
                    {
                        return !SameInk(glyph, missing);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static Bitmap RenderInk(string text, Font font)
        {
            var bmp = new Bitmap(40, 30);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                TextRenderer.DrawText(g, text, font, new Rectangle(0, 0, 40, 30), Color.White,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            return bmp;
        }

        private static bool SameInk(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            byte[] pa = InkBytes(a), pb = InkBytes(b);
            if (pa.Length != pb.Length) return false;
            // Рендер детерминирован, поэтому картинки либо совпадают, либо различаются
            // заметно. Допуск нужен лишь на пару байт края — иначе мелкие глифы (▾, ‹)
            // принимались бы за квадратик.
            int diff = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i] != pb[i] && ++diff > 4) return false;
            }
            return true;
        }

        private static byte[] InkBytes(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var buf = new byte[data.Stride * data.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                return buf;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
    /// <summary>Нативная тёмная тема (заголовок, скроллбары). Win10 1809+; на Win11
    /// скроллбары красятся через режим приложения, поэтому SetPreferredAppMode
    /// вызывается из Program.Main до создания окон. Бест-эффорт, без исключений.</summary>
    internal static class NativeTheme
    {
        private enum PreferredAppMode
        {
            Default,
            AllowDark,
            ForceDark,
            ForceLight,
            Max
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("uxtheme.dll")]
        private static extern int SetPreferredAppMode(PreferredAppMode appMode);

        [DllImport("uxtheme.dll")]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>Разрешает тёмный режим для всего процесса. Вызывать до Application.Run.</summary>
        public static void PreferDark()
        {
            try
            {
                SetPreferredAppMode(PreferredAppMode.AllowDark);
            }
            catch
            {
            }
        }

        /// <summary>Тёмный режим для окна/контрола: иммерсивная атрибутика DWM + AllowDarkModeForWindow.</summary>
        public static void EnableDarkWindow(Control c)
        {
            if (c == null || c.IsDisposed) return;
            IntPtr h = c.Handle;
            try
            {
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win10 1809+), 19 — более ранние билды
                int enabled = 1;
                DwmSetWindowAttribute(h, 20, ref enabled, 4);
                DwmSetWindowAttribute(h, 19, ref enabled, 4);
                AllowDarkModeForWindow(h, true);
            }
            catch
            {
            }
        }

        public static void EnableDarkScrollbars(Control c)
        {
            if (c == null || c.IsDisposed || c.Handle == IntPtr.Zero) return;
            try
            {
                EnableDarkWindow(c);
                // тёмные скроллбары в стиле Проводника (классический скроллбар Win10)
                SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            }
            catch
            {
            }
        }
    }

    /// <summary>ListView с тёмным фоном для пустой/незаполненной области (при OwnerDraw остаток рисуется белым).</summary>
    internal sealed class DarkListView : ListView
    {

        private const int WM_SIZE = 0x0005;
        private const int WM_HSCROLL = 0x0114;
        private const int WM_VSCROLL = 0x0115;

        /// <summary>Срабатывает после изменений, влияющих на скролл-состояние
        /// (появление/исчезновение полос, ресайз) — MainForm пересчитывает ширины колонок.</summary>
        public event Action ScrollStateChanged;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hwnd, int wBar, bool bShow);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // горизонтальная полоса не нужна никогда: когда ListView показывает её сам
            // (при переключении списков/смене клиентской ширины), прячем сразу —
            // иначе он мигает до клика/прокрутки
            if (m.Msg == WM_SIZE || m.Msg == WM_HSCROLL || m.Msg == WM_VSCROLL)
            {
                try
                {
                    ShowScrollBar(Handle, 0 /* SB_HORZ */, false);
                }
                catch
                {
                }

                Action h = ScrollStateChanged;
                if (h != null) h();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            using (var bg = new SolidBrush(YmPalette.BgMain))
            {
                pevent.Graphics.FillRectangle(bg, pevent.ClipRectangle);
            }
        }
    }

    /// <summary>Круглая кнопка с собственной отрисовкой (центрирование глифа).
    /// Форму задаёт Region: без него базовый Button рисует прямоугольный
    /// BackColor и по углам круга появляются цветные углы.</summary>
    /// <summary>Глобальный маршрутизатор колеса мыши. Windows шлёт WM_MOUSEWHEEL окну
    /// с фокусом, а не тому, что под курсором: фокус часто у оверлея «сейчас играет»
    /// или у кнопки, и список перестаёт крутиться, хотя курсор над ним. Перехватываем
    /// сообщение и прокручиваем список под курсором; если такого нет — пропускаем
    /// дальше (обычное поведение для полей ввода и кнопок).</summary>
    internal sealed class WheelRouter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private static WheelRouter _installed;

        public static void Install()
        {
            if (_installed != null) return;
            _installed = new WheelRouter();
            Application.AddMessageFilter(_installed);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            YaMusScrollBar sb = YaMusScrollBar.HostAtCursor();
            if (sb == null) return false;
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (delta == 0) return false;
            return sb.TryScrollByWheel(delta);
        }
    }

    internal sealed class RoundButton : Button
    {
        private bool _hover;
        private bool _pressed;
        private bool _loading;
        private Timer _spin;

        // Смещение, на которое чернила глифа сидят мимо центра бокса TextRenderer.
        // Считаем один раз на пару (текст, шрифт): Symbola отводит символам часть
        // эма, и центрирование по шрифту съезжает на 1–3px от центра кнопки.
        private static readonly Dictionary<string, Point> InkOffsetCache = new Dictionary<string, Point>();

        private static Point InkOffset(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return Point.Empty;
            string key = text + "|" + font.FontFamily.Name + "|" + font.SizeInPoints + "|" + font.Style;
            Point p;
            if (InkOffsetCache.TryGetValue(key, out p)) return p;
            Rectangle r = UiGlyphs.MeasureInkRect(text, font);
            if (r.IsEmpty)
            {
                InkOffsetCache[key] = Point.Empty;
                return Point.Empty;
            }
            // навстречу смещению чернил: их центр должен совпасть с центром бокса
            p = new Point(100 - (r.Left + r.Right) / 2, 48 - (r.Top + r.Bottom) / 2);
            if (InkOffsetCache.Count > 32) InkOffsetCache.Clear();
            InkOffsetCache[key] = p;
            return p;
        }

        public RoundButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.StandardClick, false);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
                Region = new Region(path);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

        /// <summary>Режим «трек грузится»: вместо глифа Play/Pause рисуем крутящиеся
        /// точки. Таймер работает только пока идёт загрузка — в покое перерисовок
        /// нет, чтобы не грузить слабый Atom.</summary>
        public bool Loading
        {
            get { return _loading; }
            set
            {
                if (_loading == value) return;
                _loading = value;
                if (value)
                {
                    if (_spin == null)
                    {
                        _spin = new Timer();
                        _spin.Interval = 60;
                        _spin.Tick += (s, e) => Invalidate();
                    }
                    _spin.Start();
                }
                else if (_spin != null)
                {
                    _spin.Stop();
                }
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_spin != null)
                {
                    _spin.Dispose();
                    _spin = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>Фон не рисуем: заливкой занимается OnPaint, и она у нас круглая.</summary>
        protected override void OnPaintBackground(PaintEventArgs pe)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color c = BackColor;
            if (_pressed) c = Darken(c, 0.82f);
            else if (_hover && FlatAppearance.MouseOverBackColor != Color.Empty) c = FlatAppearance.MouseOverBackColor;

            using (var b = new SolidBrush(c))
            {
                g.FillEllipse(b, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            }

            // лёгкая посадка внутрь, чтобы глиф не прилипал к нижней кромке круга
            Rectangle textR = ClientRectangle;
            if (textR.Width > 4) { textR.X += 2; textR.Width -= 4; }
            if (textR.Height > 4) { textR.Y += 2; textR.Height -= 4; }

            if (_loading)
            {
                DrawSpinner(g, ForeColor);
                return;
            }

            Point adj = InkOffset(Text, Font);
            Rectangle tr = textR;
            tr.X += adj.X;
            tr.Y += adj.Y - OpticalNudge();
            TextRenderer.DrawText(g, Text, Font, tr, ForeColor,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        /// <summary>Оптическая поправка по вертикали: на малом круге геометрический центр
        /// кажется ниже, и глиф/загрузка «висят» под центром. Приподнимаем значок тем
        /// сильнее, чем меньше кнопка; на больших кнопках оверлея (>48px) не трогаем —
        /// там измерения чернил уже дают точное центрирование.</summary>
        private int OpticalNudge()
        {
            int size = Math.Min(Width, Height);
            if (size >= 48) return 0;
            return Math.Max(1, (48 - size) / 10);   // 34px → 1px, 24px → 2px
        }

        /// <summary>Крутящиеся точки: центр — центр нарисованного круга ((W-1)/2, (H-1)/2),
        /// а не прямоугольника кнопки: иначе на чётных размерах загрузка съезжает на
        /// полпикселя вправо-вниз. Фаза зависит от времени — при перерисовке по таймеру
        /// загрузка выглядит движущейся.</summary>
        private void DrawSpinner(Graphics g, Color color)
        {
            // по горизонтали глиф сидит на W/2; загрузку хотелось чуть левее (компенсация
            // вращения кольца + крышка кнопки), на малой кнопке — заметнее: 34px → -2px,
            // оверлей (>48px) не трогаем, там центрирование уже точное
            float cx = Width / 2f - 1f;
            if (Math.Min(Width, Height) < 48) cx -= 0.5f;
            float cy = (Height - 1) / 2f - OpticalNudge();
            float rad = Math.Min(Width, Height) * 0.24f;
            int phase = Environment.TickCount / 45;
            for (int i = 0; i < 10; i++)
            {
                double angle = (phase + i) * Math.PI / 5.0;
                int x = (int)Math.Round(cx + (float)(Math.Cos(angle) * rad));
                int y = (int)Math.Round(cy + (float)(Math.Sin(angle) * rad));
                int alpha = 255 - i * 23;
                if (alpha < 30) alpha = 30;
                using (var b = new SolidBrush(Color.FromArgb(alpha, color)))
                {
                    g.FillEllipse(b, x - 2, y - 2, 5, 5);
                }
            }
        }

        static Color Darken(Color c, float f)
        {
            return Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }
    }

    /// <summary>Кнопка, глиф которой можно зеркалить по горизонтали — так «повтор одного»
    /// показывается отражённой стрелкой без цифры «1».</summary>
    internal sealed class MirrorGlyphButton : Button
    {
        private string _glyph = "";
        private bool _mirrored;

        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value ?? ""; Invalidate(); }
        }

        public bool Mirrored
        {
            get { return _mirrored; }
            set { _mirrored = value; Invalidate(); }
        }

        public MirrorGlyphButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            Text = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrEmpty(_glyph)) return;

            Graphics g = e.Graphics;
            var state = g.Save();
            try
            {
                // Оба режима рисуем одним путём через GDI+ (NoClip): TextRenderer идёт
                // через GDI и подрезал глиф по строчному боксу сверху/снизу, а на XP
                // ещё и не видел встроенный шрифт. Зеркало — трансформацией Graphics.
                if (_mirrored)
                {
                    g.TranslateTransform(Width, 0);
                    g.ScaleTransform(-1f, 1f);
                }
                UiGlyphs.DrawGlyph(g, _glyph, Font, ClientRectangle, ForeColor);
            }
            finally
            {
                g.Restore(state);
            }
        }
    }

    /// <summary>Обложка трека в плеере: при наведении темнеет и показывает стрелку вверх —
    /// намёк, что откроется замещающий режим «сейчас играет».</summary>
    internal sealed class CoverBox : Control
    {
        private Image _image;
        private bool _hover;
        private bool _pressed;

        public Image Image
        {
            get { return _image; }
            set { _image = value; Invalidate(); }
        }

        public CoverBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = YmPalette.BgCard;
            Cursor = Cursors.Hand;
        }

protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var b = new SolidBrush(BackColor))
            {
                g.FillRectangle(b, 0, 0, Width, Height);
            }

            if (_image != null)
            {
                // вписываем по большей стороне с сохранением пропорций (как SizeMode.Zoom)
                double scale = Math.Min((double)Width / _image.Width, (double)Height / _image.Height);
                int w = Math.Max(1, (int)Math.Round(_image.Width * scale));
                int h = Math.Max(1, (int)Math.Round(_image.Height * scale));
                g.DrawImage(_image, (Width - w) / 2, (Height - h) / 2, w, h);
            }

            if (!_hover) return;

            // затемнение + стрелка вверх
            int alpha = _pressed ? 170 : 140;
            using (var dim = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
            {
                g.FillRectangle(dim, 0, 0, Width, Height);
            }

            int size = Math.Max(10, Math.Min(20, Math.Min(Width, Height) / 2));
            using (var f = UiGlyphs.GlyphFont(size, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "▲", f, ClientRectangle, Color.FromArgb(240, 240, 245),
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>Кнопка-иконка, которая умеет зеркалить глиф. Используется в оверлее
    /// «сейчас играет» — там своя палитра от цвета обложки.</summary>
    internal sealed class GlyphButton : Button
    {
        private string _glyph = "";
        private bool _mirrored;
        private bool _hover;
        private bool _pressed;
        private bool _busy;

        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value ?? ""; Invalidate(); }
        }

        /// <summary>Режим «идёт переключение трека»: заливается фоном нажатия и держится,
        /// пока новый трек реально не заиграл — иначе клик по кнопке не видно.</summary>
        public bool Busy
        {
            get { return _busy; }
            set
            {
                if (_busy == value) return;
                _busy = value;
                Invalidate();
            }
        }

        /// <summary>Зеркалить глиф по горизонтали (через GDI+, TextRenderer игнорирует transform).</summary>
        public bool Mirrored
        {
            get { return _mirrored; }
            set { _mirrored = value; Invalidate(); }
        }

        /// <summary>Подложка при наведении.</summary>
        public Color HoverFill { get; set; }

        public Color PressFill { get; set; }

        /// <summary>Толщина рамки вокруг кнопки (px). 0 — без рамки. Нужна кнопкам
        /// истории назад/вперёд: они рисуют фон сами, поэтому FlatAppearance.BorderSize
        /// на них не действует — рамку приходится рисовать вручную.</summary>
        public int BorderSize
        {
            get { return _borderSize; }
            set { if (_borderSize == value) return; _borderSize = value; Invalidate(); }
        }

        private int _borderSize;

        public Color BorderColor { get; set; }

        /// <summary>Цвет заливки в покое. Оверлей подставляет сюда цвет своего
        /// градиента в точке, где стоит кнопка, — тогда кнопка не выделяется
        /// на фоне.</summary>
        public Color BaseFill
        {
            get { return _baseFill; }
            set { _baseFill = value; Invalidate(); }
        }

        private Color _baseFill = YmPalette.BgCard;

        public GlyphButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = "";
            Size = new Size(40, 40);
            // цвета — из текущей темы, а не зашитые тёмные: в светлой теме заливка
            // оставалась тёмной, а глиф в ней тёмным, и кнопка выглядела сломанной
            BaseFill = YmPalette.BgCard;
            HoverFill = YmPalette.BgHover;
            PressFill = YmPalette.BgSel;
            BorderColor = YmPalette.TextFaint;
        }

        /// <summary>Фон не рисуем: базовый Button заливал бы BackColor своим цветом.</summary>
        protected override void OnPaintBackground(PaintEventArgs pe)
        {
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Фон заливаем всегда и строго прямоугольником: при Color.Transparent
            // буфер двойной буферизации не очищается и в кнопке остаётся мусор от
            // прошлого кадра родителя. Заливать нужно ЦВЕТОМ ГРАДИЕНТА В ЭТОЙ ТОЧКЕ
            // (его задаёт оверлей через BaseFill), иначе кнопка видна как пятно.
            using (var b = new SolidBrush(BaseFill))
            {
                g.FillRectangle(b, 0, 0, Width, Height);
            }

            // Подложка появляется при наведении, в режиме busy — фон нажатия.
            if (_hover || _pressed || _busy)
            {
                Color fill = _busy || _pressed ? PressFill : HoverFill;
                using (var b = new SolidBrush(fill))
                {
                    g.FillRectangle(b, 0, 0, Width, Height);
                }
            }

            if (_borderSize > 0)
            {
                using (var pen = new Pen(BorderColor, _borderSize))
                {
                    float h = _borderSize / 2F;
                    g.DrawRectangle(pen, h, h, Width - _borderSize, Height - _borderSize);
                }
            }

            if (string.IsNullOrEmpty(_glyph)) return;

            var state = g.Save();
            try
            {
                if (_mirrored)
                {
                    // Зеркалить можно только через GDI+: TextRenderer игнорирует
                    // трансформацию Graphics. Шрифт при этом берётся тот же.
                    g.TranslateTransform(Width, 0);
                    g.ScaleTransform(-1f, 1f);
                    UiGlyphs.DrawGlyph(g, _glyph, Font, ClientRectangle, ForeColor);
                }
                else
                {
                    // Рисуем сами (GDI+), а не через Button: базовый Button и GDI
                    // обрезали глифы по метрикам шрифта — на XP у ‹/› срезались
                    // кончики. DrawString с NoClip этого не делает.
                    UiGlyphs.DrawGlyph(g, _glyph, Font, ClientRectangle, ForeColor);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }
    }

    /// <summary>Кастомная полоса прогресса/перемотки в стиле плеера.</summary>
    internal sealed class SeekBar : Control
    {
        private int _durationSeconds;
        private int _positionSeconds;
        private bool _dragging;

        public int DurationSeconds
        {
            get { return _durationSeconds; }
            set { _durationSeconds = value; Invalidate(); }
        }

        public int PositionSeconds
        {
            get { return _positionSeconds; }
            set { _positionSeconds = value; Invalidate(); }
        }

        public event Action<int> SeekTo;

        public SeekBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Size = new Size(100, 16);
            Cursor = Cursors.Hand;
        }

        private float FractionFromMouse(int x)
        {
            if (Width <= 0) return 0f;
            float f = (x - 3f) / (Width - 6f);
            return Math.Max(0f, Math.Min(1f, f));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _dragging = true;
            int s = (int)(FractionFromMouse(e.X) * _durationSeconds);
            if (SeekTo != null) SeekTo(s);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                int s = (int)(FractionFromMouse(e.X) * _durationSeconds);
                if (SeekTo != null) SeekTo(s);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float h = Height;
            float cy = h / 2f;
            float w = Width;

            float fillWidth = _durationSeconds > 0
                ? (w - 6f) * Math.Max(0f, Math.Min(1f, (float)_positionSeconds / _durationSeconds))
                : 0f;

            using (var track = new SolidBrush(YmPalette.Line))
            using (var fill = new SolidBrush(YmPalette.Accent))
            // ползунок тоже по теме: зашитый светлый был почти не виден на светлом фоне
            using (var thumb = new SolidBrush(Color.FromArgb(220, YmPalette.TextMain)))
            {
                g.FillRectangle(track, 2, cy - 2, w - 4, 4);
                if (fillWidth > 0)
                {
                    g.FillRectangle(fill, 2, cy - 2, fillWidth, 4);
                }
                float tx = 2f + fillWidth;
                if (_dragging || _durationSeconds > 0)
                {
                    g.FillEllipse(thumb, tx - 3, cy - 3, 6, 6);
                }
            }
        }
    }
}