using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace WarThunderSocialTap
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "WarThunderSocialTap.SingleInstance", out first))
            {
                if (!first) return; // already running in the tray
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                ServicePointManager.Expect100Continue = false;
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("UI error: " + e.Exception); };
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly Control marshal;
        readonly Tracker tracker = new Tracker();
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        readonly Thread worker;
        string shownStatus;

        public TrayApp()
        {
            SocialTap.Init();
            marshal = new Control();
            IntPtr forceHandle = marshal.Handle;

            statusItem = new ToolStripMenuItem("Starting");
            statusItem.Enabled = false;
            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Connect to SocialTap...", null, delegate { StartConnect(); });
            menu.Items.Add("Open log", null, delegate { OpenPath(Paths.In("app.log")); });
            menu.Items.Add("Open data folder", null, delegate { OpenPath(Paths.DataDir); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Quit(); });

            tray = new NotifyIcon();
            tray.Icon = LoadIcon();
            tray.Text = "War Thunder > SocialTap";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Start();

            // "WarThunderSocialTap.exe --connect" starts the login right away.
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg == "--connect") StartConnect();
        }

        void StartConnect()
        {
            var t = new Thread(delegate()
            {
                string problem = OAuth.Connect();
                if (problem == "") return; // replaced by a newer login attempt
                if (problem == null) tracker.ForceRepublish = true;
                else Log.Write("Login not completed: " + problem);
                try
                {
                    marshal.BeginInvoke((MethodInvoker)delegate
                    {
                        tray.ShowBalloonTip(8000, "War Thunder > SocialTap",
                            problem == null ? "Connected to SocialTap." : problem,
                            problem == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Run()
        {
            Log.Write("App started" + (SocialTap.DryRun ? " (not connected to SocialTap yet - nothing will be sent)" : ""));
            BrTable.Load();
            MapLearner.Load();
            while (true)
            {
                int wait = Config.PollSeconds;
                try
                {
                    BrTable.EnsureFresh();
                    wait = tracker.Tick();
                }
                catch (Exception ex)
                {
                    Log.Write("Error: " + ex);
                }
                ShowStatus(tracker.Status);
                if (stopEvent.WaitOne(wait * 1000)) return;
            }
        }

        void ShowStatus(string status)
        {
            if (status == shownStatus) return;
            shownStatus = status;
            try
            {
                marshal.BeginInvoke((MethodInvoker)delegate
                {
                    statusItem.Text = status;
                    string tip = "WT > SocialTap: " + status;
                    tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // Windows limit
                });
            }
            catch { }
        }

        void Quit()
        {
            stopEvent.Set();
            worker.Join(4000);
            try { tracker.Shutdown(); } catch { }
            tray.Visible = false;
            tray.Dispose();
            Log.Write("App closed");
            ExitThread();
        }

        // The tray icon is the War Thunder logo, taken from your own game install.
        static Icon LoadIcon()
        {
            foreach (string dir in Paths.GameDirs())
            {
                try
                {
                    string ico = Path.Combine(dir, "icon.ico");
                    if (File.Exists(ico)) return new Icon(ico, SystemInformation.SmallIconSize);
                }
                catch { }
            }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            return SystemIcons.Application;
        }

        static void OpenPath(string path)
        {
            try { Process.Start(path); } catch { }
        }
    }
}
