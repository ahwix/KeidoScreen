// Auto-update from GitHub Releases.
// Release layout expected (made by publish_release.ps1):
//   tag  vX.Y.Z
//   KeidoScreen-Setup-X.Y.Z.exe, KeidoScreen-X.Y.Z-portable.zip, SHA256SUMS.txt
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KeidoScreen
{
    public class UpdateInfo
    {
        public Version Version;
        public string Tag, Notes, PageUrl, SetupUrl, SetupName, SumsUrl;
    }

    public static class Updater
    {
        /// "owner/repo" — substituted by build_release.ps1 (-Repo). Empty => updates off.
        public const string Repo = "__REPO__";

        public static bool Configured { get { return Repo.Contains("/") && !Repo.StartsWith("__"); } }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = AppInfo.Name + "/" + AppInfo.VersionText;
            return c;
        }

        /// Latest release if newer than this build, otherwise null. Throws on network errors.
        public static UpdateInfo Check()
        {
            if (!Configured) return null;
            string json;
            using (var c = Client())
            {
                c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = c.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            }
            var r = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            string tag = (r["tag_name"] ?? "").ToString();
            Version v;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build));
            if (v <= current) return null;

            var info = new UpdateInfo { Version = v, Tag = tag, PageUrl = (r["html_url"] ?? "").ToString(),
                Notes = r.ContainsKey("body") && r["body"] != null ? r["body"].ToString() : "" };
            foreach (Dictionary<string, object> a in ((IEnumerable)r["assets"]).Cast<Dictionary<string, object>>())
            {
                string name = (a["name"] ?? "").ToString(), url = (a["browser_download_url"] ?? "").ToString();
                if (name.StartsWith(AppInfo.Name + "-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { info.SetupUrl = url; info.SetupName = name; }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
            }
            return info;
        }

        /// True when running from the installed copy (Program Files), not a portable zip.
        public static bool IsInstalled()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"Software\" + AppInfo.Name))
                {
                    string dir = k == null ? null : k.GetValue("InstallDir") as string;
                    return dir != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(AppInfo.Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// Downloads the installer, verifies its SHA-256 against SHA256SUMS.txt and
        /// starts a silent update (the installer restarts KeidoScreen when done).
        public static void DownloadAndInstall(UpdateInfo u, Action<int> progress)
        {
            if (u.SetupUrl == null || u.SumsUrl == null) throw new Exception("В релизе нет установщика или контрольных сумм.");
            string dir = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-update");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, u.SetupName);

            string sums;
            using (var c = Client()) sums = c.DownloadString(u.SumsUrl);
            string expected = sums.Split('\n')
                .Select(l => l.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[1].Equals(u.SetupName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
            if (expected == null) throw new Exception("Контрольная сумма установщика не найдена.");

            using (var c = Client())
            {
                var done = new System.Threading.ManualResetEvent(false);
                Exception err = null;
                c.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                c.DownloadFileCompleted += (s, e) => { err = e.Error; done.Set(); };
                c.DownloadFileAsync(new Uri(u.SetupUrl), file);
                done.WaitOne();
                if (err != null) throw err;
            }

            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) { File.Delete(file); throw new Exception("Файл обновления повреждён (контрольная сумма не совпала)."); }

            // /S = silent, /RUN = start KeidoScreen again after the update
            Process.Start(new ProcessStartInfo(file, "/S /RUN") { UseShellExecute = true, Verb = "runas" });
        }

        public static void OpenPage(UpdateInfo u)
        {
            try { Process.Start(new ProcessStartInfo(u != null && !string.IsNullOrEmpty(u.PageUrl) ? u.PageUrl : "https://github.com/" + Repo + "/releases/latest") { UseShellExecute = true }); }
            catch { }
        }
    }
}
