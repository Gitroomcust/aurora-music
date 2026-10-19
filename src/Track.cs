using System;
using System.IO;
using Lumen.Audio;

namespace Lumen
{
    public sealed class Track
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string Badge { get; set; }

        public static Track FromFile(string file)
        {
            Track track = new Track();
            track.Path = file;

            string extension = System.IO.Path.GetExtension(file);
            track.Badge = string.IsNullOrEmpty(extension)
                ? "AUDIO"
                : extension.TrimStart('.').ToUpperInvariant();

            string fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            if (fileName == null) fileName = "";
            fileName = fileName.Replace('_', ' ').Trim();

            string title = "";
            string artist = "";
            Id3Info info = Id3Reader.ReadText(file);
            if (info != null)
            {
                title = info.Title;
                artist = info.Artist;
                track.Album = info.Album;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                int dash = fileName.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = fileName.Substring(0, dash).Trim();
                    }
                    title = fileName.Substring(dash + 3).Trim();
                }
                else
                {
                    title = fileName;
                }
            }

            track.Title = string.IsNullOrWhiteSpace(title) ? "Без названия" : title.Trim();
            track.Artist = string.IsNullOrWhiteSpace(artist) ? "Неизвестный исполнитель" : artist.Trim();
            return track;
        }
    }
}

