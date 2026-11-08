// ============================================================================
// Steam Shutter
// ============================================================================
//
// Turns your PC off after Steam finishes downloading.
//
// HOW TO USE IT
//   1. Start your download in Steam.
//   2. Open SteamShutter.exe and walk away.
//   3. Once Steam has been quiet for 5 minutes, your PC turns off.
//   To cancel, close the Steam Shutter window. Your PC won't go to sleep
//   while it's open.
//
// WARNING
//   Open programs are closed without asking. Save your work first!
//
// HOW THE TIMER WORKS
//   Every second, it checks whether Steam is busy: downloading, or saving
//   files. All downloads from any program count, not just Steam.
//   1. When things are quiet, the 5-minute countdown starts, even if Steam
//      isn't open.
//   2. Once started, it keeps counting down, even through short bursts.
//   3. If Steam is busy for 5 seconds in a row, the timer goes back to
//      5:00 and waits until things are quiet again.
//   4. At 0:00, the PC shuts down. If it's still on, it tries again every
//      2 minutes.
//
// SETTINGS (optional)
//   Open SteamShutter.ini in Notepad to change the wait time (TimerMinutes)
//   or the speed limits. Save it, then reopen Steam Shutter.
//
// BUILDING IT
//   Double-click "SteamShutter Build.bat". Needs Windows 10 or 11, 64-bit.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("Steam Shutter")]
[assembly: AssemblyProduct("Steam Shutter")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SteamShutter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\SteamShutter.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Steam Shutter is already running.", "Steam Shutter",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    sealed class Settings
    {
        const double BytesPerMB = 1024 * 1024;

        public string ProcessName = "steam";
        public int TimerSeconds = 300;
        public double DownloadThresholdBytesPerSec = BytesPerMB;
        public double DiskThresholdBytesPerSec = BytesPerMB;

        public string DisplayName
        {
            get { return char.ToUpperInvariant(ProcessName[0]) + ProcessName.Substring(1); }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamShutter.ini"));
            }
            catch (Exception)
            {
                return settings;
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                int equals = line.IndexOf('=');
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[' || equals <= 0)
                    continue;

                string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                string value = line.Substring(equals + 1).Trim();
                double number;

                switch (key)
                {
                    case "processname":
                        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            value = value.Substring(0, value.Length - 4).Trim();
                        if (value.Length > 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                            settings.ProcessName = value;
                        break;

                    case "timerminutes":
                        if (TryParseNumber(value, out number) && number >= 1 && number <= 1440)
                            settings.TimerSeconds = (int)Math.Round(number * 60);
                        break;

                    case "downloadthresholdmbps":
                        if (TryParseNumber(value, out number) && number > 0 && number <= 100000)
                            settings.DownloadThresholdBytesPerSec = number * BytesPerMB;
                        break;

                    case "diskwritethresholdmbps":
                        if (TryParseNumber(value, out number) && number > 0 && number <= 100000)
                            settings.DiskThresholdBytesPerSec = number * BytesPerMB;
                        break;
                }
            }
            return settings;
        }

        static bool TryParseNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
    }

    sealed class MainForm : Form
    {
        readonly Settings settings = Settings.Load();

        const int ActiveSecondsToReset = 5;

        const int RetryMilliseconds = 2 * 60 * 1000;

        enum State { Busy, Countdown, ShuttingDown }

        bool darkMode;
        Color Back, Fore, Dim, Amber, Red, Green;

        readonly ActivityMonitor monitor;
        readonly Timer tickTimer = new Timer();
        readonly Timer retryTimer = new Timer();
        readonly List<Label> nameLabels = new List<Label>();
        readonly NativeMethods.ITaskbarList3 taskbar = CreateTaskbarList();

        State state = State.Busy;
        int idleCounter;
        int activeStreak;
        float dpiScale = 1F;
        int nameColumnWidth;
        int valueColumnWidth;

        Icon appSmallIcon;
        IntPtr watchedSmallIcon;
        IntPtr watchedLargeIcon;
        int iconPid = -1;

        ProgressStrip progressStrip;
        Label stateLabel;
        Label countdownLabel;
        Label steamValue;
        Label downloadValue;
        Label diskValue;

        public MainForm()
        {
            monitor = new ActivityMonitor(settings.ProcessName);
            BuildLayout();

            tickTimer.Interval = 1000;
            tickTimer.Tick += OnTick;
            tickTimer.Start();

            retryTimer.Interval = RetryMilliseconds;
            retryTimer.Tick += delegate { SendShutdown(); };

            KeepSystemAwake(true);
            monitor.Sample();
            ApplyTheme();
        }

        static bool SystemUsesDarkMode()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value is int && (int)value == 0;
            }
        }

        void ApplyTheme()
        {
            darkMode = SystemUsesDarkMode();
            if (darkMode)
            {
                Back = Color.FromArgb(30, 30, 30);
                Fore = Color.FromArgb(230, 230, 230);
                Dim = Color.FromArgb(150, 150, 150);
                Amber = Color.FromArgb(255, 183, 77);
                Red = Color.FromArgb(239, 83, 80);
                Green = Color.FromArgb(108, 203, 95);
            }
            else
            {
                Back = Color.FromArgb(243, 243, 243);
                Fore = Color.FromArgb(26, 26, 26);
                Dim = Color.FromArgb(96, 96, 96);
                Amber = Color.FromArgb(157, 93, 0);
                Red = Color.FromArgb(196, 43, 28);
                Green = Color.FromArgb(16, 124, 16);
            }

            BackColor = Back;
            ForeColor = Fore;
            progressStrip.FillColor = Green;
            progressStrip.BackColor = Back;
            foreach (Label label in nameLabels)
                label.ForeColor = Dim;
            if (IsHandleCreated)
                ApplyTitleBarTheme();
            UpdateView();
        }

        void ApplyTitleBarTheme()
        {
            int enabled = darkMode ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }

        void BuildLayout()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                dpiScale = g.DpiX / 96F;

            SuspendLayout();
            using (Stream iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("SteamShutter.app.ico"))
            {
                if (iconStream != null)
                    Icon = new Icon(iconStream);
            }
            appSmallIcon = new Icon(Icon, SystemInformation.SmallIconSize);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Opacity = 0.99;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.75F);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            nameColumnWidth = Px(190);
            valueColumnWidth = Px(440) - nameColumnWidth;
            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(Px(16), Px(12), Px(16), Px(16))
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, nameColumnWidth));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, valueColumnWidth));

            stateLabel = AddWide(layout, new Label { AutoSize = true, Font = new Font("Segoe UI", 15F, FontStyle.Bold) });

            countdownLabel = AddWide(layout, new Label { AutoSize = true, Font = new Font("Segoe UI", 30F, FontStyle.Bold) });
            countdownLabel.Margin = new Padding(0, 0, 0, Px(8));

            steamValue = AddRow(layout, settings.DisplayName + ":");
            downloadValue = AddRow(layout, "Download Speed:");
            diskValue = AddRow(layout, settings.DisplayName + " Disk Writes:");

            var outer = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            progressStrip = new ProgressStrip
            {
                Height = 1,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0)
            };
            layout.Margin = new Padding(0);
            outer.Controls.Add(progressStrip);
            outer.Controls.Add(layout);

            Controls.Add(outer);
            ResumeLayout(true);
        }

        int Px(int pixelsAt96Dpi)
        {
            return (int)Math.Round(pixelsAt96Dpi * dpiScale);
        }

        static Label AddWide(TableLayoutPanel layout, Label label)
        {
            label.Margin = new Padding(0);
            layout.Controls.Add(label);
            layout.SetColumnSpan(label, 2);
            return label;
        }

        Label AddRow(TableLayoutPanel layout, string name)
        {
            var padding = new Padding(0, Px(3), 0, Px(3));
            var nameLabel = new Label { Text = name, AutoSize = true, AutoEllipsis = true, MaximumSize = new Size(nameColumnWidth - Px(8), 0), Margin = padding };
            nameLabels.Add(nameLabel);
            layout.Controls.Add(nameLabel);
            var value = new Label { AutoSize = true, AutoEllipsis = true, MaximumSize = new Size(valueColumnWidth, 0), Margin = padding };
            layout.Controls.Add(value);
            return value;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == NativeMethods.WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
                ApplyTheme();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tickTimer.Stop();
            retryTimer.Stop();
            KeepSystemAwake(false);
            DestroyWatchedIcons();
            base.OnFormClosed(e);
        }

        void DestroyWatchedIcons()
        {
            if (watchedSmallIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(watchedSmallIcon);
            if (watchedLargeIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(watchedLargeIcon);
            watchedSmallIcon = IntPtr.Zero;
            watchedLargeIcon = IntPtr.Zero;
        }

        void UpdateWindowIcons()
        {
            int pid = monitor.SteamPid;
            if (pid == iconPid || !IsHandleCreated)
                return;
            iconPid = pid;

            var small = new IntPtr[1];
            var large = new IntPtr[1];
            string path = pid != 0 ? ActivityMonitor.GetImagePath(pid) : null;
            if (path != null)
                NativeMethods.ExtractIconEx(path, 0, large, small, 1);

            if (small[0] == IntPtr.Zero || large[0] == IntPtr.Zero)
            {
                if (small[0] != IntPtr.Zero) NativeMethods.DestroyIcon(small[0]);
                if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
                small[0] = IntPtr.Zero;
                large[0] = IntPtr.Zero;
            }

            NativeMethods.SendMessage(Handle, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL,
                small[0] != IntPtr.Zero ? small[0] : appSmallIcon.Handle);
            NativeMethods.SendMessage(Handle, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG,
                large[0] != IntPtr.Zero ? large[0] : Icon.Handle);

            DestroyWatchedIcons();
            watchedSmallIcon = small[0];
            watchedLargeIcon = large[0];
        }

        void OnTick(object sender, EventArgs e)
        {
            monitor.Sample();
            UpdateWindowIcons();

            bool idle = monitor.DownloadBytesPerSec < settings.DownloadThresholdBytesPerSec &&
                        monitor.DiskWriteBytesPerSec < settings.DiskThresholdBytesPerSec;
            activeStreak = idle ? 0 : activeStreak + 1;
            if (activeStreak >= ActiveSecondsToReset)
                idleCounter = 0;
            else if (idle || idleCounter > 0)
                idleCounter++;

            state = idleCounter > 0 ? State.Countdown : State.Busy;
            if (idleCounter >= settings.TimerSeconds)
                ShutDown();

            UpdateView();
        }

        void ShutDown()
        {
            state = State.ShuttingDown;
            tickTimer.Stop();
            SendShutdown();
            retryTimer.Start();
        }

        static void SendShutdown()
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /f /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using (Process.Start(startInfo)) { }
            }
            catch (Exception)
            {
            }
        }

        void UpdateView()
        {
            int timerSeconds = settings.TimerSeconds;
            int shownCounter = Math.Min(idleCounter, timerSeconds);
            string timerText = FormatDuration(timerSeconds - shownCounter);
            Color countdownColor = Red;
            int taskbarState = NativeMethods.TBPF_NORMAL;

            switch (state)
            {
                case State.Busy:
                    stateLabel.Text = settings.DisplayName + " is busy - waiting";
                    stateLabel.ForeColor = Amber;
                    countdownColor = Dim;
                    taskbarState = NativeMethods.TBPF_INDETERMINATE;
                    break;

                case State.Countdown:
                    stateLabel.Text = "Shutting down in";
                    stateLabel.ForeColor = Red;
                    break;

                case State.ShuttingDown:
                    stateLabel.Text = "Shutting down...";
                    stateLabel.ForeColor = Red;
                    taskbarState = NativeMethods.TBPF_ERROR;
                    break;
            }

            progressStrip.Value = (float)shownCounter / timerSeconds;
            countdownLabel.Text = timerText;
            countdownLabel.ForeColor = countdownColor;
            Text = (state == State.Countdown ? timerText + " - " : "") + settings.DisplayName + " Shutter";

            if (taskbar != null && IsHandleCreated)
            {
                taskbar.SetProgressState(Handle, taskbarState);
                if (taskbarState != NativeMethods.TBPF_INDETERMINATE)
                    taskbar.SetProgressValue(Handle, (ulong)shownCounter, (ulong)timerSeconds);
            }

            steamValue.Text = monitor.SteamRunning ? string.Format("Running (PID {0})", monitor.SteamPid) : "Not running";
            steamValue.ForeColor = monitor.SteamRunning ? Fore : Dim;
            downloadValue.Text = FormatSpeed(monitor.DownloadBytesPerSec);
            downloadValue.ForeColor = monitor.DownloadBytesPerSec >= settings.DownloadThresholdBytesPerSec ? Amber : Fore;
            diskValue.Text = FormatSpeed(monitor.DiskWriteBytesPerSec);
            diskValue.ForeColor = monitor.DiskWriteBytesPerSec >= settings.DiskThresholdBytesPerSec ? Amber : Fore;
        }

        static NativeMethods.ITaskbarList3 CreateTaskbarList()
        {
            try
            {
                var list = (NativeMethods.ITaskbarList3)new NativeMethods.TaskbarList();
                return list.HrInit() == 0 ? list : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void KeepSystemAwake(bool keepAwake)
        {
            NativeMethods.SetThreadExecutionState(keepAwake
                ? NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED
                : NativeMethods.ES_CONTINUOUS);
        }

        static string FormatSpeed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024;
            return kb >= 1024 ? (kb / 1024).ToString("N1") + " MB/s" : kb.ToString("N0") + " KB/s";
        }

        static string FormatDuration(int seconds)
        {
            return string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
        }
    }

    sealed class ProgressStrip : Control
    {
        float value;
        Color fillColor = Color.Green;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public float Value
        {
            get { return value; }
            set
            {
                float clamped = Math.Max(0F, Math.Min(1F, value));
                if (clamped != this.value)
                {
                    this.value = clamped;
                    Invalidate();
                }
            }
        }

        public Color FillColor
        {
            get { return fillColor; }
            set { fillColor = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int fillWidth = (int)Math.Round(Width * value);
            if (fillWidth > 0)
            {
                using (var brush = new SolidBrush(fillColor))
                    e.Graphics.FillRectangle(brush, 0, 0, fillWidth, Height);
            }
        }
    }

    sealed class ActivityMonitor
    {
        readonly string processName;

        public ActivityMonitor(string processName)
        {
            this.processName = processName;
        }

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastSampleTime;

        Dictionary<ulong, ulong> lastInOctets = new Dictionary<ulong, ulong>();

        int steamPid;
        ulong lastWriteBytes;
        bool haveWriteBaseline;

        public bool SteamRunning { get { return steamPid != 0; } }
        public int SteamPid { get { return steamPid; } }
        public double DownloadBytesPerSec { get; private set; }
        public double DiskWriteBytesPerSec { get; private set; }

        public void Sample()
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = now - lastSampleTime;
            lastSampleTime = now;
            if (elapsed < 0.1)
                elapsed = 0.1;

            DownloadBytesPerSec = SampleNetwork() / elapsed;
            DiskWriteBytesPerSec = SampleSteamDisk() / elapsed;
        }

        static readonly int RowSize = Marshal.SizeOf(typeof(NativeMethods.MIB_IF_ROW2));
        static readonly int LuidOffset = (int)Marshal.OffsetOf(typeof(NativeMethods.MIB_IF_ROW2), "InterfaceLuid");
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(NativeMethods.MIB_IF_ROW2), "InterfaceAndOperStatusFlags");
        static readonly int OperStatusOffset = (int)Marshal.OffsetOf(typeof(NativeMethods.MIB_IF_ROW2), "OperStatus");
        static readonly int InOctetsOffset = (int)Marshal.OffsetOf(typeof(NativeMethods.MIB_IF_ROW2), "InOctets");

        ulong SampleNetwork()
        {
            IntPtr table;
            if (NativeMethods.GetIfTable2(out table) != 0)
                return 0;

            var current = new Dictionary<ulong, ulong>();
            ulong busiest = 0;
            try
            {
                int count = Marshal.ReadInt32(table);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(table.ToInt64() + 8 + (long)i * RowSize);

                    byte flags = Marshal.ReadByte(row, FlagsOffset);
                    if ((flags & NativeMethods.IF_FLAG_HARDWARE) == 0 || (flags & NativeMethods.IF_FLAG_FILTER) != 0)
                        continue;
                    if (Marshal.ReadInt32(row, OperStatusOffset) != NativeMethods.IfOperStatusUp)
                        continue;

                    ulong luid = (ulong)Marshal.ReadInt64(row, LuidOffset);
                    ulong received = (ulong)Marshal.ReadInt64(row, InOctetsOffset);
                    current[luid] = received;

                    ulong previous;
                    if (lastInOctets.TryGetValue(luid, out previous) && received >= previous && received - previous > busiest)
                        busiest = received - previous;
                }
            }
            finally
            {
                NativeMethods.FreeMibTable(table);
            }

            lastInOctets = current;
            return busiest;
        }

        ulong SampleSteamDisk()
        {
            ulong written;
            if (steamPid == 0 || !TryGetWriteBytes(steamPid, out written))
            {
                steamPid = FindSteamPid();
                haveWriteBaseline = false;
                if (steamPid == 0 || !TryGetWriteBytes(steamPid, out written))
                {
                    steamPid = 0;
                    return 0;
                }
            }

            ulong delta = haveWriteBaseline && written >= lastWriteBytes ? written - lastWriteBytes : 0;
            lastWriteBytes = written;
            haveWriteBaseline = true;
            return delta;
        }

        int FindSteamPid()
        {
            Process[] processes = Process.GetProcessesByName(processName);
            int pid = processes.Length > 0 ? processes[0].Id : 0;
            foreach (Process p in processes)
                p.Dispose();
            return pid;
        }

        public static string GetImagePath(int pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var buffer = new StringBuilder(32768);
                int size = buffer.Capacity;
                return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        static bool TryGetWriteBytes(int pid, out ulong bytes)
        {
            bytes = 0;
            IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return false;

            try
            {
                uint exitCode;
                if (!NativeMethods.GetExitCodeProcess(handle, out exitCode) || exitCode != NativeMethods.STILL_ACTIVE)
                    return false;

                NativeMethods.IO_COUNTERS counters;
                if (!NativeMethods.GetProcessIoCounters(handle, out counters))
                    return false;

                bytes = counters.WriteTransferCount;
                return true;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    static class NativeMethods
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint STILL_ACTIVE = 259;
        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int WM_SETTINGCHANGE = 0x001A;

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        public const byte IF_FLAG_HARDWARE = 0x01;
        public const byte IF_FLAG_FILTER = 0x02;
        public const int IfOperStatusUp = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MIB_IF_ROW2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
            public uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
            public uint Mtu;
            public uint Type;
            public uint TunnelType;
            public uint MediaType;
            public uint PhysicalMediumType;
            public uint AccessType;
            public uint DirectionType;
            public byte InterfaceAndOperStatusFlags;
            public uint OperStatus;
            public uint AdminStatus;
            public uint MediaConnectState;
            public Guid NetworkGuid;
            public uint ConnectionType;
            public ulong TransmitLinkSpeed;
            public ulong ReceiveLinkSpeed;
            public ulong InOctets;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ulong[] OtherCounters;
        }

        [DllImport("iphlpapi.dll")]
        public static extern int GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll")]
        public static extern void FreeMibTable(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public const int TBPF_INDETERMINATE = 0x1;
        public const int TBPF_NORMAL = 0x2;
        public const int TBPF_ERROR = 0x4;

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        public class TaskbarList { }

        [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ITaskbarList3
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        }
    }
}
