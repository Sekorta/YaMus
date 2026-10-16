using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Diagnostics;
using YaMus.Core.Media;

namespace YaMus.UI
{
    /// <summary>Компактная карточка альбома: обложка + название + подпись; клик открывает альбом.
    /// Используется на странице исполнителя в сетке «Альбомы».</summary>
    internal sealed class MediaCardCell : Control
    {
        private static readonly Font FTitle = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        private static readonly Font FSub = new Font("Segoe UI", 7.5F);
        private static readonly Font FNote = UiGlyphs.GlyphFont(28F, FontStyle.Regular);

        private readonly CoverLoader _loader;
        private readonly long _id;
        private readonly string _title;
        private readonly string _subtitle;
        private readonly string _coverUri;
        private Image _cover;
        private bool _coverLoaded;
        private bool _hover;

        public event Action<long> Clicked;

        public MediaCardCell(long id, string title, string subtitle, string coverUri, CoverLoader loader)
        {
            _id = id;
            _title = title ?? "";
            _subtitle = subtitle ?? "";
            _coverUri = coverUri;
            _loader = loader;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = YmPalette.BgCard;
            Cursor = Cursors.Hand;
            TabStop = false;
            Size = new Size(150, 190);
        }

        public void ReleaseImage()
        {
            var old = _cover;
            _cover = null;
            _coverLoaded = false;
            if (old != null) old.Dispose();
        }

        public void BeginCoverLoad()
        {
            if (_coverLoaded || string.IsNullOrEmpty(_coverUri) || _loader == null) return;
            _coverLoaded = true;
            int s = Math.Max(8, Width - 24);
            CoverLoader.BeginLoad(() => _loader.Fetch(_coverUri, "200x200"), s, img =>
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

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Clicked != null) Clicked(_id);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(_hover ? YmPalette.BgHover : YmPalette.BgCard))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            int cover = Math.Max(8, Width - 24);
            if (_cover != null)
            {
                g.DrawImage(_cover, 12, 12, cover, cover);
            }
            else
            {
                using (var empty = new SolidBrush(YmPalette.BgMain))
                {
                    g.FillRectangle(empty, 12, 12, cover, cover);
                }
                TextRenderer.DrawText(g, UiGlyphs.Note, FNote,
                                      new Rectangle(12, 12, cover, cover), YmPalette.TextFaint,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int ty = 12 + cover + 8;
            TextRenderer.DrawText(g, _title, FTitle,
                                  new Rectangle(12, ty, Width - 24, 20), YmPalette.TextMain,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _subtitle, FSub,
                                  new Rectangle(12, ty + 18, Width - 24, 16), YmPalette.TextSub,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            sw.Stop();
            Diag.UiProfile.Add("cell-paint", sw.ElapsedMilliseconds);
        }
    }
}