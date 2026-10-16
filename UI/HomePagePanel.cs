using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using YaMus.Core.Media;

namespace YaMus.UI
{
    /// <summary>Главная страница: название мода, краткое описание и аккаунт (аватар + имя).
    /// Заменяет список на старте и по клику «Главная» в сайдбаре.</summary>
    internal sealed class HomePagePanel : Panel
    {
        private static readonly Font FGlyph = UiGlyphs.GlyphFont(52F, FontStyle.Regular);
        private static readonly Font FName = new Font("Segoe UI", 34F, FontStyle.Bold);
        private static readonly Font FSub = new Font("Segoe UI", 12F);
        private static readonly Font FHint = new Font("Segoe UI", 9.5F);
        private static readonly Font FAccount = new Font("Segoe UI", 15F, FontStyle.Bold);
        private static readonly Font FEmail = new Font("Segoe UI", 11F);

        /// <summary>Версия сборки для отображения на главной.</summary>
        private static readonly string _version = ReadVersion();

        private static string ReadVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var attrs = asm.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    return ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                }
                return asm.GetName().Version.ToString();
            }
            catch
            {
                return "1.0.0";
            }
        }

        private string _accountName;
        private string _accountEmail;
        private string _avatarUri;
        private Image _avatar;
        private Core.Media.CoverLoader _coverLoader;

        public HomePagePanel()
        {
            BackColor = YmPalette.BgMain;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        /// <summary>Имя, почта и аватарка аккаунта. Вызывать после логина.</summary>
        public void SetAccount(string name, string email, string avatarUri, Core.Media.CoverLoader loader)
        {
            _accountName = name;
            _accountEmail = email;
            _avatarUri = avatarUri;
            _coverLoader = loader;
            string baseUrl = BuildAvatarBase(avatarUri);
            if (baseUrl != null && loader != null)
            {
                Core.Diagnostics.Diag.Log("account avatar base: " + baseUrl);
                CoverLoader.BeginLoad(() =>
                {
                    // сеть к avatars.yandex.net флапает: неудачу пробуем ещё пару раз
                    // с паузой (один long-запрос на хостинге короче, чем череда short-TLS)
                    byte[] result = null;
                    for (int attempt = 0; attempt < 2 && result == null; attempt++)
                    {
                        result = FetchAvatar(loader, baseUrl);
                        if (result == null && attempt == 0) Thread.Sleep(5000);
                    }
                    if (result == null)
                    {
                        Core.Diagnostics.Diag.Log("account avatar: не найден по " + baseUrl);
                    }
                    return result;
                }, 96, img =>
                {
                    if (_avatar != null) _avatar.Dispose();
                    _avatar = img;
                    Core.Diagnostics.Diag.Log("account avatar: загружена");
                    Invalidate();
                });
            }
            Invalidate();
        }

        /// <summary>Размерные хвосты у Яндекс.Паспорта разные — пробуем по очереди.</summary>
        private static byte[] FetchAvatar(Core.Media.CoverLoader loader, string baseUrl)
        {
            string[] sizes = { "islands-200", "islands-retina-200", "orig" };
            foreach (string s in sizes)
            {
                byte[] b = loader.Fetch(baseUrl + s, s);
                if (b != null && b.Length > 0) return b;
            }
            return null;
        }

        /// <summary>Аватарки приходят как "//avatars.yandex.net/..." или через avatarId.
        /// Возвращает адрес без размерного хвоста: чинит двойной слеш в хосте и
        /// отбрасывает существующий размер, чтобы подставить свой.</summary>
        private static string BuildAvatarBase(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (uri.IndexOf("%%", StringComparison.Ordinal) >= 0) return CoverLoader.CoverUrl(uri, "islands-200");
            string u = uri.IndexOf("://", StringComparison.Ordinal) >= 0 ? uri : "https://" + uri;
            while (u.StartsWith("https:////", StringComparison.Ordinal)) u = "https://" + u.Substring(10);
            int slash = u.LastIndexOf('/');
            return slash < 0 ? u + "/" : u.Substring(0, slash + 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(YmPalette.BgMain);

            int cx = ClientSize.Width / 2;
            int cy = ClientSize.Height / 2 - 70;

            string name = "YaMus";
            string sub = "Яндекс Музыка · v" + _version;
            string hint = "Слушайте «Мою волну», «Мне нравится» и плейлисты";

            Size sGlyph = TextRenderer.MeasureText(g, UiGlyphs.Note, FGlyph);
            Size sName = TextRenderer.MeasureText(g, name, FName);
            Size sSub = TextRenderer.MeasureText(g, sub, FSub);
            Size sHint = TextRenderer.MeasureText(g, hint, FHint);

            int total = sGlyph.Height + 26 + sName.Height + 10 + sSub.Height + 26 + sHint.Height;
            int y = cy - total / 2;

            TextRenderer.DrawText(g, UiGlyphs.Note, FGlyph,
                                  new Rectangle(cx - sGlyph.Width / 2, y, sGlyph.Width, sGlyph.Height), YmPalette.Accent,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            y += sGlyph.Height + 20;

            TextRenderer.DrawText(g, name, FName,
                                  new Rectangle(cx - sName.Width / 2, y, sName.Width, sName.Height), YmPalette.TextMain,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            y += sName.Height + 10;

            TextRenderer.DrawText(g, sub, FSub,
                                  new Rectangle(cx - sSub.Width / 2, y, sSub.Width, sSub.Height), YmPalette.TextSub,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            y += sSub.Height + 24;

            using (var b = new SolidBrush(YmPalette.WaveAccent))
            {
                g.FillRectangle(b, cx - 40, y, 80, 2);
            }
            y += 18;

            TextRenderer.DrawText(g, hint, FHint,
                                  new Rectangle(cx - sHint.Width / 2, y, sHint.Width, sHint.Height), YmPalette.TextFaint,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            y += 40;

            if (!string.IsNullOrEmpty(_accountName))
            {
                DrawAccount(g, cx, y);
            }
        }

        private void DrawAccount(Graphics g, int cx, int y)
        {
            const int avatarSize = 64;
            int rowTop = y + 8;

            bool hasMail = !string.IsNullOrEmpty(_accountEmail);
            Size sAcct = TextRenderer.MeasureText(g, _accountName, FAccount);
            Size sMail = hasMail ? TextRenderer.MeasureText(g, _accountEmail, FEmail) : Size.Empty;
            int textH = sAcct.Height + (hasMail ? sAcct.Height == 0 ? 0 : 3 + sMail.Height : 0);
            int rowW = avatarSize + 16 + Math.Max(sAcct.Width, sMail.Width);
            int rowX = cx - rowW / 2;
            int rowH = Math.Max(avatarSize, textH);

            Rectangle ar = new Rectangle(rowX, rowTop + (rowH - avatarSize) / 2, avatarSize, avatarSize);
            int nameY = rowTop + (rowH - textH) / 2;
            Rectangle tr = new Rectangle(rowX + avatarSize + 16, nameY, sAcct.Width, sAcct.Height);

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(ar);
                using (var bg = new SolidBrush(YmPalette.BgCard))
                {
                    g.FillPath(bg, path);
                }
                if (_avatar != null)
                {
                    g.SetClip(path);
                    g.DrawImage(_avatar, ar);
                    g.ResetClip();
                }
                else if (!string.IsNullOrEmpty(_accountName))
                {
                    string letter = _accountName.Substring(0, 1).ToUpperInvariant();
                    using (var fb = new Font("Segoe UI", 28F, FontStyle.Bold))
                    using (var ab = new SolidBrush(YmPalette.Accent))
                    {
                        TextRenderer.DrawText(g, letter, fb, ar, Color.White,
                                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                              TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    }
                }
                using (var pb = new Pen(YmPalette.Line))
                {
                    g.DrawPath(pb, path);
                }
            }

            TextRenderer.DrawText(g, _accountName, FAccount, tr, YmPalette.TextMain,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            if (hasMail)
            {
                Rectangle er = new Rectangle(rowX + avatarSize + 16, nameY + sAcct.Height + 3, sMail.Width, sMail.Height);
                TextRenderer.DrawText(g, _accountEmail, FEmail, er, YmPalette.TextSub,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}