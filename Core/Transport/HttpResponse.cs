using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace YaMus.Core.Transport
{
    public sealed class HttpResponse : IDisposable
    {
        public int StatusCode;
        public string ReasonPhrase;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string FinalUrl;
        public Stream BodyStream;
        private byte[] _body;

        public bool HasHeader(string name)
        {
            return Headers.ContainsKey(name);
        }

        public string GetHeader(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public byte[] Body
        {
            get
            {
                if (_body == null)
                {
                    if (BodyStream == null) return new byte[0];
                    using (var ms = new MemoryStream())
                    {
                        if (BodyStream.CanSeek) BodyStream.Position = 0;
                        CopyTo(BodyStream, ms);
                        _body = ms.ToArray();
                    }
                }
                return _body;
            }
        }

        public string BodyText
        {
            get
            {
                var bytes = Body;
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        public static void CopyTo(Stream source, Stream destination)
        {
            CopyTo(source, destination, 81920, 0);
        }

        /// <summary>Копирование с паузой после каждого куска. Вызывается для фонового
        /// префетча: притормаживая приём, уменьшаем нагрузку на сеть/драйвер, чтобы
        /// на слабых машинах (Atom) шум Wi-Fi не ронял звук. Скорость при
        /// bufferSize=32768 и pauseMs=46 — примерно 700 КБ/с.</summary>
        public static void CopyTo(Stream source, Stream destination, int bufferSize, int pauseMs)
        {
            CopyTo(source, destination, bufferSize, pauseMs, CancellationToken.None);
        }

        /// <summary>То же, но с отменой: проверяем токен на каждом куске, чтобы гасить
        /// ненужное скачивание сразу (переключились на другой трек). Отмена бросает
        /// OperationCanceledException, частичный файл удаляет вызывающий.</summary>
        public static void CopyTo(Stream source, Stream destination, int bufferSize, int pauseMs, CancellationToken token)
        {
            byte[] buffer = new byte[bufferSize];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                if (pauseMs > 0) Thread.Sleep(pauseMs);
            }
        }

        public void Dispose()
        {
            if (BodyStream != null)
            {
                try { BodyStream.Dispose(); }
                catch { }
                BodyStream = null;
            }
        }
    }
}