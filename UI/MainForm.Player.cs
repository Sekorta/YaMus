using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Concurrency;
using YaMus.Core.Diagnostics;
using YaMus.Core.Media;
using YaMus.Core.Models;
using YaMus.Core.Playback;

namespace YaMus.UI
{
    public sealed partial class MainForm
    {
        // ==================== плеер ====================

        private int _playmarkRow = -1;

        /// <summary>Отмена незавершённого скачивания трека (StartObtain). При быстрых
        /// Next/Prev каждая новая качка должна гасить предыдущую, иначе на слабом Atom
        /// очередь параллельных загрузок душит сеть, путает обложки и ломает индикацию.</summary>
        private CancellationTokenSource _obtainCts;

        /// <summary>Перерисовывает маркер играющего трека после смены _currentIndex.</summary>
        private void RefreshPlayingMark()
        {
            int cur = _currentIndex;
            if (cur != _playmarkRow)
            {
                if (_playmarkRow >= 0) InvalidateTrackRow(_playmarkRow);
                _playmarkRow = cur;
                if (cur >= 0) InvalidateTrackRow(cur);
            }
            if (_artistPage != null && _artistPage.Visible) _artistPage.RefreshRows();
            if (_searchPage != null && _searchPage.Visible) _searchPage.RefreshRows();
        }

        private void TrackListOnDoubleClick(object sender, EventArgs e)
        {
            if (_trackList.SelectedIndices.Count == 0) return;
            int index = _trackList.SelectedIndices[0];
            PlayTrack(index);
        }

        /// <summary>Играем трек из «Популярные треки» страницы исполнителя.</summary>
        private void ArtistTrackActivated(Track track, int index)
        {
            if (_artistPage == null || _artistPage.Tracks == null) return;
            if (index < 0 || index >= _artistPage.Tracks.Count) return;
            if (track == null || _artistPage.Tracks[index] != track) return;
            _tracks = _artistPage.Tracks;
            _currentIndex = index;
            _trackList.Items.Clear();
            PlayTrack(index);
        }

        /// <summary>Играем трек из результатов поиска: контекстом становится вся
        /// секция «Треки» страницы поиска.</summary>
        private void SearchTrackActivated(Track track, int index)
        {
            if (_searchPage == null || _searchPage.Tracks == null) return;
            if (index < 0 || index >= _searchPage.Tracks.Count) return;
            if (track == null || _searchPage.Tracks[index] != track) return;
            _tracks = _searchPage.Tracks;
            _currentIndex = index;
            _trackList.Items.Clear();
            PlayTrack(index);
        }

        /// <summary>ПКМ по треку (поиск или страница исполнителя): стандартное меню трека
        /// (открыть альбом/исполнителя, поделиться, «Моя волна по треку») в точке курсора.</summary>
        private void TrackContextMenuRequested(Track track, Point screenPoint)
        {
            if (track == null || _trackMenu == null) return;
            ShowTrackContextMenu(track);
            _trackMenu.Show(screenPoint);
        }

        /// <summary>Сбрасывает только запущенную загрузку списка. Сам список лайков не трогаем:
        /// он кэшируется и обновляется точечно в Add/RemoveLikedToLikesView.</summary>
        private void InvalidateLikesCache()
        {
            lock (_likesLock) { _likesTask = null; }
        }

        /// <summary>Трек, к которому применяется лайк: играющий, иначе выделенная строка.
        /// Картинка кнопки считает состояние того же трека, поэтому нажатие и значок
        /// не расходятся.</summary>
        private Track LikeTargetTrack()
        {
            Track playing = _currentTrack;
            if (playing != null && !string.IsNullOrEmpty(playing.Id)) return playing;
            if (_currentIndex >= 0 && _currentIndex < _tracks.Count)
            {
                Track byIndex = _tracks[_currentIndex];
                if (byIndex != null && !string.IsNullOrEmpty(byIndex.Id)) return byIndex;
            }
            if (_trackList != null && _trackList.SelectedIndices.Count > 0)
            {
                int i = _trackList.SelectedIndices[0];
                if (i >= 0 && i < _tracks.Count) return _tracks[i];
            }
            return null;
        }

        /// <summary>Лайк/анлайк играющего трека (кнопка под плеером, F, оверлей).</summary>
        private void ToggleLike()
        {
            if (_uid == 0)
            {
                SetStatus("Лайки недоступны: нет авторизации.");
                return;
            }
            Track track = LikeTargetTrack();
            if (track == null || string.IsNullOrEmpty(track.Id))
            {
                SetStatus("Выберите трек, чтобы поставить лайк.");
                return;
            }
            ApplyLike(track, !_likedIds.Contains(track.Id));
        }

        /// <summary>Единая точка лайка для всех источников: состояние применяется сразу,
        /// запрос уходит в фон, при ошибке откатывается.</summary>
        private void ApplyLike(Track track, bool like)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            if (_uid == 0)
            {
                SetStatus("Лайки недоступны: нет авторизации.");
                return;
            }
            if (!_likeBusy.Add(track.Id))
            {
                SetStatus("Запрос уже выполняется, подождите.");
                return;
            }

            if (like) _likedIds.Add(track.Id);
            else _likedIds.Remove(track.Id);
            SyncLikeUi();
            DisableNavigation();

            Bg.Run(() =>
            {
                if (like) _api.LikeTrack(_uid, track.Id);
                else _api.UnlikeTrack(_uid, track.Id);
            }).ContinueWith(t =>
            {
                EnableNavigation();
                _likeBusy.Remove(track.Id);
                if (t.IsFaulted)
                {
                    // откат оптимистичного изменения — иначе UI врал бы про лайк
                    if (like) _likedIds.Remove(track.Id);
                    else _likedIds.Add(track.Id);
                    SyncLikeUi();
                    SetStatus("Ошибка лайка: " + Describe(t.Exception));
                    return;
                }
                SyncLikeUi();
                if (like) AddLikedToLikesView(track);
                else RemoveUnlikedFromLikesView(track);
                SyncLikeUi();
                SetStatus(like ? "Добавлено в «Мне нравится»" : "Убрано из «Мне нравится»");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Лайкнутый трек должен сразу появиться в открытом разделе и в кэше
        /// списка лайков, иначе он появлялся только после перезахода.</summary>
        private void AddLikedToLikesView(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            lock (_likesLock)
            {
                if (_likesCache != null && IndexOfTrackById(_likesCache, track.Id) < 0)
                {
                    // сервер отдаёт лайки новыми сверху — держим тот же порядок
                    _likesCache.Insert(0, track);
                }
            }
            if (_currentView != "likes" || _tracks == null) return;
            if (IndexOfTrackById(_tracks, track.Id) >= 0) return;

            _tracks.Insert(0, track);
            if (_currentIndex >= 0) _currentIndex++;          // индексы сдвинулись на строку
            if (_currentTrack != null && ReferenceEquals(_currentTrack, track)) _currentIndex = 0;
            FillTrackList();
            SetStatus("«Мне нравится»: " + _tracks.Count + " треков");
        }

        /// <summary>Убранный из «Мне нравится» трек исчезает из списка. Ищем по id, а не по
        /// индексу: индексы к моменту ответа могли уехать. Если играющий трек покинул
        /// список — маркер снимаем, сама игра не прерывается.</summary>
        private void RemoveUnlikedFromLikesView(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            lock (_likesLock)
            {
                if (_likesCache != null)
                {
                    int cached = IndexOfTrackById(_likesCache, track.Id);
                    if (cached >= 0) _likesCache.RemoveAt(cached);
                }
            }
            if (_currentView != "likes" || _tracks == null) return;
            int idx = IndexOfTrackById(_tracks, track.Id);
            if (idx < 0) return;

            _tracks.RemoveAt(idx);
            if (_currentIndex == idx) _currentIndex = -1;
            else if (_currentIndex > idx) _currentIndex--;

            int sel = idx < _tracks.Count ? idx : _tracks.Count - 1;
            FillTrackList(() =>
            {
                if (sel >= 0 && sel < _trackList.Items.Count)
                {
                    _trackList.SelectedIndices.Clear();
                    _trackList.SelectedIndices.Add(sel);
                }
                SetStatus("«Мне нравится»: " + _tracks.Count + " треков");
            });
        }

        /// <summary>Возвращает (путь,codec) уже скачанного файла трека или null; битые записи очищает.</summary>
        private Tuple<string, string> TryGetCachedFile(string trackId)
        {
            lock (_trackCacheLock)
            {
                Tuple<string, string> entry;
                if (_trackFileCache.TryGetValue(trackId, out entry) && entry != null && File.Exists(entry.Item1))
                {
                    return entry;
                }
                _trackFileCache.Remove(trackId);
                return null;
            }
        }

        private void CacheTrackFile(string trackId, string path, string codec)
        {
            lock (_trackCacheLock)
            {
                Tuple<string, string> existing;
                if (_trackFileCache.TryGetValue(trackId, out existing))
                {
                    // устаревшая копия того же трека — удаляем, оставляем только свежую
                    if (existing.Item1 != path)
                    {
                        try { if (File.Exists(existing.Item1)) File.Delete(existing.Item1); }
                        catch { }
                        lock (_tempFiles) _tempFiles.Remove(existing.Item1);
                        _trackFileCache[trackId] = Tuple.Create(path, codec);
                    }
                    return;
                }
                _trackFileCache[trackId] = Tuple.Create(path, codec);
                _trackFileOrder.Enqueue(trackId);
                while (_trackFileOrder.Count > TrackFileCacheLimit)
                {
                    string oldest = _trackFileOrder.Dequeue();
                    Tuple<string, string> victim;
                    if (_trackFileCache.TryGetValue(oldest, out victim))
                    {
                        _trackFileCache.Remove(oldest);
                        try { if (File.Exists(victim.Item1)) File.Delete(victim.Item1); }
                        catch { }
                        lock (_tempFiles) _tempFiles.Remove(victim.Item1);
                    }
                }
            }
        }

        private void PlayTrack(int index)
        {
            PlayTrackIn(_tracks, index);
        }

        /// <summary>Запуск трека из указанного списка. Next/Prev ходят по его снимку _playTracks,
        /// который не меняется при переходах в другие разделы. _currentIndex же описывает
        /// то, что открыто на экране, поэтому позиция играющего трека в нём ищется по id
        /// (RestorePlayIndex) — иначе маркер уезжал бы на чужую строку.</summary>
        private void PlayTrackIn(List<Track> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return;
            Track track = list[index];
            if (track == null) return;

            if (!track.Available)
            {
                SetStatus("Трек недоступен для прослушивания: " + track.Title);
                return;
            }

_playingTrackId = track.Id;
            _currentTrack = track;
            // предыдущее скачивание больше не нужно — гасим его до того, как
            // запустится своё (иначе при быстрых переключениях копятся очереди)
            CancelObtain();
            bool sameSnapshot = SnapshotMatches(_playTracks, list, index);
            // «новый источник» — это НЕ тот же список, что играл (и не его продолжение
            // после догрузки волны): у продолжения старый снимок является префиксом нового
            bool newContext = !sameSnapshot && !IsPrefixOf(_playTracks, list);
            if (!sameSnapshot)
                _playTracks = new List<Track>(list);   // снимок источника, а не ссылка на него

            // станция волны текущего контекста: по ней Next/автопереход смогут догружать
            // треки даже после того, как пользователь ушёл со страницы волны
            if (_waveMode && ReferenceEquals(list, _tracks)) _playStation = _waveStation;
            else if (newContext) _playStation = "";

            if (newContext)
            {
                ResetHistory();
                _shuffleNext = -1;
            }
            PushHistory(track);

            _playIndex = index;
            _currentIndex = ReferenceEquals(list, _tracks) ? index : RestorePlayIndex(_tracks);
            RefreshPlayingMark();
            _playbackIntended = true;
            UpdatePlayButton();
            SetSwitchBusy(true);
            _switching = true;

            // быстрый путь: следующий трек уже предзагружен и подготовлен заранее.
            // Переключение НИКОГДА не ждёт префетч: если файла ещё нет, качаем сами
            // (StartObtain) и гасим виснущую задачу префетча, чтобы не качать дважды.
            string prefetchedFile = null;
            string prefetchedCodec = null;
            PreparedTrack prefetchedPrepared = null;
            PreparedTrack stalePrepared = null;
            lock (_prefetchLock)
            {
                if (index == _prefetchIndex && _prefetchFile != null && File.Exists(_prefetchFile))
                {
                    prefetchedFile = _prefetchFile;
                    prefetchedCodec = _prefetchCodec;
                    prefetchedPrepared = _prefetchedPrepared;
                }
                else
                {
                    stalePrepared = _prefetchedPrepared;
                }
                _prefetchIndex = -1;
                _prefetchFile = null;
                _prefetchCodec = null;
                _prefetchedPrepared = null;
            }
            if (stalePrepared != null) stalePrepared.Dispose();

            SetStatus("Загрузка: " + track.ArtistsText + " — " + track.Title + "…");
            UpdateLikeButton(track);
            // меняем title/обложку сразу, не дожидаясь конца скачивания/префетча:
            // иначе при долгом переключении плеер и оверлей висят на старом треке
            ShowNowTrackUi(track);

            if (prefetchedFile != null)
            {
                Diag.Log("SWITCH pre " + track.Title + " idx=" + index + " pos=" + _player.Position);
                StartTrackPlayback(prefetchedFile, prefetchedCodec, prefetchedPrepared, track);
            }
            else
            {
                CancelPrefetch();
                Diag.Log("SWITCH obtain " + track.Title + " idx=" + index + " pos=" + _player.Position);
                StartObtain(track);
            }
        }

        /// <summary>Отменяет выполняющийся префетч (переключились на трек, который он качал,
        /// или он устарел). Не ждём завершения — просто подаём сигнал отмены.</summary>
        private void CancelPrefetch()
        {
            lock (_prefetchLock)
            {
                if (_prefetchCts != null)
                {
                    try { _prefetchCts.Cancel(); }
                    catch { }
                }
                _prefetchTask = null;
                _prefetchTaskIndex = -1;
            }
        }

/// <summary>Отменяет скачивание трека (StartObtain), ставшее ненужным после
        /// переключения. Не ждём завершения — просто подаём сигнал отмены;
        /// продолжение задачи само удалит частичный tmp-файл.</summary>
        private void CancelObtain()
        {
            if (_obtainCts != null)
            {
                try { _obtainCts.Cancel(); }
                catch { }
                _obtainCts = null;
            }
        }

        /// <summary>Старт трека из кэша либо скачивание от ошибки префетча.</summary>
        private void StartObtain(Track track)
        {
            Tuple<string, string> cached = TryGetCachedFile(track.Id);
            if (cached != null)
            {
                StartTrackPlayback(cached.Item1, cached.Item2, null, track);
                return;
            }
            var sw = Stopwatch.StartNew();
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            _obtainCts = cts;
            // путь файла держим и снаружи лямбды: если качку отменили/она упала,
            // продолжение должно удалить частичный tmp-файл, а не копить мусор
            string usedTmp = null;
            Bg.Run(() =>
            {
                var info = _downloader.Resolve(track.Id);
                string dir = Path.Combine(Path.GetTempPath(), "YaMus");
                Directory.CreateDirectory(dir);
                bool isMp3 = info.Codec != null && info.Codec.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0;
                string ext = isMp3 ? ".mp3" : ".m4a";
                string tmp = Path.Combine(dir, track.Id + "_" + Guid.NewGuid().ToString("N") + ext);
                usedTmp = tmp;
                _downloader.Download(info, tmp, token);
                return new KeyValuePair<string, string>(tmp, info.Codec);
            }).ContinueWith(task =>
            {
                if (IsDisposed) return;
                if (task.IsFaulted)
                {
                    Exception inner = task.Exception != null && task.Exception.InnerExceptions.Count > 0
                        ? task.Exception.InnerExceptions[0]
                        : null;
                    if (inner is OperationCanceledException)
                    {
                        // мы сами отменили качку после переключения — никакого сбоя нет
                        Diag.Log("OBTAIN cancelled " + track.Title + " " + sw.ElapsedMilliseconds + "ms");
                        if (usedTmp != null)
                        {
                            try { if (File.Exists(usedTmp)) File.Delete(usedTmp); }
                            catch { }
                        }
                        return;
                    }
                    if (!IsStillCurrent(track)) return; // устаревший сбой не трогает текущий трек
                    Diag.Log("OBTAIN fail " + track.Title + " " + sw.ElapsedMilliseconds + "ms: "
                             + Describe(task.Exception));
                    SetStatus("Не удалось загрузить трек: " + Describe(task.Exception));
                    SetSwitchBusy(false);
                    _switching = false;
                    _playbackIntended = false;
                    UpdatePlayButton();
                    return;
                }
                string tmp = task.Result.Key;
                string codec = task.Result.Value;
                lock (_tempFiles) _tempFiles.Add(tmp);
                CacheTrackFile(track.Id, tmp, codec);
                Diag.Log("OBTAIN ok " + track.Title + " " + sw.ElapsedMilliseconds + "ms"
                         + (IsStillCurrent(track) ? "" : " (stale)"));
                if (!IsStillCurrent(track))
                {
                    return; // пользователь уже переключился на другой трек
                }
                StartTrackPlayback(tmp, codec, null, track);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

/// <summary>Пока трек качается/готовится, кнопка Play показывает значок загрузки.
        /// Раньше заливали фон Prev/Next — при быстрых переключениях фон «залипал» и
        /// ломал индикацию; теперь загрузка видна только в кнопке воспроизведения.</summary>
        private void SetSwitchBusy(bool busy)
        {
            if (_playButton != null) _playButton.Loading = busy;
            if (_nowPlaying != null) _nowPlaying.SetBusy(busy);
        }

        /// <summary>Показывает инфо трека в плеере и оверлее. Вызывается сразу при переключении,
        /// чтобы пользователь видел новый трек ещё до того, как он реально заиграл.</summary>
        private void ShowNowTrackUi(Track track)
        {
            SetNowTitleText(track.Title, track.Version);
            SetNowLinkText(_nowSub, track.ArtistsText);
            TrySetPlayerCover(track);

            if (_nowPlaying != null && _nowPlaying.Visible)
            {
                string overlayTitle = track.Title;
                if (!string.IsNullOrEmpty(track.Version) && track.Version != "original")
                {
                    overlayTitle = overlayTitle + " (" + track.Version + ")";
                }
                _nowPlaying.ShowTrack(TrackCoverKey(track), overlayTitle, track.ArtistsText,
                                      (int)track.Duration.TotalSeconds, _coverLoader);
                SyncNowPlayingState();
            }
        }

        /// <summary>Трек всё ещё тот, что играет. Сравниваем id, а не индекс: индекс берётся из
        /// списка-источника (снимка), а _currentIndex — из открытого на экране списка, и в
        /// «всех треках» они уезжали друг от друга — скачанный файл отбрасывался, а трек
        /// висел на «Загрузка…».</summary>
        private bool IsStillCurrent(Track track)
        {
            if (track == null) return false;
            string id = track.Id;
            if (string.IsNullOrEmpty(id))
                return ReferenceEquals(track, _currentTrack);
            return string.Equals(id, _playingTrackId, StringComparison.Ordinal);
        }

        private void StartTrackPlayback(string tmp, string codec, PreparedTrack prepared, Track track)
        {
            ShowNowTrackUi(track);
            UpdatePlayButton();
            SetStatus("Играет: " + track.ArtistsText + " — " + track.Title);

            Bg.Run(() =>
            {
                // задачи на пуле не упорядочены: между кликом и реальным открытием
                // мог придти ещё один Prev/Next — не выталкиваем уже выбранный трек.
                // Guard ставим ПОСЛЕ Prepare, чтобы медленный декод не «проигрывал» стейл
                if (prepared != null)
                {
                    if (!IsStillCurrent(track)) { prepared.Dispose(); return; }
                    _player.OpenPrepared(prepared, tmp, codec);
                }
                else
                {
                    var prep = _player.Prepare(tmp, codec, track.Duration);
                    if (!IsStillCurrent(track)) { prep.Dispose(); return; }
                    _player.OpenPrepared(prep, tmp, codec);
                }
            }).ContinueWith(t =>
            {
                if (IsDisposed) return;
if (t.IsFaulted)
                {
                    if (!IsStillCurrent(track)) return; // устаревший сбой не трогает текущий трек
                    SetStatus("Не удалось проиграть: " + Describe(t.Exception));
                    Diag.Log("PLAY fail " + track.Title + ": " + Describe(t.Exception));
                    SetSwitchBusy(false);
                    _switching = false;
                    _playbackIntended = false;
                    UpdatePlayButton();
                    return;
                }
if (!IsStillCurrent(track)) return;
                SetSwitchBusy(false);
                _switching = false;
                Diag.Log("PLAY " + track.Title);
                ApplyPendingSeek();
                SchedulePrefetch();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Ставит текст подписи плеера и делает всю строку ссылкой (hover-подчёркивание).</summary>
        private static void SetNowLinkText(LinkLabel label, string text)
        {
            label.Text = text;
            label.Links.Clear();
            if (!string.IsNullOrEmpty(text))
            {
                label.Links.Add(0, text.Length);
            }
        }

        /// <summary>Заголовок плеера: название (ссылка) + бледная пометка версии трека.</summary>
        private void SetNowTitleText(string title, string version)
        {
            SetNowLinkText(_nowTitle, title);
            _nowVersion.Text = string.IsNullOrEmpty(version) || version == "original" ? "" : version;
            LayoutNowTitleRow();
        }

        /// <summary>Выравнивает название и версию по вертикали строки.</summary>
        private void LayoutNowTitleRow()
        {
            if (_nowTitleHost == null) return;
            int h = _nowTitleHost.ClientSize.Height;
            _nowTitle.Top = Math.Max(0, (h - _nowTitle.Height) / 2);
            _nowVersion.Top = Math.Max(0, (h - _nowVersion.Height) / 2);
            _nowVersion.Left = _nowTitle.Width > 0 ? _nowTitle.Width + 5 : 0;
        }

        /// <summary>Клик по названию трека: открываем альбом.</summary>
        private void NowTitleClicked(object sender, EventArgs e)
        {
            Track t = _currentTrack;
            if (t == null) return;
            long albumId = 0;
            if (t.Albums != null)
            {
                foreach (Album al in t.Albums)
                {
                    if (al != null && al.Id != 0) { albumId = al.Id; break; }
                }
            }
            if (albumId == 0)
            {
                SetStatus("Альбом трека неизвестен.");
                return;
            }
            NavigateToAlbum(albumId);
        }

        /// <summary>Клик по авторам: один — открываем сразу, несколько — меню выбора.</summary>
        private void NowSubClicked(object sender, EventArgs e)
        {
            Track t = _currentTrack;
            if (t == null) return;
            var list = new List<Artist>();
            if (t.Artists != null)
            {
                foreach (Artist ar in t.Artists)
                {
                    if (ar != null && ar.Id != 0 && !ar.Various) list.Add(ar);
                }
            }
            if (list.Count == 0)
            {
                SetStatus("Исполнитель неизвестен.");
                return;
            }
            if (list.Count == 1)
            {
                NavigateToArtist(list[0].Id);
                return;
            }
            _trackMenu.Items.Clear();
            foreach (Artist ar in list)
            {
                long id = ar.Id;
                var item = new ToolStripMenuItem(string.IsNullOrEmpty(ar.Name) ? ("Исполнитель #" + id) : ar.Name);
                item.Click += (s, ea) => NavigateToArtist(id);
                _trackMenu.Items.Add(item);
            }
            _trackMenu.Show(_nowSub, new Point(0, _nowSub.Height));
        }

        private void TrySetPlayerCover(Track track)
        {
            try
            {
                string uri = TrackCoverKey(track);
                if (string.IsNullOrEmpty(uri))
                {
                    SetPlayerCoverPlaceholder();
                    return;
                }
                if (_covers.Images.ContainsKey(uri))
                {
                    var old = _coverBox.Image;
                    _coverBox.Image = new Bitmap(_covers.Images[uri], 48, 48);
                    if (old != null && old != _coverPlaceholder) old.Dispose();
                    return;
                }
                SetPlayerCoverPlaceholder();
                UpdatePlayerCover(track);
            }
            catch { }
        }

private void ClearPrefetch()
        {
            string stale = null;
            PreparedTrack stalePrepared;
            lock (_prefetchLock)
            {
                if (_prefetchCts != null)
                {
                    try { _prefetchCts.Cancel(); }
                    catch { }
                    _prefetchCts = null;
                }
                stale = _prefetchFile;
                stalePrepared = _prefetchedPrepared;
                _prefetchIndex = -1;
                _prefetchFile = null;
                _prefetchCodec = null;
                _prefetchedPrepared = null;
                _prefetchTask = null;
                _prefetchTaskIndex = -1;
            }
            if (stalePrepared != null) stalePrepared.Dispose();
            if (stale != null)
            {
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch { }
                lock (_tempFiles) _tempFiles.Remove(stale);
            }
        }

/// <summary>Кладёт префетч, только если контекст не сменился с момента планирования:
        /// всё ещё играет тот же трек, для которого мы искали следующего (cur).
        /// index — целевой (следующий) трек. Возвращает prep, который нужно
        /// освобождить снаружи (лишний или заменённый).</summary>
        private PreparedTrack SetPrefetch(int cur, int index, string file, string codec, PreparedTrack prepared)
        {
            lock (_prefetchLock)
            {
                if (PlayContextIndex() == cur)
                {
                    PreparedTrack replaced = _prefetchedPrepared;
                    _prefetchIndex = index;
                    _prefetchFile = file;
                    _prefetchCodec = codec;
                    _prefetchedPrepared = prepared;
                    return replaced;
                }
                return prepared;
            }
        }

        /// <summary>Предзагрузка следующего трека, чтобы не было пауз. В случайном режиме
        /// следующий трек теперь не случаен: он выбирается ЗДЕСЬ один раз и запоминается
        /// (_shuffleNext), поэтому качается ровно тот трек, который потом сыграет Next.</summary>
        private void SchedulePrefetch()
        {
            List<Track> list = PlayContext();
            if (list == null || list.Count == 0) return;
            int cur = PlayContextIndex();
            int next = _shuffle ? ShuffleNextIndex(list, cur) : FindNextPlayable(list, cur, +1);
            if (next < 0) return;
            Track nextTrack = list[next];
            if (nextTrack == null || !nextTrack.Available) return;

            Tuple<string, string> cached = TryGetCachedFile(nextTrack.Id);
            if (cached != null)
            {
                string file = cached.Item1;
                string codec = cached.Item2;
                // готовим декодер заранее: чтение/индексация/ffmpeg не должны
                // выполняться в момент переключения — оттуда и подхрип
Task pipeline = Bg.Run(() => _player.Prepare(file, codec, nextTrack.Duration)).ContinueWith(t =>
                {
                    PreparedTrack prep = t.IsFaulted ? null : t.Result;
                    PreparedTrack stale = SetPrefetch(cur, next, file, codec, prep);
                    if (stale != null) stale.Dispose();
                }, TaskScheduler.FromCurrentSynchronizationContext());
                lock (_prefetchLock)
                {
                    _prefetchTask = pipeline;
                    _prefetchTaskIndex = next;
                }
                return;
            }

var swP = Stopwatch.StartNew();
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            Task pipeline2 = Bg.Run(() =>
            {
                // Префетч стартует рано и тихо, чтобы докачаться задолго до конца
                // трека. Первые ~3 с нового трека сеть не трогаем (в этот момент
                // даже плавный приём по Wi-Fi на слабом Atom способен ронять звук),
                // затем качаем лимитированно (~700 КБ/с, BelowNormal). Если трек
                // сменили или остановили — гасимся по токену без пауз для переключения.
                const double QuietStartS = 3.0;
                if (_player.IsPlaying)
                {
                    TimeSpan dur = _player.Duration;
                    if (dur > TimeSpan.Zero)
                    {
                        TimeSpan pos = _player.Position;
                        if (pos < TimeSpan.FromSeconds(QuietStartS))
                        {
                            double waitMs = (TimeSpan.FromSeconds(QuietStartS) - pos).TotalMilliseconds;
                            while (waitMs > 0 && !token.IsCancellationRequested &&
                                   _player.IsPlaying && PlayContextIndex() == cur)
                            {
                                int step = (int)Math.Min(waitMs, 50);
                                Thread.Sleep(step < 1 ? 1 : step);
                                waitMs -= step;
                            }
                        }
                    }
                }
                if (token.IsCancellationRequested || PlayContextIndex() != cur || !_player.IsPlaying)
                {
                    Diag.Log("PREFETCH stale " + nextTrack.Title);
                    return new Tuple<string, string, int, PreparedTrack>(null, null, -1, null);
                }
                Diag.Log("PREFETCH dl start " + nextTrack.Title + " at pos=" + _player.Position);
                var info = _downloader.Resolve(nextTrack.Id);
                string dir = Path.Combine(Path.GetTempPath(), "YaMus");
                Directory.CreateDirectory(dir);
                bool isMp3 = info.Codec != null && info.Codec.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0;
                string ext = isMp3 ? ".mp3" : ".m4a";
                string tmp = Path.Combine(dir, nextTrack.Id + "_" + Guid.NewGuid().ToString("N") + "_pre" + ext);
                try
                {
                    _downloader.DownloadBackground(info, tmp, token);
                }
                catch (OperationCanceledException)
                {
                    Diag.Log("PREFETCH cancelled " + nextTrack.Title);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return new Tuple<string, string, int, PreparedTrack>(null, null, -1, null);
                }
                if (token.IsCancellationRequested || PlayContextIndex() != cur || !_player.IsPlaying)
                {
                    Diag.Log("PREFETCH stale " + nextTrack.Title);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return new Tuple<string, string, int, PreparedTrack>(null, null, -1, null);
                }
                lock (_tempFiles) if (!_tempFiles.Contains(tmp)) _tempFiles.Add(tmp);
                CacheTrackFile(nextTrack.Id, tmp, info.Codec);
                var prep = _player.Prepare(tmp, info.Codec, nextTrack.Duration);
                return new Tuple<string, string, int, PreparedTrack>(tmp, info.Codec, next, prep);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Exception inner = t.Exception != null && t.Exception.InnerExceptions.Count > 0
                        ? t.Exception.InnerExceptions[0]
                        : null;
                    if (!(inner is OperationCanceledException))
                    {
                        string failed = inner != null ? inner.Message : "?";
                        Diag.Log("PREFETCH fail " + nextTrack.Title + " " + swP.ElapsedMilliseconds + "ms: " + failed);
                        SetStatus("Не удалось предзагрузить: " + failed);
                    }
                    return;
                }
                if (t.Result.Item3 < 0)
                {
                    return; // префетч устарел или был отменён во время ожидания/скачивания
                }
                Diag.Log("PREFETCH ready " + nextTrack.Title + " " + swP.ElapsedMilliseconds + "ms");
                PreparedTrack stale = SetPrefetch(cur, t.Result.Item3, t.Result.Item1, t.Result.Item2, t.Result.Item4);
                if (stale != null) stale.Dispose();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            lock (_prefetchLock)
            {
                if (_prefetchCts != null)
                {
                    try { _prefetchCts.Cancel(); }
                    catch { }
                }
                _prefetchCts = cts;
                _prefetchTask = pipeline2;
                _prefetchTaskIndex = next;
            }
        }

        private void PlayPause()
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _playbackIntended = false;
                UpdatePlayButton();
            }
            else if (_player.IsActive)
            {
                _player.Resume();
                _playbackIntended = true;
                UpdatePlayButton();
            }
            else if (_playbackIntended)
            {
                // трек ещё грузится — клик по Play игнорируем до конца загрузки
                return;
            }
            else if (_currentIndex >= 0 && _currentIndex < _tracks.Count)
            {
                PlayTrack(_currentIndex);
            }
            else if (_tracks.Count > 0)
            {
                PlayTrack(0);
            }
        }

        /// <summary>Список, по которому ходят Next/Prev: источник текущего трека.</summary>
        private List<Track> PlayContext()
        {
            if (_playTracks != null && _playTracks.Count > 0) return _playTracks;
            return _tracks;
        }

        /// <summary>Позиция играющего трека в контексте воспроизведения; -1, если её нет.</summary>
        private int PlayContextIndex()
        {
            if (_playTracks == null) return _currentIndex;
            if (_playIndex >= 0 && _playIndex < _playTracks.Count) return _playIndex;
            return RestorePlayIndex(_playTracks);
        }

        /// <summary>Снимок описывает тот же трек на той же позиции — он ещё актуален.</summary>
        private static bool SnapshotMatches(List<Track> snapshot, List<Track> list, int index)
        {
            if (snapshot == null || list == null) return false;
            if (index < 0 || index >= snapshot.Count || index >= list.Count) return false;
            Track a = snapshot[index];
            Track b = list[index];
            if (a == null || b == null) return false;
            return string.Equals(a.Id, b.Id, StringComparison.Ordinal);
        }

        /// <summary>Следующий трек (в случайном режиме — предвыбранный _shuffleNext);
        /// на конце волны подтягивает новую порцию.</summary>
        private void Next()
        {
            List<Track> list = PlayContext();
            if (list == null || list.Count == 0) return;
            int cur = PlayContextIndex();
            int next = _shuffle ? ShuffleNextIndex(list, cur) : FindNextPlayable(list, cur, +1);
            if (next >= 0)
            {
                PlayTrackIn(list, next);
                return;
            }
            if (_waveMode)
            {
                // на последнем треке волны «вперёд» подтягивает новую порцию и играет с неё
                FetchWaveMore(true);
                return;
            }
            if (CanExtendWaveContext())
            {
                // страница волны закрыта, но её контекст ещё играет — догружаем прямо в него
                FetchWaveMoreForPlayback();
            }
        }

        /// <summary>Назад: сперва перемотка в начало, затем предыдущий трек
        /// (в случайном режиме — по истории воспроизведения).</summary>
        private void Prev()
        {
            List<Track> list = PlayContext();
            if (list == null || list.Count == 0) return;
            if (_player.Position > TimeSpan.FromSeconds(3))
            {
                RestartCurrent();
                return;
            }
            // случайный порядок: сквозной Prev возвращал «соседа по индексу», а не реально
            // сыгранный трек. Идём по истории воспроизведения.
            if (_shuffle && _historyIndex > 0 && _historyIndex < _playHistory.Count)
            {
                Track t = _playHistory[_historyIndex - 1];
                int pos = IndexOfTrackById(list, t != null ? t.Id : null);
                if (pos >= 0)
                {
                    _historyIndex--;   // переезжаем по истории, не дописывая её заново
                    _shuffleNext = -1;
                    PlayTrackIn(list, pos);
                    return;
                }
            }
            int cur = PlayContextIndex();
            int prev = FindNextPlayable(list, cur, -1);
            if (prev >= 0) PlayTrackIn(list, prev);
        }

        private void RestartCurrent()
        {
            _player.Stop();
            List<Track> list = PlayContext();
            int cur = PlayContextIndex();
            if (list != null && cur >= 0 && cur < list.Count)
            {
                PlayTrackIn(list, cur);
            }
        }

        private void ToggleShuffle()
        {
            _shuffle = !_shuffle;
            _shuffleButton.ForeColor = _shuffle ? YmPalette.Accent : YmPalette.TextMain;
            _shuffleNext = -1;   // старый предвыбор недействителен после смены режима
            SyncNowPlayingState();
        }

        private void ToggleRepeat()
        {
            // выкл → весь список → один трек → выкл
            if (_repeatOne)
            {
                _repeatOne = false;
                _repeat = false;
            }
            else if (_repeat)
            {
                _repeat = false;
                _repeatOne = true;
            }
            else
            {
                _repeat = true;
            }
            UpdateRepeatVisual();
        }

        private void UpdateRepeatVisual()
        {
            // «повтор одного» — та же закольцованная стрелка, но отражённая и без цифры «1»
            _repeatButton.Glyph = UiGlyphs.Repeat;
            if (_repeatOne)
            {
                _repeatButton.Mirrored = true;
                _repeatButton.ForeColor = YmPalette.LikeGold;
            }
            else
            {
                _repeatButton.Mirrored = false;
                _repeatButton.ForeColor = _repeat ? YmPalette.Accent : YmPalette.TextMain;
            }
            SyncNowPlayingState();
        }

        private static int IndexOfTrackById(List<Track> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                Track t = list[i];
                if (t != null && string.Equals(t.Id, id, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private int FindNextPlayable(List<Track> list, int from, int step)
        {
            if (list == null) return -1;
            int count = list.Count;
            if (count == 0) return -1;

            for (int k = 1; k <= count; k++)
            {
                int idx = from + step * k;
                if (idx < 0 || idx >= count)
                {
                    if (_repeat) idx = (idx + count) % count;
                    else return -1;
                }
                Track t = list[idx];
                if (t != null && t.Available) return idx;
            }
            return -1;
        }

        private int RandomPlayableIndex(List<Track> list, int excludeIndex)
        {
            if (list == null) return -1;
            var playable = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Available) playable.Add(i);
            }
            if (playable.Count == 0) return -1;
            int pick = playable[new Random().Next(playable.Count)];
            if (pick == excludeIndex && playable.Count > 1)
            {
                pick = playable[(playable.IndexOf(pick) + 1) % playable.Count];
            }
            return pick;
        }

        /// <summary>Следующий трек в случайном режиме: выбор делается один раз и
        /// переиспользуется Next()/автопереходом/префетчем, иначе каждый вызывающий
        /// выбирал бы свою «случайную» точку — трек то качался, то перескакивал.</summary>
        private int ShuffleNextIndex(List<Track> list, int cur)
        {
            if (list == null || list.Count == 0) return -1;
            int pick = _shuffleNext;
            if (pick >= 0 && pick < list.Count)
            {
                Track t = list[pick];
                if (t != null && t.Available && pick != cur) return pick;
            }
            int fresh = RandomPlayableIndex(list, cur);
            _shuffleNext = fresh;
            return fresh;
        }

        /// <summary>Кладёт трек в историю воспроизведения (для Prev в случайном режиме).
        /// Повторный запуск записи, на которой мы уже стоим (Prev→Next по истории),
        /// ничего не меняет; запуск нового трека после Prev отрезает «будущее».</summary>
        private void PushHistory(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            if (_historyIndex >= 0 && _historyIndex < _playHistory.Count)
            {
                Track cur = _playHistory[_historyIndex];
                if (cur != null && string.Equals(cur.Id, track.Id, StringComparison.Ordinal)) return;
            }
            if (_historyIndex >= 0 && _historyIndex < _playHistory.Count - 1)
            {
                _playHistory.RemoveRange(_historyIndex + 1, _playHistory.Count - _historyIndex - 1);
            }
            _playHistory.Add(track);
            _historyIndex = _playHistory.Count - 1;
            if (_playHistory.Count > PlayHistoryLimit)
            {
                int drop = _playHistory.Count - PlayHistoryLimit;
                _playHistory.RemoveRange(0, drop);
                _historyIndex -= drop;
                if (_historyIndex < 0) _historyIndex = 0;
            }
        }

        private void ResetHistory()
        {
            _playHistory.Clear();
            _historyIndex = -1;
        }

        /// <summary>prefix совпадает с началом full по id: снимок — это тот же источник,
        /// который догрузился (продолжение волны), а не совсем другой список.</summary>
        private static bool IsPrefixOf(List<Track> prefix, List<Track> full)
        {
            if (prefix == null || prefix.Count == 0) return true;
            if (full == null || prefix.Count > full.Count) return false;
            for (int i = 0; i < prefix.Count; i++)
            {
                Track a = prefix[i];
                Track b = full[i];
                if (a == null || b == null) return false;
                if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>Играет волна/радио, но её страница уже закрыта: порция треков должна
        /// уходить в играющий контекст, а не в чужой открытый раздел.</summary>
        private bool CanExtendWaveContext()
        {
            return !string.IsNullOrEmpty(_playStation) && !_waveMode;
        }

        private void OnTick()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ScheduleVisibleCovers();
            if (!_player.IsActive) return;

            TimeSpan pos = _player.Position;
            TimeSpan dur = _player.Duration;

            if (dur > TimeSpan.Zero)
            {
                int seconds = (int)dur.TotalSeconds;
                _seekBar.DurationSeconds = seconds;
                _seekBar.PositionSeconds = Math.Max(0, Math.Min(seconds, (int)pos.TotalSeconds));
                _timeLabel.Text = FormatTime(pos) + " / " + FormatTime(dur);

                // тот же прогресс дублируем в оверлее «сейчас играет»
                if (_nowPlaying != null && _nowPlaying.Visible)
                {
                    _nowPlaying.SetProgress((int)pos.TotalSeconds, seconds);
                }
            }

            // после конца файла WaveOutEvent переходит в Stopped — не даём повторно
            // запустить AdvanceAfterEnd, пока качается следующий трек
            if (!_switching && _player.IsPlaying && pos > TimeSpan.Zero && dur > TimeSpan.Zero &&
                pos >= dur - TimeSpan.FromMilliseconds(500))
            {
                AdvanceAfterEnd();
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 30) Diag.LogStall("OnTick", sw.ElapsedMilliseconds);
        }

        private void AdvanceAfterEnd()
        {
            List<Track> list = PlayContext();
            if (list == null || list.Count == 0)
            {
                StopPlayback();
                return;
            }
            int cur = PlayContextIndex();
            if (_repeatOne && cur >= 0 && cur < list.Count &&
                list[cur] != null && list[cur].Available)
            {
                PlayTrackIn(list, cur);
                return;
            }
            int next = _shuffle ? ShuffleNextIndex(list, cur) : FindNextPlayable(list, cur, +1);
            if (next >= 0)
            {
                PlayTrackIn(list, next);
                return;
            }
            if (_waveMode)
            {
                // волна закончилась — подтягиваем следующую порцию и продолжаем с первого нового
                FetchWaveMore(true);
                return;
            }
            if (CanExtendWaveContext())
            {
                // пользователь ушёл со страницы волны, а волна ещё играет — догружаем в контекст
                FetchWaveMoreForPlayback();
                return;
            }
            StopPlayback();
            SetStatus("Конец списка.");
        }

        private void StopPlayback()
        {
            CancelObtain();
            _playingTrackId = null;
            _currentTrack = null;
            _playbackIntended = false;
            _playStation = "";
            _shuffleNext = -1;
            _pendingSeek = -1;
            _lastDeviceLostTick = 0;
            ResetHistory();
            _player.Stop();
            RefreshPlayingMark();
            UpdatePlayButton();
            _seekBar.DurationSeconds = 0;
            _seekBar.PositionSeconds = 0;
            _timeLabel.Text = "0:00 / 0:00";
            SetSwitchBusy(false);
                    _switching = false;
            SetNowTitleText("Ничего не играет", null);
            SetNowLinkText(_nowSub, "");
            SetPlayerCoverPlaceholder();
            UpdateLikeButton(null);
            if (_nowPlaying != null && _nowPlaying.Visible)
            {
                _nowPlaying.ShowTrack(null, "Ничего не играет", "", 0, _coverLoader);
                SyncNowPlayingState();
            }
        }

        /// <summary>Пропавшее звуковое устройство: плеер сообщает со своего потока.</summary>
        private void OnPlayerDeviceLost()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(HandlePlayerDeviceLost)); }
            catch { }
        }

        /// <summary>Звуковое устройство сменилось/отвалилось: перезапускаем текущий трек с
        /// прежней позиции. Иначе Play/Next/клик по строке просто не реагируют —
        /// _output мёртв, а _playbackIntended остался true.</summary>
        private void HandlePlayerDeviceLost()
        {
            if (IsDisposed) return;
            // гасим серию сбоев одного и того же устройства (WaveOutEvent может слать
            // несколько падений подряд) — один перезапуск за 1.5 с
            int now = Environment.TickCount;
            int dt = now - _lastDeviceLostTick;
            if (_lastDeviceLostTick != 0 && dt >= 0 && dt < 1500) return;
            _lastDeviceLostTick = now;

            Diag.Log("DEVICE lost; restart at " + _player.Position);

            Track track = _currentTrack;
            if (track == null)
            {
                StopPlayback();
                return;
            }
            // позицию читаем до Stop(): текущий поток устройства уже не играет,
            // но CurrentTime на ридере ещё читается корректно
            _pendingSeek = (int)_player.Position.TotalSeconds;
            _playbackIntended = true;
            UpdatePlayButton();
            SetStatus("Звуковое устройство сменилось — продолжаю с прежнего места…");
            RestartCurrent();
        }

        /// <summary>Восстанавливает позицию, сохранённую после смены устройства.</summary>
        private void ApplyPendingSeek()
        {
            if (_pendingSeek <= 0) return;
            int seek = _pendingSeek;
            _pendingSeek = -1;
            try { _player.Seek(TimeSpan.FromSeconds(seek)); }
            catch { }
        }

        private void UpdatePlayButton()
        {
            bool playing = _player.IsPlaying || _playbackIntended;
            _playButton.Text = playing ? UiGlyphs.Pause : UiGlyphs.Play;
            SyncNowPlayingState();
        }

        /// <summary>Состояние кнопки лайка под плеером. Аргумент игнорируется: кнопка показывает
        /// состояние трека, к которому применится нажатие (см. LikeTargetTrack).</summary>
        private void UpdateLikeButton(Track track)
        {
            SyncLikeUi();
        }

        /// <summary>Синхронизирует все «сердца» приложения с _likedIds: кнопку под
        /// плеером, оверлей и строки страниц поиска/исполнителя.</summary>
        private void SyncLikeUi()
        {
            if (_likeButton != null)
            {
                Track target = LikeTargetTrack();
                bool liked = target != null && !string.IsNullOrEmpty(target.Id) && _likedIds.Contains(target.Id);
                _likeButton.Glyph = liked ? UiGlyphs.Like : UiGlyphs.Unlike;
                _likeButton.ForeColor = liked ? YmPalette.LikeGold : YmPalette.TextMain;
            }
            if (_artistPage != null) _artistPage.RefreshLikes();
            if (_searchPage != null) _searchPage.RefreshLikes();
            SyncNowPlayingState();
        }

private void UpdatePlayerCover(Track track)
        {
            string uri = TrackCoverKey(track);
            if (string.IsNullOrEmpty(uri))
            {
                SetPlayerCoverPlaceholder();
                return;
            }
            string trackId = track != null ? track.Id : null;
            CoverLoader.BeginLoad(() => _coverLoader.Fetch(uri, "100x100"), 48, img =>
            {
                if (IsDisposed) return;
                // пока обложка грузилась, трек могли сменить — чужую картинку не вешаем,
                // иначе при быстрых Next/Prev обложки путаются
                if (trackId == null || !string.Equals(trackId, _playingTrackId, StringComparison.Ordinal))
                {
                    if (img != null) img.Dispose();
                    return;
                }
                try
                {
                    if (img == null)
                    {
                        SetPlayerCoverPlaceholder();
                        return;
                    }
                    var old = _coverBox.Image;
                    _coverBox.Image = img;
                    if (old != null && old != _coverPlaceholder) old.Dispose();
                }
                catch { }
            });
        }

        private void SetPlayerCoverPlaceholder()
        {
            if (_coverBox.Image != _coverPlaceholder)
            {
                _coverBox.Image = _coverPlaceholder;
            }
        }
    }
}
