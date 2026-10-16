using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using YaMus.Core.Api;
using YaMus.Core.Crypto;
using YaMus.Core.Transport;

namespace YaMus.Core.Playback
{
    public sealed class DownloadInfo
    {
        public string TrackId;
        public string Quality;
        public string Codec;
        public string Url;
        public string Key;
    }

    /// <summary>
    /// Получение прямой ссылки на трек (аналог getTrackUrl из мода) и скачивание
    /// с расшифровкой AES-128-CTR (ключ отдаёт сервер в downloadInfo.key).
    /// </summary>
    public sealed class TrackDownloader
    {
        // Общий секрет шифрования ссылок из официального клиента. Не наш, может быть
        // изменён Яндексом в любой момент — тогда расшифровка сломается до обновления.
        private const string SecretKey = "kzqU4XhfCaY6B6JTHODeq5";

        private readonly RestClient _rest;
        private readonly HttpClient _http;

        public TrackDownloader(RestClient rest, HttpClient http)
        {
            _rest = rest;
            _http = http;
        }

        /// <summary>Перебирает варианты выбранного уровня качества, пока сервер не вернёт валидную ссылку.</summary>
        public DownloadInfo Resolve(string trackId)
        {
            Exception last = null;
            foreach (QualityAttempt attempt in MusicQualityCatalog.AttemptsFor(MusicQualityCatalog.Current))
            {
                try
                {
                    return ResolveQuality(trackId, attempt.Quality, attempt.Codecs);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new IOException("Не удалось получить ссылку на трек" +
                (last != null ? ": " + last.Message : ""));
        }

        private DownloadInfo ResolveQuality(string trackId, string quality, string[] codecs)
        {
            string ts = UnixSeconds().ToString();
            string data = ts + trackId + quality + string.Join("", codecs) + "encraw";
            string sign = Sign(data);

            string url = RestClient.ApiBase + "/get-file-info?ts=" + ts +
                         "&trackId=" + Uri.EscapeDataString(trackId) +
                         "&quality=" + Uri.EscapeDataString(quality) +
                         "&codecs=" + Uri.EscapeDataString(string.Join(",", codecs)) +
                         "&transports=encraw" +
                         "&sign=" + Uri.EscapeDataString(sign);

            using (var resp = _rest.Request(url))
            {
                if (resp.StatusCode != 200)
                {
                    throw new IOException("get-file-info HTTP " + resp.StatusCode);
                }

                JToken json;
                try { json = JToken.Parse(resp.BodyText); }
                catch (Exception ex) { throw new IOException("get-file-info: битый ответ (" + ex.Message + ")"); }

                JToken di = json != null && json["result"] != null ? json["result"]["downloadInfo"] : null;
                if (di == null) throw new IOException("get-file-info: нет downloadInfo");

                if (di["trackId"] != null && di["trackId"].ToString() != trackId)
                {
                    throw new IOException("get-file-info: несовпадение trackId");
                }

                var info = new DownloadInfo
                {
                    TrackId = trackId,
                    Quality = quality,
                    Codec = di["codec"] != null ? di["codec"].ToString() : "",
                    Url = di["url"] != null ? di["url"].ToString() : null,
                    Key = di["key"] != null ? di["key"].ToString() : null,
                };
                if (string.IsNullOrEmpty(info.Url)) throw new IOException("get-file-info: пустой url");
                if (string.IsNullOrEmpty(info.Key)) throw new IOException("get-file-info: пустой ключ");
                return info;
            }
        }

        /// <summary>Скачивает файл, расшифровывает потоком и сохраняет в destPath.</summary>
        public void Download(DownloadInfo info, string destPath)
        {
            using (var resp = _http.Get(info.Url, null))
            {
                if (resp.StatusCode != 200)
                {
                    throw new IOException("Скачивание трека HTTP " + resp.StatusCode);
                }

                using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                using (var decryptor = new AesCtrStream(resp.BodyStream, HexToBytes(info.Key)))
                {
                    HttpResponse.CopyTo(decryptor, outFs);
                }
            }
        }

        /// <summary>Скачивание с отменой: нужен для переключения треков, чтобы быстрый
        /// Next/Prev не плодил параллельных загрузок (на слабом Atom они душат и сеть,
        /// и звук). Отмена рвёт запись посреди файла — вызывающий удаляет частичный tmp.</summary>
        public void Download(DownloadInfo info, string destPath, CancellationToken token)
        {
            using (var resp = _http.Get(info.Url, null))
            {
                if (resp.StatusCode != 200)
                {
                    throw new IOException("Скачивание трека HTTP " + resp.StatusCode);
                }

                using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                using (var decryptor = new AesCtrStream(resp.BodyStream, HexToBytes(info.Key)))
                {
                    HttpResponse.CopyTo(decryptor, outFs, 81920, 0, token);
                }
            }
        }

        /// <summary>То же, что Download, но с плавным приёмом данных и пониженным
        /// приоритетом текущей нити на время загрузки. На слабых машинах (Atom) бурное
        /// скачивание по Wi-Fi само по себе роняет звук: драйвер сети устраивает
        /// DPC-шторм, который не лечится приоритетами потоков. Поэтому трафик
        /// ограничиваем паузами, а нить временно опускаем ниже обычного.
        /// Метод синхронный: вызывать его нужно из фоновой нити (у нас так и есть).</summary>
        public void DownloadBackground(DownloadInfo info, string destPath, CancellationToken token = default(CancellationToken))
        {
            ThreadPriority saved = Thread.CurrentThread.Priority;
            try
            {
                if (saved != ThreadPriority.BelowNormal)
                {
                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
                    catch { }
                }
                DownloadPaced(info, destPath, token);
            }
            finally
            {
                if (Thread.CurrentThread.Priority != saved)
                {
                    try { Thread.CurrentThread.Priority = saved; }
                    catch { }
                }
            }
        }

        /// <summary>Скачивание с лимитом ~700 КБ/с: большие паузы между кусками дают
        /// сети/драйверу «выдохнуть» и не душат аудио на слабом железе.</summary>
        private void DownloadPaced(DownloadInfo info, string destPath, CancellationToken token)
        {
            using (var resp = _http.Get(info.Url, null))
            {
                if (resp.StatusCode != 200)
                {
                    throw new IOException("Скачивание трека HTTP " + resp.StatusCode);
                }

                using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                using (var decryptor = new AesCtrStream(resp.BodyStream, HexToBytes(info.Key)))
                {
                    HttpResponse.CopyTo(decryptor, outFs, 32768, 46, token);
                }
            }
        }

        private static string Sign(string data)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey)))
            {
                byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                // мод: btoa(...).slice(0, -1) — стандартный base64 без "=" в конце
                return Convert.ToBase64String(sig).TrimEnd('=');
            }
        }

        private static long UnixSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (hex == null || hex.Length == 0) throw new ArgumentException("Пустой hex-ключ", "hex");
            int n = hex.Length / 2;
            var bytes = new byte[n];
            for (int i = 0; i < n; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}