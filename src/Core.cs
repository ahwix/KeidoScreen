// KeidoScreen core: settings, engine (UxPlay) control, hardware auto-tuning.
// C# 5 (compiled by the .NET Framework 4.x csc that ships with Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KeidoScreen
{
    public static class AppInfo
    {
        public const string Name = "KeidoScreen";
        public static Version Version { get { return typeof(AppInfo).Assembly.GetName().Version; } }
        public static string VersionText { get { return Version.ToString(3); } }
        public static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    }

    // ---------------------------------------------------------------- settings
    public static class Settings
    {
        static readonly string FilePath = Path.Combine(AppInfo.DataDir, "settings.ini");
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static void Load()
        {
            try
            {
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                File.WriteAllLines(FilePath, values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch { }
        }

        public static string Get(string k, string def) { string v; return values.TryGetValue(k, out v) ? v : def; }
        public static bool Get(string k, bool def) { string v; return values.TryGetValue(k, out v) ? v == "1" : def; }
        public static int Get(string k, int def) { string v; int r; return values.TryGetValue(k, out v) && int.TryParse(v, out r) ? r : def; }
        public static void Set(string k, string v) { values[k] = v ?? ""; }
        public static void Set(string k, bool v) { values[k] = v ? "1" : "0"; }
        public static void Set(string k, int v) { values[k] = v.ToString(); }
    }

    // ---------------------------------------------------------------- native helpers
    public static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        static IntPtr job = IntPtr.Zero;

        /// The engine is killed by Windows when KeidoScreen exits for any reason (even a crash).
        public static void KillWithParent(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? 0x80000003u : 0x80000000u); // CONTINUOUS | SYSTEM | DISPLAY
        }

        /// Dark title bar + Windows 11 rounded corners (ignored on older systems).
        public static void StyleWindow(IntPtr hwnd)
        {
            try { int v = 1; DwmSetWindowAttribute(hwnd, 20, ref v, 4); } catch { }
            try { int v = 2; DwmSetWindowAttribute(hwnd, 33, ref v, 4); } catch { }
        }

        /// Largest monitor in physical pixels (independent of 125%/150% scaling).
        public static int[] LargestMonitor()
        {
            int bw = 1920, bh = 1080;
            var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)))
            {
                if ((dd.StateFlags & 1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(dd.DeviceName, -1, ref dm) && (long)dm.dmPelsWidth * dm.dmPelsHeight > (long)bw * bh)
                { bw = dm.dmPelsWidth; bh = dm.dmPelsHeight; }
            }
            return new[] { bw, bh };
        }

        public static string LocalIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("192.168.0.1", 9);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return "—"; }
        }

        public static string HostName()
        {
            try { return Dns.GetHostName(); } catch { return Environment.MachineName; }
        }
    }

    // ---------------------------------------------------------------- engine
    public static class Engine
    {
        public static string Root, Bin, Uxplay, Inspect, Launch, ProbeDir;
        public static bool Bundled;

        public static void Locate()
        {
            string bundled = Path.Combine(AppInfo.Dir, "engine");
            if (File.Exists(Path.Combine(bundled, @"bin\uxplay.exe"))) { Root = bundled; Bundled = true; }
            else Root = @"C:\msys64\ucrt64"; // developer machine
            Bin = Path.Combine(Root, "bin");
            Uxplay = Path.Combine(Bin, "uxplay.exe");
            Inspect = Path.Combine(Bin, "gst-inspect-1.0.exe");
            Launch = Path.Combine(Bin, "gst-launch-1.0.exe");
            ProbeDir = Path.Combine(Root, @"share\keidoscreen");
            if (!Directory.Exists(ProbeDir)) ProbeDir = Path.Combine(AppInfo.Dir, "probe");
        }

        public static bool Present { get { return File.Exists(Uxplay); } }

        public static void Prepare(ProcessStartInfo psi)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["PATH"] = Bin + ";" + Environment.GetEnvironmentVariable("PATH");
            if (Bundled)
            {
                // isolate the bundled GStreamer from any other GStreamer on the PC
                psi.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(Root, @"lib\gstreamer-1.0");
                psi.EnvironmentVariables["GST_PLUGIN_PATH_1_0"] = "";
                psi.EnvironmentVariables["GST_PLUGIN_SCANNER_1_0"] = Path.Combine(Root, @"libexec\gstreamer-1.0\gst-plugin-scanner.exe");
                psi.EnvironmentVariables["GST_REGISTRY_1_0"] = Path.Combine(AppInfo.DataDir, "gst-registry.bin");
            }
        }

        /// Runs a helper (gst-inspect / gst-launch); returns exit code, -1 on timeout.
        public static int Run(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
                Prepare(psi);
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var outp = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    output = outp.Result + err.Result;
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -2; }
        }

        public static bool Has(string element)
        {
            string o; return Run(Inspect, "--exists " + element, 60000, out o) == 0;
        }
    }

    // ---------------------------------------------------------------- hardware auto-tuning
    /// A decoder family: same API for H.264 and H.265 (UxPlay derives the H.265
    /// element name from the H.264 one by replacing "264" with "265").
    public class Family
    {
        public string Key, Label, Dec264, Sink, LowLat264, LowLatAny;
        public bool Hardware;
        public Family(string key, string label, string dec264, string sink, bool hw, string lowLat264, string lowLatAny)
        { Key = key; Label = label; Dec264 = dec264; Sink = sink; Hardware = hw; LowLat264 = lowLat264; LowLatAny = lowLatAny; }
        public string Dec265 { get { return Dec264.Replace("264", "265"); } }
    }

    public class Profile
    {
        public string Signature = "";
        public List<string> Ok264 = new List<string>();   // family keys that decode H.264
        public List<string> Ok265 = new List<string>();   // family keys that decode H.265
        public double SoftwareFps;                        // 1080p H.264 frames/s on the CPU
        public string GpuName = "";

        public string Serialize()
        {
            return string.Join("|", new[] { Signature, string.Join(",", Ok264), string.Join(",", Ok265),
                SoftwareFps.ToString(System.Globalization.CultureInfo.InvariantCulture), GpuName.Replace("|", "/") });
        }

        public static Profile Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var p = s.Split('|');
            if (p.Length < 5) return null;
            var r = new Profile { Signature = p[0], GpuName = p[4] };
            r.Ok264.AddRange(p[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            r.Ok265.AddRange(p[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r.SoftwareFps);
            return r;
        }
    }

    public static class Hardware
    {
        // preference order; the probe keeps only those that really decode on this PC
        public static readonly Family[] Families = {
            new Family("d3d12", "Direct3D 12", "d3d12h264dec", "d3d12videosink", true, " compliance=flexible", ""),
            new Family("nvdec", "NVIDIA NVDEC", "nvh264dec", "d3d11videosink", true, " compliance=flexible max-display-delay=0", " max-display-delay=0"),
            new Family("d3d11", "Direct3D 11", "d3d11h264dec", "d3d11videosink", true, " compliance=flexible", ""),
            new Family("cpu", "Процессор", "avdec_h264", "d3d11videosink", false, "", ""),
        };

        public static Family Get(string key) { return Families.FirstOrDefault(f => f.Key == key); }

        /// Changes whenever GPUs, drivers or the app version change => re-probe.
        public static string Signature()
        {
            var sb = new StringBuilder(AppInfo.VersionText);
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                        sb.Append(';').Append(o["Name"]).Append('/').Append(o["DriverVersion"]).Append('/').Append(o["PNPDeviceID"]);
            }
            catch { sb.Append(";wmi-unavailable"); }
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 16);
        }

        /// Decodes small 1080p test clips with every decoder family.
        public static Profile Probe(Action<string> progress)
        {
            var prof = new Profile { Signature = Signature() };
            string h264 = Path.Combine(Engine.ProbeDir, "probe.h264").Replace('\\', '/');
            string h265 = Path.Combine(Engine.ProbeDir, "probe.h265").Replace('\\', '/');
            string o;
            progress("Подготовка движка…");
            Engine.Run(Engine.Inspect, "--version", 120000, out o); // builds the plugin registry once

            foreach (var f in Families)
            {
                progress("Проверяю декодер: " + f.Label + "…");
                if (!Engine.Has(f.Dec264)) continue;
                var sw = Stopwatch.StartNew();
                int rc = Engine.Run(Engine.Launch, "-q filesrc location=\"" + h264 + "\" ! h264parse ! " + f.Dec264 + " ! fakesink sync=false", 30000, out o);
                sw.Stop();
                if (rc != 0) continue;
                prof.Ok264.Add(f.Key);
                if (f.Key == "cpu") prof.SoftwareFps = 60.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds);
                if (Engine.Has(f.Dec265) &&
                    Engine.Run(Engine.Launch, "-q filesrc location=\"" + h265 + "\" ! h265parse ! " + f.Dec265 + " ! fakesink sync=false", 30000, out o) == 0)
                    prof.Ok265.Add(f.Key);
            }

            // human-readable GPU name, from the decoder's own description
            var best = BestFamily(prof);
            if (best != null && best.Hardware && Engine.Run(Engine.Inspect, best.Dec264, 30000, out o) == 0)
            {
                foreach (var line in o.Split('\n'))
                {
                    int i = line.IndexOf("Long-name", StringComparison.Ordinal);
                    if (i < 0) continue;
                    string n = line.Substring(i + 9).Trim();
                    foreach (var cut in new[] { "Direct3D12/DXVA H.264", "Direct3D11/DXVA H.264", "NVDEC H.264", "Decoder" }) n = n.Replace(cut, "");
                    prof.GpuName = n.Trim();
                    break;
                }
            }
            if (prof.GpuName.Length == 0) prof.GpuName = WmiGpuName();
            return prof;
        }

        public static Family BestFamily(Profile p)
        {
            if (p == null) return null;
            return Families.FirstOrDefault(f => p.Ok264.Contains(f.Key));
        }

        public static Family NextFamily(Profile p, string afterKey)
        {
            bool seen = false;
            foreach (var f in Families)
            {
                if (seen && p.Ok264.Contains(f.Key)) return f;
                if (f.Key == afterKey) seen = true;
            }
            return null;
        }

        static string WmiGpuName()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string pnp = (o["PNPDeviceID"] ?? "").ToString();
                        if (pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return (o["Name"] ?? "").ToString();
                    }
            }
            catch { }
            return "";
        }
    }

    // ---------------------------------------------------------------- quality presets
    public class Quality
    {
        public string Key, Label;
        public Quality(string key, string label) { Key = key; Label = label; }
        public override string ToString() { return Label; }
    }

    public class RunPlan
    {
        public List<string> Args = new List<string>();
        public Family Family;
        public bool H265;
        public string Summary = "";
    }

    public static class Planner
    {
        public static List<Quality> Qualities()
        {
            var m = Native.LargestMonitor();
            string wh = m[0] + "×" + m[1];
            return new List<Quality> {
                new Quality("auto", "Авто — лучшее для этого ПК"),
                new Quality("monitor-h265", "Под монитор " + wh + " · H.265"),
                new Quality("monitor-h264", "Под монитор " + wh + " · H.264"),
                new Quality("4k-h265", "4K · H.265"),
                new Quality("1080-60", "1080p · 60 fps"),
                new Quality("1080-30", "1080p · 30 fps — слабый Wi-Fi"),
            };
        }

        /// Turns settings + the hardware profile into UxPlay arguments.
        public static RunPlan Build(Profile prof, string name, string qualityKey, string familyKey, bool lowLatency,
                                    bool audio, bool fullscreen, bool keepWindow, bool takeover)
        {
            var plan = new RunPlan();
            var a = plan.Args;
            a.Add("-n"); a.Add(name); a.Add("-nh");

            Family fam = familyKey == "auto" || prof == null ? Hardware.BestFamily(prof) : Hardware.Get(familyKey);
            if (fam == null) fam = Hardware.Get("d3d11");
            plan.Family = fam;
            bool h265ok = prof == null || prof.Ok265.Contains(fam.Key);

            var m = Native.LargestMonitor();
            int w = m[0], h = m[1], fps = 60;
            bool h265;
            switch (qualityKey)
            {
                case "monitor-h265": h265 = h265ok; break;
                case "monitor-h264": h265 = false; break;
                case "4k-h265": h265 = h265ok; w = 3840; h = 2160; break;
                case "1080-60": h265 = false; w = 1920; h = 1080; break;
                case "1080-30": h265 = false; w = 1920; h = 1080; fps = 30; break;
                default:
                    // auto: hardware H.265 at monitor resolution; CPU-only PCs get 1080p,
                    // and 30 fps if the CPU cannot comfortably decode 1080p60
                    if (fam.Hardware) h265 = h265ok;
                    else
                    {
                        h265 = false; w = Math.Min(w, 1920); h = Math.Min(h, 1080);
                        if (prof != null && prof.SoftwareFps > 0 && prof.SoftwareFps < 150) fps = 30;
                    }
                    break;
            }
            plan.H265 = h265;
            if (h265) a.Add("-h265");
            a.Add("-s"); a.Add(w + "x" + h + "@60");
            a.Add("-fps"); a.Add(fps.ToString());

            string dec = fam.Dec264;
            if (lowLatency)
            {
                a.Add("-vsync"); a.Add("no");
                dec += h265 ? fam.LowLatAny : fam.LowLat264; // compliance= exists only on H.264 decoders
            }
            a.Add("-vd"); a.Add(dec);
            if (lowLatency)
            {
                // single-slot leaky queue after the decoder: lag can never accumulate
                a.Add("-vc"); a.Add("queue max-size-buffers=1 max-size-bytes=0 max-size-time=0 leaky=downstream ! videoconvert");
            }
            a.Add("-vs"); a.Add(fam.Sink);
            a.Add("-as"); a.Add(audio ? "wasapisink" : "0");
            if (fullscreen) a.Add("-fs");
            if (keepWindow) a.Add("-nc");
            if (takeover) a.Add("-nohold");
            plan.Summary = w + "×" + h + " · " + (h265 ? "H.265" : "H.264") + " · " + fps + " fps";
            return plan;
        }

        public static string Quote(string a)
        {
            return (a.Length == 0 || a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0) ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
        }
    }
}
