using System;
using System.IO;
using System.Net;
using System.Reflection;

namespace Lumen.Net
{
    internal static class WebUi
    {
        private static readonly object Sync = new object();
        private static string _template;

        public static string Render(string userName, string computerName)
        {
            string template = GetTemplate();
            return template
                .Replace("{{USER}}", WebUtility.HtmlEncode(userName ?? ""))
                .Replace("{{COMPUTER}}", WebUtility.HtmlEncode(computerName ?? ""))
                .Replace("{{HOST}}", WebUtility.HtmlEncode(computerName ?? ""));
        }

        private static string GetTemplate()
        {
            lock (Sync)
            {
                if (_template != null) return _template;
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("WebUi.html"))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException("WebUi.html embedded resource is missing.");
                    }
                    using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8, true))
                    {
                        _template = reader.ReadToEnd();
                    }
                }
                return _template;
            }
        }
    }
}
