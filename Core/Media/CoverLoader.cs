using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using YaMus.Core.Concurrency;
using YaMus.Core.Diagnostics;
using YaMus.Core.Transport;

namespace YaMus.Core.Media
{
    /// <summary>Загрузка и кэширование обложек с avatars.yandex.net через наш HttpClient.</summary>
    public sealed class CoverLoader
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(8);

        private sealed class Job
        {
            public Func<byte[]> Fetch;
            public int Size;
            public Action<Image> OnUi;
            public TaskScheduler Scheduler;
        }

        private static readonly object QueueLock = new object();
        private static readonly Queue<Job> Queue = new Queue<Job>();
        private static Timer Pacer;
        private static int Running;

        // Плавная загрузка обложек: не больше MaxRunning одновременно, новые
        // стартуют по одному с интервалом PaceMs. Иначе лавина HTTP+декодов
        // «подвешивает» UI даже на слабых машинах.
        private const int MaxRunning = 2;
        private const int PaceMs = 25;
        private const int MaxPerTick = 1;

        /// <summary>Ставит загрузку+декод обложки в общую очередь. Вызывать только с UI-потока:
        /// fetch выполняется в фоне, onUi вызывается на UI-потоке.</summary>
        public static void BeginLoad(Func<byte[]> fetch, int size, Action<Image> onUi)
        {
            var job = new Job
            {
                Fetch = fetch,
                Size = size,
                OnUi = onUi,
                Scheduler = TaskScheduler.FromCurrentSynchronizationContext()
            };
            lock (QueueLock)
            {
                Queue.Enqueue(job);
                if (Pacer == null)
                {
                    Pacer = new Timer(Pump, null, PaceMs, Timeout.Infinite);
                }
            }
        }

        private static void Pump(object state)
        {
            Job job = null;
            lock (QueueLock)
            {
                if (Running < MaxRunning && Queue.Count > 0)
                {
                    job = Queue.Dequeue();
                    Running++;
                }
                if (Pacer != null)
                {
                    Pacer.Dispose();
                    Pacer = null;
                }
                if (Queue.Count > 0)
                {
                    Pacer = new Timer(Pump, null, (long)PaceMs * MaxPerTick, Timeout.Infinite);
                }
            }
            if (job == null) return;

            Bg.Run(() => DecodeAndScale(job.Fetch(), job.Size))
                .ContinueWith(t =>
                {
                    Image img = null;
                    try { if (!t.IsFaulted && t.Result != null) img = t.Result; }
                    catch { }
                    lock (QueueLock)
                    {
                        if (Running > 0) Running--;
                    }
                    try { if (img != null) job.OnUi(img); }
                    catch { if (img != null) img.Dispose(); }
                }, job.Scheduler);
        }

        private readonly object _lock = new object();
        private readonly HttpClient _http;
        private readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Не бьёмся о падающий хост большими тиражами: после сбоя (TLS/сеть) держим хост
        // на короткой паузе, чтобы не устраивать slow-TLS на каждый скролл списка.
        // Пауза меньше, чем CoverRetryMs в списках (20 с): следующая плановая попытка
        // успеет пройти, если хост снова поднялся.
        private const long FailCooldownMs = 10000;
        private readonly Dictionary<string, long> _failUntil = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object _failLock = new object();

        public CoverLoader(HttpClient http)
        {
            _http = http;
        }

        /// <summary>Полный URL обложки: coverUri вида "avatars.yandex.net/.../%%", %% → размер.</summary>
        public static string CoverUrl(string coverUri, string size)
        {
            if (string.IsNullOrEmpty(coverUri)) return null;
            string baseUrl = coverUri.IndexOf("://", StringComparison.Ordinal) >= 0
                ? coverUri
                : "https://" + coverUri;
            baseUrl = baseUrl.Replace("https:////", "https://");
            return baseUrl.Replace("%%", size);
        }

        /// <summary>Декодирует байты картинки и уменьшает до квадрата size×size.
        /// Вызывать с фонового потока: операция может быть дорогой — не блокировать ей UI.</summary>
        public static Image DecodeAndScale(byte[] bytes, int size)
        {
            if (bytes == null || size <= 0) return null;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var bmp = new Bitmap(ms))
                {
                    var img = new Bitmap(size, size);
                    using (var g = Graphics.FromImage(img))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(bmp, 0, 0, size, size);
                    }
                    return img;
                }
            }
            catch { return null; }
        }

        /// <summary>Блокирующий вызов (из фонового потока). Возвращает байты изображения или null.</summary>
        public byte[] Fetch(string coverUri, string size)
        {
            if (string.IsNullOrEmpty(coverUri)) return null;
            string key = coverUri + "@" + size;

            lock (_lock)
            {
                byte[] cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }

            byte[] data = null;
            Gate.Wait();
            try
            {
                string url = CoverUrl(coverUri, size);
                if (url == null)
                {
                    Diag.Log("cover fetch: пустой URL для " + coverUri);
                    return null;
                }
                data = TryFetch(url);
                if (data == null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // системный TLS: надёжнее BouncyCastle к современным CDN (тот умеет
                    // флапать alert 80 на pull-нодах), а иногда и вовсе http-вариант
                    data = FetchSystem(url) ?? FetchSystem("http://" + url.Substring(8));
                }
            }
            finally
            {
                Gate.Release();
            }

            if (data != null && data.Length > 0)
            {
                lock (_lock)
                {
                    if (!_cache.ContainsKey(key)) _cache[key] = data;
                }
            }
            return data;
        }
        private static bool _tls12;

        /// <summary>Загрузка изображения системным HTTP-стеком (.NET HttpWebRequest).
        /// Обложки — картинки, не большие: таймаут и логика проще. TLS 1.2 включаем
        /// явно: на Windows TPack системный дефолт бывает старее, чем хочет CDN.
        /// Возвращает байты или null. Системный стек до Того же URL добирается там,
        /// где BouncyCastle падает на alert 80.</summary>
        private static byte[] FetchSystem(string url)
        {
            try
            {
                if (!_tls12)
                {
                    _tls12 = TryEnableTls12();
                }
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "YaMus/0.1";
                req.Accept = "*/*";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return null;
                    using (var stream = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        private static bool TryEnableTls12()
        {
            try
            {
                // числовые значения: 0xC0 = Tls, 0x300 = Tls11, 0xC00 = Tls12
                // (в .NET 4.0 перечисление беднее, но биты уже совпадают с 4.5+)
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)(0xC00 | 0x300 | 0xC0);
                return true;
            }
            catch { return false; }
        }

        private byte[] TryFetch(string url)
        {
            string host = null;
            try { host = new Uri(url).Host; }
            catch { }
            long now = Environment.TickCount;
            if (host != null)
            {
                lock (_failLock)
                {
                    long blocked;
                    if (_failUntil.TryGetValue(host, out blocked) && now < blocked) return null;
                }
            }
            try
            {
                using (var resp = _http.Get(url, null))
                {
                    if (resp.StatusCode == 200)
                    {
                        using (var ms = new MemoryStream())
                        {
                            HttpResponse.CopyTo(resp.BodyStream, ms);
                            return ms.ToArray();
                        }
                    }
                    Diag.Log("cover fetch HTTP " + resp.StatusCode + ": " + url);
                }
            }
            catch (Exception ex)
            {
                Diag.Log("cover fetch failed: " + url + ": " + ex.Message);
                if (host != null)
                {
                    lock (_failLock)
                    {
                        _failUntil[host] = now + FailCooldownMs;
                    }
                }
            }
            return null;
        }
    }
}