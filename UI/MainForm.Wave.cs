using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Api;
using YaMus.Core.Concurrency;
using YaMus.Core.Models;

namespace YaMus.UI
{
    public sealed partial class MainForm
    {
        /// <summary>Берёт у ротора одну порцию (один запрос на партию, как официальный клиент),
        /// отсеивая повторы уже показывавшихся в текущей сессии. Один запрос — фид станции
        /// сдвигается ровно на показанное, не «уезжая» вперёд для официального приложения.</summary>
        private List<Track> FetchWaveBatch(int maxCalls)
        {
            return FetchWaveBatch(_waveStation, maxCalls);
        }

        /// <summary>Порция ротора для конкретной станции. Вынесено из FetchWaveBatch, чтобы
        /// догрузка «в контекст воспроизведения» могла работать с сохранённой станцией,
        /// даже когда _waveStation уже переключён на другую страницу.</summary>
        private List<Track> FetchWaveBatch(string station, int maxCalls)
        {
            var result = new List<Track>();
            var items = _api.GetRotation(station);
            if (items == null) return result;
            bool filterSeen = station == "user:onyourwave";
            foreach (Track tt in items)
            {
                if (tt == null || string.IsNullOrEmpty(tt.Id)) continue;
                if (!filterSeen || AddWaveSeen(tt.Id)) result.Add(tt);
            }
            return result;
        }

        /// <summary>Показывает кнопки волны: «Ещё»/«Обновить» всегда в режиме волны,
        /// «Вернуть волну» — только если станция не личная.</summary>
        private void UpdateWaveButtons()
        {
            if (_moreButton != null) _moreButton.Visible = _waveMode;
            if (_refreshButton != null) _refreshButton.Visible = _waveMode;
            if (_backButton != null) _backButton.Visible = _waveMode && _waveStation != "user:onyourwave";
            UpdateListTitleInsets();
        }

        /// <summary>Сдвигает заголовок влево ровно на ширину видимых кнопок волны,
        /// чтобы длинное название станции не заезжало под них.</summary>
        private void UpdateListTitleInsets()
        {
            var title = FindListTitle(this);
            if (title == null) return;
            int reserved = 0;
            if (_moreButton != null && _moreButton.Visible) reserved += _moreButton.Width + 10;
            if (_refreshButton != null && _refreshButton.Visible) reserved += _refreshButton.Width + 6;
            if (_backButton != null && _backButton.Visible) reserved += _backButton.Width + 6;
            title.Padding = new Padding(0, 0, reserved, 0);
        }

        /// <summary>Выход из режима волны: прячем кнопки «Ещё»/«Обновить».
        /// Последующие концы списков просто останавливают воспроизведение.</summary>
        private void LeaveWaveMode()
        {
            // кэшируем только когда реально уходим с волны/радио: иначе залипший
            // _waveMode записал бы в кэш волны треки текущей чужой страницы
            if (_waveMode && IsWaveView(_currentView) && _tracks != null)
            {
                _waveCache = _tracks;
                _waveCacheIndex = _currentIndex;
                _waveCacheStation = _waveStation;
            }
            _waveMode = false;
            UpdateWaveButtons();
        }

        /// <summary>Возврат к личной «Моей волне»: сбрасывает станцию и тянет свежую партию.</summary>
        private void ResetToPersonalWave()
        {
            if (!_waveMode) return;
            _waveStation = "user:onyourwave";
            SetListTitle("Моя волна");
            UpdateWaveButtons();
            RefreshWave();
        }

        /// <summary>Добавляет ID в историю «виденного» сессии с учётом лимита; true — если ID новый.</summary>
        private bool AddWaveSeen(string id)
        {
            bool added;
            lock (_waveSeenLock)
            {
                added = _waveSeenIds.Add(id);
                if (added)
                {
                    _waveSeenOrder.Add(id);
                    while (_waveSeenOrder.Count > WaveSeenLimit)
                    {
                        string oldest = _waveSeenOrder[0];
                        _waveSeenOrder.RemoveAt(0);
                        _waveSeenIds.Remove(oldest);
                    }
                }
            }
            return added;
        }

        private void FetchWaveMore(bool andPlay)
        {
            if (!_waveMode) return;
            if (_waveFetchBusy)
            {
                SetStatus("Треки уже загружаются…");
                return;
            }
            _waveFetchBusy = true;
            SetStatus("Загружаю ещё…");
            DisableNavigation();
            Bg.Run(() =>
            {
                List<Track> add = FetchWaveBatch(6);
                add.RemoveAll(x => x == null);
                if (add.Count == 0 && _waveStation == "user:onyourwave")
                {
                    // личная волна вернула уже виденное — сбрасываем историю сессии и пробуем ещё раз
                    lock (_waveSeenLock)
                    {
                        _waveSeenIds.Clear();
                        _waveSeenOrder.Clear();
                    }
                    add = FetchWaveBatch(6);
                    add.RemoveAll(x => x == null);
                }
                return add;
            }).ContinueWith(t =>
            {
                _waveFetchBusy = false;
                EnableNavigation();
                if (t.IsFaulted)
                {
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    return;
                }
                var add = t.Result;
                add.RemoveAll(x => x == null || _tracks.Exists(y => y != null && y.Id == x.Id));
                if (add.Count == 0)
                {
                    SetStatus("Новых треков в волне пока нет.");
                    return;
                }
                int playFrom = _tracks.Count; // считаем уже после загрузки, чтобы не было гонок
                _tracks.AddRange(add);
                _waveCache = _tracks;
                AppendTrackList(add);
                if (_waveMode)
                {
                    SetStatus("Моя волна: " + _tracks.Count + " треков");
                }
                if (andPlay && playFrom < _tracks.Count)
                {
                    PlayTrack(playFrom);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Пользователь ушёл со страницы волны, но слушает её дальше: на конце
        /// играющего контекста догружаем новую порцию прямо в _playTracks, не трогая
        /// список открытого раздела (_tracks). Иначе треки «не подкидывались».</summary>
        private void FetchWaveMoreForPlayback()
        {
            string station = _playStation;
            if (string.IsNullOrEmpty(station)) return;
            if (_waveFetchBusy)
            {
                SetStatus("Треки уже загружаются…");
                return;
            }
            _waveFetchBusy = true;
            SetStatus("Загружаю продолжение волны…");
            Bg.Run(() =>
            {
                List<Track> add = FetchWaveBatch(station, 6);
                add.RemoveAll(x => x == null);
                if (add.Count == 0 && station == "user:onyourwave")
                {
                    // личная волна вернула уже виденное — сбрасываем историю сессии и пробуем снова
                    lock (_waveSeenLock)
                    {
                        _waveSeenIds.Clear();
                        _waveSeenOrder.Clear();
                    }
                    add = FetchWaveBatch(station, 6);
                    add.RemoveAll(x => x == null);
                }
                return add;
            }).ContinueWith(t =>
            {
                _waveFetchBusy = false;
                if (t.IsFaulted)
                {
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    StopPlayback();
                    return;
                }
                List<Track> add = t.Result;
                if (_playTracks == null) _playTracks = new List<Track>();
                bool cacheMatches = _waveCache != null &&
                                    string.Equals(_waveCacheStation, station, StringComparison.Ordinal);
                add.RemoveAll(x => x == null
                    || _playTracks.Exists(y => y != null && y.Id == x.Id)
                    || (cacheMatches && _waveCache.Exists(y => y != null && y.Id == x.Id)));
                if (add.Count == 0)
                {
                    SetStatus("Конец волны.");
                    StopPlayback();
                    return;
                }
                int playFrom = _playTracks.Count;
                _playTracks.AddRange(add);
                // Кэш «Моей волны» (по нему LoadWave восстанавливает страницу) получает те же
                // треки, иначе при возврате на страницу волны догруженные в фоне «пропадали».
                if (cacheMatches)
                {
                    _waveCache.AddRange(add);
                    if (_waveMode && ReferenceEquals(_tracks, _waveCache)) AppendTrackList(add);
                }
                PlayTrackIn(_playTracks, playFrom);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Радио по треку: станция «track:{id}» вместо личной волны; играет первый трек.</summary>
        private void StartTrackRadio(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            LeaveWaveMode();
            _nav.SelectedNode = null;
            _waveStation = "track:" + track.Id;
            _waveMode = true;
            SetView("radio:" + track.Id, track);
            HideArtistPage();
            ClearPrefetch();
            _currentIndex = -1;
            UpdateWaveButtons();
            SetListTitle("Волна по треку: " + (string.IsNullOrEmpty(track.Title) ? track.Id : track.Title));
            DisableNavigation();
            SetStatus("Загружаю волну по треку…");
            Bg.Run(() => FetchWaveBatch(6)).ContinueWith(t =>
            {
                EnableNavigation();
                if (t.IsFaulted)
                {
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    return;
                }
                var items = t.Result;
                items.RemoveAll(x => x == null);
                if (items.Count == 0)
                {
                    SetStatus("По этому треку не удалось собрать волну.");
                    return;
                }
                _tracks = items;
                _waveCache = items;
                _waveCacheStation = _waveStation;
                _waveCacheIndex = -1;
                FillTrackList();
                SetStatus("Волна по треку: " + _tracks.Count + " треков");
                PlayTrack(0);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Меняет текущие треки волны на новую партию (исключая уже показанные; при пустом результате грузит заново).</summary>
        private void RefreshWave()
        {
            if (!_waveMode) return;
            if (_waveFetchBusy)
            {
                SetStatus("Треки уже загружаются…");
                return;
            }
            _waveFetchBusy = true;
            SetStatus("Обновляю волну…");
            DisableNavigation();
            Bg.Run(() =>
            {
                foreach (Track t in _tracks)
                {
                    if (t != null && !string.IsNullOrEmpty(t.Id)) AddWaveSeen(t.Id);
                }
                List<Track> fresh = FetchWaveBatch(6);
                if ((fresh == null || fresh.Count == 0) && _waveStation == "user:onyourwave")
                {
                    // личная волна не дала ничего нового — не оставляем пустую, берём свежую партию
                    lock (_waveSeenLock)
                    {
                        _waveSeenIds.Clear();
                        _waveSeenOrder.Clear();
                    }
                    fresh = FetchWaveBatch(6);
                }
                return fresh;
            }).ContinueWith(t =>
            {
                _waveFetchBusy = false;
                EnableNavigation();
                if (t.IsFaulted)
                {
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    return;
                }
                List<Track> items = t.Result;
                items.RemoveAll(x => x == null);
                if (items.Count == 0)
                {
                    SetStatus("Новых треков в волне пока нет.");
                    return;
                }
                _tracks = items;
                _waveCache = items;
                _waveCacheStation = _waveStation;
                _waveCacheIndex = RestorePlayIndex(_tracks);
                _currentIndex = _waveCacheIndex;
                FillTrackList();
                UpdateWaveButtons();
                bool personal = _waveStation == "user:onyourwave";
                SetStatus((personal ? "Моя волна обновлена: " : "Волна обновлена: ") +
                          _tracks.Count + " треков");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}