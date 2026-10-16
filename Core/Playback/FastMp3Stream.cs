using System;
using System.IO;
using NAudio.Wave;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Playback
{
    /// <summary>Быстрое открытие mp3 без полного скана файла. Mp3FileReader в конструкторе
    /// строит таблицу-оглавление (CreateTableOfContents читает каждый кадр) — для 20 МБ это
    /// 2–11 с, которые уходят ещё и на перемотки + «eof»-спам read-ahead. Здесь формат берётся
    /// из первого кадра (плюс Xing-заголовок или длительность из метаданных трека), декодирование
    /// кадров идёт по мере чтения, перемотка — пропорциональная с резником по sync-слову.</summary>
    public sealed class FastMp3Stream : WaveStream
    {
        private readonly object _lock = new object();
        private readonly Stream _mp3Stream;      // владеем потоком: закрываем при Dispose
        private readonly long _dataStartPosition;
        private readonly long _mp3DataLength;
        private readonly Mp3WaveFormat _mp3WaveFormat;
        private readonly IMp3FrameDecompressor _decompressor;
        private readonly WaveFormat _waveFormat; // выходной (PCM) формат
        private readonly int _sourceSampleRate;  // частота дискретизации исходного файла
        private readonly int _bytesPerSample;
        private readonly int _bytesPerDecodedFrame;
        private readonly byte[] _decompressBuffer;
        private readonly long _totalSamples;

        private long _position;                  // позиция в декодированных байтах
        private int _decompressBufferOffset;
        private int _decompressLeftovers;
        private bool _repositioned;
        private int _warmupFramesToSkip;
        private bool _disposed;

        public FastMp3Stream(Stream inputStream, double durationSeconds)
        {
            if (inputStream == null) throw new ArgumentNullException("inputStream");
            _mp3Stream = inputStream;
            try
            {
                long dataStart = SkipId3v2Tag(_mp3Stream);

                Mp3Frame firstFrame = Mp3Frame.LoadFromStream(_mp3Stream);
                if (firstFrame == null)
                    throw new InvalidDataException("Invalid MP3 file - no MP3 Frames Detected");
                double bitRate = firstFrame.BitRate;
                XingHeader xingHeader = XingHeader.LoadXingHeader(firstFrame);
                // если первый кадр — Xing/LAME-заголовок, реальные данные начинаются после него
                if (xingHeader != null) dataStart = _mp3Stream.Position;

                // обходной путь для файлов с ложной сменой формата в первом кадре
                Mp3Frame secondFrame = Mp3Frame.LoadFromStream(_mp3Stream);
                if (secondFrame != null &&
                    (secondFrame.SampleRate != firstFrame.SampleRate ||
                     secondFrame.ChannelMode != firstFrame.ChannelMode))
                {
                    dataStart = secondFrame.FileOffset;
                    firstFrame = secondFrame;
                }

                _dataStartPosition = dataStart;
                _mp3DataLength = _mp3Stream.Length - _dataStartPosition;

                // ID3v1-тег (128 байт в конце) можно отбросить, но не проверяем его здесь:
                // Seek к концу файла заставляет read-ahead упереться в EOF и логировать
                // «eof read=» при каждом открытии. Выигрыш от коррекции ~128 байт из 7–20 МБ
                // ничтожен, поэтому не дёргаем поток и не шумим в лог.
                _mp3Stream.Position = dataStart;

                _mp3WaveFormat = new Mp3WaveFormat(firstFrame.SampleRate,
                    firstFrame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                    firstFrame.FrameLength, (int)bitRate);
                _decompressor = Mp3FileReader.CreateAcmFrameDecompressor(_mp3WaveFormat);
                _waveFormat = _decompressor.OutputFormat;
                _sourceSampleRate = firstFrame.SampleRate;
                _bytesPerSample = _waveFormat.BitsPerSample / 8 * _waveFormat.Channels;
                // кадров больше 1152 отсчётов не бывает, но поле в 2 кадра страхует
                // от случаев, когда декодер после Reset отдаёт «сдвоенную» порцию.
                _bytesPerDecodedFrame = 1152 * _bytesPerSample;
                _decompressBuffer = new byte[_bytesPerDecodedFrame * 4];

                // длительность: Xing (точная), иначе длительность из метаданных трека,
                // иначе CBR-оценка по битрейту первого кадра
                long samples;
                string how;
                if (xingHeader != null && xingHeader.Frames > 0)
                {
                    samples = (long)xingHeader.Frames * firstFrame.SampleCount;
                    how = "xing";
                }
                else if (durationSeconds > 0)
                {
                    samples = (long)(durationSeconds * firstFrame.SampleRate);
                    how = "meta";
                }
                else
                {
                    samples = (long)(_mp3DataLength * 8.0 / bitRate * firstFrame.SampleRate);
                    how = "cbr";
                }
                _totalSamples = samples;

                Diag.Log("FASTMP3 " + _waveFormat.SampleRate + "Hz/" + _waveFormat.Channels + "ch "
                         + "dur=" + (long)(_totalSamples * 1000 / _waveFormat.SampleRate) + "ms via " + how
                         + " (no scan)");
            }
            catch
            {
                _mp3Stream.Dispose();
                throw;
            }
        }

        public override WaveFormat WaveFormat
        {
            get { return _waveFormat; }
        }

        /// <summary>Полная длина декодированных PCM-данных.</summary>
        public override long Length
        {
            get { return _totalSamples * _bytesPerSample; }
        }

        public override long Position
        {
            get { lock (_lock) return _position; }
            set
            {
                lock (_lock)
                {
                    value = Math.Max(0, Math.Min(value, Length));
                    // пропорциональный поиск: точной таблицы кадров нет, зато резник
                    // Mp3Frame сам найдёт ближайший sync-слово
                    long fileOffset;
                    if (_totalSamples > 0)
                    {
                        long sampleTarget = value / _bytesPerSample;
                        fileOffset = _dataStartPosition
                                     + (long)((double)sampleTarget / _totalSamples * _mp3DataLength);
                    }
                    else fileOffset = _dataStartPosition;
                    if (fileOffset < _dataStartPosition) fileOffset = _dataStartPosition;
                    if (fileOffset > _dataStartPosition + _mp3DataLength)
                        fileOffset = _dataStartPosition + _mp3DataLength;

                    _mp3Stream.Position = fileOffset;
                    _decompressor.Reset();
                    _decompressBufferOffset = 0;
                    _decompressLeftovers = 0;
                    _repositioned = true;
                    _warmupFramesToSkip = 2;   // после Reset декодер отдаёт «прогретый» кадр — пропускаем
                    _position = value;
                }
            }
        }

        public override int Read(byte[] sampleBuffer, int offset, int numBytes)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                int bytesRead = 0;
                if (_decompressLeftovers != 0)
                {
                    int toCopy = Math.Min(_decompressLeftovers, numBytes);
                    Array.Copy(_decompressBuffer, _decompressBufferOffset, sampleBuffer, offset, toCopy);
                    _decompressLeftovers -= toCopy;
                    if (_decompressLeftovers == 0) _decompressBufferOffset = 0;
                    else _decompressBufferOffset += toCopy;
                    bytesRead += toCopy;
                    offset += toCopy;
                }

                while (bytesRead < numBytes)
                {
                    Mp3Frame frame = ReadNextFrame();
                    if (frame == null) break;
                    if (_repositioned)
                    {
                        _repositioned = false;
                        _warmupFramesToSkip = 2;
                    }
                    int decompressed;
                    try
                    {
                        decompressed = _decompressor.DecompressFrame(frame, _decompressBuffer, 0);
                    }
                    catch (ArgumentException)
                    {
                        // кадр из ложного sync-слова даёт декодеру размер больше буфера —
                        // просто останавливаемся, чтоб не ронять плейер
                        break;
                    }
                    if (decompressed > _decompressBuffer.Length) decompressed = _decompressBuffer.Length;
                    if (_warmupFramesToSkip > 0 && decompressed > 0)
                    {
                        _warmupFramesToSkip--;
                        continue;
                    }
                    if (decompressed == 0) continue;   // декодеру нужен ещё один кадр (после Reset)

                    int toCopy = Math.Min(decompressed - _decompressBufferOffset, numBytes - bytesRead);
                    Array.Copy(_decompressBuffer, _decompressBufferOffset, sampleBuffer, offset, toCopy);
                    if (toCopy + _decompressBufferOffset < decompressed)
                    {
                        _decompressBufferOffset = toCopy + _decompressBufferOffset;
                        _decompressLeftovers = decompressed - _decompressBufferOffset;
                    }
                    else
                    {
                        _decompressBufferOffset = 0;
                        _decompressLeftovers = 0;
                    }
                    offset += toCopy;
                    bytesRead += toCopy;
                }

                _position += bytesRead;
                return bytesRead;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    if (_decompressor != null) { try { _decompressor.Dispose(); } catch { } }
                    try { _mp3Stream.Dispose(); }
                    catch { }
                }
            }
            base.Dispose(disposing);
        }

        private Mp3Frame ReadNextFrame()
        {
            // После пропорционального поиска LoadFromStream резнится на первый встречный
            // синхронизирующий маркер. В середине битового потока такой «sync» может быть
            // ложным (например, sr=16000 у файла, который на деле 44100 Гц) — декодер тогда
            // отдаёт порцию, не влезающую в буфер. Частота дискретизации внутри одного mp3
            // постоянна, поэтому кадры с «чужой» частотой пропускаем как мусор.
            while (true)
            {
                Mp3Frame frame;
                try { frame = Mp3Frame.LoadFromStream(_mp3Stream); }
                catch (EndOfStreamException) { return null; }
                if (frame == null) return null;
                if (frame.SampleRate == _sourceSampleRate) return frame;
            }
        }

        /// <summary>Пропускает ID3v2-тег (по syncsafe-размеру в заголовке), возвращая позицию
        /// начала аудиоданных. Если тега нет — сбрасывает поток на ноль.</summary>
        private static long SkipId3v2Tag(Stream s)
        {
            long start = s.Position;
            if (s.Length - s.Position < 10) return start;
            byte[] hdr = new byte[10];
            if (s.Read(hdr, 0, 10) < 10)
            {
                s.Position = start;
                return start;
            }
            if (hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3')
            {
                s.Position = start;
                return start;
            }
            int size = ((hdr[6] & 0x7f) << 21) | ((hdr[7] & 0x7f) << 14) | ((hdr[8] & 0x7f) << 7) | (hdr[9] & 0x7f);
            int footer = (hdr[5] & 0x10) != 0 ? 10 : 0;
            long end = start + 10 + size + footer;
            if (end > s.Length) end = s.Length;
            s.Position = end;
            return end;
        }
    }
}