using System;
using System.Drawing;
using System.Windows.Forms;

namespace Lumen
{
    /// <summary>
    /// Tray icon with playback controls and the current track as a tooltip.
    /// Owns WinForms objects only; all callbacks are marshalled by the caller.
    /// </summary>
    internal sealed class TrayController : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _playItem;
        private readonly ToolStripMenuItem _statusItem;
        private bool _disposed;

        public TrayController(Icon icon, Action showWindow, Action togglePlay,
            Action next, Action previous, Action exit)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            _statusItem = new ToolStripMenuItem("Lumen");
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Открыть Lumen", null, delegate { showWindow(); });
            menu.Items.Add(new ToolStripSeparator());

            _playItem = new ToolStripMenuItem("Играть / Пауза", null, delegate { togglePlay(); });
            menu.Items.Add(_playItem);
            menu.Items.Add("Следующий трек", null, delegate { next(); });
            menu.Items.Add("Предыдущий трек", null, delegate { previous(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { exit(); });

            _icon = new NotifyIcon();
            _icon.Icon = icon;
            _icon.Text = "Lumen";
            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { showWindow(); };
        }

        public void Update(string title, string artist, bool playing)
        {
            if (_disposed) return;

            string status;
            if (string.IsNullOrWhiteSpace(title))
            {
                status = "Lumen — Aurora Music";
            }
            else if (string.IsNullOrWhiteSpace(artist))
            {
                status = title;
            }
            else
            {
                status = artist + " — " + title;
            }

            _icon.Text = Truncate(status, 63);
            _statusItem.Text = Truncate(status, 60);
            _playItem.Text = playing ? "Пауза" : "Играть";
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= limit) return value;
            return value.Substring(0, limit - 1) + "…";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
        }
    }
}
