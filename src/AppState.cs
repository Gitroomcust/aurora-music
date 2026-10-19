using System;
using System.Globalization;
using Lumen.Audio;
using Lumen.Net;

namespace Lumen
{
    internal sealed class AppState
    {
        public readonly string UserName;
        public readonly string ComputerName;
        public readonly string UserInitial;
        public readonly AppSettings Settings;
        public readonly LiveAudioHub LiveAudio;
        public readonly LanServer Server;
        public readonly string StartupView;
        public readonly string StartupFolder;
        public Action<string> Notify;

        public AppState(string[] args)
        {
            UserName = NormalizeUserName(Environment.UserName);
            if (string.IsNullOrWhiteSpace(UserName)) UserName = "друг";
            ComputerName = string.IsNullOrWhiteSpace(Environment.MachineName)
                ? "этот компьютер"
                : Environment.MachineName;

            string initial = UserName.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture);
            UserInitial = initial;

            Settings = AppSettings.Load();
            LiveAudio = new LiveAudioHub();
            Server = new LanServer(LiveAudio, UserName, ComputerName);
            StartupView = ParseStartupView(args);
            StartupFolder = ParseStartupFolder(args);
        }

        public string DisplayUserName
        {
            get { return Capitalize(UserName); }
        }

        public string Greeting
        {
            get
            {
                int hour = DateTime.Now.Hour;
                if (hour >= 5 && hour < 12) return "Доброе утро,";
                if (hour >= 12 && hour < 18) return "Добрый день,";
                if (hour >= 18 && hour < 23) return "Добрый вечер,";
                return "Доброй ночи,";
            }
        }

        public string StreamUrl
        {
            get
            {
                if (Server == null) return "";
                if (!string.IsNullOrEmpty(Server.LanUrl)) return Server.LanUrl;
                return Server.LocalUrl ?? "";
            }
        }

        public void ShowMessage(string message)
        {
            Action<string> handler = Notify;
            if (handler != null) handler(message);
        }

        private static string ParseStartupView(string[] args)
        {
            if (args == null) return "welcome";
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--view", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                {
                    string value = args[i + 1].ToLowerInvariant();
                    if (value == "local" || value == "capture" || value == "welcome")
                    {
                        return value;
                    }
                }
            }
            return "welcome";
        }

        private static string ParseStartupFolder(string[] args)
        {
            if (args == null) return "";
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--folder", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
            return "";
        }

        private static string NormalizeUserName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            if (value.IndexOf('\\') >= 0)
            {
                value = value.Substring(value.LastIndexOf('\\') + 1);
            }
            if (value.IndexOf('@') >= 0)
            {
                value = value.Substring(0, value.IndexOf('@'));
            }
            return value;
        }

        private static string Capitalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length == 1) return value.ToUpper(CultureInfo.CurrentCulture);
            return value.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture) + value.Substring(1);
        }
    }
}
