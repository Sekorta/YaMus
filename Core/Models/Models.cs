using System;
using System.Collections.Generic;

namespace YaMus.Core.Models
{
    public sealed class AccountInfo
    {
        public long Uid;
        public string Login;
        public string PublicName;
        public string Email;
        public string AvatarUri;
        public bool HasPlus;
        public bool HasMusicSubscription;
        public bool ServiceAvailable;
    }

    public sealed class Artist
    {
        public long Id;
        public string Name;
        public bool Various;
        public bool Composer;
        public string CoverUri;
    }

    public sealed class Album
    {
        public long Id;
        public string Title;
        public string CoverUri;
        public int Year;
        public int TrackCount;
        public string Genre;
        public List<Artist> Artists = new List<Artist>();
    }

    /// <summary>Альбом с его треками (GET /albums/{id}/with-tracks).</summary>
    public sealed class AlbumDetail
    {
        public Album Album;
        public List<Track> Tracks = new List<Track>();
    }

    /// <summary>Результаты поиска (GET /search?type=all): секции треков, альбомов и исполнителей.</summary>
    public sealed class SearchResults
    {
        public string Query;
        public List<Track> Tracks = new List<Track>();
        public List<Album> Albums = new List<Album>();
        public List<Artist> Artists = new List<Artist>();
    }

    /// <summary>Исполнитель с его альбомами (GET /artists/{id}/direct-albums).</summary>
    public sealed class ArtistProfile
    {
        public long Id;
        public string Name;
        public List<Track> Tracks = new List<Track>();
        public List<Album> Albums = new List<Album>();
    }

    /// <summary>Трек. Поля мапятся из JSON регистронезависимо (Newtonsoft).</summary>
    public sealed class Track
    {
        public string Id;
        public string RealId;
        public string Title;
        public string Version;
        public long DurationMs;
        public List<Artist> Artists = new List<Artist>();
        public List<Album> Albums = new List<Album>();
        public string CoverUri;
        public bool Available;
        public bool AvailableFullWithoutPermission;

        public string ArtistsText
        {
            get
            {
                if (Artists == null || Artists.Count == 0) return "";
                var names = new List<string>();
                foreach (var a in Artists)
                {
                    if (a != null && !string.IsNullOrEmpty(a.Name)) names.Add(a.Name);
                }
                return string.Join(", ", names.ToArray());
            }
        }

        public string AlbumTitle
        {
            get
            {
                if (Albums == null || Albums.Count == 0) return "";
                for (int i = 0; i < Albums.Count; i++)
                {
                    if (Albums[i] != null && !string.IsNullOrEmpty(Albums[i].Title)) return Albums[i].Title;
                }
                return "";
            }
        }

        public TimeSpan Duration
        {
            get { return TimeSpan.FromMilliseconds(DurationMs); }
        }

        public string DurationText
        {
            get
            {
                var d = Duration;
                return ((int)d.TotalMinutes).ToString("00") + ":" + d.Seconds.ToString("00");
            }
        }
    }

    public sealed class PlaylistSummary
    {
        public long Kind;
        public string Title;
        public int TrackCount;
        public string Modified;
    }

    /// <summary>Элемент плейлиста из /users/{uid}/playlists/{kind}.</summary>
    public sealed class PlaylistItem
    {
        public string Id;
        public long? AlbumId;
        public string Timestamp;
        public int OriginalIndex;
        public Track Track;
    }

    /// <summary>Запись «нравится» из /users/{uid}/likes/tracks (только id).</summary>
    public sealed class LikeItem
    {
        public string Id;
        public long? AlbumId;
        public string Timestamp;
    }
}