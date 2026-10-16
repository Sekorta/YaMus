using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YaMus.Core.Diagnostics;
using YaMus.Core.Models;

namespace YaMus.Core.Api
{
    /// <summary>Методы Yandex Music API.</summary>
    public sealed class YandexApi
    {
        private const int BatchSize = 200;

        private readonly RestClient _rest;

        public YandexApi(RestClient rest)
        {
            _rest = rest;
        }

        public JToken GetAccountAbout()
        {
            // при 401 (неверный токен) бросаем UnauthorizedAccessException
            try
            {
                return _rest.GetJson("/account/about");
            }
            catch (ApiError ex)
            {
                if (ex.StatusCode == 401)
                {
                    throw new UnauthorizedAccessException("Неверный OAuth-токен");
                }
                throw;
            }
        }

        public AccountInfo GetAccount()
        {
            JToken about = GetAccountAbout();
            JToken r = about["result"];
            var info = new AccountInfo();
            if (r == null) return info;
            info.Uid = GetLong(r, "uid");
            info.Login = GetString(r, "login");
            info.PublicName = GetString(r, "publicName");
            info.Email = GetString(r, "email");
            info.AvatarUri = GetString(r, "avatar");
            if (string.IsNullOrEmpty(info.AvatarUri)) info.AvatarUri = GetString(r, "avatarUri");
            if (string.IsNullOrEmpty(info.AvatarUri)) info.AvatarUri = GetString(r, "avatarPortraitUri");
            if (string.IsNullOrEmpty(info.AvatarUri))
            {
                // /account/about отдаёт только avatarId вида "65952/iwOUq6...-1" — это
                // аватар Яндекс.Паспорта, адрес получается через get-yapic.
                string avatarId = GetString(r, "avatarId");
                if (!string.IsNullOrEmpty(avatarId))
                {
                    info.AvatarUri = "avatars.yandex.net/get-yapic/" + avatarId.Trim('/') + "/islands-200";
                }
            }
            info.HasPlus = GetBool(r, "hasPlus");
            info.HasMusicSubscription = GetBool(r, "hasMusicSubscription");
            info.ServiceAvailable = GetBool(r, "serviceAvailable");
            return info;
        }

        public List<PlaylistSummary> GetPlaylists(long uid)
        {
            JToken resp = _rest.GetJson("/users/" + uid + "/playlists/list");
            var result = new List<PlaylistSummary>();
            foreach (var item in (resp["result"] as JArray) ?? new JArray())
            {
                result.Add(new PlaylistSummary
                {
                    Kind = GetLong(item, "kind"),
                    Title = GetString(item, "title"),
                    TrackCount = (int)GetLong(item, "trackCount"),
                    Modified = GetString(item, "modified")
                });
            }
            return result;
        }

        public List<PlaylistItem> GetPlaylist(long uid, long kind)
        {
            JToken resp = _rest.GetJson("/users/" + uid + "/playlists/" + kind);
            var result = new List<PlaylistItem>();
            var tracks = resp["result"] != null ? resp["result"]["tracks"] as JArray : null;
            if (tracks == null) return result;

            for (int i = 0; i < tracks.Count; i++)
            {
                var item = tracks[i];
                var itemData = new PlaylistItem
                {
                    Id = GetString(item, "id"),
                    AlbumId = GetLong(item, "albumId"),
                    Timestamp = GetString(item, "timestamp"),
                    OriginalIndex = i
                };
                if (item["track"] != null) itemData.Track = DeserializeTrack(item["track"]);
                result.Add(itemData);
            }
            return result;
        }

        public List<LikeItem> GetLikeIds(long uid)
        {
            JToken resp = _rest.GetJson("/users/" + uid + "/likes/tracks");
            var result = new List<LikeItem>();
            var tracks = resp["result"] != null && resp["result"]["library"] != null
                ? resp["result"]["library"]["tracks"] as JArray
                : null;
            if (tracks == null) return result;

            foreach (var item in tracks)
            {
                result.Add(new LikeItem
                {
                    Id = GetString(item, "id"),
                    AlbumId = GetLong(item, "albumId"),
                    Timestamp = GetString(item, "timestamp")
                });
            }
            return result;
        }

        /// <summary>«Мне нравится»: id → полные треки.</summary>
        public List<Track> GetLikedTracks(long uid)
        {
            var ids = GetLikeIds(uid).Select(l => l.Id).ToList();
            return GetTracks(ids);
        }

        /// <summary>Поиск по каталогу (GET /search). type: "track" | "album" | "artist" | "all".
        /// Для категории парсится только её секция — боковые мини-выдачи API не подмешиваются.</summary>
        public SearchResults Search(string text, string type, int limit)
        {
            var result = new SearchResults { Query = text };
            if (string.IsNullOrEmpty(text)) return result;

            bool all = string.IsNullOrEmpty(type) || type == "all";
            string t = all ? "all" : type;

            JToken resp = _rest.GetJson("/search?text=" + Uri.EscapeDataString(text) +
                                        "&type=" + t + "&page=0&pageSize=" + Math.Max(20, limit) + "&nocorrect=false");
            JToken r = resp["result"];
            if (r == null) return result;

            if (all || t == "track") ParseSearchTracks(r, result, limit);
            if (all || t == "album") ParseSearchAlbums(r, result, limit);
            if (all || t == "artist") ParseSearchArtists(r, result, limit);
            return result;
        }

        private static void ParseSearchTracks(JToken r, SearchResults result, int limit)
        {
            JArray arr = r["tracks"] != null ? r["tracks"]["results"] as JArray : null;
            if (arr == null) return;
            foreach (var t in arr)
            {
                if (result.Tracks.Count >= limit) break;
                var track = DeserializeTrack(t);
                if (track != null) result.Tracks.Add(track);
            }
        }

        private static void ParseSearchAlbums(JToken r, SearchResults result, int limit)
        {
            JArray arr = r["albums"] != null ? r["albums"]["results"] as JArray : null;
            if (arr == null) return;
            foreach (var a in arr)
            {
                if (result.Albums.Count >= limit) break;
                var album = DeserializeAlbum(a);
                if (album != null && album.Id != 0) result.Albums.Add(album);
            }
        }

        private static void ParseSearchArtists(JToken r, SearchResults result, int limit)
        {
            JArray arr = r["artists"] != null ? r["artists"]["results"] as JArray : null;
            if (arr == null) return;
            foreach (var a in arr)
            {
                if (result.Artists.Count >= limit) break;
                if (a == null) continue;
                long id = GetLong(a, "id");
                if (id == 0) continue;
                result.Artists.Add(new Artist
                {
                    Id = id,
                    Name = GetString(a, "name"),
                    Various = GetBool(a, "various"),
                    Composer = GetBool(a, "composer"),
                    CoverUri = ArtistCoverUri(a)
                });
            }
        }

        /// <summary>Обложка исполнителя: в поисковой выдаче приходит объектом cover.uri,
        /// реже строкой; запасные варианты — ogImage и coverUri.</summary>
        private static string ArtistCoverUri(JToken a)
        {
            if (a == null) return null;
            JToken cover = a["cover"];
            string uri = null;
            if (cover != null && cover.Type == JTokenType.Object) uri = GetString(cover, "uri");
            else if (cover != null && cover.Type == JTokenType.String) uri = cover.ToString();
            if (string.IsNullOrEmpty(uri)) uri = GetString(a, "ogImage");
            if (string.IsNullOrEmpty(uri)) uri = GetString(a, "coverUri");
            return uri;
        }

        public List<Track> GetTracks(IList<string> ids)
        {
            var result = new List<Track>();
            if (ids == null || ids.Count == 0) return result;

            for (int start = 0; start < ids.Count; start += BatchSize)
            {
                int count = Math.Min(BatchSize, ids.Count - start);
                var chunk = new string[count];
                for (int i = 0; i < count; i++) chunk[i] = ids[start + i];

                JToken resp = _rest.GetJson("/tracks?trackIds=" + Uri.EscapeDataString(string.Join(",", chunk)));
                var arr = resp["result"] as JArray;
                if (arr == null) continue;
                foreach (var t in arr)
                {
                    var track = DeserializeTrack(t);
                    if (track != null) result.Add(track);
                }
            }
            return result;
        }

        public void LikeTrack(long uid, string trackId)
        {
            try
            {
                _rest.PostEmpty("/users/" + uid + "/likes/tracks/add?trackId=" + Uri.EscapeDataString(trackId));
            }
            catch (ApiError)
            {
                throw new InvalidOperationException("Не удалось добавить в «Мне нравится»");
            }
        }

        public void UnlikeTrack(long uid, string trackId)
        {
            try
            {
                // remove ждёт trackIds (мн. число) — подтверждено живым запросом
                _rest.PostEmpty("/users/" + uid + "/likes/tracks/remove?trackIds=" + Uri.EscapeDataString(trackId));
            }
            catch (ApiError)
            {
                throw new InvalidOperationException("Не удалось убрать из «Мне нравится»");
            }
        }

        /// <summary>Станция: следующая порция треков (sequence содержит полные Track).
        /// Стандартная — «user:onyourwave», по треку — «track:{trackId}».</summary>
        public List<Track> GetRotation(string station = "user:onyourwave")
        {
            JToken resp = _rest.GetJson("/rotor/station/" + station + "/tracks");
            var result = new List<Track>();

            var seq = resp["result"] != null ? resp["result"]["sequence"] as JArray : null;
            if (seq == null) return result;

            foreach (var item in seq)
            {
                if (item == null) continue;
                string type = item["type"] != null ? item["type"].ToString() : null;
                if (type != "track") continue;
                var track = DeserializeTrack(item["track"]);
                if (track != null) result.Add(track);
            }
            return result;
        }

        public static Track DeserializeTrack(JToken t)
        {
            if (t == null) return null;
            var track = new Track
            {
                Id = GetString(t, "id"),
                RealId = GetString(t, "realId"),
                Title = GetString(t, "title"),
                Version = GetString(t, "version"),
                DurationMs = GetLong(t, "durationMs"),
                CoverUri = GetString(t, "coverUri"),
                Available = GetBool(t, "available"),
                AvailableFullWithoutPermission = GetBool(t, "availableFullWithoutPermission")
            };

            var artists = t["artists"] as JArray;
            if (artists != null)
            {
                foreach (var a in artists)
                {
                    if (a == null) continue;
                    track.Artists.Add(new Artist
                    {
                        Id = GetLong(a, "id"),
                        Name = GetString(a, "name"),
                        Various = GetBool(a, "various"),
                        Composer = GetBool(a, "composer")
                    });
                }
            }

            var albums = t["albums"] as JArray;
            if (albums != null)
            {
                foreach (var a in albums)
                {
                    var album = DeserializeAlbum(a);
                    if (album != null) track.Albums.Add(album);
                }
            }
            return track;
        }

        /// <summary>Альбом с треками: GET /albums/{id}/with-tracks (объединяет все диски volumes).</summary>
        public AlbumDetail GetAlbumDetail(long albumId)
        {
            var detail = new AlbumDetail();
            JToken resp = _rest.GetJson("/albums/" + albumId + "/with-tracks");
            JToken r = resp["result"];
            if (r == null) return detail;

            detail.Album = DeserializeAlbum(r);
            var tracks = new List<Track>();

            var volumes = r["volumes"] as JArray;
            if (volumes != null)
            {
                foreach (var disc in volumes)
                {
                    var arr = disc as JArray;
                    if (arr == null) continue;
                    foreach (var t in arr)
                    {
                        var track = DeserializeTrack(t);
                        if (track != null) tracks.Add(track);
                    }
                }
            }
            var flat = r["tracks"] as JArray;
            if (flat != null)
            {
                foreach (var t in flat)
                {
                    var track = DeserializeTrack(t);
                    if (track != null) tracks.Add(track);
                }
            }

            detail.Tracks = tracks;
            return detail;
        }

        /// <summary>Карточка исполнителя: популярные треки + прямые альбомы (GET /artists/{id}/direct-albums).</summary>
        public ArtistProfile GetArtistProfile(long artistId)
        {
            var profile = new ArtistProfile { Id = artistId };
            // имя берём у самого исполнителя: у прямых альбомов первый артист может
            // оказаться фитом/соавтором (баг «grcz → Hancerios Rain»)
            profile.Name = GetArtistName(artistId);
            profile.Tracks = GetArtistTopTracks(artistId, 10);
            ParseArtistAlbums(profile, _rest.GetJson("/artists/" + artistId + "/direct-albums"));
            if (string.IsNullOrEmpty(profile.Name))
            {
                foreach (Track tt in profile.Tracks)
                {
                    if (tt.Artists == null) continue;
                    foreach (Artist ar in tt.Artists)
                    {
                        if (ar != null && ar.Id == artistId && !string.IsNullOrEmpty(ar.Name))
                        {
                            profile.Name = ar.Name;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(profile.Name)) break;
                }
            }
            if (string.IsNullOrEmpty(profile.Name)) profile.Name = "Исполнитель";
            return profile;
        }

        /// <summary>Собственное имя исполнителя (GET /artists/{id}/brief-info).</summary>
        private string GetArtistName(long artistId)
        {
            try
            {
                JToken resp = _rest.GetJson("/artists/" + artistId + "/brief-info");
                JToken artist = resp["result"] != null ? resp["result"]["artist"] : null;
                if (artist != null)
                {
                    string name = GetString(artist, "name");
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch (Exception ex)
            {
                // имя исполнителя не критично — не показываем ошибку, но фиксируем в логе
                Diag.Log("artist name fail id=" + artistId + ": " + ex.Message);
            }
            return null;
        }

        /// <summary>Популярные треки исполнителя: GET /artists/{id}/tracks.</summary>
        public List<Track> GetArtistTopTracks(long artistId, int count)
        {
            return ArtistTracksPage(artistId, 0, count);
        }

        /// <summary>Все треки исполнителя: листаем /artists/{id}/tracks по страницам,
        /// пока страница не приходит неполной. count — сколько треков максимум
        /// (защита от бесконечного листания у исполнителей с тысячами треков).</summary>
        public List<Track> GetAllArtistTracks(long artistId, int count)
        {
            var result = new List<Track>();
            if (artistId == 0 || count <= 0) return result;
            const int pageSize = 200;
            int page = 0;
            while (result.Count < count)
            {
                int want = Math.Min(pageSize, count - result.Count);
                List<Track> part = ArtistTracksPage(artistId, page, want);
                if (part.Count == 0) break;
                // страницы могут пересекаться по трекам (составные/переиздания) —
                // дубли по id убираем, иначе список расползётся повторами
                var seen = new HashSet<string>(result.Select(t => t.Id));
                foreach (var t in part)
                {
                    if (t == null) continue;
                    if (!string.IsNullOrEmpty(t.Id) && !seen.Add(t.Id)) continue;
                    result.Add(t);
                }
                if (part.Count < want) break;
                page++;
            }
            return result;
        }

        private List<Track> ArtistTracksPage(long artistId, int page, int count)
        {
            var result = new List<Track>();
            JToken resp = _rest.GetJson("/artists/" + artistId + "/tracks?page=" + page + "&pageSize=" + count);
            JToken r = resp["result"];
            if (r == null) return result;

            JArray arr = r as JArray;
            if (arr == null && r["tracks"] != null) arr = r["tracks"] as JArray;
            if (arr == null) return result;

            foreach (var item in arr)
            {
                if (item == null) continue;
                JToken t = item["track"] != null ? item["track"] : item;
                var track = DeserializeTrack(t);
                if (track != null) result.Add(track);
            }
            return result;
        }

        private void ParseArtistAlbums(ArtistProfile profile, JToken resp)
        {
            JToken r = resp["result"];
            JArray arr = r as JArray;
            if (arr == null && r != null) arr = r["albums"] as JArray;
            if (arr == null) return;

            foreach (var a in arr)
            {
                if (a == null) continue;
                var album = DeserializeAlbum(a);
                if (album == null || album.Id == 0) continue;
                if (string.IsNullOrEmpty(profile.Name) && album.Artists != null)
                {
                    // только исполнитель с нужным id — первый в списке может быть фитом
                    foreach (Artist ar in album.Artists)
                    {
                        if (ar != null && ar.Id == profile.Id && !string.IsNullOrEmpty(ar.Name))
                        {
                            profile.Name = ar.Name;
                            break;
                        }
                    }
                }
                profile.Albums.Add(album);
            }
        }

        private static Album DeserializeAlbum(JToken a)
        {
            if (a == null) return null;
            var album = new Album
            {
                Id = GetLong(a, "id"),
                Title = GetString(a, "title"),
                CoverUri = GetString(a, "coverUri"),
                Year = (int)GetLong(a, "year"),
                TrackCount = (int)GetLong(a, "trackCount"),
                Genre = GetString(a, "genre")
            };

            var artists = a["artists"] as JArray;
            if (artists != null)
            {
                foreach (var ar in artists)
                {
                    if (ar == null) continue;
                    album.Artists.Add(new Artist
                    {
                        Id = GetLong(ar, "id"),
                        Name = GetString(ar, "name"),
                        Various = GetBool(ar, "various"),
                        Composer = GetBool(ar, "composer")
                    });
                }
            }
            return album;
        }

        private static string GetString(JToken o, string key)
        {
            return o != null && o[key] != null ? o[key].ToString() : null;
        }

        private static long GetLong(JToken o, string key)
        {
            JToken v = o != null ? o[key] : null;
            if (v == null || v.Type == JTokenType.Null) return 0;
            if (v.Type == JTokenType.String)
            {
                long parsed;
                return long.TryParse(v.ToString(), out parsed) ? parsed : 0;
            }
            try { return v.ToObject<long>(); }
            catch { return 0; }
        }

        private static bool GetBool(JToken o, string key)
        {
            JToken v = o != null ? o[key] : null;
            return v != null && v.Type == JTokenType.Boolean && v.Value<bool>();
        }
    }
}