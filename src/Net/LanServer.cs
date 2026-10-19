using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Lumen.Audio;

namespace Lumen.Net
{
    internal sealed class LanServer : IDisposable
    {
        private readonly LiveAudioHub _hub;
        private readonly string _userName;
        private readonly string _computerName;
        private readonly object _trackSync = new object();
        private readonly object _lifecycleSync = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private List<Track> _tracks = new List<Track>();

        public int Port { get; private set; }
        public string LanUrl { get; private set; }
        public string LocalUrl { get; private set; }
        public volatile bool IsLive;
        public double LiveSeconds;

        public LanServer(LiveAudioHub hub, string userName, string computerName)
        {
            _hub = hub;
            _userName = userName;
            _computerName = computerName;
            LanUrl = "";
            LocalUrl = "";
        }

        public string UserName
        {
            get { return _userName; }
        }

        public string ComputerName
        {
            get { return _computerName; }
        }

        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_running) return;

                IPAddress address = FindLanAddress();
                Exception lastError = null;
                for (int port = 8737; port < 8758; port++)
                {
                    try
                    {
                        TcpListener listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                        _listener = listener;
                        Port = port;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                if (_listener == null)
                {
                    throw new InvalidOperationException("Не удалось занять сетевой порт.", lastError);
                }

                LanUrl = "http://" + FormatAddress(address) + ":" + Port + "/";
                LocalUrl = "http://127.0.0.1:" + Port + "/";
                _running = true;
                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "Lumen LAN server";
                _acceptThread.Start();
            }
        }

        public void SetTracks(IList<Track> tracks)
        {
            List<Track> copy = new List<Track>();
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i] != null) copy.Add(tracks[i]);
                }
            }
            lock (_trackSync)
            {
                _tracks = copy;
            }
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                _running = false;
                if (_listener != null)
                {
                    try { _listener.Stop(); }
                    catch { }
                    _listener = null;
                }
            }
            try
            {
                if (_acceptThread != null) _acceptThread.Join(700);
            }
            catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Write("LAN accept failed", ex);
                    Thread.Sleep(120);
                    continue;
                }

                Thread thread = new Thread(ClientThread);
                thread.IsBackground = true;
                thread.Name = "Lumen HTTP client";
                thread.Start(client);
            }
        }

        private void ClientThread(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 12000;
                client.SendTimeout = 18000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine)) return;

                    string[] parts = requestLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad request", false);
                        return;
                    }

                    string method = parts[0].ToUpperInvariant();
                    string target = parts[1];
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Uri uri;
                        if (Uri.TryCreate(target, UriKind.Absolute, out uri))
                        {
                            target = uri.PathAndQuery;
                        }
                    }

                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string line;
                    while ((line = reader.ReadLine()) != null && line.Length > 0)
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                        }
                    }

                    string path = target;
                    int query = path.IndexOf('?');
                    if (query >= 0) path = path.Substring(0, query);

                    bool head = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !head)
                    {
                        WriteText(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                            "Only GET and HEAD are supported.", head);
                        return;
                    }

                    if (path == "/" || path == "/index.html")
                    {
                        string html = WebUi.Render(_userName, _computerName);
                        WriteText(stream, 200, "OK", "text/html; charset=utf-8", html, head);
                    }
                    else if (path == "/api/state")
                    {
                        WriteText(stream, 200, "OK", "application/json; charset=utf-8",
                            BuildStateJson(), head);
                    }
                    else if (path == "/api/tracks")
                    {
                        WriteText(stream, 200, "OK", "application/json; charset=utf-8",
                            BuildTracksJson(), head);
                    }
                    else if (path.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        ServeAudio(stream, headers, path.Substring(7), head);
                    }
                    else if (path == "/live.wav")
                    {
                        ServeLive(stream, client, head);
                    }
                    else if (path == "/health")
                    {
                        WriteText(stream, 200, "OK", "text/plain; charset=utf-8", "ok", head);
                    }
                    else if (path == "/favicon.ico")
                    {
                        WriteText(stream, 204, "No Content", "image/x-icon", "", head);
                    }
                    else
                    {
                        WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8",
                            "Not found", head);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HTTP client failed", ex);
                try { client.Close(); }
                catch { }
            }
        }

        private void ServeAudio(NetworkStream stream, Dictionary<string, string> headers,
            string indexText, bool head)
        {
            int index;
            if (!int.TryParse(Uri.UnescapeDataString(indexText), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out index))
            {
                WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad track index", head);
                return;
            }

            Track track = null;
            lock (_trackSync)
            {
                if (index >= 0 && index < _tracks.Count) track = _tracks[index];
            }

            if (track == null || !File.Exists(track.Path))
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Track not found", head);
                return;
            }

            long length;
            try
            {
                length = new FileInfo(track.Path).Length;
            }
            catch
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Track not found", head);
                return;
            }
            if (length <= 0)
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Empty track", head);
                return;
            }

            long start = 0;
            long end = length - 1;
            bool partial = false;
            string range;
            if (headers.TryGetValue("Range", out range) && !string.IsNullOrWhiteSpace(range))
            {
                if (!TryParseRange(range, length, out start, out end))
                {
                    WriteHeaders(stream, 416, "Range Not Satisfiable",
                        "text/plain; charset=utf-8", 0,
                        "bytes */" + length.ToString(CultureInfo.InvariantCulture), null, head);
                    return;
                }
                partial = true;
            }

            long contentLength = end - start + 1;
            string contentType = MimeFor(track.Path);
            string contentRange = partial
                ? "bytes " + start.ToString(CultureInfo.InvariantCulture) + "-" +
                  end.ToString(CultureInfo.InvariantCulture) + "/" +
                  length.ToString(CultureInfo.InvariantCulture)
                : null;

            WriteHeaders(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK",
                contentType, contentLength, contentRange, "bytes", head);
            if (head) return;

            try
            {
                using (FileStream file = new FileStream(track.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
                {
                    file.Seek(start, SeekOrigin.Begin);
                    byte[] buffer = new byte[65536];
                    long remaining = contentLength;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int read = file.Read(buffer, 0, want);
                        if (read <= 0) break;
                        stream.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
            catch (IOException)
            {
                // The browser closed the connection or seeked elsewhere.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ServeLive(NetworkStream stream, TcpClient client, bool head)
        {
            if (!IsLive || !_hub.IsConfigured)
            {
                WriteText(stream, 503, "Service Unavailable", "text/plain; charset=utf-8",
                    "Live stream is not active. Start capture in Lumen.", head);
                return;
            }

            if (head)
            {
                WriteText(stream, 200, "OK", "audio/wav", "", true);
                return;
            }

            string headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: audio/wav\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                "Pragma: no-cache\r\n" +
                "Connection: close\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();

            LiveAudioHub.Subscriber subscriber = null;
            try
            {
                byte[] wavHeader = _hub.CreateWavHeader();
                WriteChunk(stream, wavHeader, wavHeader.Length);
                subscriber = _hub.Subscribe();

                while (true)
                {
                    byte[] data;
                    if (subscriber.TryTake(out data, 900))
                    {
                        if (data != null && data.Length > 0)
                        {
                            WriteChunk(stream, data, data.Length);
                        }
                        continue;
                    }

                    if (!IsLive || !_hub.IsConfigured) break;
                    if (!ClientStillConnected(client)) break;
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.Write("Live stream failed", ex);
            }
            finally
            {
                if (subscriber != null) _hub.Unsubscribe(subscriber);
                try
                {
                    byte[] terminator = Encoding.ASCII.GetBytes("0\r\n\r\n");
                    stream.Write(terminator, 0, terminator.Length);
                    stream.Flush();
                }
                catch { }
            }
        }

        private static bool ClientStillConnected(TcpClient client)
        {
            try
            {
                Socket socket = client.Client;
                if (socket == null || !socket.Connected) return false;
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteChunk(NetworkStream stream, byte[] data, int count)
        {
            if (count <= 0) return;
            string size = count.ToString("X", CultureInfo.InvariantCulture) + "\r\n";
            byte[] sizeBytes = Encoding.ASCII.GetBytes(size);
            stream.Write(sizeBytes, 0, sizeBytes.Length);
            stream.Write(data, 0, count);
            byte[] end = new byte[] { 13, 10 };
            stream.Write(end, 0, 2);
            stream.Flush();
        }

        private static void WriteText(NetworkStream stream, int code, string status,
            string contentType, string body, bool head)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            WriteHeaders(stream, code, status, contentType, bytes.Length, null, null, head);
            if (head) return;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void WriteHeaders(NetworkStream stream, int code, string status,
            string contentType, long contentLength, string contentRange, string acceptRanges, bool head)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
            builder.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrEmpty(contentRange))
            {
                builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(acceptRanges))
            {
                builder.Append("Accept-Ranges: ").Append(acceptRanges).Append("\r\n");
            }
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();
        }

        private static bool TryParseRange(string value, long length, out long start, out long end)
        {
            start = 0;
            end = length - 1;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string spec = value.Substring(6).Trim();
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string startText = spec.Substring(0, dash).Trim();
            string endText = spec.Substring(dash + 1).Trim();
            if (startText.Length == 0)
            {
                long suffix;
                if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out suffix) ||
                    suffix <= 0) return false;
                if (suffix > length) suffix = length;
                start = length - suffix;
                end = length - 1;
                return true;
            }

            if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                start < 0 || start >= length) return false;

            if (endText.Length == 0)
            {
                end = length - 1;
                return true;
            }

            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) ||
                end < start) return false;
            if (end >= length) end = length - 1;
            return true;
        }

        private string BuildStateJson()
        {
            int count;
            lock (_trackSync)
            {
                count = _tracks.Count;
            }
            bool live = IsLive && _hub.IsConfigured;
            string seconds = (live ? Math.Max(0, LiveSeconds) : 0).ToString("0.0", CultureInfo.InvariantCulture);
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"user\":\"").Append(Json(_userName)).Append("\",");
            builder.Append("\"computer\":\"").Append(Json(_computerName)).Append("\",");
            builder.Append("\"port\":").Append(Port).Append(',');
            builder.Append("\"trackCount\":").Append(count).Append(',');
            builder.Append("\"live\":").Append(live ? "true" : "false").Append(',');
            builder.Append("\"seconds\":").Append(seconds).Append(',');
            builder.Append("\"subscribers\":").Append(_hub.SubscriberCount).Append('}');
            return builder.ToString();
        }

        private string BuildTracksJson()
        {
            List<Track> snapshot;
            lock (_trackSync)
            {
                snapshot = new List<Track>(_tracks);
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{\"tracks\":[");
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0) builder.Append(',');
                Track track = snapshot[i];
                builder.Append("{\"title\":\"").Append(Json(track.Title)).Append("\",");
                builder.Append("\"artist\":\"").Append(Json(track.Artist)).Append("\",");
                builder.Append("\"ext\":\"").Append(Json(track.Badge)).Append("\",");
                builder.Append("\"album\":\"").Append(Json(track.Album)).Append("\"}");
            }
            builder.Append("]}");
            return builder.ToString();
        }

        private static string Json(string value)
        {
            if (value == null) return "";
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        private static string MimeFor(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension == null) return "application/octet-stream";
            switch (extension.ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".flac": return "audio/flac";
                case ".m4a":
                case ".aac": return "audio/mp4";
                case ".ogg":
                case ".opus": return "audio/ogg";
                case ".wma": return "audio/x-ms-wma";
                case ".aiff":
                case ".aif": return "audio/aiff";
                default: return "application/octet-stream";
            }
        }

        private static IPAddress FindLanAddress()
        {
            IPAddress best = null;
            int bestScore = -1;
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    NetworkInterface ni = interfaces[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties properties;
                    try { properties = ni.GetIPProperties(); }
                    catch { continue; }

                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        IPAddress address = info.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(address)) continue;
                        byte[] bytes = address.GetAddressBytes();
                        if (bytes[0] == 169 && bytes[1] == 254) continue;

                        int score = 0;
                        if (bytes[0] == 192 && bytes[1] == 168) score += 6;
                        else if (bytes[0] == 10) score += 5;
                        else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) score += 4;
                        else score += 1;

                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 2;
                        else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 3;
                        else score -= 1;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = address;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("LAN address detection failed", ex);
            }

            if (best != null) return best;
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addresses[i]))
                    {
                        return addresses[i];
                    }
                }
            }
            catch { }
            return IPAddress.Loopback;
        }

        private static string FormatAddress(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return "[" + address.ToString() + "]";
            }
            return address.ToString();
        }
    }
}
