using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RoundSoundMimic.Services
{
    public interface ITrayIconService
    {
        void InitializeTrayIcon(ImageSource iconSource);
        void UpdateTrayIcon(ImageSource source);
        void ShowTrayBalloon(string title, string artists);
        void UpdateTrayNowPlayingText(string title, string artists);
        void ShowFromTray();
        void Dispose();
    }

    public class TrayIconService : ITrayIconService
    {
        private Forms.NotifyIcon? _trayIcon;
        private Drawing.Icon? _trayIconImage;
        private bool _trayBalloonShown;

        public void InitializeTrayIcon(ImageSource iconSource)
        {
            _trayIconImage = CreateTrayIcon(iconSource);
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "RoundSound Mimic",
                Visible = true
            };
            _trayIcon.BalloonTipTitle = "RoundSound Mimic";

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowFromTray());
            menu.Items.Add("Exit", null, (_, _) => { Application.Current.Shutdown(); });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        }

        private static Drawing.Icon? CreateTrayIcon(ImageSource source)
        {
            var size = 64;
            var drawingVisual = new DrawingVisual();
            using (var context = drawingVisual.RenderOpen())
            {
                context.DrawRectangle(new ImageBrush(source) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, size, size));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawingVisual);
            bitmap.Freeze();

            using var stream = new System.IO.MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            stream.Position = 0;

            using var gdiBitmap = new Drawing.Bitmap(stream);
            var iconHandle = gdiBitmap.GetHicon();
            var icon = (Drawing.Icon)Drawing.Icon.FromHandle(iconHandle).Clone();
            DestroyIcon(iconHandle);
            return icon;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void UpdateTrayIcon(ImageSource source)
        {
            if (_trayIcon is null)
            {
                return;
            }

            _trayIconImage?.Dispose();
            _trayIconImage = CreateTrayIcon(source);
            if (_trayIconImage is not null)
            {
                _trayIcon.Icon = _trayIconImage;
            }
        }

        public void ShowTrayBalloon(string title, string artists)
        {
            if (_trayIcon is null)
            {
                return;
            }

            var text = string.IsNullOrWhiteSpace(artists)
                ? title
                : $"{title}\n{artists}";

            _trayIcon.BalloonTipText = text;
            _trayIcon.ShowBalloonTip(_trayBalloonShown ? 1500 : 3000);
            _trayBalloonShown = true;
        }

        public void UpdateTrayNowPlayingText(string title, string artists)
        {
            if (_trayIcon is null)
            {
                return;
            }

            var display = string.IsNullOrWhiteSpace(artists) ? title : $"{title} - {artists}";
            var hint = string.IsNullOrWhiteSpace(display) ? "RoundSound Mimic" : $"Now Playing: {display}";
            _trayIcon.Text = TruncateTrayText(hint);
            try
            {
                _trayIcon.BalloonTipTitle = "Now Playing";
                _trayIcon.BalloonTipText = display;
            }
            catch { }
        }

        private static string TruncateTrayText(string text)
        {
            const int maxLength = 63;
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength - 1) + "…";
        }

        public void ShowFromTray()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = Application.Current.MainWindow;
                if (window != null)
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                }
            });
        }

        public void Dispose()
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _trayIconImage?.Dispose();
            _trayIconImage = null;
        }
    }
}