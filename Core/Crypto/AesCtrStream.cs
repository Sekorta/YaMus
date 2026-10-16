using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace YaMus.Core.Crypto
{
    /// <summary>
    /// Потоковый AES-128-CTR декриптор. Реализует поведение WebCrypto AES-CTR
    /// (counter = 16 байт нулей, length = 128): блок-счётчик увеличивается как
    /// беззнаковое big-endian целое на каждый блок (16 байт).
    /// Используется при скачивании потока трека в файл/память.
    /// </summary>
    public sealed class AesCtrStream : Stream
    {
        private static readonly int CounterBits = 128; // если сервер с другим счётчиком — поправить на 64

        private readonly Stream _inner;
        private readonly ICryptoTransform _transform;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _keystreamPos = 16;
        private long _position;
        private bool _disposed;

        public AesCtrStream(Stream inner, byte[] key)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (key == null || key.Length != 16)
            {
                throw new ArgumentException("AES-128 ключ должен быть 16 байт", "key");
            }
            _inner = inner;

            var rijndael = new RijndaelManaged();
            rijndael.KeySize = 128;
            rijndael.BlockSize = 128;
            rijndael.Mode = CipherMode.ECB;
            rijndael.Padding = PaddingMode.None;
            rijndael.Key = key;
            rijndael.IV = new byte[16];
            _transform = rijndael.CreateEncryptor();
        }

        private void NextBlock()
        {
            _transform.TransformBlock(_counter, 0, 16, _keystream, 0);
            IncrementCounter();
            _keystreamPos = 0;
        }

        private void IncrementCounter()
        {
            if (CounterBits == 128)
            {
                for (int i = 15; i >= 0; i--)
                {
                    if (++_counter[i] != 0) break;
                }
            }
            else if (CounterBits == 64)
            {
                for (int i = 15; i >= 8; i--)
                {
                    if (++_counter[i] != 0) break;
                }
            }
            else
            {
                throw new NotSupportedException("CounterBits должно быть 64 или 128");
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                if (_keystreamPos >= 16)
                {
                    NextBlock();
                }
                int avail = Math.Min(16 - _keystreamPos, count - totalRead);
                int read = _inner.Read(buffer, offset + totalRead, avail);
                if (read <= 0) break;

                for (int i = 0; i < read; i++)
                {
                    buffer[offset + totalRead + i] ^= _keystream[_keystreamPos + i];
                }
                _keystreamPos += read;
                totalRead += read;
                _position += read;
                // декрипт чисто на CPU — раз в ~64 КБ отдаём квант остальным нитям
                // (аудио/UI), чтобы на слабом Atom скачивание не выжирало всё
                if ((_position & 0xFFFF) == 0) Thread.Sleep(0);
            }
            return totalRead;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { return _position; }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _transform.Dispose();
                try { _inner.Dispose(); }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}