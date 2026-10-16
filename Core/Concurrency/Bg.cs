using System;
using System.Threading;
using System.Threading.Tasks;

namespace YaMus.Core.Concurrency
{
    /// <summary>Запуск фоновой работы строго на пуле потоков.
    ///
    /// Task.Factory.StartNew без явного планировщика берёт TaskScheduler.Current, а
    /// внутри ContinueWith(..., TaskScheduler.FromCurrentSynchronizationContext())
    /// это планировщик UI-потока. Тогда «фоновый» код (скачивание трека, API,
    /// декодирование) выполняется в насосе сообщений WinForms, и весь интерфейс
    /// — плеер, список, оверлей — висит на всё время сетевого запроса.
    /// Здесь планировщик всегда ThreadPool, независимо от места вызова.
    /// Task.Run появился только в .NET 4.5, а проект собирается под 4.0.</summary>
    public static class Bg
    {
        public static Task Run(Action action)
        {
            return Task.Factory.StartNew(action, CancellationToken.None,
                TaskCreationOptions.None, TaskScheduler.Default);
        }

        public static Task<T> Run<T>(Func<T> func)
        {
            return Task.Factory.StartNew(func, CancellationToken.None,
                TaskCreationOptions.None, TaskScheduler.Default);
        }
    }
}