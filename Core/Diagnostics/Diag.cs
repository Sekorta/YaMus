using System;
using System.Collections.Generic;
using System.IO;

namespace YaMus.Core.Diagnostics
{
    /// <summary>Минимальное логирование сбоев в %APPDATA%\YaMus\crash.log.</summary>
    public static class Diag
    {
        private static readonly object Lock = new object();

        /// <summary>Начало новой сессии: прошлый crash.log копируется в crash.prev.log, а сам
        /// crash.log перезаписывается заголовком. Файл НЕ перемещается — так crash.log не может
        /// «исчезнуть», даже если запись заголовка почему-то не удалась.</summary>
        public static void StartSession()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMus");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string log = Path.Combine(dir, "crash.log");
                string prev = Path.Combine(dir, "crash.prev.log");

                try { if (File.Exists(log)) File.Copy(log, prev, true); }
                catch { }

                string header = "=== session start v" + AppVersion() + " " + Environment.OSVersion
                                + " verbose=" + Verbose + " " + dir + " ===";
                try
                {
                    File.WriteAllText(log, Stamp() + " " + header + Environment.NewLine);
                }
                catch
                {
                    // не смогли обновить — оставляем прежний crash.log как есть
                }
            }
            catch
            {
            }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }

        private static string AppVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var attrs = asm.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    return ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                }
                return asm.GetName().Version.ToString();
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>Подробные логи (аудио, обложки, ротор, префетч) включаются только в
        /// отладочной сборке либо при переменной окружения YAMUS_DEBUG=1. В релизе молчат:
        /// синхронная запись на горячих путях заметна на слабом Atom. Исключения пишутся всегда.</summary>
        public static readonly bool Verbose = DefaultVerbose();

        private static bool DefaultVerbose()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("YAMUS_DEBUG");
                if (!string.IsNullOrEmpty(env) && env != "0") return true;
            }
            catch
            {
            }
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        public static void Log(string msg)
        {
            if (!Verbose) return;
            Write(msg);
        }

        /// <summary>Критичные сбои (сеть/TLS/сертификаты) — пишутся всегда, независимо от
        /// режима подробного лога, чтобы их было видно и в Release.</summary>
        public static void LogError(string msg)
        {
            Write("ERROR " + msg);
        }

        private static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMus");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"),
                        Stamp() + " " + msg + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        public static void LogStall(string what, long ms)
        {
            Log("STALL " + what + ": " + ms + " ms");
        }

        /// <summary>Временной профиль UI-потока: раз в секунду выгружает в лог категории,
        /// набравшие больше 250 мс. Расход на замер минимальный (только Stopwatch), без потоков.</summary>
        public static class UiProfile
        {
            private static readonly object Lock = new object();
            private static readonly Dictionary<string, long> Acc = new Dictionary<string, long>();
            private static readonly System.Diagnostics.Stopwatch Win = System.Diagnostics.Stopwatch.StartNew();

            public static void Add(string what, long ms)
            {
                lock (Lock)
                {
                    long v;
                    Acc.TryGetValue(what, out v);
                    Acc[what] = v + ms;
                    if (Win.ElapsedMilliseconds > 1000)
                    {
                        foreach (var kv in Acc)
                        {
                            if (kv.Value > 250) LogStall("bucket " + kv.Key, kv.Value);
                        }
                        Acc.Clear();
                        Win.Restart();
                    }
                }
            }
        }

        public static void LogException(string where, Exception ex)
        {
            if (ex == null)
            {
                Write(where + ": null");
                return;
            }
            // известный безвредный баг WinForms .NET 4.0: у виртуального списка (VirtualMode)
            // .NET обращается к неподдерживаемому Items[index] и бросает это исключение.
            // Отрисовка при этом продолжается — просто игнорируем шум, чтобы лог был чистым.
            if (ex.Message != null &&
                ex.Message.IndexOf("RetrieveVirtualListItem", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }
            Write(where + ": " + ex.GetType().FullName + ": " + ex.Message);
            Write("  stack: " + (ex.StackTrace ?? "<нет>"));
        }
    }
}