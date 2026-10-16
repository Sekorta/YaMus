using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using YaMus.Core.Auth;
using YaMus.Core.Concurrency;

namespace YaMus.UI
{
    /// <summary>
    /// Логин: два способа получить токен —
    ///  1) вход по коду (OAuth Device Flow: код + ссылка на страницу подтверждения);
    ///  2) ручной ввод токена из music.yandex.ru (devtools -> LocalStorage -> oauth).
    /// Вход через встроенный WebBrowser убран: старый IE-движок не открывал современный
    /// SPA Яндекса, и вкладка только путала пользователя.
    /// </summary>
    public sealed class LoginForm : Form
    {
        public string Token { get; private set; }

        // Публичные OAuth-креды официального Android-приложения Яндекс.Музыки (для Device Flow).
        // ВНИМАНИЕ: они не наши, могут быть отозваны в любой момент (тогда вход сломается),
        // а их использование — против правил сервиса. Осознанное решение для «своих».
        private const string DeviceClientId = "23cabbbdc6cd418abb4b39c32c41195d";
        private const string DeviceClientSecret = "53bc75238f0c4d08a118e51fe9203300";

        private Label _title;
        private Button _tabToken;
        private Button _tabCode;
        private Panel _tokenPanel;
        private Panel _codePanel;

        private TextBox _tokenBox;
        private Label _tokenHint;
        private Button _tokenSaveButton;

        private Button _codeStartButton;
        private Label _userCodeLabel;
        private LinkLabel _deviceAuthLink;
        private Label _codeStatus;

        // device flow
        private Timer _deviceTimer;
        private string _deviceCode;
        private string _verificationUrl;
        private bool _devicePolling;

        private static readonly Font FTitleBig = new Font("Segoe UI", 13F, FontStyle.Bold);
        private static readonly Font FButton = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        private static readonly Font FTabActive = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        private static readonly Font FTabNormal = new Font("Segoe UI", 9.5F);
        private static readonly Font FCodeBig = new Font("Consolas", 20F, FontStyle.Bold, GraphicsUnit.Point);
        private static readonly Font FCodeBigUnderline = new Font(
            "Consolas", 20F, FontStyle.Bold | FontStyle.Underline, GraphicsUnit.Point);

        // окно входа всегда тёмное, независимо от выбранной темы основного окна
        private static readonly Theme D = ThemeManager.Dark;

        public LoginForm()
        {
            Text = "Вход в Яндекс Музыку";
            Icon = MainForm.LoadAppIcon();
            Width = 560;
            Height = 420;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = D.BgSide;
            FormClosed += OnFormClosed;

            BuildLayout();
            TokenStorage.ClearYandexCookies();
        }

        private void BuildLayout()
        {
            var root = new Panel();
            root.Dock = DockStyle.Fill;
            root.BackColor = D.BgSide;

            _title = new Label();
            _title.Text = "Вход в Яндекс Музыку";
            _title.Dock = DockStyle.Top;
            _title.Height = 44;
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _title.Padding = new Padding(24, 0, 0, 0);
            _title.Font = FTitleBig;
            _title.ForeColor = Color.White;
            _title.BackColor = D.BgSide;

            // переключатели способов входа (аккуратная тёмная лента)
            var strip = new Panel();
            strip.Dock = DockStyle.Top;
            strip.Height = 36;
            strip.BackColor = D.BgSide;

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.BackColor = D.BgSide;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.Padding = new Padding(18, 4, 6, 0);

            _tabCode = MakeTabButton("Вход по коду", 0);
            _tabToken = MakeTabButton("Вход по токену", 1);
            flow.Controls.Add(_tabCode);
            flow.Controls.Add(_tabToken);
            strip.Controls.Add(flow);

            var line = new Panel();
            line.Dock = DockStyle.Top;
            line.Height = 1;
            line.BackColor = D.Line;

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = D.BgSide;

            _tokenPanel = BuildTokenPanel();
            _codePanel = BuildCodePanel();
            content.Controls.Add(_tokenPanel);
            content.Controls.Add(_codePanel);

            root.Controls.Add(content);
            root.Controls.Add(line);
            root.Controls.Add(strip);
            root.Controls.Add(_title);

            Controls.Add(root);

            SelectTab(0);
        }

        private static Font TabFont(bool active)
        {
            return active ? FTabActive : FTabNormal;
        }

        private Button MakeTabButton(string text, int index)
        {
            var b = new Button();
            b.Text = text;
            b.Tag = index;
            b.Width = 150;
            b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = D.BgHover;
            b.Click += (s, e) => SelectTab((int)((Button)s).Tag);
            return b;
        }

        private void SelectTab(int index)
        {
            ApplyTabStyle(_tabCode, index == 0);
            ApplyTabStyle(_tabToken, index == 1);

            _tokenPanel.Visible = index == 1;
            _codePanel.Visible = index == 0;
            if (_codePanel.Visible) _codePanel.BringToFront();
            if (_tokenPanel.Visible) _tokenPanel.BringToFront();
        }

        private void ApplyTabStyle(Button b, bool active)
        {
            b.BackColor = active ? D.BgCard : D.BgSide;
            b.ForeColor = active ? Color.White : D.TextSub;
            b.Font = TabFont(active);
        }

        // ---------- лента «Вход по токену» ----------

        private Panel BuildTokenPanel()
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = D.BgSide;
            p.Padding = new Padding(24, 18, 24, 0);

            _tokenHint = new Label();
            _tokenHint.Text = "Вставьте OAuth-токен Яндекс Музыки.\n" +
                              "music.yandex.ru -> Ctrl+Shift+I -> Application -> Local Storage -> oauth";
            _tokenHint.Dock = DockStyle.Top;
            _tokenHint.Height = 58;
            _tokenHint.ForeColor = D.TextFaint;

            _tokenBox = new TextBox();
            _tokenBox.Dock = DockStyle.Top;
            _tokenBox.Height = 30;
            _tokenBox.Margin = new Padding(0, 8, 0, 0);
            _tokenBox.BackColor = D.BgCard;
            _tokenBox.ForeColor = D.TextMain;
            _tokenBox.BorderStyle = BorderStyle.FixedSingle;

            var tokenRow = new FlowLayoutPanel();
            tokenRow.Dock = DockStyle.Top;
            tokenRow.Height = 44;
            tokenRow.Margin = new Padding(0, 10, 0, 0);
            tokenRow.Padding = new Padding(0, 8, 0, 0);
            tokenRow.BackColor = D.BgSide;

            _tokenSaveButton = BuildAccentButton("Сохранить токен");
            _tokenSaveButton.Click += (s, e) => SaveToken();
            tokenRow.Controls.Add(_tokenSaveButton);

            p.Controls.Add(tokenRow);
            p.Controls.Add(_tokenBox);
            p.Controls.Add(_tokenHint);
            return p;
        }

        private void SaveToken()
        {
            string token = _tokenBox.Text.Trim();
            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show(this, "Вставьте токен из music.yandex.ru", "YaMus",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AcceptToken(token);
            Close();
        }

        // ---------- лента «Вход по коду» ----------

        private Panel BuildCodePanel()
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = D.BgSide;
            p.Padding = new Padding(24, 18, 24, 0);

            var hint = new Label();
            hint.Text = "Этот способ не зависит от встроенного браузера.\n" +
                        "Запросите код, перейдите по ссылке и подтвердите вход.";
            hint.Dock = DockStyle.Top;
            hint.Height = 48;
            hint.ForeColor = D.TextFaint;

            var codeRow = new FlowLayoutPanel();
            codeRow.Dock = DockStyle.Top;
            codeRow.Height = 44;
            codeRow.Margin = new Padding(0, 8, 0, 0);
            codeRow.Padding = new Padding(0, 8, 0, 0);
            codeRow.BackColor = D.BgSide;

            _codeStartButton = BuildAccentButton("Получить код");
            _codeStartButton.Width = 150;
            _codeStartButton.Click += CodeStartButtonOnClick;
            codeRow.Controls.Add(_codeStartButton);

            _userCodeLabel = new Label();
            _userCodeLabel.Text = "";
            _userCodeLabel.Visible = false;
            _userCodeLabel.Dock = DockStyle.Top;
            _userCodeLabel.Height = 50;
            _userCodeLabel.TextAlign = ContentAlignment.MiddleCenter;
            _userCodeLabel.Font = FCodeBig;
            _userCodeLabel.ForeColor = Color.White;
            _userCodeLabel.Margin = new Padding(0, 8, 0, 0);
            _userCodeLabel.Cursor = Cursors.Hand;
            _userCodeLabel.MouseEnter += (s, a) =>
            {
                // подчёркиваем только когда есть что копировать — пустое поле не трогаем
                if (!string.IsNullOrEmpty(_userCodeLabel.Text))
                {
                    _userCodeLabel.Font = FCodeBigUnderline;
                }
            };
            _userCodeLabel.MouseLeave += (s, a) => _userCodeLabel.Font = FCodeBig;
            _userCodeLabel.Click += UserCodeLabelOnClick;

            var copyTip = new ToolTip();
            copyTip.SetToolTip(_userCodeLabel, "Кликните, чтобы скопировать код");

            _deviceAuthLink = new LinkLabel();
            _deviceAuthLink.Text = "";
            _deviceAuthLink.Visible = false;
            _deviceAuthLink.Dock = DockStyle.Top;
            _deviceAuthLink.Height = 24;
            _deviceAuthLink.TextAlign = ContentAlignment.MiddleCenter;
            _deviceAuthLink.Font = FTabNormal;
            _deviceAuthLink.LinkBehavior = LinkBehavior.HoverUnderline;
            _deviceAuthLink.LinkColor = D.AccentHot;
            _deviceAuthLink.ActiveLinkColor = Color.White;
            _deviceAuthLink.VisitedLinkColor = D.Accent;
            _deviceAuthLink.LinkClicked += DeviceAuthLinkOnLinkClicked;

            _codeStatus = new Label();
            _codeStatus.Text = "";
            _codeStatus.Dock = DockStyle.Top;
            _codeStatus.Height = 52;
            _codeStatus.TextAlign = ContentAlignment.TopLeft;
            _codeStatus.ForeColor = D.TextFaint;

            p.Controls.Add(_codeStatus);
            p.Controls.Add(_deviceAuthLink);
            p.Controls.Add(_userCodeLabel);
            p.Controls.Add(codeRow);
            p.Controls.Add(hint);
            return p;
        }

        private static string NiceUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath;
            }
            catch
            {
                return url;
            }
        }

        private void UserCodeLabelOnClick(object sender, EventArgs e)
        {
            string code = _userCodeLabel.Text;
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                Clipboard.SetText(code);
                Log("DEVICE code copied to clipboard");
                _codeStatus.Text = "Код скопирован.";
            }
            catch (Exception ex)
            {
                Log("COPY device code failed: " + ex.Message);
                MessageBox.Show(this, "Не удалось скопировать код: " + ex.Message, "YaMus",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DeviceAuthLinkOnLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (string.IsNullOrEmpty(_verificationUrl)) return;
            try
            {
                Process.Start(_verificationUrl);
                Log("DEVICE auth page opened: " + SafeUrl(_verificationUrl));
            }
            catch (Exception ex)
            {
                Log("OPEN device-auth failed: " + ex.Message);
                _codeStatus.Text = "Не удалось открыть браузер. Откройте ссылку вручную: " +
                                   _deviceAuthLink.Text;
            }
        }

        private static Button BuildAccentButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.Width = 200;
            b.Height = 34;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = D.Accent;
            b.ForeColor = Color.White;
            b.Font = FButton;
            b.FlatAppearance.MouseOverBackColor = D.AccentHot;
            return b;
        }

        // ---------- общее ----------

        private static readonly object LogLock = new object();

        private static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMus");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "login.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
                }
            }
            catch
            {
                // логирование не должно валить приложение
            }
        }

        private static string SafeUrl(string url)
        {
            if (url == null) return "<null>";
            string s = url.Length > 220 ? url.Substring(0, 220) : url;
            return s;
        }

        private void AcceptToken(string token)
        {
            Token = token;
            Log("ACCEPT token len=" + token.Length);
            // куки браузера больше не нужны (говорим с API по токену) — вычищаем,
            // чтобы следующий вход не подхватывал сохранённую сессию
            TokenStorage.ClearYandexCookies();
            // без этого ShowDialog() вернёт Cancel, и MainForm отбросит токен
            DialogResult = DialogResult.OK;
        }

        // ---------- вход по коду (OAuth Device Flow) ----------

        private void CodeStartButtonOnClick(object sender, EventArgs e)
        {
            StopDeviceTimer();
            _codeStartButton.Enabled = false;
            _userCodeLabel.Text = "";
            _userCodeLabel.Visible = false;
            _deviceAuthLink.Visible = false;
            _codeStatus.Text = "Запрашиваю код...";

            Bg.Run(RequestDeviceCode)
                .ContinueWith(HandleDeviceCodeResult, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string RequestDeviceCode()
        {
            string body = "client_id=" + Uri.EscapeDataString(DeviceClientId) +
                          "&device_id=" + Uri.EscapeDataString(TokenStorage.GetOrCreateDeviceId()) +
                          "&device_name=" + Uri.EscapeDataString("YaMus Windows");
            return PostForm("https://oauth.yandex.ru/device/code", body);
        }

        private void HandleDeviceCodeResult(Task<string> task)
        {
            if (IsDisposed) return;
            try
            {
                string json = task.Result;
                Log("DEVICE code response: " + SafeUrl(json));
                var obj = JObject.Parse(json);

                string deviceCode = obj["device_code"]?.ToString();
                string userCode = obj["user_code"]?.ToString();
                _verificationUrl = obj["verification_url"]?.ToString();
                int interval = 5;
                int.TryParse(obj["interval"]?.ToString(), out interval);

                if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
                {
                    _codeStatus.Text = "Неожиданный ответ Яндекса. Попробуйте ещё раз.";
                    _codeStartButton.Enabled = true;
                    return;
                }

                _deviceCode = deviceCode;
                _userCodeLabel.Text = userCode;
                _userCodeLabel.Visible = true;

                if (!string.IsNullOrEmpty(_verificationUrl))
                {
                    _deviceAuthLink.Text = NiceUrl(_verificationUrl);
                    _deviceAuthLink.Visible = true;
                    _codeStatus.Text = "Перейдите по ссылке, введите код и подтвердите вход.\n" +
                                       "Кликните по коду, чтобы скопировать.";
                }
                else
                {
                    _deviceAuthLink.Visible = false;
                    _codeStatus.Text = "Введите код на странице Яндекса и подтвердите вход.\n" +
                                       "Кликните по коду, чтобы скопировать.";
                }

                _deviceTimer = new Timer();
                _deviceTimer.Interval = Math.Max(1000, Math.Min(30000, interval * 1000));
                _deviceTimer.Tick += (s, a) => DevicePollTick();
                _deviceTimer.Start();

                PollDeviceToken();
            }
            catch (Exception ex)
            {
                Log("DEVICE code failed: " + ex.Message);
                _codeStatus.Text = "Не удалось получить код: " + ex.Message;
                _codeStartButton.Enabled = true;
            }
        }

        private void DevicePollTick()
        {
            if (_devicePolling) return;
            PollDeviceToken();
        }

        private void PollDeviceToken()
        {
            _devicePolling = true;
            string code = _deviceCode;
            Bg.Run(() => PollDeviceTokenRequest(code))
                .ContinueWith(HandleDevicePollResult, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private sealed class DevicePollResult
        {
            public bool Pending;
            public string Json;
            public string Error;
        }

        private static DevicePollResult PollDeviceTokenRequest(string deviceCode)
        {
            string body = "grant_type=device_code" +
                          "&code=" + Uri.EscapeDataString(deviceCode) +
                          "&client_id=" + Uri.EscapeDataString(DeviceClientId) +
                          "&client_secret=" + Uri.EscapeDataString(DeviceClientSecret);
            try
            {
                return new DevicePollResult { Json = PostForm("https://oauth.yandex.ru/token", body) };
            }
            catch (WebException ex)
            {
                string err = ReadErrorBody(ex);
                if (err.IndexOf("authorization_pending", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new DevicePollResult { Pending = true };
                }
                return new DevicePollResult { Error = err };
            }
        }

        private void HandleDevicePollResult(Task<DevicePollResult> task)
        {
            if (IsDisposed) return;
            if (task.Exception != null)
            {
                Log("DEVICE poll failed: " + task.Exception.InnerException);
                _codeStatus.Text = "Ошибка сети. Попробуйте ещё раз.";
                StopDeviceTimer();
                _codeStartButton.Enabled = true;
                return;
            }

            DevicePollResult result = task.Result;
            if (result.Pending)
            {
                _devicePolling = false;
                return; // пользователь ещё не подтвердил — опрашиваем дальше по таймеру
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                StopDeviceTimer();
                string err = result.Error.ToLowerInvariant();
                if (err.Contains("access_denied"))
                {
                    _codeStatus.Text = "Вход не подтверждён. Нажмите «Получить код» заново.";
                }
                else if (err.Contains("expired_token"))
                {
                    _codeStatus.Text = "Код устарел. Нажмите «Получить код» заново.";
                }
                else
                {
                    _codeStatus.Text = "Не удалось подтвердить: " + result.Error;
                }
                _codeStartButton.Enabled = true;
                return;
            }

            try
            {
                var obj = JObject.Parse(result.Json);
                string token = obj["access_token"]?.ToString();
                if (!string.IsNullOrEmpty(token))
                {
                    StopDeviceTimer();
                    AcceptToken(token);
                    Close();
                }
                else
                {
                    _codeStatus.Text = "В ответе Яндекса нет токена.";
                    _codeStartButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                Log("DEVICE token parse failed: " + ex.Message);
                _codeStatus.Text = "Не удалось разобрать ответ Яндекса.";
                _codeStartButton.Enabled = true;
            }
        }

        private void StopDeviceTimer()
        {
            _devicePolling = false;
            if (_deviceTimer != null)
            {
                _deviceTimer.Stop();
                _deviceTimer.Dispose();
                _deviceTimer = null;
            }
        }

        private static string PostForm(string url, string body)
        {
            EnableTls12();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = "YaMus/1.0";
            req.Accept = "application/json";
            req.Timeout = 15000;
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (Stream s = req.GetRequestStream())
            {
                s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private static bool _tls12Done;

        /// <summary>Яндекс требует TLS 1.2, а .NET 4.0 по умолчанию умеет только 1.0/1.1,
        /// из-за чего вход по коду падал с «закрытое соединение». Включаем 1.2 (+1.1/1.0).</summary>
        private static void EnableTls12()
        {
            if (_tls12Done) return;
            _tls12Done = true;
            try
            {
                // 0xC0 = Tls, 0x300 = Tls11, 0xC00 = Tls12 (числа совпадают с .NET 4.5+)
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)(0xC00 | 0x300 | 0xC0);
            }
            catch { }
        }

        private static string ReadErrorBody(WebException ex)
        {
            try
            {
                var resp = (HttpWebResponse)ex.Response;
                if (resp == null) return ex.Message;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                return ex.Message;
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            StopDeviceTimer();
        }
    }
}