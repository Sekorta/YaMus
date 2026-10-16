using System;
using System.IO;

namespace YaMus.Core.Transport
{
    /// <summary>Декодирует HTTP chunked transfer encoding на лету (для больших тел).</summary>
    internal sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;
        private bool _done;

        public ChunkedStream(Stream inner)
        {
            _inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_done || count <= 0) return 0;

            while (_remaining <= 0)
            {
                if (!NextChunkStart()) return 0;
            }

            int toRead = (int)Math.Min(_remaining, count);
            int read = _inner.Read(buffer, offset, toRead);
            if (read <= 0) return 0;
            _remaining -= read;

            if (_remaining == 0)
            {
                // пропустить \r\n после чанка
                if (ReadByte(_inner) != '\r' || ReadByte(_inner) != '\n')
                {
                    throw new IOException("Malformed chunk terminator");
                }
            }
            return read;
        }

        private bool NextChunkStart()
        {
            string line = ReadAsciiLine(_inner);
            if (line == null)
            {
                _done = true;
                return false;
            }

            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);

            long size;
            try
            {
                size = Convert.ToInt64(line.Trim(), 16);
            }
            catch
            {
                throw new IOException("Invalid chunk size: " + line);
            }

            if (size == 0)
            {
                // трейлеры до пустой строки
                while (ReadAsciiLine(_inner) != null) { }
                _done = true;
                return false;
            }

            _remaining = size;
            return true;
        }

        private static int ReadByte(Stream s)
        {
            return s.ReadByte();
        }

        private static string ReadAsciiLine(Stream s)
        {
            var bytes = new System.Collections.Generic.List<byte>(32);
            int b;
            while ((b = s.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            if (bytes.Count == 0 && b == -1) return null;
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}