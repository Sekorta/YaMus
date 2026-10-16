using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Auth
{
    /// <summary>Хранение OAuth-токена в %APPDATA%\YaMus\config.json.</summary>
    public static class TokenStorage
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMus");

        private static readonly string FilePath = Path.Combine(Dir, "config.json");

        public static string ConfigPath
        {
            get { return FilePath; }
        }

        public static TokenInfo Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                var info = JsonConvert.DeserializeObject<TokenInfo>(json);
                if (info == null) return null;

                // новый формат: токен лежит зашифрованным (DPAPI), здесь расшифровываем
                if (string.IsNullOrEmpty(info.OAuthToken) && !string.IsNullOrEmpty(info.TokenProtected))
                {
                    info.OAuthToken = Unprotect(info.TokenProtected);
                }
                if (string.IsNullOrEmpty(info.OAuthToken)) return null;
                return info;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(TokenInfo info)
        {
            if (!Directory.Exists(Dir))
            {
                Directory.CreateDirectory(Dir);
            }
            if (info == null)
            {
                return;
            }

            // на диск токен пишем только в зашифрованном виде; в памяти держим как есть
            string plain = info.OAuthToken;
            if (!string.IsNullOrEmpty(plain))
            {
                string enc = Protect(plain);
                if (enc != null)
                {
                    info.TokenProtected = enc;
                    info.OAuthToken = null;
                }
                else
                {
                    info.TokenProtected = null; // DPAPI недоступен — пишем как раньше, открытым
                }
            }
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(info, Formatting.Indented), Encoding.UTF8);
            }
            finally
            {
                info.OAuthToken = plain;
            }
        }

        private static string Protect(string plain)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                Diag.Log("token protect failed: " + ex.Message);
                return null;
            }
        }

        private static string Unprotect(string b64)
        {
            try
            {
                byte[] enc = Convert.FromBase64String(b64);
                byte[] data = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                Diag.Log("token unprotect failed: " + ex.Message);
                return null;
            }
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Стирает куки Яндекса из хранилища встроенного WebBrowser (WinINet), чтобы
        /// после выхода не происходил мгновенный повторный вход за сохранённую сессию.
        /// Удаляются только yandex-файлы куки — без глобального InternetSetOption(END_BROWSER_SESSION),
        /// который рушил сессии сторонних приложений. Возвращает число удалённых файлов куки.
        /// NB: если на WebBrowser-флоу вернётся мгновенный ре-логин (сессионные куки живут в памяти
        /// процесса до перезапуска), возможно, придётся вернуть сброс сессии.</summary>
        public static int ClearYandexCookies()
        {
            int deleted = 0;

            try
            {
                // обычное хранилище IE11 и low-integrity (Enhanced Protected Mode)
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\Windows\\INetCookies");
                deleted += TryDeleteCookieFiles(baseDir);
                deleted += TryDeleteCookieFiles(Path.Combine(baseDir, "low"));

                Diag.Log("cookies: cleared " + deleted + " yandex file(s)");
            }
            catch
            {
                // ignore
            }
            return deleted;
        }

        private static int TryDeleteCookieFiles(string dir)
        {
            int deleted = 0;
            if (!Directory.Exists(dir)) return 0;
            foreach (string f in Directory.GetFiles(dir, "*.txt"))
            {
                if (Path.GetFileName(f).IndexOf("yandex", StringComparison.OrdinalIgnoreCase) < 0) continue;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Delete(f);
                        deleted++;
                        break;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(60);
                    }
                }
            }
            return deleted;
        }

        /// <summary>"dark" | "black" | "light". Дефолт — тёмная.
        /// Читает тему даже при отсутствии токена; мигрирует старые конфиги с bool ThemeDark.</summary>
        public static string ReadThemeName()
        {
            try
            {
                if (!File.Exists(FilePath)) return "dark";
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                var info = JsonConvert.DeserializeObject<TokenInfo>(json);
                if (info == null) return "dark";

                if (!string.IsNullOrEmpty(info.Theme))
                {
                    string t = info.Theme.ToLowerInvariant();
                    if (t == "black" || t == "light") return t;
                    return "dark";
                }

                // старый формат: только bool
                if (info.ThemeDark.HasValue) return info.ThemeDark.Value ? "dark" : "light";
                return "dark";
            }
            catch
            {
                return "dark";
            }
        }

        /// <summary>Сохраняет выбранную тему по имени, не трогая остальные поля конфига.</summary>
        public static void SaveTheme(string name)
        {
            try
            {
                TokenInfo info = null;
                if (File.Exists(FilePath))
                {
                    try
                    {
                        info = JsonConvert.DeserializeObject<TokenInfo>(File.ReadAllText(FilePath, Encoding.UTF8));
                    }
                    catch
                    {
                        info = null;
                    }
                }
                if (info == null) info = new TokenInfo();
                info.Theme = name;
                info.ThemeDark = null;
                Save(info);
            }
            catch
            {
                // не критично
            }
        }

        /// <summary>"max" | "medium" | "low". Дефолт — "max". Читается без токена.</summary>
        public static string ReadAudioQuality()
        {
            try
            {
                if (!File.Exists(FilePath)) return "max";
                var info = JsonConvert.DeserializeObject<TokenInfo>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (info == null || string.IsNullOrEmpty(info.AudioQuality)) return "max";
                string q = info.AudioQuality.ToLowerInvariant();
                if (q == "max" || q == "low") return q;
                return "medium";
            }
            catch
            {
                return "max";
            }
        }

        /// <summary>Сохраняет выбранное качество, не трогая остальные поля конфига.</summary>
        public static void SaveAudioQuality(string name)
        {
            try
            {
                TokenInfo info = null;
                if (File.Exists(FilePath))
                {
                    try
                    {
                        info = JsonConvert.DeserializeObject<TokenInfo>(File.ReadAllText(FilePath, Encoding.UTF8));
                    }
                    catch
                    {
                        info = null;
                    }
                }
                if (info == null) info = new TokenInfo();
                info.AudioQuality = name;
                Save(info);
            }
            catch
            {
                // не критично
            }
        }

        /// <summary>Устройство в OAuth-флоу. Генерируется один раз и сохраняется.</summary>
        public static string GetOrCreateDeviceId()
        {
            var existing = Load();
            if (existing != null && !string.IsNullOrEmpty(existing.DeviceId))
            {
                return existing.DeviceId;
            }

            var rnd = new Random();
            var sb = new StringBuilder(32);
            for (int i = 0; i < 32; i++)
            {
                if (i > 0 && i % 8 == 0) sb.Append('-');
                sb.Append(rnd.Next(0, 16).ToString("x"));
            }
            string deviceId = sb.ToString();

            if (existing == null) existing = new TokenInfo();
            existing.DeviceId = deviceId;
            Save(existing);
            return deviceId;
        }
    }
}