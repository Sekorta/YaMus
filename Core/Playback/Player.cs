using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;
using YaMus.Core.Concurrency;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Playback
{
    /// <summary>Трек, подготовленный заранее: декодер уже открыт (для flac/m4a ffmpeg уже
    /// отработал), поэтому переключение не читает файл и не декодирует на ходу.</summary>
    public sealed class PreparedTrack : IDisposable
    {
        private readonly object _sync = new object();
        private WaveStream _reader;
        private string _wavPath;
        private bool _disposed;

        internal PreparedTrack(WaveStream reader, string wavPath)
        {
            _reader = reader;
            _wavPath = wavPath;
        }

        /// <summary>Забирает ресурсы у подготовленного трека. false — уже освобождён/забран.</summary>
        internal bool TryTake(out WaveStream reader, out string wavPath)
        {
            lock (_sync)
            {
                if (_disposed || _reader == null)
                {
                    reader = null;
                    wavPath = null;
                    return false;
                }
                reader = _reader;
                wavPath = _wavPath;
                _reader = null;
                _wavPath = null;
                return true;
            }
        }

        public void Dispose()
        {
            WaveStream reader;
            string wavPath;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                reader = _reader;
                wavPath = _wavPath;
                _reader = null;
                _wavPath = null;
            }
            if (reader != null) { try { reader.Dispose(); } catch { } }
            if (wavPath != null)
            {
                try { if (File.Exists(wavPath)) File.Delete(wavPath); }
                catch { }
            }
        }
    }

    /// <summary>Обёртка над NAudio (WaveOutEvent). mp3 играется напрямую, flac декодируется в wav через ffmpeg.</summary>
    public sealed class Player : IDisposable
    {
        private readonly object _lock = new object();
        private IWavePlayer _output;
        private WaveStream _reader;
        private string _codec;
        private string _extraFile;
        private bool _manualStop;

        /// <summary>Звуковое устройство пропало или сменилось (например, Bluetooth-наушники
        /// отвалились) — WaveOutEvent упал с исключением. Вызывается на потоке устройства;
        /// подписчик обязан перенести работу в UI-поток.</summary>
        public event Action DeviceLost;

        public bool IsActive
        {
            get { lock (_lock) return _output != null; }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return _output != null && _output.PlaybackState == PlaybackState.Playing;
                }
            }
        }

        public string Codec
        {
            get { lock (_lock) return _codec; }
        }

        public TimeSpan Position
        {
            get
            {
                lock (_lock)
                {
                    try { return _reader != null ? _reader.CurrentTime : TimeSpan.Zero; }
                    catch { return TimeSpan.Zero; }
                }
            }
        }

        public TimeSpan Duration
        {
            get
            {
                lock (_lock)
                {
                    try { return _reader != null ? _reader.TotalTime : TimeSpan.Zero; }
                    catch { return TimeSpan.Zero; }
                }
            }
        }

        /// <summary>Перемотка.</summary>
        public void Seek(TimeSpan position)
        {
            lock (_lock)
            {
                if (_reader == null) return;
                try
                {
                    TimeSpan d = Duration;
                    if (d > TimeSpan.Zero && position > d) position = d;
                    if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                    _reader.CurrentTime = position;
                }
                catch { }
            }
        }

        /// <summary>Начинает проигрывание файла. Останавливает предыдущее.</summary>
        public void Open(string filePath, string codec)
        {
            OpenPrepared(Prepare(filePath, codec), filePath, codec);
        }

        /// <summary>Открывает файл и готовит декодер, НЕ останавливая текущий трек.
        /// Вызывается заранее (из префетча), чтобы чтение/декод не случались в момент
        /// переключения.</summary>
        public PreparedTrack Prepare(string filePath, string codec)
        {
            return Prepare(filePath, codec, TimeSpan.Zero);
        }

        /// <summary>Как <see cref="Prepare(string,string)"/>, но с длительностью трека из метаданных:
        /// она нужна быстрому mp3-ридеру (длительность без полного скана файла).</summary>
        public PreparedTrack Prepare(string filePath, string codec, TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            bool isMp3 = codec != null && codec.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0;
            WaveStream reader;
            string wavPath = null;
            if (isMp3)
            {
                // быстрый открытие: формат из первого кадра + длительность из Xing/метаданных.
                // Если файл нестандартный — откат на обычный (медленный) Mp3FileReader.
                try
                {
                    reader = new FastMp3Stream(OpenFileStream(filePath), duration.TotalSeconds);
                }
                catch (Exception ex)
                {
                    Diag.Log("PREP fast-mp3 fallback " + Path.GetFileName(filePath) + ": " + ex.Message);
                    reader = new Mp3FileReader(OpenFileStream(filePath));
                }
            }
            else
            {
                // aac/he-aac (m4a) и flac NAudio напрямую не декодирует — через ffmpeg в wav
                wavPath = DecodeViaFfmpeg(filePath);
                reader = new WaveFileReader(OpenFileStream(wavPath));
            }
            sw.Stop();
            Diag.Log("PREP " + (isMp3 ? "mp3" : "ffmpeg")
                     + " " + Path.GetFileName(filePath) + " " + sw.ElapsedMilliseconds + "ms");
            return new PreparedTrack(reader, wavPath);
        }

        /// <summary>Играет подготовленный трек. Если ресурсы уже забраны/освобождены —
        /// обычное открытие файла.</summary>
        public void OpenPrepared(PreparedTrack prep, string filePath, string codec)
        {
            WaveStream reader;
            string wavPath;
            if (prep == null || !prep.TryTake(out reader, out wavPath))
            {
                Open(filePath, codec);
                return;
            }

            Stop();

            var output = new WaveOutEvent();
            // крупные буферы: слабый диск и активная сеть (DPC от Wi-Fi на Atom) не должны
            // ронять звук — при 1000 мс × 3 устройство терпит паузы ОС до ~3 с без фриза
            output.DesiredLatency = 1000;
            output.NumberOfBuffers = 3;
            output.Init(reader);
            try { output.Volume = 0f; }
            catch { }

            lock (_lock)
            {
                _reader = reader;
                _output = output;
                _codec = codec;
                _extraFile = wavPath;
            }
            _manualStop = false;
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
            RampAsync(output, 4, true);
        }

        /// <summary>Остановка по концу файла — обычное дело; падение из-за пропавшего
        /// устройства — нам нужно знать об этом, чтобы перезапустить трек на новом.</summary>
        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (_manualStop) return;
            if (e == null || e.Exception == null) return;   // обычное окно файла

            IWavePlayer died = sender as IWavePlayer;
            lock (_lock)
            {
                if (!ReferenceEquals(died, _output)) return;   // уже заменён новым устройством
            }
            Diag.Log("AUDIO device lost: " + e.Exception.Message);
            RaiseDeviceLost();
        }

        private void RaiseDeviceLost()
        {
            Action h = DeviceLost;
            if (h != null) h();
        }

        public void Pause()
        {
            lock (_lock) if (_output != null) _output.Pause();
        }

        public void Resume()
        {
            IWavePlayer output;
            lock (_lock) output = _output;
            if (output == null) return;
            try
            {
                output.Play();
            }
            catch (Exception ex)
            {
                // устройство могло пропасть прямо между Pause и Resume (Bluetooth)
                Diag.Log("AUDIO resume failed: " + ex.Message);
                if (_manualStop) return;
                RaiseDeviceLost();
            }
        }

        public void Stop()
        {
            IWavePlayer outputToStop;
            string extraToDelete;
            lock (_lock)
            {
                outputToStop = _output;
                _output = null;
                extraToDelete = _extraFile;
                _extraFile = null;
            }
            _manualStop = true;
            if (outputToStop != null)
            {
                Ramp(outputToStop, 4, false);   // глушим перед Stop — без щелчка на срезе
                try { outputToStop.Stop(); }
                catch { }
                try { outputToStop.Dispose(); }
                catch { }
            }
            lock (_lock)
            {
                if (_reader != null)
                {
                    var r = _reader;
                    _reader = null;
                    try { r.Dispose(); }
                    catch { }
                }
                _codec = null;
            }
            if (extraToDelete != null)
            {
                try { if (File.Exists(extraToDelete)) File.Delete(extraToDelete); }
                catch { }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Быстрое (без 15 мс на шаг) плавное появление звука — на фоновом
        /// потоке, чтобы не морозить UI при переключении треков.</summary>
        private static void RampAsync(IWavePlayer output, int steps, bool up)
        {
            Bg.Run(() => Ramp(output, steps, up));
        }

        /// <summary>Плавный набор/сброс громкости (~15 мс на шаг). up=true — от 0 до 1.
        /// Тихо проворачивается на фоне; громкость WaveOutEvent не трогает системный миксер.
        /// НЕ использовать с WasapiOut: его Volume в shared-режиме пишет в системный мастер-громк.</summary>
        private static void Ramp(IWavePlayer output, int steps, bool up)
        {
            for (int i = 0; i < steps; i++)
            {
                float v = up ? (float)(i + 1) / steps : (float)(steps - 1 - i) / steps;
                try { output.Volume = v; }
                catch { return; }
                Thread.Sleep(15);
            }
            if (!up)
            {
                try { output.Volume = 0f; }
                catch { }
            }
        }

        private static Stream OpenFileStream(string path)
        {
            return new ReadAheadStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                                       Path.GetFileName(path));
        }

        private static string DecodeViaFfmpeg(string srcPath)
        {
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                throw new IOException("FLAC-трек, но ffmpeg.exe не найден рядом с приложением. Положите ffmpeg.exe рядом с YaMus.exe");
            }

            string wavPath = Path.Combine(Path.GetTempPath(), "YaMus",
                Path.GetFileNameWithoutExtension(srcPath) + "_" + Guid.NewGuid().ToString("N") + ".wav");
            try { Directory.CreateDirectory(Path.GetDirectoryName(wavPath)); }
            catch { }

            var psi = new ProcessStartInfo();
            psi.FileName = ffmpeg;
            // -threads 1 до -i: один декодер-поток, чтобы ffmpeg не дрался с проигрыванием
            psi.Arguments = "-y -loglevel error -threads 1 -i \"" + srcPath + "\" \"" + wavPath + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;

            using (var proc = Process.Start(psi))
            {
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120000);
                if (proc.ExitCode != 0 || !File.Exists(wavPath) || new FileInfo(wavPath).Length == 0)
                {
                    throw new IOException("ffmpeg не смог декодировать FLAC: " + stderr);
                }
            }
            return wavPath;
        }

        private static string FindFfmpeg()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            try
            {
                var env = Environment.GetEnvironmentVariable("PATH");
                if (env != null)
                {
                    foreach (string p in env.Split(Path.PathSeparator))
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        try
                        {
                            string candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}