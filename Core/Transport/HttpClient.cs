using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto.Tls;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Transport
{
    /// <summary>
    /// HTTP/1.1 клиент поверх TLS от BouncyCastle (используется и на XP, и на Win7).
    /// Каждый запрос — новое соединение (Connection: close). Умеет: chunked, redirects, GET/POST.
    /// </summary>
    public sealed class HttpClient : IDisposable
    {
        private const int MaxRedirects = 5;
        private readonly CertificateValidator _validator;
        private int _timeoutMs;

        public HttpClient(CertificateValidator validator, int timeoutMs = 30000)
        {
            _validator = validator ?? throw new ArgumentNullException("validator");
            _timeoutMs = timeoutMs;
        }

        public int TimeoutMs
        {
            get { return _timeoutMs; }
            set { _timeoutMs = value; }
        }

        public HttpResponse Get(string url, IDictionary<string, string> headers = null)
        {
            return Request("GET", url, null, null, headers, 0);
        }

        public HttpResponse GetStream(string url, IDictionary<string, string> headers = null)
        {
            return Request("GET", url, null, null, headers, 0);
        }

        public HttpResponse Post(string url, byte[] body, string contentType, IDictionary<string, string> headers = null)
        {
            return Request("POST", url, body, contentType, headers, 0);
        }

        private HttpResponse Request(string method, string url, byte[] body, string contentType,
            IDictionary<string, string> headers, int redirects)
        {
            if (redirects > MaxRedirects)
            {
                throw new IOException("Слишком много редиректов: " + url);
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                throw new IOException("Невалидный URL: " + url);
            }

            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Неподдерживаемая схема: " + uri.Scheme);
            }

            bool isHttps = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            int port = uri.IsDefaultPort ? (isHttps ? 443 : 80) : uri.Port;

            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = _timeoutMs;
            tcp.SendTimeout = _timeoutMs;

            try
            {
                tcp.Connect(uri.Host, port);

                Stream io = tcp.GetStream();
                TlsClientProtocol tlsProtocol = null;

                if (isHttps)
                {
                    tlsProtocol = new TlsClientProtocol(io, new Org.BouncyCastle.Security.SecureRandom());
                    tlsProtocol.Connect(new TlsClientImpl(uri.Host, _validator));
                    io = tlsProtocol.Stream;
                }

                HttpResponse response = Exchange(io, uri, method, body, contentType, headers, isHttps);

                // редирект
                int code = response.StatusCode;
                if ((code == 301 || code == 302 || code == 303 || code == 307) && method == "GET")
                {
                    string location = response.GetHeader("Location");
                    response.Dispose();
                    if (!string.IsNullOrEmpty(location))
                    {
                        Uri next;
                        if (Uri.TryCreate(location, UriKind.Absolute, out next))
                        {
                            if (string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                            {
                                return Request(method, location, body, contentType, headers, redirects + 1);
                            }
                        }
                        else if (Uri.TryCreate(uri, location, out next))
                        {
                            return Request(method, next.AbsoluteUri, body, contentType, headers, redirects + 1);
                        }
                    }
                    // не смогли следовать — вернём как есть
                    return response;
                }

                response.FinalUrl = url;
                return response;
            }
            catch (TlsFatalAlert ex)
            {
                // BouncyCastle заворачивает исключения валидатора в generic alert (internal_error=80),
                // поэтому достаём исходную причину из InnerException — иначе непонятно, что сломалось.
                string detail = ex.AlertDescription.ToString();
                if (ex.InnerException != null && !string.IsNullOrEmpty(ex.InnerException.Message))
                {
                    detail += " (" + ex.InnerException.Message + ")";
                }
                Diag.LogError("TLS fail host=" + uri.Host + ": " + detail);
                throw new IOException("TLS handshake failed: " + detail, ex);
            }
            finally
            {
                // соединение не держим: тело закрывает поток, см. HttpResponse.Dispose
            }
        }

        private HttpResponse Exchange(Stream io, Uri uri, string method, byte[] body, string contentType,
            IDictionary<string, string> headers, bool isHttps)
        {
            string pathAndQuery = uri.PathAndQuery;
            if (pathAndQuery.Length == 0) pathAndQuery = "/";

            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(uri.Host);
            if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
            sb.Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Accept: */*\r\n");
            sb.Append("Accept-Encoding: identity\r\n");
            sb.Append("User-Agent: YaMus/0.1\r\n");

            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                }
            }

            if (body != null && body.Length > 0)
            {
                sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                if (contentType != null)
                {
                    sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                }
            }

            sb.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            io.Write(headBytes, 0, headBytes.Length);
            if (body != null && body.Length > 0)
            {
                io.Write(body, 0, body.Length);
            }
            io.Flush();

            // --- статус-линия ---
            string statusLine = ReadAsciiLine(io);
            if (statusLine == null)
            {
                throw new IOException("Пустой ответ сервера");
            }

            string[] statusParts = statusLine.Split(' ');
            if (statusParts.Length < 2)
            {
                throw new IOException("Некорректная статус-строка: " + statusLine);
            }

            var response = new HttpResponse();
            int code;
            if (!int.TryParse(statusParts[1], out code))
            {
                throw new IOException("Не удалось распарсить код: " + statusLine);
            }
            response.StatusCode = code;
            if (statusParts.Length >= 3)
            {
                response.ReasonPhrase = string.Join(" ", statusParts, 2, statusParts.Length - 2);
            }

            // --- заголовки ---
            string line;
            while ((line = ReadAsciiLine(io)) != null && line.Length > 0)
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string key = line.Substring(0, colon).Trim();
                    string val = line.Substring(colon + 1).Trim();
                    response.Headers[key] = val;
                }
            }

            // --- тело ---
            if (code == 204 || code == 304)
            {
                response.BodyStream = new MemoryStream();
                return response;
            }

            if ("chunked".Equals(response.GetHeader("Transfer-Encoding"), StringComparison.OrdinalIgnoreCase))
            {
                response.BodyStream = new ChunkedStream(io);
                return response;
            }

            string contentLength = response.GetHeader("Content-Length");
            if (contentLength != null)
            {
                long len;
                if (!long.TryParse(contentLength, out len))
                {
                    len = -1;
                }
                if (len >= 0)
                {
                    response.BodyStream = new LimitedStream(io, len, response);
                    return response;
                }
            }

            // без длины — читаем до закрытия соединения
            response.BodyStream = io;
            return response;
        }

        private static string ReadAsciiLine(Stream s)
        {
            var bytes = new List<byte>(64);
            int b;
            bool any = false;
            while ((b = s.ReadByte()) != -1)
            {
                any = true;
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            if (bytes.Count == 0 && !any) return null;
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public void Dispose()
        {
        }

        /// <summary>Поток, читающий ровно Content-Length байт из нижележащего соединения.</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;
            private readonly IDisposable _owner;
            private bool _ownerDisposed;

            public LimitedStream(Stream inner, long length, IDisposable owner)
            {
                _inner = inner;
                _remaining = length;
                _owner = owner;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int toRead = (int)Math.Min(_remaining, count);
                int read = _inner.Read(buffer, offset, toRead);
                if (read > 0) _remaining -= read;
                return read;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _remaining; } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    // Защита от рекурсии: ответ (owner) при Dispose закрывает свой же
                    // BodyStream — этот поток. Внешний Dispose может вызвать owner.Dispose(),
                    // поэтому сперва закрываем нижележащий TLS-поток и гасим повторы флагом.
                    if (!_ownerDisposed)
                    {
                        _ownerDisposed = true;
                        try { if (_inner != null) _inner.Dispose(); }
                        catch { }
                        try { if (_owner != null) _owner.Dispose(); }
                        catch { }
                    }
                }
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}