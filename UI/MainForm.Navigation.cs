using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Api;
using YaMus.Core.Auth;
using YaMus.Core.Concurrency;
using YaMus.Core.Diagnostics;
using YaMus.Core.Models;
using YaMus.Core.Playback;
using YaMus.Core.Transport;

namespace YaMus.UI
{
    public sealed partial class MainForm
    {
        /// <summary>Шрифт глифов-стрелок дерева (▾/▸): на XP нужен Symbola,
        /// в Segoe UI этих символов нет.</summary>
        private static readonly Font FontArrow = UiGlyphs.GlyphFont(9F, FontStyle.Bold);

        private void PopulateNav(AccountInfo account, List<PlaylistSummary> playlists)
        {
            var psw = System.Diagnostics.Stopwatch.StartNew();
            _suppressSelect = true;
            _nav.Nodes.Clear();
            _playlistTitles.Clear();

            var homeNode = new TreeNode("Главная");
            homeNode.Tag = "home";
            homeNode.Name = "home";
            homeNode.NodeFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nav.Nodes.Add(homeNode);

            var searchNode = new TreeNode("Поиск");
            searchNode.Tag = "search";
            searchNode.Name = "search";
            searchNode.NodeFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nav.Nodes.Add(searchNode);

            var likesNode = new TreeNode("Мне нравится");
            likesNode.Tag = "likes";
            likesNode.Name = "likes";
            likesNode.NodeFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nav.Nodes.Add(likesNode);

            var waveNode = new TreeNode("Моя волна");
            waveNode.Tag = "wave";
            waveNode.Name = "wave";
            waveNode.NodeFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nav.Nodes.Add(waveNode);

            var playlistsNode = new TreeNode("ПЛЕЙЛИСТЫ");
            playlistsNode.Tag = "playlists";
            playlistsNode.NodeFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);
            _nav.Nodes.Add(playlistsNode);

            foreach (var p in playlists.OrderBy(p => p.Kind))
            {
                var child = new TreeNode(p.Title + "  (" + p.TrackCount + ")");
                child.Tag = (object)p.Kind;
                child.NodeFont = new Font("Segoe UI", 9.5F);
                playlistsNode.Nodes.Add(child);
                _playlistTitles[p.Kind] = p.Title;
            }
            playlistsNode.Collapse();

            var settingsNode = new TreeNode("Настройки");
            settingsNode.Tag = "settings";
            settingsNode.Name = "settings";
            settingsNode.NodeFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _nav.Nodes.Add(settingsNode);

            _suppressSelect = false;

            if (homeNode != null)
            {
                _nav.SelectedNode = homeNode;
            }
            psw.Stop();
            if (psw.ElapsedMilliseconds > 100) Diag.LogStall("PopulateNav[" + playlists.Count + "]", psw.ElapsedMilliseconds);
        }

        /// <summary>Фиксирует ширину сайдбара ровно по «Мне нравится» (без запаса и динамики).</summary>
        private void FitSidebarWidth()
        {
            try
            {
                int w = 0;
                for (int i = 0; i < _nav.Nodes.Count; i++)
                {
                    TreeNode n = _nav.Nodes[i];
                    if ((n.Tag as string) == "likes")
                    {
                        w = TextRenderer.MeasureText(n.Text, n.NodeFont ?? _nav.Font).Width;
                    }
                }
                // отступ слева до текста (8) + отступ справа (6)
                int target = w + 14;
                if (_rootSplit != null)
                {
                    _rootSplit.SplitterDistance = Math.Max(210, target);
                }
            }
            catch { }
        }

        private void NavOnDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null) return;

            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;

            // заливаем всю полосу строки (ширину панели), включая область индента/стрелок —
            // иначе вокруг выбранного пункта остаются системные (белые) элементы
            using (var bg = new SolidBrush(selected ? YmPalette.BgSel : YmPalette.BgSide))
            {
                g.FillRectangle(bg, 0, r.Y, _nav.ClientSize.Width, r.Height);
            }
            if (selected)
            {
                using (var ac = new SolidBrush(YmPalette.Accent))
                {
                    g.FillRectangle(ac, r.X, r.Y, 3, r.Height);
                }
            }

            string tag = e.Node.Tag as string;
            Color fg = selected ? YmPalette.TextMain : YmPalette.TextMain;
            if (tag == "likes") fg = YmPalette.LikeGold;
            else if (tag == "wave") fg = YmPalette.WaveAccent;
            else if (tag == "playlists") fg = YmPalette.TextFaint;

            bool hasChildren = e.Node.Nodes.Count > 0;
            int x = r.Left + (hasChildren ? 22 : 8);
            if (hasChildren)
            {
                using (var b = new SolidBrush(YmPalette.TextFaint))
                {
                    string arrow = e.Node.IsExpanded ? UiGlyphs.ExpanderOpen : UiGlyphs.ExpanderClosed;
                    // шрифт глифа (в т.ч. встроенный Symbola), иначе на XP ▾/▸ не рисуются
                    g.DrawString(arrow, FontArrow, b,
                                 new PointF(r.Left + 6, r.Top + (r.Height - FontArrow.GetHeight(g)) / 2F));
                }
            }

            Font font = e.Node.NodeFont ?? _nav.Font;
            Rectangle textR = new Rectangle(x, r.Y, _nav.ClientSize.Width - x - 6, r.Height);
            TextRenderer.DrawText(g, e.Node.Text, font, textR, fg,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void NavOnMouseClick(object sender, MouseEventArgs e)
        {
            if (_suppressSelect) return;
            TreeNode node = _nav.GetNodeAt(e.Location);
            if (node == null) return;

            int hitX = node.Bounds.Left + 17;
            if (node.Nodes.Count > 0 && Math.Abs(e.Location.X - hitX) <= 10)
            {
                if (node.IsExpanded) node.Collapse();
                else node.Expand();
                return;
            }

            // повторный клик по уже выбранному пункту — возврат из альбома/исполнителя
            if (node.Nodes.Count == 0 && node.Tag != null && node == _nav.SelectedNode)
            {
                NavOnAfterSelect(this, new TreeViewEventArgs(node));
            }
        }

        private void NavOnAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressSelect || e.Node == null || e.Node.Tag == null) return;

            HideArtistPage();

            LeaveWaveMode();

            if (e.Node.Tag is string)
            {
                string what = (string)e.Node.Tag;
                if (what == "home") ShowHomePage();
                else if (what == "search") ShowSearchPage();
                else if (what == "likes") LoadLikes();
                else if (what == "wave") LoadWave();
                else if (what == "settings") ShowSettingsPage();
                return;
            }
            if (e.Node.Tag is long || e.Node.Tag is int || e.Node.Tag is long?)
            {
                long kind = Convert.ToInt64(e.Node.Tag);
                LoadPlaylist(kind);
            }
        }

        private void SetListTitle(string text)
        {
            _listTitleControl = null;
            var root = FindListTitle(this);
            if (root != null) root.Text = text;
            _navName = text;
        }

        private string _navName = "";
        private Label _listTitleControl;

        private Label FindListTitle(Control parent)
        {
            if (_listTitleControl != null) return _listTitleControl;
            foreach (Control c in parent.Controls)
            {
                if (c.Name == "listTitle") { _listTitleControl = (Label)c; return _listTitleControl; }
                var found = FindListTitle(c);
                if (found != null) { _listTitleControl = found; return _listTitleControl; }
            }
            return null;
        }

        /// <summary>Возвращает позицию играющего сейчас трека внутри нового списка;
        /// -1, если его там нет. Сохраняет контекст при перезаходе в список.</summary>
        private int RestorePlayIndex(List<Track> list)
        {
            if (list == null) return -1;
            string id = _playingTrackId;
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && string.Equals(list[i].Id, id, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private void LoadLikes()
        {
            SetView("likes", null);
            HideArtistPage();
            ClearPrefetch();
            _currentIndex = -1;
            SetListTitle("Мне нравится");

            List<Track> cached;
            Task<List<Track>> task = null;
            lock (_likesLock)
            {
                cached = _likesCache;
                if (cached == null)
                {
                    if (_likesTask == null)
                    {
                        DisableNavigation();
                        _likesTask = Bg.Run(() => _api.GetLikedTracks(_uid));
                    }
                    task = _likesTask;
                }
            }

            if (cached != null)
            {
                // список уже есть в памяти — показываем мгновенно, без обращения к сети
                ShowLikesList(cached);
                return;
            }

            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_currentView == "likes")
                    {
                        SetStatus("Ошибка: " + Describe(t.Exception));
                        _trackList.Items.Clear();
                    }
                    EnableNavigation();
                    return;
                }
                List<Track> list = t.Result;
                lock (_likesLock)
                {
                    if (list != null && list.Count > 0) _likesCache = list;
                }
                if (_currentView == "likes") ShowLikesList(list);
                EnableNavigation();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Показ списка лайков. _tracks получает копию: сам список мутируется
        /// при удалении строк, а _likesCache должен остаться цельным.</summary>
        private void ShowLikesList(List<Track> list)
        {
            if (list == null) return;
            _tracks = new List<Track>(list);
            _currentIndex = RestorePlayIndex(_tracks);
            FillTrackList();
            SetStatus("«Мне нравится»: " + _tracks.Count + " треков");
        }

        private void LoadPlaylist(long kind)
        {
            SetView("p" + kind, null);
            HideArtistPage();
            ClearPrefetch();
            _currentIndex = -1;
            string title;
            SetListTitle(_playlistTitles.TryGetValue(kind, out title) ? title : ("Плейлист #" + kind));
            DisableNavigation();
            Bg.Run(() => _api.GetPlaylist(_uid, kind)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    EnableNavigation();
                    return;
                }
                _tracks = t.Result
                    .Where(i => i.Track != null)
                    .Select(i => i.Track)
                    .ToList();
                _currentIndex = RestorePlayIndex(_tracks);
                FillTrackList();
                EnableNavigation();
                SetStatus("Плейлист: " + _tracks.Count + " треков");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void LoadWave()
        {
            _waveMode = true;
            SetView("wave", null);
            HideArtistPage();
            ClearPrefetch();
            SetListTitle("Моя волна");

            // волна уже грузилась в этой сессии — показываем как есть, без автообновления
            if (_waveCache != null && _waveCache.Count > 0)
            {
                _tracks = _waveCache;
                _waveStation = _waveCacheStation;
                UpdateWaveButtons();
                int byPlay = RestorePlayIndex(_tracks);
                _currentIndex = byPlay >= 0
                    ? byPlay
                    : ((_waveCacheIndex >= 0 && _waveCacheIndex < _tracks.Count) ? _waveCacheIndex : -1);
                FillTrackList();
                SetStatus("Моя волна: " + _tracks.Count + " треков (кнопка «Ещё» — догрузить, «Обновить» — новые)");
                return;
            }

            UpdateWaveButtons();
            DisableNavigation();
            Bg.Run(() =>
            {
                List<Track> batch = FetchWaveBatch(6);
                if (batch == null || batch.Count == 0)
                {
                    // всё уже показывалось — сбрасываем историю и берём свежую партию
                    lock (_waveSeenLock)
                    {
                        _waveSeenIds.Clear();
                        _waveSeenOrder.Clear();
                    }
                    batch = FetchWaveBatch(6);
                }
                return batch;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    EnableNavigation();
                    SetStatus("Ошибка: " + Describe(t.Exception));
                    return;
                }
                var items = t.Result;
                items.RemoveAll(x => x == null);
                _tracks = items;
                _waveCache = items;
                _waveCacheStation = _waveStation;
                _waveCacheIndex = RestorePlayIndex(_tracks);
                _currentIndex = _waveCacheIndex;
                FillTrackList();
                UpdateWaveButtons();
                EnableNavigation();
                SetStatus(_waveMode ? "Моя волна: " + _tracks.Count + " треков (кнопка «Ещё» — догрузить, «Обновить» — новые)" : "");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ==================== страница исполнителя и переходы ====================

        private void OnArtistAlbumClicked(long albumId)
        {
            NavigateToAlbum(albumId);
        }

        /// <summary>Возвращает правую область к обычному списку треков.</summary>
        private void HideArtistPage()
        {
            if (_artistPage != null) _artistPage.Visible = false;
            if (_homePage != null) _homePage.Visible = false;
            if (_settingsPage != null) _settingsPage.Visible = false;
            if (_searchPage != null) _searchPage.Visible = false;
            if (_trackList != null) _trackList.Visible = true;
        }

        /// <summary>Показывает страницу поиска. Панель постоянная, поэтому результаты
        /// прошлого запроса сохраняются — возврат из альбома/исполнителя их не теряет.</summary>
        private void ShowSearchPage()
        {
            SetView("search", null);
            ClearPrefetch();
            if (_artistPage != null) _artistPage.Visible = false;
            if (_homePage != null) _homePage.Visible = false;
            if (_settingsPage != null) _settingsPage.Visible = false;
            if (_trackList != null) _trackList.Visible = false;
            if (_searchPage != null)
            {
                _searchPage.BringToFront();
                _searchPage.Visible = true;
                _searchPage.FocusInput();
            }
            SetListTitle("Поиск");
            SetStatus("Введите запрос — найдём треки, альбомы и исполнителей");
        }

        /// <summary>Запрос из поля поиска по выбранной категории. Счётчик отсекает
        /// устаревшие ответы, когда пользователь печатает быстрее, чем отвечает API.</summary>
        private void OnSearchRequested(string query, string type)
        {
            if (_searchPage == null) return;
            if (_currentView != "search") return;

            int seq = ++_searchSeq;
            if (string.IsNullOrEmpty(query))
            {
                _searchPage.ShowIdle();
                SetStatus("Поиск");
                return;
            }

            SetStatus("Поиск: «" + query + "»…");
            Bg.Run(() => _api.Search(query, type, 40)).ContinueWith(t =>
            {
                if (seq != _searchSeq) return;
                if (_currentView != "search") return;
                if (t.IsFaulted)
                {
                    _searchPage.ShowStatus("Не удалось выполнить поиск: " + Describe(t.Exception));
                    SetStatus("Ошибка поиска");
                    return;
                }
                SearchResults r = t.Result;
                _searchPage.ShowResults(r, _coverLoader, IsCurrentSearchRow);

                int count = type == "album" ? r.Albums.Count
                          : type == "artist" ? r.Artists.Count
                          : r.Tracks.Count;
                string noun = type == "album" ? "альбомов"
                            : type == "artist" ? "артистов"
                            : "треков";
                SetStatus("Найдено " + count + " " + noun);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Показывает панель настроек.</summary>
        private void ShowSettingsPage()
        {
            SetView("settings", null);
            ClearPrefetch();
            if (_artistPage != null) _artistPage.Visible = false;
            if (_homePage != null) _homePage.Visible = false;
            if (_searchPage != null) _searchPage.Visible = false;
            if (_trackList != null) _trackList.Visible = false;
            if (_settingsPage != null)
            {
                _settingsPage.BringToFront();
                _settingsPage.Visible = true;
            }
            SetListTitle("Настройки");
            SetStatus("Настройки");
        }

        /// <summary>Выход из аккаунта: стираем токен и куки браузера, перезапускаемся в форму входа.</summary>
        private void Logout()
        {
            StopPlayback();
            TokenStorage.Delete();
            TokenStorage.ClearYandexCookies();
            Application.Restart();
        }

        /// <summary>Автовыход без перезапуска: сервер ответил 401, токен неверный/отозван.
        /// В отличие от Logout() не перезапускаем процесс — просто стираем токен и
        /// показываем окно входа ещё раз в текущем цикле.</summary>
        private void LogoutToLogin()
        {
            StopPlayback();
            TokenStorage.Delete();
            TokenStorage.ClearYandexCookies();
            DoLogin();
        }

        /// <summary>Показывает главную страницу с названием мода.</summary>
        private void ShowHomePage()
        {
            SetView("home", null);
            ClearPrefetch();
            if (_artistPage != null) _artistPage.Visible = false;
            if (_searchPage != null) _searchPage.Visible = false;
            if (_trackList != null) _trackList.Visible = false;
            if (_homePage != null)
            {
                _homePage.BringToFront();
                _homePage.Visible = true;
            }
            SetListTitle("Главная");
            SetStatus("YaMus — клиент Яндекс Музыки");
        }

        /// <summary>Подсветка играющего трека на странице исполнителя. Проверяем не только
        /// индекс, но и id: пока играет трек из другого списка, индекс указывал на
        /// постороннюю строку.</summary>
        private bool IsCurrentArtistRow(int index)
        {
            if (!_currentView.StartsWith("artist:")) return false;
            Track row = TrackAt(_artistPage == null ? null : _artistPage.Tracks, index);
            if (row == null || string.IsNullOrEmpty(row.Id)) return false;
            if (!string.Equals(row.Id, _playingTrackId, StringComparison.Ordinal)) return false;
            // если этот же список стал плейлистом, ориентируемся на позицию в нём
            // (в списке могут быть повторы id)
            if (_tracks != null && ReferenceEquals(_tracks, _artistPage.Tracks))
                return _currentIndex == index;
            return true;
        }

        /// <summary>То же для результатов поиска.</summary>
        private bool IsCurrentSearchRow(int index)
        {
            if (_currentView != "search") return false;
            Track row = TrackAt(_searchPage == null ? null : _searchPage.Tracks, index);
            if (row == null || string.IsNullOrEmpty(row.Id)) return false;
            if (!string.Equals(row.Id, _playingTrackId, StringComparison.Ordinal)) return false;
            if (_tracks != null && ReferenceEquals(_tracks, _searchPage.Tracks))
                return _currentIndex == index;
            return true;
        }

        private static Track TrackAt(List<Track> list, int index)
        {
            if (list == null) return null;
            if (index < 0 || index >= list.Count) return null;
            return list[index];
        }

        /// <summary>Вьюха исполнителя: «Популярные треки» + компактная сетка «Альбомы».</summary>
        private void ShowArtistPage(ArtistProfile p)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SetListTitle(string.IsNullOrEmpty(p.Name) ? "Исполнитель" : p.Name);
            // запоминаем популярные треки и сбрасываем кэш «всех треков»: он относится
            // к конкретному исполнителю, и при переходе к другому показывать нельзя
            _artistPopularTracks = p.Tracks ?? new List<Track>();
            if (_artistAllTracksFor != p.Id)
            {
                _artistAllTracks = null;
                _artistAllTracksFor = p.Id;
                _artistAllLoaded = false;
            }
            _artistAlbums = p.Albums;
            _artistPage.ShowArtist(p.Id, _artistPopularTracks, _artistAlbums, _coverLoader,
                                   IsCurrentArtistRow);
            _artistPage.BringToFront();
            _artistPage.Visible = true;
            if (_artistPopularTracks.Count == 0 && (p.Albums == null || p.Albums.Count == 0))
            {
                SetStatus("У исполнителя пока нет доступных треков.");
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 30) Diag.LogStall("ShowArtistPage", sw.ElapsedMilliseconds);
        }

        /// <summary>Клик по заголовку «Популярные треки»: переключаем на все треки
        /// исполнителя и обратно. Загрузка идёт в фоне, список перерисовывается на
        /// UI-потоке, только если мы всё ещё на странице этого исполнителя.</summary>
        private void ArtistAllTracksToggled(long artistId)
        {
            if (artistId == 0 || _artistPage == null) return;
            if (_artistPage.ShowsAllTracks)
            {
                _artistPage.ShowTracks(_artistPopularTracks, "Популярные треки", _artistAlbums, true);
                SetStatus("Популярные треки");
                return;
            }
            if (_artistAllLoaded && _artistAllTracks != null)
            {
                _artistPage.ShowTracks(_artistAllTracks, "Все треки", _artistAlbums, false);
                SetStatus("Все треки: " + _artistAllTracks.Count);
                return;
            }
            SetStatus("Загружаю все треки…");
            Bg.Run(() => _api.GetAllArtistTracks(artistId, 2000)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    SetStatus("Не удалось загрузить все треки: " + Describe(t.Exception));
                    return;
                }
                // пока грузили, могли уйти на другую страницу — тогда результат не показываем
                if (!_currentView.StartsWith("artist:" + artistId) || _artistPage == null) return;
                _artistAllTracks = t.Result ?? new List<Track>();
                _artistAllTracksFor = artistId;
                _artistAllLoaded = true;
                _artistPage.ShowTracks(_artistAllTracks, "Все треки", _artistAlbums, false);
                if (_artistAllTracks.Count == 0) SetStatus("У исполнителя нет доступных треков.");
                else SetStatus("Все треки: " + _artistAllTracks.Count);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Открывает альбом: заголовок + его треки в общем списке.</summary>
        private void NavigateToAlbum(long albumId)
        {
            if (albumId == 0) return;
            LeaveWaveMode();
            _nav.SelectedNode = null; // уходим из списка сайдбара — подсветка вкладки не нужна
            SetView("album:" + albumId, null);
            HideArtistPage();
            ClearPrefetch();
            _currentIndex = -1;
            SetListTitle("Альбом…");
            DisableNavigation();
            Bg.Run(() => _api.GetAlbumDetail(albumId)).ContinueWith(t =>
            {
                EnableNavigation();
                if (t.IsFaulted)
                {
                    SetStatus("Не удалось открыть альбом: " + Describe(t.Exception));
                    return;
                }
                if (_currentView != "album:" + albumId) return;
                AlbumDetail d = t.Result;
                SetListTitle(d.Album != null && !string.IsNullOrEmpty(d.Album.Title) ? d.Album.Title : "Альбом");
                _tracks = d.Tracks ?? new List<Track>();
                _tracks.RemoveAll(x => x == null);
                _currentIndex = RestorePlayIndex(_tracks);
                FillTrackList();
                SetStatus("Альбом: " + _tracks.Count + " треков");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Открывает исполнителя: популярные треки + компактные карточки альбомов.</summary>
        private void NavigateToArtist(long artistId)
        {
            if (artistId == 0) return;
            LeaveWaveMode();
            _nav.SelectedNode = null; // уходим из списка сайдбара — подсветка вкладки не нужна
            SetView("artist:" + artistId, null);
            HideArtistPage();
            ClearPrefetch();
            _currentIndex = -1;
            SetListTitle("Исполнитель…");
            DisableNavigation();
            Bg.Run(() => _api.GetArtistProfile(artistId)).ContinueWith(t =>
            {
                EnableNavigation();
                if (t.IsFaulted)
                {
                    SetStatus("Не удалось открыть исполнителя: " + Describe(t.Exception));
                    return;
                }
                if (_currentView != "artist:" + artistId) return;
                ArtistProfile p = t.Result;
                ShowArtistPage(p);
                SetStatus("Исполнитель: «" + p.Name + "» — " +
                          (p.Tracks == null ? 0 : p.Tracks.Count) + " популярных треков, " +
                          (p.Albums == null ? 0 : p.Albums.Count) + " альбомов");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}