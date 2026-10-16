using System;
using System.IO;
using System.Threading;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Playback
{
    /// <summary>Read-ahead над файлом: фоновая нить заранее читает данные в кольцевой буфер,
    /// чтобы чтение во время воспроизведения не конкурировало с фоновой загрузкой файлов.
    /// При сбое нити или зависании диска прозрачно переходит на прямое чтение из файла.</summary>
    public sealed class ReadAheadStream : Stream
    {
        private const int RingSize = 1 << 20;      // 1 МБ впрок
        private const int ChunkSize = 64 * 1024;
        private const int WaitSliceMs = 20;
        private const int MaxStallMs = 1500;       // нет данных дольше — уходим на прямое чтение
        private const int StallLogMinMs = 40;      // ждали больше — вероятный микрофриз
        private const int StallLogIntervalMs = 400;

        private readonly Stream _inner;
        private readonly string _name;
        private readonly byte[] _ring = new byte[RingSize];
        private readonly object _sync = new object();
        private readonly Thread _feeder;
        private int _head;
        private int _tail;
        private int _count;
        private int _stallMs;
        private long _lastStallLogTicks = -StallLogIntervalMs;
        private bool _eof;
        private bool _disposed;
        private bool _failed;
        private bool _direct;

        public ReadAheadStream(Stream inner, string name)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (!inner.CanSeek) throw new ArgumentException("ReadAheadStream требует seekable-поток", "inner");
            _inner = inner;
            _name = name ?? "?";
            Diag.Log("AUDIO " + _name + " open len=" + _inner.Length);
            _feeder = new Thread(FeederLoop);
            _feeder.IsBackground = true;
            _feeder.Name = "YaMus.ReadAhead";
            _feeder.Priority = ThreadPriority.AboveNormal;
            _feeder.Start();
        }

        public override bool CanRead { get { return _inner.CanRead; } }
        public override bool CanSeek { get { return _inner.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _inner.Length; } }

        public override long Position
        {
            get { lock (_sync) return LogicalPosition(); }
            set { Seek(value, SeekOrigin.Begin); }
        }

        private long LogicalPosition()
        {
            long p = _inner.Position;
            return _direct ? p : p - _count;
        }

        public override void Flush()
        {
            lock (_sync) _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException();
            if (offset + count > buffer.Length) throw new ArgumentException();
            if (count == 0) return 0;

            lock (_sync)
            {
                int total = 0;
                while (total < count)
                {
                    if (_count == 0 && !_eof && !_direct && !_failed && !_disposed)
                    {
                        // кольцо пусто — потребитель (аудио) ждёт данные диска;
                        // замеряем ожидание, чтобы ловить микрофризы
                        long wStart = Environment.TickCount;
                        WaitForData();
                        long wMs = unchecked(Environment.TickCount - wStart);
                        if (wMs >= StallLogMinMs &&
                            unchecked(Environment.TickCount - _lastStallLogTicks) >= StallLogIntervalMs)
                        {
                            _lastStallLogTicks = Environment.TickCount;
                            Diag.Log("AUDIO " + _name + " starve " + wMs + "ms ring=" + _count / 1024 + "K " +
                                     (_direct ? "direct" : _failed ? "io-fail" : _eof ? "eof" : "filling"));
                        }
                    }
                    else
                    {
                        WaitForData();
                    }
                    if (_disposed) break;
                    if (_direct)
                    {
                        int r = _inner.Read(buffer, offset + total, count - total);
                        if (r <= 0) break;
                        total += r;
                        continue;
                    }
                    if (_count == 0) break;                 // конец файла
                    int n = count - total < _count ? count - total : _count;
                    int first = n;
                    if (_ring.Length - _head < first) first = _ring.Length - _head;
                    Buffer.BlockCopy(_ring, _head, buffer, offset + total, first);
                    if (n > first) Buffer.BlockCopy(_ring, 0, buffer, offset + total + first, n - first);
                    _head = (_head + n) % _ring.Length;
                    _count -= n;
                    _stallMs = 0;
                    total += n;
                    Monitor.PulseAll(_sync);                // место под чтение нити
                }
                return total;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_sync)
            {
                if (_direct) return _inner.Seek(offset, origin);
                long target;
                if (origin == SeekOrigin.Begin) target = offset;
                else if (origin == SeekOrigin.Current) target = LogicalPosition() + offset;
                else target = _inner.Length + offset;
                if (target < 0) target = 0;
                _inner.Seek(target, SeekOrigin.Begin);
                _head = 0;
                _tail = 0;
                _count = 0;
                _eof = false;
                _stallMs = 0;
                Monitor.PulseAll(_sync);
                return target;
            }
        }

        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                if (_disposed) { base.Dispose(disposing); return; }
                _disposed = true;
                Monitor.PulseAll(_sync);
            }
            if (_feeder != Thread.CurrentThread)
            {
                try { _feeder.Join(500); }
                catch { }
            }
            Diag.Log("AUDIO " + _name + " close");
            if (disposing)
            {
                try { _inner.Dispose(); }
                catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>Ждёт данные в кольце; возвращает при EOF/сбое/переходе на прямое чтение.</summary>
        private void WaitForData()
        {
            while (_count == 0 && !_eof && !_failed && !_direct && !_disposed)
            {
                Monitor.Wait(_sync, WaitSliceMs);
                if (_count == 0 && !_eof && !_failed && !_direct && !_disposed)
                {
                    _stallMs += WaitSliceMs;
                    if (_stallMs >= MaxStallMs) ToDirect();
                }
            }
            if (_failed && !_direct) ToDirect();
        }

        /// <summary>Откатывает прочитанное нитью впрок и отключает read-ahead (вызывать под _sync).</summary>
        private void ToDirect()
        {
            Diag.Log("AUDIO " + _name + " -> direct stall=" + _stallMs + "ms pos=" + LogicalPosition());
            try { _inner.Seek(LogicalPosition(), SeekOrigin.Begin); }
            catch { }
            _direct = true;
            _failed = false;
            Monitor.PulseAll(_sync);
        }

        private void FeederLoop()
        {
            try
            {
                while (true)
                {
                    lock (_sync)
                    {
                        if (_disposed || _direct) return;
                        if (_eof || _count == _ring.Length)
                        {
                            // ждём Seek или место: без него повторное чтение на EOF съело бы CPU
                            Monitor.Wait(_sync, WaitSliceMs);
                            continue;
                        }
                        int got = ReadChunk();
                        if (got == 0)
                        {
                            _eof = true;
                        }
                        else _count += got;
                        Monitor.PulseAll(_sync);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Log("AUDIO " + _name + " feeder-fail: " + ex.GetType().Name + ": " + ex.Message);
                lock (_sync)
                {
                    _failed = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        /// <summary>Читает до ChunkSize в свободную часть кольца, переходя через границу массива.</summary>
        private int ReadChunk()
        {
            int want = _ring.Length - _count;
            if (want > ChunkSize) want = ChunkSize;

            int limit = _head > _tail ? _head : _ring.Length;   // свободно от _tail до limit
            int first = limit - _tail < want ? limit - _tail : want;
            if (first <= 0) return 0;
            int got = _inner.Read(_ring, _tail, first);
            if (got <= 0) return 0;

            if (got == first && first < want && _head <= _tail)
            {
                // докусок в начало кольца: свободны байты [0.._head)
                int second = _head < want - first ? _head : want - first;
                if (second > 0)
                {
                    int g2 = _inner.Read(_ring, 0, second);
                    if (g2 > 0) got += g2;
                }
            }
            _tail = (_tail + got) % _ring.Length;
            return got;
        }
    }
}
