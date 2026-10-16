using System;

namespace YaMus.Core.Playback
{
    /// <summary>Уровень качества загружаемой музыки.</summary>
    public enum MusicQuality
    {
        Low,
        Medium,
        Max
    }

    /// <summary>Одна попытка получить ссылку: качество + допустимые кодеки.</summary>
    public sealed class QualityAttempt
    {
        public readonly string Quality;
        public readonly string[] Codecs;

        public QualityAttempt(string quality, string[] codecs)
        {
            Quality = quality;
            Codecs = codecs;
        }
    }

    /// <summary>Каталог уровней качества: подпись, описание (формат/битрейт) и
    /// цепочка попыток от лучшего варианта к запасному.</summary>
    public static class MusicQualityCatalog
    {
        private static MusicQuality _current = MusicQuality.Max;

        public static MusicQuality Current
        {
            get { return _current; }
            set { _current = value; }
        }

        public static MusicQuality Parse(string name)
        {
            if (string.Equals(name, "max", StringComparison.OrdinalIgnoreCase)) return MusicQuality.Max;
            if (string.Equals(name, "low", StringComparison.OrdinalIgnoreCase)) return MusicQuality.Low;
            return MusicQuality.Medium;
        }

        public static string NameOf(MusicQuality q)
        {
            switch (q)
            {
                case MusicQuality.Max: return "max";
                case MusicQuality.Low: return "low";
                default: return "medium";
            }
        }

        public static string TitleOf(MusicQuality q)
        {
            switch (q)
            {
                case MusicQuality.Max: return "Максимальное";
                case MusicQuality.Low: return "Низкое";
                default: return "Среднее";
            }
        }

        /// <summary>Примерный формат и битрейт для подписи в настройках.</summary>
        public static string Describe(MusicQuality q)
        {
            switch (q)
            {
                case MusicQuality.Max:
                    return "MP3 ~320 кбит/с, а FLAC lossless — только на подписке Плюс.\n" +
                           "Максимальное качество, самый большой файл.";
                case MusicQuality.Low:
                    return "AAC / HE-AAC ~64 кбит/с — только на подписке Плюс.\n" +
                           "Без Плюса сервер всё равно отдаёт MP3 ~192 кбит/с.";
                default:
                    return "MP3 ~192 кбит/с (или AAC ~192 кбит/с).\n" +
                           "Баланс качества и размера файла.";
            }
        }

        public static QualityAttempt[] AttemptsFor(MusicQuality q)
        {
            switch (q)
            {
                case MusicQuality.Max:
                    return new[]
                    {
                        new QualityAttempt("lossless", new[] { "flac" }),
                        new QualityAttempt("hq", new[] { "aac" }),
                        new QualityAttempt("nq", new[] { "mp3", "aac", "he-aac" })
                    };
                case MusicQuality.Low:
                    return new[]
                    {
                        new QualityAttempt("lq", new[] { "he-aac", "aac" }),
                        new QualityAttempt("nq", new[] { "mp3", "aac", "he-aac" })
                    };
                default:
                    return new[]
                    {
                        new QualityAttempt("nq", new[] { "mp3", "aac", "he-aac" }),
                        new QualityAttempt("lq", new[] { "he-aac", "aac" })
                    };
            }
        }
    }
}
