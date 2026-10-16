using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using YaMus.Core.Models;

namespace YaMus.UI
{
    /// <summary>История переходов по страницам: кнопки «назад/вперёд» в шапке и
    /// боковые кнопки мыши. Стек хранит не строки, а записи NavEntry: у радио
    /// нужно помнить сам трек, иначе вернуться нельзя (по id его не восстановить).</summary>
    public sealed partial class MainForm
    {
        private const int NavHistoryLimit = 50;

        /// <summary>Шаг истории: строка вьюхи + полезная нагрузка (Track для радио).</summary>
        private sealed class NavEntry
        {
            public string View;
            public object Payload;
        }

        private readonly List<NavEntry> _navBack = new List<NavEntry>();
        private readonly List<NavEntry> _navForward = new List<NavEntry>();
        /// <summary>Подавляет запись в историю, пока идёт GoBack/GoForward.</summary>
        private bool _navRestoring;
        /// <summary>Полезная нагрузка текущего шага (нужна, чтобы вернуть радио).</summary>
        private object _navPayload;

        private GlyphButton _navBackButton;
        private GlyphButton _navForwardButton;
        private long _lastNavCmdTicks;

        /// <summary>Единственная точка смены вьюхи. Все страницы ходят через неё,
        /// поэтому история не может разойтись с тем, что реально показано.</summary>
        private void SetView(string view, object payload)
        {
            if (string.IsNullOrEmpty(view) || view == _currentView) return;

            // Волна/радио — особое состояние (_waveMode + кнопки «Ещё/Обновить/Вернуть»),
            // не привязанное к строке вьюхи. Раньше его сбрасывал только клик по сайдбару,
            // а восстановление из истории (назад на «Главную»/лайки/плейлист/настройки)
            // проскакивало мимо: кнопки волны залипали на чужой странице, и следующий
            // LeaveWaveMode клал треки этой страницы в _waveCache — «Моя волна» потом
            // открывала чужой список. Поэтому гасим волну ровно здесь.
            bool wasWave = IsWaveView(_currentView);
            bool isWave = IsWaveView(view);
            if (wasWave && !isWave) LeaveWaveMode();

            if (!_navRestoring && !string.IsNullOrEmpty(_currentView))
            {
                _navBack.Add(new NavEntry { View = _currentView, Payload = _navPayload });
                if (_navBack.Count > NavHistoryLimit) _navBack.RemoveAt(0);
                // новый переход в другую ветку ломает «вперёд», как в браузере
                _navForward.Clear();
            }

            _currentView = view;
            _navPayload = payload;

            if (!isWave && _waveMode)
            {
                _waveMode = false;
                UpdateWaveButtons();
            }

            UpdateNavButtons();
        }

        /// <summary>Волна это или радио по треку — единственные вьюхи, где _waveMode
        /// должен быть включён и видны кнопки «Ещё/Обновить/Вернуть волну».</summary>
        private static bool IsWaveView(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            return v == "wave" || v.StartsWith("radio:", StringComparison.Ordinal);
        }

        private void GoBack()
        {
            if (_navBack.Count == 0) return;
            NavStep(-1);
        }

        private void GoForward()
        {
            if (_navForward.Count == 0) return;
            NavStep(+1);
        }

        private void NavStep(int dir)
        {
            List<NavEntry> from = dir < 0 ? _navBack : _navForward;
            List<NavEntry> to = dir < 0 ? _navForward : _navBack;
            if (from.Count == 0) return;

            NavEntry target = from[from.Count - 1];

            // Проверяем восстановимость ДО изменения стеков: если шаг вернуть нельзя
            // (радио без сохранённого трека), уходим на главную. Слой сразу выбрасываем,
            // иначе он застрянет во «вперёд» и назад будет уводить в никуда.
            if (!CanRestore(target))
            {
                from.RemoveAt(from.Count - 1);
                SetView("home", null);
                UpdateNavButtons();
                return;
            }

            from.RemoveAt(from.Count - 1);
            to.Add(new NavEntry { View = _currentView, Payload = _navPayload });

            _navRestoring = true;
            try
            {
                // НЕ выставляем _currentView/_navPayload заранее: пусть это сделает
                // SetView внутри загрузчика. Иначе SetView увидит уже новую вьюху,
                // сработает ранний выход, и сброс состояния волны не выполнится.
                RestoreView(target);
            }
            finally
            {
                _navRestoring = false;
                // в finally, а не после try: если загрузчик вдруг бросит,
                // кнопки всё равно должны показать реальные стеки
                UpdateNavButtons();
            }
        }

        /// <summary>Можно ли снова показать этот шаг. Всё, кроме радио, восстанавливается
        /// из самой строки; радио требует сохранённого объекта Track.</summary>
        private static bool CanRestore(NavEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.View)) return false;
            if (!entry.View.StartsWith("radio:", StringComparison.Ordinal)) return true;
            return entry.Payload is Track;
        }

        /// <summary>Повторяет показ вьюхи, записанной в истории. Строка разбирается
        /// обратно в вызов нужного загрузчика — тем же путём, что и обычный клик
        /// по сайдбару, поэтому подсветка и заголовок остаются корректными.</summary>
        private void RestoreView(NavEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.View)) return;
            string v = entry.View;

            if (v == "home") { ShowHomePage(); return; }
            if (v == "search") { ShowSearchPage(); return; }
            if (v == "likes") { LoadLikes(); return; }
            if (v == "wave") { LoadWave(); return; }
            if (v == "settings") { ShowSettingsPage(); return; }

            if (v.StartsWith("album:", StringComparison.Ordinal))
            {
                long id;
                if (long.TryParse(v.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    NavigateToAlbum(id);
                    return;
                }
            }
            else if (v.StartsWith("artist:", StringComparison.Ordinal))
            {
                long id;
                if (long.TryParse(v.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    NavigateToArtist(id);
                    return;
                }
            }
            else if (v.StartsWith("radio:", StringComparison.Ordinal))
            {
                var t = entry.Payload as Track;
                if (t != null) { StartTrackRadio(t); return; }
            }
            else if (v.StartsWith("p", StringComparison.Ordinal))
            {
                long kind;
                if (long.TryParse(v.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out kind))
                {
                    LoadPlaylist(kind);
                    return;
                }
            }

            // шаг не восстановился (например, радио без сохранённого трека) —
            // возвращаемся на главную, чтобы не остаться с мёртвой вьюхой
            ShowHomePage();
        }

        private void UpdateNavButtons()
        {
            if (_navBackButton != null)
            {
                bool hasBack = _navBack.Count > 0;
                SetNavButton(_navBackButton, UiGlyphs.NavBack, hasBack);
            }
            if (_navForwardButton != null)
            {
                bool hasForward = _navForward.Count > 0;
                SetNavButton(_navForwardButton, UiGlyphs.NavForward, hasForward);
            }
        }

        /// <summary>Обработка боковых кнопок мыши (назад/вперёд) и эквивалентных
        /// WM_APPCOMMAND. Вызывается из WndProc: DefWndProc на XButton1/XButton2
        /// ничего не делает, но сообщение всё равно нужно гасить, иначе оно
        /// уедет вверх и некоторые драйверы отправят его повторно.</summary>
        private bool HandleNavCommand(int code)
        {
            // 1 = APPCOMMAND_BROWSERBACKWARD, 2 = APPCOMMAND_BROWSERFORWARD
            if (code != 1 && code != 2) return false;

            // оверлей — не шаг истории, поэтому навигация в нём нечего не делает
            if (_nowPlaying != null && _nowPlaying.Visible) return true;

            // XButton1/XButton2 на многих мышах дублируются как WM_APPCOMMAND:
            // без debounce одно нажатие уходило бы на два шага
            long now = Environment.TickCount;
            if (now - _lastNavCmdTicks < 150) return true;
            _lastNavCmdTicks = now;
            // та же кнопка часто приходит ещё и как APPCOMMAND_MEDIA_PREVTRACK/NEXTTRACK:
            // без метки следом сработал бы Prev()/Next() и трек перезапустился с 0:00
            _lastMediaCmdTicks = now;

            if (code == 1) GoBack(); else GoForward();
            return true;
        }
    }
}
