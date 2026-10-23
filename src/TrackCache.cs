using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Lumen
{
    /// <summary>
    /// Caches ID3 metadata on disk so rescanning a large folder does not read
    /// every file again. Entries are invalidated by file size and modification
    /// time, so edited tags are picked up automatically.
    /// </summary>
    internal static class TrackCache
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, Entry> _entries;
        private static bool _dirty;
        private static string _cachePath;

        private sealed class Entry
        {
            public long Ticks;
            public long Size;
            public string Title;
            public string Artist;
            public string Album;
            public string Badge;
        }

        public static Track GetOrCreate(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;

            Entry cached = Lookup(file);
            if (cached != null)
            {
                Track restored = new Track();
                restored.Path = file;
                restored.Title = cached.Title;
                restored.Artist = cached.Artist;
                restored.Album = cached.Album;
                restored.Badge = cached.Badge;
                return restored;
            }

            Track track = Track.FromFile(file);
            if (track != null) Remember(track);
            return track;
        }

        private static Dictionary<string, Entry> Entries
        {
            get
            {
                lock (Sync)
                {
                    if (_entries == null) Load();
                    return _entries;
                }
            }
        }

        private static Entry Lookup(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) return null;

                Entry entry;
                if (!Entries.TryGetValue(path, out entry)) return null;
                if (entry.Ticks != info.LastWriteTimeUtc.Ticks) return null;
                if (entry.Size != info.Length) return null;
                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static void Remember(Track track)
        {
            try
            {
                FileInfo info = new FileInfo(track.Path);
                if (!info.Exists) return;

                Entry entry = new Entry();
                entry.Ticks = info.LastWriteTimeUtc.Ticks;
                entry.Size = info.Length;
                entry.Title = track.Title ?? "";
                entry.Artist = track.Artist ?? "";
                entry.Album = track.Album ?? "";
                entry.Badge = track.Badge ?? "";

                lock (Sync)
                {
                    Entries[track.Path] = entry;
                    _dirty = true;
                }
            }
            catch { }
        }

        public static void Save()
        {
            string payload;
            lock (Sync)
            {
                if (!_dirty || _entries == null) return;
                StringBuilder builder = new StringBuilder();
                foreach (KeyValuePair<string, Entry> pair in _entries)
                {
                    builder.Append(Escape(pair.Key)).Append('\t');
                    builder.Append(pair.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    builder.Append(pair.Value.Size.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    builder.Append(Escape(pair.Value.Title)).Append('\t');
                    builder.Append(Escape(pair.Value.Artist)).Append('\t');
                    builder.Append(Escape(pair.Value.Album)).Append('\t');
                    builder.Append(Escape(pair.Value.Badge)).Append('\n');
                }
                payload = builder.ToString();
                _dirty = false;
            }

            try
            {
                string path = CachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, payload, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("Track cache save failed", ex);
            }
        }

        private static void Load()
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length < 7) continue;

                    long ticks;
                    long size;
                    if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) continue;

                    Entry entry = new Entry();
                    entry.Ticks = ticks;
                    entry.Size = size;
                    entry.Title = parts[3];
                    entry.Artist = parts[4];
                    entry.Album = parts[5];
                    entry.Badge = parts[6];
                    _entries[parts[0]] = entry;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Track cache load failed", ex);
                _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string CachePath
        {
            get
            {
                if (_cachePath != null) return _cachePath;
                try
                {
                    string directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Lumen");
                    Directory.CreateDirectory(directory);
                    _cachePath = Path.Combine(directory, "tracks-cache.tsv");
                }
                catch
                {
                    _cachePath = Path.Combine(Path.GetTempPath(), "lumen-tracks-cache.tsv");
                }
                return _cachePath;
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
