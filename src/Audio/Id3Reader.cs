using System;
using System.IO;
using System.Text;

namespace Lumen.Audio
{
    internal sealed class Id3Info
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public byte[] Picture;
    }

    internal static class Id3Reader
    {
        private const int TextReadLimit = 384 * 1024;
        private const int PictureReadLimit = 24 * 1024 * 1024;

        public static Id3Info ReadText(string path)
        {
            return Read(path, false);
        }

        public static byte[] ReadPicture(string path)
        {
            Id3Info info = Read(path, true);
            return info == null ? null : info.Picture;
        }

        private static Id3Info Read(string path, bool withPicture)
        {
            try
            {
                string extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < 10) return null;
                    byte[] header = new byte[10];
                    if (ReadFully(stream, header, 0, 10) != 10) return null;
                    if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

                    int version = header[3];
                    if (version != 2 && version != 3 && version != 4) return null;
                    int flags = header[5];
                    int tagSize = SyncSafe(header, 6);
                    long available = stream.Length - 10;
                    int limit = withPicture ? PictureReadLimit : TextReadLimit;
                    int bytesToRead = (int)Math.Min(Math.Min((long)tagSize, available), limit);
                    if (bytesToRead <= 0) return null;

                    byte[] data = new byte[bytesToRead];
                    int read = ReadFully(stream, data, 0, bytesToRead);
                    if (read < bytesToRead) Array.Resize(ref data, read);

                    return Parse(data, version, flags, withPicture);
                }
            }
            catch (Exception ex)
            {
                Log.Write("ID3 read failed: " + path, ex);
                return null;
            }
        }

        private static Id3Info Parse(byte[] data, int version, int flags, bool withPicture)
        {
            Id3Info info = new Id3Info();
            int position = 0;

            if ((flags & 0x40) != 0 && data.Length >= 4)
            {
                int extendedSize;
                if (version == 4)
                {
                    extendedSize = SyncSafe(data, 0);
                    position = extendedSize;
                }
                else
                {
                    extendedSize = ReadBigEndianInt32(data, 0);
                    position = 4 + extendedSize;
                }
            }

            int headerSize = version == 2 ? 6 : 10;
            while (position + headerSize <= data.Length)
            {
                string id;
                int frameSize;
                if (version == 2)
                {
                    id = Ascii(data, position, 3);
                    frameSize = (data[position + 3] << 16) | (data[position + 4] << 8) | data[position + 5];
                }
                else
                {
                    id = Ascii(data, position, 4);
                    if (version == 4)
                    {
                        frameSize = SyncSafe(data, position + 4);
                    }
                    else
                    {
                        frameSize = ReadBigEndianInt32(data, position + 4);
                    }
                }

                if (string.IsNullOrEmpty(id) || id[0] == '\0') break;
                if (frameSize <= 0 || frameSize > data.Length) break;
                int body = position + headerSize;
                if (body + frameSize > data.Length) break;

                if (id == "TIT2" || id == "TT2")
                {
                    info.Title = DecodeText(data, body, frameSize);
                }
                else if (id == "TPE1" || id == "TP1")
                {
                    info.Artist = DecodeText(data, body, frameSize);
                }
                else if (id == "TALB" || id == "TAL")
                {
                    info.Album = DecodeText(data, body, frameSize);
                }
                else if (withPicture && info.Picture == null && (id == "APIC" || id == "PIC"))
                {
                    byte[] picture;
                    if (version == 2)
                    {
                        picture = ParsePictureV22(data, body, frameSize);
                    }
                    else
                    {
                        picture = ParsePicture(data, body, frameSize);
                    }
                    if (picture != null && picture.Length > 64)
                    {
                        info.Picture = picture;
                    }
                }

                position = body + frameSize;
            }

            info.Title = Clean(info.Title);
            info.Artist = Clean(info.Artist);
            info.Album = Clean(info.Album);
            return info;
        }

        private static byte[] ParsePicture(byte[] data, int offset, int count)
        {
            if (count < 6) return null;
            int end = offset + count;
            int position = offset;
            int encoding = data[position++];

            while (position < end && data[position] != 0) position++;
            position++; // MIME terminator
            if (position >= end) return null;
            position++; // picture type

            if (encoding == 1 || encoding == 2)
            {
                while (position + 1 < end && !(data[position] == 0 && data[position + 1] == 0))
                {
                    position += 2;
                }
                position += 2;
            }
            else
            {
                while (position < end && data[position] != 0) position++;
                position++;
            }

            if (position >= end) return null;
            int length = end - position;
            if (length <= 0) return null;
            byte[] picture = new byte[length];
            Buffer.BlockCopy(data, position, picture, 0, length);
            return LooksLikeImage(picture) ? picture : null;
        }

        private static byte[] ParsePictureV22(byte[] data, int offset, int count)
        {
            if (count < 6) return null;
            int end = offset + count;
            int position = offset;
            int encoding = data[position++];
            position += 3; // image format, e.g. JPG
            if (position >= end) return null;
            position++; // picture type

            if (encoding == 1 || encoding == 2)
            {
                while (position + 1 < end && !(data[position] == 0 && data[position + 1] == 0))
                {
                    position += 2;
                }
                position += 2;
            }
            else
            {
                while (position < end && data[position] != 0) position++;
                position++;
            }

            if (position >= end) return null;
            int length = end - position;
            byte[] picture = new byte[length];
            Buffer.BlockCopy(data, position, picture, 0, length);
            return LooksLikeImage(picture) ? picture : null;
        }

        private static bool LooksLikeImage(byte[] data)
        {
            if (data.Length < 8) return false;
            if (data[0] == 0xFF && data[1] == 0xD8) return true;
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
            if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return true;
            if (data[0] == 'B' && data[1] == 'M') return true;
            return false;
        }

        private static string DecodeText(byte[] data, int offset, int count)
        {
            if (count <= 1) return "";
            int encoding = data[offset];
            int start = offset + 1;
            int length = count - 1;
            if (length <= 0) return "";

            Encoding textEncoding;
            if (encoding == 1)
            {
                if (length >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
                {
                    textEncoding = Encoding.BigEndianUnicode;
                    start += 2;
                    length -= 2;
                }
                else if (length >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
                {
                    textEncoding = Encoding.Unicode;
                    start += 2;
                    length -= 2;
                }
                else
                {
                    textEncoding = Encoding.Unicode;
                }
            }
            else if (encoding == 2)
            {
                textEncoding = Encoding.BigEndianUnicode;
            }
            else if (encoding == 3)
            {
                textEncoding = Encoding.UTF8;
            }
            else
            {
                textEncoding = Encoding.GetEncoding(28591);
            }

            string value = textEncoding.GetString(data, start, length);
            return value.Trim('\0', '\uFEFF', ' ', '\r', '\n', '\t');
        }

        private static string Clean(string value)
        {
            if (value == null) return "";
            int nullIndex = value.IndexOf('\0');
            if (nullIndex >= 0) value = value.Substring(0, nullIndex);
            return value.Trim();
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static int SyncSafe(byte[] data, int offset)
        {
            return ((data[offset] & 0x7F) << 21) |
                   ((data[offset + 1] & 0x7F) << 14) |
                   ((data[offset + 2] & 0x7F) << 7) |
                   (data[offset + 3] & 0x7F);
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3];
        }

        private static string Ascii(byte[] data, int offset, int count)
        {
            if (offset < 0 || offset + count > data.Length) return "";
            char[] chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                chars[i] = (char)data[offset + i];
            }
            return new string(chars);
        }
    }
}
