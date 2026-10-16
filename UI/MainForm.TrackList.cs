using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using YaMus.Core.Diagnostics;
using YaMus.Core.Media;
using YaMus.Core.Models;

namespace YaMus.UI
{
    public sealed partial class MainForm
    {
        private void FillTrackList(Action onDone = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // обычный (невиртуальный) режим: кастомная отрисовка строк (LVS_OWNERDRAWFIXED)
            // в .NET 4.0 не выставляется для виртуальных списков, поэтому VirtualMode выключен
            CancelFill();
            // колбэт надо сохранить: список может наполняться по частям, и выделение
            // строки после перестроения возможно только когда он реально готов
            _fillComplete = onDone;
            _rowToImage.Clear();
            _coverRequests.Clear();
            ReuseCachedCovers();

            var items = BuildTrackItems();

            if (items.Count <= FillChunk)
            {
                _trackList.BeginUpdate();
                _trackList.Items.Clear();
                _trackList.Items.AddRange(items.ToArray());
                _trackList.EndUpdate();
                _trackList.Invalidate();
                ScheduleVisibleCovers();
                OnFillComplete();
                sw.Stop();
                if (sw.ElapsedMilliseconds > 30) Diag.LogStall("FillTrackList[" + _tracks.Count + "]", sw.ElapsedMilliseconds);
                return;
            }

            _fillItems = items;
            _fillStep = 0;
            _fillSuppressed = true;
            _trackList.BeginUpdate();
            _trackList.Items.Clear();
            FillTrackListChunk();
            sw.Stop();
            if (sw.ElapsedMilliseconds > 30) Diag.LogStall("FillStart[" + _tracks.Count + "]", sw.ElapsedMilliseconds);
        }

        private void FillTrackListChunk()
        {
            if (_fillItems == null) return;
            int start = _fillStep;
            _fillStep += FillChunk;
            int end = Math.Min(_fillStep, _fillItems.Count);
            for (int i = start; i < end; i++) _trackList.Items.Add(_fillItems[i]);
            if (end >= _fillItems.Count)
            {
                _fillItems = null;
                _fillSuppressed = false;
                _trackList.EndUpdate();
                _trackList.Invalidate();
                ScheduleVisibleCovers();
                if (_fillTimer != null) _fillTimer.Stop();
                OnFillComplete();
            }
            else
            {
                StartFillTimerIfNeeded();
            }
        }

        private void StartFillTimerIfNeeded()
        {
            if (_fillItems == null) return;
            if (_fillTimer == null)
            {
                _fillTimer = new Timer { Interval = 10 };
                _fillTimer.Tick += (s, e) => FillTrackListChunk();
            }
            if (!_fillTimer.Enabled) _fillTimer.Start();
        }

        private void CancelFill()
        {
            _fillItems = null;
            _fillComplete = null;
            if (_fillTimer != null) _fillTimer.Stop();
            if (_fillSuppressed)
            {
                _fillSuppressed = false;
                _trackList.EndUpdate();
            }
        }

        private void OnFillComplete()
        {
            var done = _fillComplete;
            _fillComplete = null;
            if (done != null) done();
        }

        /// <summary>Не перекачивает обложки при перезаполнении: если картинка уже в ImageList
        /// (тот же coverUri, кэш не очищался), сразу вешает её на строку.</summary>
        private void ReuseCachedCovers()
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                string coverUri = TrackCoverKey(_tracks[i]);
                if (coverUri != null && _covers.Images.ContainsKey(coverUri))
                {
                    _rowToImage[i] = _covers.Images.IndexOfKey(coverUri);
                }
            }
        }

        /// <summary>Догрузка волны «Ещё»: добавляет новые строки в конец без пересоздания списка,
        /// чтобы не было мерцания и не сбрасывались уже загруженные обложки.</summary>
        private void AppendTrackList(List<Track> add)
        {
            if (add == null || add.Count == 0) return;
            CancelFill();
            int baseIdx = _trackList.Items.Count;
            var newItems = new List<ListViewItem>(add.Count);
            for (int i = 0; i < add.Count; i++)
            {
                Track track = add[i];
                int idx = baseIdx + i;
                var item = new ListViewItem((idx + 1).ToString());
                if (track != null)
                {
                    if (!track.Available) item.ForeColor = Color.Gray;
                    item.SubItems.Add(string.IsNullOrEmpty(track.Title) ? "—" : track.Title);
                    item.SubItems.Add(track.ArtistsText);
                    item.SubItems.Add(track.AlbumTitle);
                    item.SubItems.Add(track.DurationText);
                }
                else
                {
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                }
                item.Tag = idx;
                newItems.Add(item);
            }
            _trackList.BeginUpdate();
            _trackList.Items.AddRange(newItems.ToArray());
            _trackList.EndUpdate();
            _trackList.Invalidate();
            ScheduleVisibleCovers();
        }

        private List<ListViewItem> BuildTrackItems()
        {
            var items = new List<ListViewItem>(_tracks.Count);
            for (int i = 0; i < _tracks.Count; i++)
            {
                Track track = _tracks[i];
                var item = new ListViewItem((i + 1).ToString());
                if (track != null)
                {
                    if (!track.Available) item.ForeColor = Color.Gray;
                    item.SubItems.Add(string.IsNullOrEmpty(track.Title) ? "—" : track.Title);
                    item.SubItems.Add(track.ArtistsText);
                    item.SubItems.Add(track.AlbumTitle);
                    item.SubItems.Add(track.DurationText);
                }
                else
                {
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                }
                item.Tag = i;
                items.Add(item);
            }
            return items;
        }

        private static string TrackCoverKey(Track track)
        {
            if (track == null) return null;
            if (track.Albums != null && track.Albums.Count > 0 && track.Albums[0] != null &&
                !string.IsNullOrEmpty(track.Albums[0].CoverUri))
            {
                return track.Albums[0].CoverUri;
            }
            return track.CoverUri;
        }

        private static readonly Font FBase = new Font("Segoe UI", 9F);
        private static readonly Font FBold = new Font("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font FStrike = new Font("Segoe UI", 9F, FontStyle.Strikeout);
        private static readonly Font FHeader = new Font("Segoe UI", 7.5F, FontStyle.Bold);
        private static readonly Font FPlayIcon = UiGlyphs.GlyphFont(15F, FontStyle.Regular);
        private static readonly Font FPlayMark = UiGlyphs.GlyphFont(9F, FontStyle.Bold);
        private static readonly Font FNoteIcon = UiGlyphs.GlyphFont(10F, FontStyle.Regular);

        private const int FillChunk = 300;
        private List<ListViewItem> _fillItems;
        private int _fillStep;
        private Action _fillComplete;
        private Timer _fillTimer;
        private bool _fillSuppressed;

        private void FillHeaderWidths()
        {
            try
            {
                ColumnHeader last = _trackList.Columns[_trackList.Columns.Count - 1];
                int sum = 0;
                for (int i = 0; i < _trackList.Columns.Count - 1; i++)
                {
                    sum += _trackList.Columns[i].Width;
                }
                int rem = _trackList.ClientSize.Width - sum;
                last.Width = rem > 6 ? rem : 8;
            }
            catch { }
        }

        private void TrackListOnDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // весь рендер в DrawSubItem; запрещаем стандартную отрисовку, иначе строки рисуются белыми
            e.DrawDefault = false;
        }

        private void TrackListOnDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = false;
            using (var b = new SolidBrush(YmPalette.BgSide))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            string caption = e.Header.Text;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            if (e.ColumnIndex == _trackList.Columns.Count - 1)
            {
                flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            }
            TextRenderer.DrawText(e.Graphics, caption, FHeader,
                                  Rectangle.Inflate(e.Bounds, -6, 0), YmPalette.TextFaint, flags);
        }

        private void TrackListOnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                TrackListOnDrawSubItemCore(sender, e);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.Diag.LogException("draw row=" + e.ItemIndex + " col=" + e.ColumnIndex, ex);
            }
            sw.Stop();
            Diag.UiProfile.Add("list-paint", sw.ElapsedMilliseconds);
        }

        private void TrackListOnDrawSubItemCore(object sender, DrawListViewSubItemEventArgs e)
        {
            int row = e.ItemIndex;
            Color bg = YmPalette.BgMain;
            bool selected = e.Item != null && e.Item.Selected;
            bool current = row == _currentIndex;

            if (selected) bg = YmPalette.BgSel;
            else if (row == _hoverRow) bg = YmPalette.BgHover;
            else if (current) bg = YmPalette.BgCard;

            using (var b = new SolidBrush(bg))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }

            Track track = row >= 0 && row < _tracks.Count ? _tracks[row] : null;
            if (track == null) return;

            Color sub = track.Available ? YmPalette.TextSub : YmPalette.TextFaint;
            Font textFont = track.Available ? FBase : FStrike;

            switch (e.ColumnIndex)
            {
                case 0:
                {
                    Color c = current ? YmPalette.Accent : YmPalette.TextFaint;
                    if (current)
                    {
                        TextRenderer.DrawText(e.Graphics, "▶", FPlayMark,
                                              Rectangle.Inflate(e.Bounds, -8, 0), c,
                                              TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, (row + 1).ToString(), FBase,
                                              Rectangle.Inflate(e.Bounds, -8, 0), c,
                                              TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    }
                    break;
                }
                case 1:
                {
                    const int box = 40;
                    int x = e.Bounds.Left + 4;
                    int y = e.Bounds.Top + (e.Bounds.Height - box) / 2;
                    DrawCover(e.Graphics, row, x, y, box);
                    int tx = x + box + 8;
                    if (current && !selected) textFont = FBold;
                    int avail = e.Bounds.Width - (tx - e.Bounds.Left);
                    Rectangle titleRc = new Rectangle(tx, e.Bounds.Top, Math.Max(1, avail), e.Bounds.Height);
                    string title = string.IsNullOrEmpty(track.Title) ? "—" : track.Title;
                    TextRenderer.DrawText(e.Graphics, title, textFont, titleRc,
                                          track.Available ? YmPalette.TextMain : YmPalette.TextFaint,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    if (!string.IsNullOrEmpty(track.Version) && track.Version != "original" && track.Available)
                    {
                        Size sz = TextRenderer.MeasureText(e.Graphics, title, textFont);
                        int vx = tx + sz.Width + 4;
                        if (sz.Width < avail - 20)
                        {
                            TextRenderer.DrawText(e.Graphics, track.Version, textFont,
                                                  new Rectangle(vx, e.Bounds.Top, Math.Max(1, avail - sz.Width - 4), e.Bounds.Height),
                                                  YmPalette.TextFaint,
                                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        }
                    }
                    break;
                }
                case 2:
                    TextRenderer.DrawText(e.Graphics, track.ArtistsText, textFont,
                                          Rectangle.Inflate(e.Bounds, -8, 0), sub,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case 3:
                    TextRenderer.DrawText(e.Graphics, track.AlbumTitle, textFont,
                                          Rectangle.Inflate(e.Bounds, -8, 0), sub,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case 4:
                    TextRenderer.DrawText(e.Graphics, track.DurationText, textFont,
                                          Rectangle.Inflate(e.Bounds, -8, 0), sub,
                                          TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    break;
            }
        }

        private void DrawCover(Graphics g, int row, int x, int y, int size)
        {
            int idx;
            if (_rowToImage.TryGetValue(row, out idx) && idx >= 0 && idx < _covers.Images.Count)
            {
                g.DrawImage(_covers.Images[idx], x, y, size, size);
                return;
            }
            using (var b = new SolidBrush(YmPalette.BgCard))
            {
                g.FillRectangle(b, x, y, size, size);
            }
            TextRenderer.DrawText(g, UiGlyphs.Note, size <= 24 ? FNoteIcon : FPlayIcon,
                                  new Rectangle(x, y, size, size), YmPalette.TextFaint,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void TrackListOnMouseMove(object sender, MouseEventArgs e)
        {
            ListViewItem item = _trackList.GetItemAt(e.X, e.Y);
            int row = item != null ? item.Index : -1;
            if (row != _hoverRow)
            {
                int oldRow = _hoverRow;
                _hoverRow = row;
                // перерисовываем только затронутые строки, а не весь список
                if (oldRow >= 0 && oldRow < _trackList.Items.Count)
                {
                    Rectangle rc = _trackList.GetItemRect(oldRow);
                    _trackList.Invalidate(Rectangle.Inflate(rc, 2, 0));
                }
                if (row >= 0)
                {
                    Rectangle rc = _trackList.GetItemRect(row);
                    _trackList.Invalidate(Rectangle.Inflate(rc, 2, 0));
                }
            }
        }

        private void TrackListOnMouseLeave(object sender, EventArgs e)
        {
            if (_hoverRow != -1)
            {
                int old = _hoverRow;
                _hoverRow = -1;
                if (old >= 0 && old < _trackList.Items.Count)
                {
                    Rectangle rc = _trackList.GetItemRect(old);
                    _trackList.Invalidate(Rectangle.Inflate(rc, 2, 0));
                }
            }
        }

        private void ScheduleVisibleCovers()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_tracks.Count == 0) return;

            int top = 0;
            try { if (_trackList.IsHandleCreated) top = SendMessage(_trackList.Handle, LVM_GETTOPINDEX, IntPtr.Zero, 0); }
            catch { }
            if (top < 0) top = 0;

            int rowHeight = 40 + 2;
            int visible = Math.Max(1, _trackList.ClientSize.Height / Math.Max(20, rowHeight));
            int pad = 5;
            int from = Math.Max(0, top - pad);
            int to = Math.Min(_tracks.Count - 1, top + visible + pad);
            for (int i = from; i <= to; i++)
            {
                RequestCover(i);
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 50) Diag.UiProfile.Add("sched-covers", sw.ElapsedMilliseconds);
        }

        /// <summary>Перерисовывает только одну строку списка (OwnerDraw-ListView не
        /// двухбуферится нативно, поэтому полная Invalidate моргает всем списком).</summary>
        private void InvalidateTrackRow(int row)
        {
            if (_trackList == null || row < 0 || row >= _trackList.Items.Count) return;
            try
            {
                var rc = _trackList.GetItemRect(row);
                if (rc.Width <= 0 || rc.Height <= 0) return;
                rc.X = 0;
                rc.Width = _trackList.ClientSize.Width;
                _trackList.Invalidate(rc);
            }
            catch { }
        }

        private void RequestCover(int row)
        {
            if (row < 0 || row >= _tracks.Count) return;
            if (_rowToImage.ContainsKey(row)) return;

            long now = Environment.TickCount;
            long last;
            if (_coverRequests.TryGetValue(row, out last) && now - last < CoverRetryMs) return;
            _coverRequests[row] = now;

            Track track = _tracks[row];
            string coverUri = TrackCoverKey(track);
            if (string.IsNullOrEmpty(coverUri)) return;
            string trackId = track != null ? track.Id : null;

            // декод+скейл на фоновом потоке; на UI только запись в ImageList и перерисовка
            CoverLoader.BeginLoad(() => _coverLoader.Fetch(coverUri, "150x150"), 40, img =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (IsDisposed) return;
                    int idx;
                    if (_covers.Images.ContainsKey(coverUri))
                    {
                        idx = _covers.Images.IndexOfKey(coverUri);
                        img.Dispose();
                    }
                    else
                    {
                        _covers.Images.Add(coverUri, img);
                        idx = _covers.Images.IndexOfKey(coverUri);
                    }
                    // пока обложка грузилась, список могли перестроить и на этой строке
                    // теперь другой трек — картинку кэшируем, но к строке не привязываем,
                    // иначе после быстрого поиска обложки «плыли» по строкам
                    if (row < 0 || row >= _tracks.Count) return;
                    Track rowTrack = _tracks[row];
                    if (trackId == null || rowTrack == null ||
                        !string.Equals(rowTrack.Id, trackId, StringComparison.Ordinal))
                    {
                        _coverRequests.Remove(row);
                        return;
                    }
                    _rowToImage[row] = idx;
                    _coverRequests.Remove(row);
                    InvalidateTrackRow(row);
                }
                catch
                {
                    // битые изображения пропускаем (повторная попытка через CoverRetryMs)
                }
                finally
                {
                    sw.Stop();
                    if (!IsDisposed) Diag.UiProfile.Add("cover-apply", sw.ElapsedMilliseconds);
                }
            });
        }

        // ==================== контекстное меню трека ====================

        private void TrackListOnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ListViewItem item = _trackList.GetItemAt(e.X, e.Y);
            if (item == null) return;
            int row = item.Index;
            if (row < 0 || row >= _tracks.Count) return;
            Track track = _tracks[row];
            if (track == null) return;

            if (_trackList.SelectedIndices.Count != 1 || _trackList.SelectedIndices[0] != row)
            {
                _trackList.SelectedIndices.Clear();
                _trackList.SelectedIndices.Add(row);
            }
            ShowTrackContextMenu(track);
            _trackMenu.Show(_trackList, e.Location);
        }

        private void ShowTrackContextMenu(Track track)
        {
            _trackMenu.Items.Clear();

            if (track.Albums != null)
            {
                foreach (Album al in track.Albums)
                {
                    if (al == null || al.Id == 0) continue;
                    string label = "Открыть альбом" + (string.IsNullOrEmpty(al.Title) ? "" : ": " + al.Title);
                    long id = al.Id;
                    var item = new ToolStripMenuItem(label);
                    item.Click += (s, ea) => NavigateToAlbum(id);
                    _trackMenu.Items.Add(item);
                    break; // показываем один (основной) альбом
                }
            }

            if (track.Artists != null && track.Artists.Count > 0)
            {
                if (_trackMenu.Items.Count > 0) _trackMenu.Items.Add(new ToolStripSeparator());
                foreach (Artist ar in track.Artists)
                {
                    if (ar == null || ar.Id == 0 || ar.Various) continue;
                    string label = "Открыть исполнителя" + (string.IsNullOrEmpty(ar.Name) ? "" : ": " + ar.Name);
                    long id = ar.Id;
                    var item = new ToolStripMenuItem(label);
                    item.Click += (s, ea) => NavigateToArtist(id);
                    _trackMenu.Items.Add(item);
                }
            }

            if (_trackMenu.Items.Count > 0) _trackMenu.Items.Add(new ToolStripSeparator());

            var share = new ToolStripMenuItem("Поделиться");
            share.Click += (s, ea) => ShareTrack(track);
            _trackMenu.Items.Add(share);

            var radio = new ToolStripMenuItem("Моя волна по треку");
            radio.Click += (s, ea) => StartTrackRadio(track);
            _trackMenu.Items.Add(radio);
        }

        /// <summary>Копирует ссылку на трек в буфер обмена.</summary>
        private void ShareTrack(Track track)
        {
            if (track == null) return;
            long albumId = 0;
            if (track.Albums != null)
            {
                foreach (Album al in track.Albums)
                {
                    if (al != null && al.Id != 0) { albumId = al.Id; break; }
                }
            }
            string id = string.IsNullOrEmpty(track.Id) ? track.RealId : track.Id;
            if (string.IsNullOrEmpty(id))
            {
                SetStatus("Ссылку не удалось сформировать.");
                return;
            }
            string url = albumId != 0
                ? "https://music.yandex.ru/album/" + albumId + "/track/" + Uri.EscapeDataString(id)
                : "https://music.yandex.ru/track/" + Uri.EscapeDataString(id);
            try
            {
                Clipboard.SetText(url);
                SetStatus("Ссылка скопирована: " + url);
            }
            catch
            {
                SetStatus("Не удалось скопировать: " + url);
            }
        }
    }

    /// <summary>Тёмный рендер ContextMenuStrip (фон/хoвер/разделитель), чтобы меню не выпадало белым.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(YmPalette.BgSide))
            {
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(YmPalette.BgSide))
            {
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(YmPalette.Line))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (var b = new SolidBrush(e.Item.Selected ? YmPalette.BgHover : YmPalette.BgSide))
            {
                e.Graphics.FillRectangle(b, new Rectangle(0, 0, e.Item.Width, e.Item.Height));
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var b = new SolidBrush(YmPalette.BgSide))
            {
                e.Graphics.FillRectangle(b, 0, 0, e.Item.Width, e.Item.Height);
            }
            using (var b = new SolidBrush(YmPalette.Line))
            {
                e.Graphics.FillRectangle(b, 8, e.Item.Height / 2, Math.Max(0, e.Item.Width - 16), 1);
            }
        }
    }
}