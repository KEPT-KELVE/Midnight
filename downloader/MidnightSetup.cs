// midnightOS Setup for Windows: downloads the midnightOS ISO and writes it to a USB stick.
//
//   1. You choose a folder.  2. It downloads the ISO's three parts at the same time (in the background:
//   minimise it to the tray), joins them into the ISO and checks the SHA-256.  3. It asks whether to
//   flash it now: Flash to USB asks for administrator rights, erases the stick you pick, writes the ISO
//   and checks it.
//
// "--render FOLDER" saves a picture of each page (for checking the layout) and exits.
//
// Build (no Visual Studio needed; uses the C# compiler that ships with Windows):
//   website\downloader\build.cmd [manifest URL]
//
// The release description (release.json) comes from MANIFEST_URL, or from "--manifest URL", or from a
// file "manifest-url.txt" next to the program.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("midnightOS Setup")]
[assembly: System.Reflection.AssemblyProduct("midnightOS")]
[assembly: System.Reflection.AssemblyVersion("0.9.0.0")]

namespace Midnight
{
    static class Config
    {
        public const string MANIFEST_URL = "@MANIFEST_URL@";
    }

    public class Part { public string url { get; set; } public long size { get; set; } public string sha256 { get; set; } }
    public class Release
    {
        public string name { get; set; }
        public string version { get; set; }
        public long size { get; set; }
        public string sha256 { get; set; }
        public List<Part> parts { get; set; }
        public string source;          // where release.json came from: part addresses may be relative to it
    }

    // The download itself (used by the window, and by "--test-download URL FOLDER" without a window).
    //
    // The ISO is published in parts (three, about 1 GB each). All parts download at the same time, each into
    // its own file next to the ISO ("NAME.part1" ...), so a stopped download continues where each part was.
    // A part with a published SHA-256 is checked as soon as it's complete and fetched again if it's damaged.
    // Then the parts are joined into the ISO (part 1 is renamed and the others appended, so only the ISO's
    // size plus one part is ever needed on disk) and the whole ISO's SHA-256 is checked on the way.
    static class Downloader
    {
        public static Release Fetch(string manifestUrl)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            ServicePointManager.DefaultConnectionLimit = 16;
            string json;
            if (File.Exists(manifestUrl)) json = File.ReadAllText(manifestUrl, Encoding.UTF8);
            else using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "midnightOS-Setup"; wc.Encoding = Encoding.UTF8; json = wc.DownloadString(manifestUrl); }
            var release = new JavaScriptSerializer().Deserialize<Release>(json);
            if (release == null || release.parts == null || release.parts.Count == 0 || string.IsNullOrEmpty(release.name)) throw new Exception("The release information is incomplete.");
            if (release.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("The release has an unusable file name.");
            long sum = 0;
            foreach (var p in release.parts) sum += p.size;
            if (sum != release.size) throw new Exception("The release information doesn't add up (the parts aren't the size of the ISO).");
            release.source = File.Exists(manifestUrl) ? new Uri(Path.GetFullPath(manifestUrl)).AbsoluteUri : manifestUrl;
            return release;
        }

        static string Address(Release release, Part part)
        {
            Uri absolute;
            if (Uri.TryCreate(part.url, UriKind.Absolute, out absolute)) return part.url;
            return new Uri(new Uri(release.source), part.url).AbsoluteUri;     // "name.iso.part1" next to release.json
        }

        public static string PartFile(string target, int index) { return target + ".part" + (index + 1); }

        // progress(bytesDone, partsFinished, bytesPerSecond); joining(fraction); returns the finished ISO's path.
        public static string Download(Release release, string folder, Action<long, int, double> progress, Action<double> joining, Func<bool> cancelled)
        {
            string target = Path.Combine(folder, release.name);
            int count = release.parts.Count;
            var done = new long[count];
            var finished = new bool[count];
            long already = 0, biggest = 0;
            for (int p = 0; p < count; p++)
            {
                biggest = Math.Max(biggest, release.parts[p].size);
                string file = PartFile(target, p);
                if (File.Exists(file) && new FileInfo(file).Length == release.parts[p].size) { finished[p] = true; done[p] = release.parts[p].size; }
                else if (File.Exists(file + ".download")) done[p] = Math.Min(new FileInfo(file + ".download").Length, release.parts[p].size);
                already += done[p];
            }
            if (File.Exists(target + ".download")) File.Delete(target + ".download");   // a join that was interrupted
            long need = release.size - already + biggest + (64L << 20);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace;
            if (free < need) throw new IOException("There isn't enough free space in that folder: midnightOS needs " + MainForm.Size(need) + " free and there's " + MainForm.Size(free) + ".");

            Exception failure = null;
            bool stop = false;
            var threads = new List<Thread>();
            for (int p = 0; p < count; p++)
            {
                if (finished[p]) continue;
                int index = p;
                var thread = new Thread(delegate()
                {
                    try { FetchPart(release, index, PartFile(target, index), delegate(long d) { Interlocked.Exchange(ref done[index], d); }, delegate { return stop; }); finished[index] = true; }
                    catch (Exception ex) { lock (threads) { if (failure == null) failure = ex; } stop = true; }
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }
            var clock = Stopwatch.StartNew();
            while (true)
            {
                bool running = false;
                foreach (var t in threads) if (t.IsAlive) running = true;
                long sum = 0; int complete = 0;
                for (int p = 0; p < count; p++) { sum += Interlocked.Read(ref done[p]); if (finished[p]) complete++; }
                progress(sum, complete, (sum - already) / Math.Max(0.5, clock.Elapsed.TotalSeconds));
                if (!running) break;
                if (cancelled()) stop = true;
                Thread.Sleep(200);
            }
            if (cancelled()) throw new OperationCanceledException();
            if (failure != null) throw failure;

            // Join: part 1 becomes the ISO, the others are appended (and removed) one by one.
            string temp = target + ".download";
            File.Move(PartFile(target, 0), temp);
            string hash;
            using (var sha = SHA256.Create())
            using (var output = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                var buffer = new byte[4 << 20];
                long seen = 0; int n;
                while ((n = output.Read(buffer, 0, buffer.Length)) > 0) { sha.TransformBlock(buffer, 0, n, null, 0); seen += n; joining((double)seen / release.size); }
                for (int p = 1; p < count; p++)
                {
                    string file = PartFile(target, p);
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancelled()) throw new OperationCanceledException();
                            sha.TransformBlock(buffer, 0, n, null, 0); output.Write(buffer, 0, n); seen += n; joining((double)seen / release.size);
                        }
                    File.Delete(file);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                hash = Hex(sha.Hash);
            }
            if (new FileInfo(temp).Length != release.size) { File.Delete(temp); throw new IOException("The joined file has the wrong size. Please download it again."); }
            if (!string.IsNullOrEmpty(release.sha256) && !hash.Equals(release.sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new Exception("The downloaded file is damaged (its checksum doesn't match). Please download it again.");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            return target;
        }

        // One part, with up to four tries; resumes from what's already in FILE.download.
        static void FetchPart(Release release, int index, string file, Action<long> report, Func<bool> stop)
        {
            Part part = release.parts[index];
            string temp = file + ".download";
            var buffer = new byte[1 << 20];
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    long have = File.Exists(temp) ? new FileInfo(temp).Length : 0;
                    if (have > part.size) { File.Delete(temp); have = 0; }
                    report(have);
                    var address = new Uri(Address(release, part));
                    if (have < part.size && address.IsFile)
                    {   // parts in a folder (a USB drive, a network share): copy from where we stopped
                        using (var input = new FileStream(address.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                        using (var output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                        {
                            input.Seek(have, SeekOrigin.Begin);
                            output.SetLength(have);
                            output.Seek(have, SeekOrigin.Begin);
                            int read;
                            while (have < part.size && (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, part.size - have))) > 0)
                            {
                                if (stop()) throw new OperationCanceledException();
                                output.Write(buffer, 0, read);
                                have += read;
                                report(have);
                            }
                        }
                    }
                    else if (have < part.size)
                    {
                        var req = (HttpWebRequest)WebRequest.Create(address);
                        req.UserAgent = "midnightOS-Setup";
                        req.AllowAutoRedirect = true;
                        req.Timeout = 30000; req.ReadWriteTimeout = 60000;
                        if (have > 0) req.AddRange(have);
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var input = resp.GetResponseStream())
                        using (var output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                        {
                            if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0;   // the server sent it all again
                            output.SetLength(have);
                            output.Seek(have, SeekOrigin.Begin);
                            int read;
                            while (have < part.size && (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, part.size - have))) > 0)
                            {
                                if (stop()) throw new OperationCanceledException();
                                output.Write(buffer, 0, read);
                                have += read;
                                report(have);
                            }
                        }
                    }
                    if (have != part.size) throw new IOException("Part " + (index + 1) + " ended early.");
                    if (!string.IsNullOrEmpty(part.sha256))
                    {
                        string sum;
                        using (var sha = SHA256.Create()) using (var f = File.OpenRead(temp)) sum = Hex(sha.ComputeHash(f));
                        if (!sum.Equals(part.sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(temp);
                            report(0);
                            throw new IOException("Part " + (index + 1) + " arrived damaged.");
                        }
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(temp, file);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (attempt >= 4 || stop()) throw;
                    Thread.Sleep(2000 * attempt);
                }
            }
        }

        public static string Hex(byte[] hash)
        {
            var sb = new StringBuilder();
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(250, 250, 253);
        public static readonly Color Card = Color.FromArgb(240, 239, 246);
        public static readonly Color Line = Color.FromArgb(214, 212, 226);
        public static readonly Color Text = Color.FromArgb(27, 26, 34);
        public static readonly Color Muted = Color.FromArgb(96, 94, 114);
        public static readonly Color Accent = Color.FromArgb(36, 0, 64);              // #240040, midnight purple
        public static readonly Color OnAccent = Color.White;
        public static readonly Color Danger = Color.FromArgb(196, 43, 28);
        public static Font Font(float size, FontStyle style) { return new Font("Segoe UI", size, style); }
    }

    class FlatButton : Button
    {
        public FlatButton(string text, bool primary)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = primary ? 0 : 1;
            FlatAppearance.BorderColor = Theme.Line;
            BackColor = primary ? Theme.Accent : Theme.Card;
            ForeColor = primary ? Theme.OnAccent : Theme.Text;
            Font = Theme.Font(10f, FontStyle.Bold);
            Height = 38;
            Width = 150;
            Cursor = Cursors.Hand;
            Color normal = BackColor;
            Color ink = ForeColor;
            EnabledChanged += delegate { BackColor = Enabled ? normal : Theme.Card; ForeColor = Enabled ? ink : Theme.Muted; Cursor = Enabled ? Cursors.Hand : Cursors.Default; };
        }
    }

    class SlimBar : Control
    {
        double value;
        public SlimBar() { Height = 6; DoubleBuffered = true; }
        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Line)) e.Graphics.FillRectangle(b, 0, 0, Width, Height);
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Height), Color.FromArgb(92, 33, 143), Theme.Accent, 0f))
                e.Graphics.FillRectangle(b, 0, 0, (int)(Width * value), Height);
        }
    }

    class MainForm : Form
    {
        readonly string[] args;
        Panel page;
        Image logo;
        Release release;
        string isoPath;
        Thread worker;
        volatile bool cancel;

        public MainForm(string[] args)
        {
            this.args = args;
            Text = "midnightOS Setup";
            ClientSize = new Size(680, 460);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(10f, FontStyle.Regular);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var stream = typeof(MainForm).Assembly.GetManifestResourceStream("logo.png");
            if (stream != null) logo = Image.FromStream(stream);
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (worker != null && worker.IsAlive)
                {
                    if (MessageBox.Show(this, "Stop and close midnightOS Setup?", "midnightOS Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
                    cancel = true;
                }
            };
            SetUpTray();
            int flash = Array.IndexOf(args, "--flash");
            if (flash >= 0 && flash + 1 < args.Length) { isoPath = args[flash + 1]; ShowFlash(); }
            else ShowWelcome();
        }

        // ---------- layout helpers ----------
        Panel NewPage()
        {
            if (page != null) { Controls.Remove(page); page.Dispose(); }
            page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Padding = new Padding(36, 28, 36, 28) };
            Controls.Add(page);
            if (logo != null)
                page.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(36, 28) });
            var brand = new Label { Text = "midnightOS", Font = Theme.Font(10.5f, FontStyle.Bold), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(88, 40) };
            page.Controls.Add(brand);
            return page;
        }
        Label Title(string text, int y)
        {
            var l = new Label { Text = text, Font = Theme.Font(20f, FontStyle.Regular), ForeColor = Theme.Text, AutoSize = false, Size = new Size(608, 44), Location = new Point(36, y) };
            page.Controls.Add(l); return l;
        }
        Label Body(string text, int y, int height)
        {
            var l = new Label { Text = text, Font = Theme.Font(10.5f, FontStyle.Regular), ForeColor = Theme.Muted, AutoSize = false, Size = new Size(608, height), Location = new Point(36, y) };
            page.Controls.Add(l); return l;
        }
        FlatButton Btn(string text, bool primary, int x, EventHandler click)
        {
            var b = new FlatButton(text, primary) { Location = new Point(x, 390) };
            b.Click += click; page.Controls.Add(b); return b;
        }
        void Ui(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

        // ---------- 1. where to save ----------
        void ShowWelcome()
        {
            NewPage();
            Title("Get midnightOS Consumer Preview", 96);
            Body("Choose where to save midnightOS. It arrives in 3 parts that download at the same time and are then joined into one disc image (ISO), about 3 GB. Afterwards Setup writes it to a USB stick for you.", 146, 66);
            var where = new TextBox { Location = new Point(36, 222), Size = new Size(470, 30), Font = Theme.Font(11f, FontStyle.Regular), BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            int given = Array.IndexOf(args, "--folder");
            where.Text = given >= 0 && given + 1 < args.Length ? args[given + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            page.Controls.Add(where);
            var browse = new FlatButton("Browse…", false) { Location = new Point(514, 218), Width = 130 };
            browse.Click += delegate
            {
                using (var d = new FolderBrowserDialog { Description = "Where should the midnightOS ISO go?", SelectedPath = where.Text, ShowNewFolderButton = true })
                    if (d.ShowDialog(this) == DialogResult.OK) where.Text = d.SelectedPath;
            };
            page.Controls.Add(browse);
            var info = Body("", 266, 60);
            Btn("Close", false, 340, delegate { Close(); });
            EventHandler go = delegate
            {
                string folder = where.Text.Trim();
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { info.ForeColor = Theme.Danger; info.Text = "That folder can't be used: " + ex.Message; return; }
                ShowDownload(folder);
            };
            var download = Btn("Download", true, 494, go);
            AcceptButton = download;
            ActiveControl = download;          // don't open with the whole path selected
            if (Array.IndexOf(args, "--autostart") >= 0)   // used by the release checks
                Shown += delegate { go(this, EventArgs.Empty); };
        }

        // ---------- 2. download ----------
        string ManifestUrl()
        {
            int i = Array.IndexOf(args, "--manifest");
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            string here = Path.GetDirectoryName(Application.ExecutablePath);
            string file = Path.Combine(here, "manifest-url.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            string local = Path.Combine(here, "release.json");       // parts handed out on a USB drive or share
            if (File.Exists(local)) return local;
            return Config.MANIFEST_URL;
        }

        void ShowDownload(string folder)
        {
            NewPage();
            var title = Title("Downloading midnightOS", 96);
            var status = Body("Finding the latest release…", 146, 26);
            var bar = new SlimBar { Location = new Point(36, 196), Width = 608 };
            page.Controls.Add(bar);
            var detail = Body("", 214, 48);
            var stop = Btn("Cancel", false, 494, delegate { cancel = true; });
            cancel = false;
            worker = new Thread(delegate()
            {
                try
                {
                    release = Downloader.Fetch(ManifestUrl());
                    Ui(delegate { title.Text = "Downloading midnightOS " + release.version; });
                    long lastJoin = -1;
                    isoPath = Downloader.Download(release, folder,
                        delegate(long done, int parts, double speed)
                        {
                            Ui(delegate
                            {
                                bar.Value = (double)done / release.size;
                                status.Text = string.Format("{0} of {1}", Size(done), Size(release.size));
                                string partsText = release.parts.Count > 1
                                    ? string.Format(" · {0} parts at once, {1} of {0} done", release.parts.Count, parts) : "";
                                detail.Text = string.Format("{0}/s · about {1} left{2}\nYou can minimise this window: midnightOS keeps downloading in the background.",
                                                            Size((long)speed), Eta((release.size - done) / Math.Max(1, speed)), partsText);
                                Tray(string.Format("Downloading midnightOS: {0:0}%", 100.0 * done / release.size));
                            });
                        },
                        delegate(double f)
                        {
                            long step = (long)(f * 200);
                            if (step == lastJoin) return;
                            lastJoin = step;
                            Ui(delegate
                            {
                                status.Text = release.parts.Count > 1 ? "Putting the parts together…" : "Checking the download…";
                                detail.Text = "Joining them into one disc image and making sure every byte arrived as published (SHA-256).";
                                stop.Enabled = false; bar.Value = f;
                                Tray("Putting midnightOS together…");
                            });
                        },
                        delegate { return cancel; });
                    Ui(ShowDone);
                }
                catch (OperationCanceledException) { Ui(ShowWelcome); }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    Ui(delegate { ShowError("The download didn't finish", message + "\n\nWhat's been downloaded is kept: start again and it continues where it stopped."); });
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        public static new string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
        public static string Eta(double seconds)
        {
            if (seconds > 3600) return string.Format("{0:0} h {1:0} min", Math.Floor(seconds / 3600), (seconds % 3600) / 60);
            if (seconds > 90) return string.Format("{0:0} min", seconds / 60);
            return string.Format("{0:0} s", seconds);
        }
        public static string Sha256(string path, Action<double> progress)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buffer = new byte[4 << 20];
                long total = f.Length, done = 0; int n;
                while ((n = f.Read(buffer, 0, buffer.Length)) > 0) { sha.TransformBlock(buffer, 0, n, null, 0); done += n; progress((double)done / total); }
                sha.TransformFinalBlock(buffer, 0, 0);
                var sb = new StringBuilder();
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------- 3. done ----------
        void ShowDone()
        {
            NewPage();
            Title("midnightOS is ready. Flash it now?", 96);
            Body("The parts were downloaded, joined and checked. Your disc image is here:\n" + isoPath +
                 "\n\nWrite it to a USB stick (8 GB or bigger) to start or install midnightOS from it. Setup erases the stick you pick, writes midnightOS and checks it.", 146, 130);
            Btn("Show in folder", false, 36, delegate { Process.Start("explorer.exe", "/select,\"" + isoPath + "\""); });
            Btn("Not now", false, 340, delegate { Close(); });
            var flash = Btn("Flash to USB", true, 494, delegate { StartFlash(); });
            AcceptButton = flash;
            ActiveControl = flash;
            Tray(null);
            if (WindowState == FormWindowState.Minimized || !Visible)
            {
                tray.Visible = true;
                tray.ShowBalloonTip(15000, "midnightOS is downloaded", "Click here to write it to a USB stick.", ToolTipIcon.Info);
            }
            else Activate();
        }

        public void RenderPages(string folder)
        {
            Directory.CreateDirectory(folder);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            Action<string> save = delegate(string name)
            {
                for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    page.DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                    bmp.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            };
            ShowWelcome(); save("1-welcome");
            NewPage();
            Title("Downloading midnightOS Consumer Preview", 96);
            Body("1.27 GB of 2.80 GB", 146, 26);
            page.Controls.Add(new SlimBar { Location = new Point(36, 196), Width = 608, Value = 0.45 });
            Body("11.8 MB/s · about 2 min left · 3 parts at once, 0 of 3 done\nYou can minimise this window: midnightOS keeps downloading in the background.", 214, 48);
            Btn("Cancel", false, 494, delegate { });
            save("2-download");
            isoPath = @"C:\Users\You\Downloads\midnightOS-951121202612_Dev-20261124.iso";
            ShowDone(); save("3-done");
            ShowFlash(); save("4-flash");
        }

        // ---------- the tray: keeps downloading while the window is minimised ----------
        NotifyIcon tray;
        void SetUpTray()
        {
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "midnightOS Setup", Visible = false };
            EventHandler restore = delegate { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; };
            tray.DoubleClick += restore;
            tray.BalloonTipClicked += restore;
            tray.ContextMenu = new ContextMenu(new[] { new MenuItem("Show midnightOS Setup", restore) });
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && worker != null && worker.IsAlive)
                {
                    Hide();
                    tray.Visible = true;
                    tray.ShowBalloonTip(5000, "Still downloading", "midnightOS keeps downloading in the background. It tells you when it's ready.", ToolTipIcon.Info);
                }
            };
            FormClosed += delegate { tray.Visible = false; tray.Dispose(); };
        }
        void Tray(string text)
        {
            if (tray == null) return;
            text = text ?? "midnightOS Setup";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void ShowError(string title, string text)
        {
            NewPage();
            Title(title, 96);
            var b = Body(text, 146, 200); b.ForeColor = Theme.Danger;
            Btn("Close", false, 340, delegate { Close(); });
            Btn("Start again", true, 494, delegate { ShowWelcome(); });
        }

        // ---------- 4. flash to USB ----------
        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        void StartFlash()
        {
            if (IsAdmin()) { ShowFlash(); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--flash \"" + isoPath + "\"") { Verb = "runas", UseShellExecute = true });
                Close();
            }
            catch (Win32Exception) { /* the person said no to administrator rights */ }
        }

        class Drive { public int index; public string model; public long size; public override string ToString() { return model + "  —  " + Size(size) + "  (disk " + index + ")"; } }

        static int SystemDisk()
        {
            try
            {
                string sys = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');
                foreach (ManagementObject part in new ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + sys + "'} WHERE AssocClass=Win32_LogicalDiskToPartition").Get())
                    return Convert.ToInt32(part["DiskIndex"]);
            }
            catch { }
            return 0;
        }

        static List<Drive> UsbDrives(long minimum)
        {
            var list = new List<Drive>();
            int system = SystemDisk();
            foreach (ManagementObject d in new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive").Get())
            {
                string iface = Convert.ToString(d["InterfaceType"]);
                string media = Convert.ToString(d["MediaType"]);
                int index = Convert.ToInt32(d["Index"]);
                long size = d["Size"] == null ? 0 : Convert.ToInt64(d["Size"]);
                bool usb = iface == "USB" || (media != null && media.StartsWith("Removable"));
                if (!usb || index == system || size < minimum) continue;
                list.Add(new Drive { index = index, model = Convert.ToString(d["Model"]).Trim(), size = size });
            }
            return list;
        }

        void ShowFlash()
        {
            NewPage();
            Title("Write midnightOS to a USB stick", 96);
            long need = File.Exists(isoPath) ? new FileInfo(isoPath).Length : 0;
            Body("Plug in a USB stick of 8 GB or more. Everything on it will be erased.", 146, 26);
            var pick = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(36, 186), Width = 470, Font = Theme.Font(11f, FontStyle.Regular), BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
            page.Controls.Add(pick);
            var refresh = new FlatButton("Refresh", false) { Location = new Point(514, 182), Width = 130 };
            page.Controls.Add(refresh);
            var note = Body("", 230, 60);
            Button write = null;
            Action fill = delegate
            {
                pick.Items.Clear();
                foreach (var d in UsbDrives(need)) pick.Items.Add(d);
                bool any = pick.Items.Count > 0;
                pick.Enabled = any;
                if (write != null) write.Enabled = any;
                if (any) { pick.SelectedIndex = 0; note.Text = ""; }
                else { note.ForeColor = Theme.Muted; note.Text = "No USB stick found that's big enough. Plug one in and click Refresh."; }
            };
            refresh.Click += delegate { fill(); };
            Btn("Close setup", false, 340, delegate { Close(); });
            write = Btn("Erase and write", true, 494, delegate
            {
                var drive = pick.SelectedItem as Drive;
                if (drive == null) return;
                if (MessageBox.Show(this, "Erase " + drive.model + " (" + Size(drive.size) + ") and write midnightOS to it?\n\nEverything on this USB stick will be lost.", "midnightOS Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                Flash(drive);
            });
            fill();
            ActiveControl = refresh;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3;
        const uint NO_BUFFERING = 0x20000000, WRITE_THROUGH = 0x80000000;
        const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020, IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

        static void Diskpart(int index)
        {
            string script = Path.GetTempFileName();
            File.WriteAllText(script, "select disk " + index + "\r\nclean\r\nrescan\r\nexit\r\n");
            var p = Process.Start(new ProcessStartInfo("diskpart.exe", "/s \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            File.Delete(script);
            if (p.ExitCode != 0) throw new Exception("Windows couldn't clear the USB stick (diskpart):\n" + output.Trim());
        }

        void Flash(Drive drive)
        {
            NewPage();
            Title("Writing midnightOS to the USB stick", 96);
            var status = Body("Clearing the USB stick…", 146, 26);
            var bar = new SlimBar { Location = new Point(36, 196), Width = 608 };
            page.Controls.Add(bar);
            var detail = Body("Don't unplug the stick until this is finished.", 214, 48);
            worker = new Thread(delegate()
            {
                try
                {
                    Diskpart(drive.index);
                    Thread.Sleep(1500);
                    using (var disk = CreateFile(@"\\.\PhysicalDrive" + drive.index, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, WRITE_THROUGH, IntPtr.Zero))
                    {
                        if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The USB stick couldn't be opened for writing");
                        uint ret;
                        DeviceIoControl(disk, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                        DeviceIoControl(disk, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                        using (var device = new FileStream(disk, FileAccess.ReadWrite, 1, false))
                        using (var iso = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                        {
                            long total = iso.Length, done = 0;
                            var buffer = new byte[4 << 20];
                            var clock = Stopwatch.StartNew();
                            long lastUi = 0;
                            int n;
                            while ((n = iso.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cancel) throw new OperationCanceledException();
                                int aligned = (n + 511) / 512 * 512;           // whole sectors only
                                if (aligned != n) Array.Clear(buffer, n, aligned - n);
                                device.Write(buffer, 0, aligned);
                                done += n;
                                if (clock.ElapsedMilliseconds - lastUi > 250)
                                {
                                    lastUi = clock.ElapsedMilliseconds;
                                    double speed = done / Math.Max(0.5, clock.Elapsed.TotalSeconds);
                                    long d = done;
                                    Ui(delegate { bar.Value = (double)d / total * 0.85; status.Text = "Writing… " + Size(d) + " of " + Size(total); detail.Text = Size((long)speed) + "/s · about " + Eta((total - d) / Math.Max(1, speed)) + " left. Don't unplug the stick."; });
                                }
                            }
                            device.Flush();
                            // Read it back and compare, so a bad stick is caught now and not at start-up.
                            Ui(delegate { status.Text = "Checking the USB stick…"; detail.Text = "Reading back what was written."; });
                            device.Seek(0, SeekOrigin.Begin);
                            iso.Seek(0, SeekOrigin.Begin);
                            var check = new byte[4 << 20];
                            done = 0;
                            while ((n = iso.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                int aligned = (n + 511) / 512 * 512;
                                int got = 0;
                                while (got < aligned) { int r = device.Read(check, got, aligned - got); if (r <= 0) break; got += r; }
                                for (int i = 0; i < n; i++) if (buffer[i] != check[i]) throw new IOException("The USB stick didn't keep what was written (at byte " + (done + i) + "). Try another stick.");
                                done += n;
                                long d = done;
                                Ui(delegate { bar.Value = 0.85 + 0.15 * d / total; });
                            }
                        }
                        DeviceIoControl(disk, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                    }
                    Ui(delegate
                    {
                        NewPage();
                        Title("Your USB stick is ready", 96);
                        Body("To start midnightOS: restart your PC and open its boot menu (usually F12, F8, F11 or Esc right after switching on; on many laptops it's F2 for settings). Pick the USB stick.\n\nmidnightOS starts straight to the desktop. To install it, open \"Install midnightOS\" on the desktop.\n\nTip: turn Secure Boot off in your PC's firmware settings if the stick doesn't start.", 146, 200);
                        Btn("Close setup", true, 494, delegate { Close(); });
                    });
                }
                catch (OperationCanceledException) { Ui(ShowDone); }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    Ui(delegate { ShowError("Writing the USB stick didn't work", message); });
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--render")
            {   // a picture of each page, to check the layout
                Application.EnableVisualStyles();
                var form = new MainForm(new string[0]);
                form.RenderPages(args[1]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--test-download")
            {   // headless check of the download path: writes a log into the folder
                string log = Path.Combine(args[2], "midnight-setup-test.log");
                try
                {
                    var release = Downloader.Fetch(args[1]);
                    string file = Downloader.Download(release, args[2], delegate(long d, int p, double sp) { }, delegate(double f) { }, delegate { return false; });
                    File.WriteAllText(log, "OK " + file + " " + new FileInfo(file).Length);
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(log, "FAIL " + ex.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
            return 0;
        }
    }
}
