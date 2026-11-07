// KeidoScreen window logic (UI is MainWindow.xaml, embedded and loaded at runtime).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("KeidoScreen")]
[assembly: AssemblyProduct("KeidoScreen")]
[assembly: AssemblyDescription("iPhone/iPad screen mirroring receiver for Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace KeidoScreen
{
    enum State { Probing, Idle, Waiting, Connected, Streaming, Error }

    class MainController
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly Color CAccent = Color.FromRgb(0x7C, 0x8C, 0xFF);
        static readonly Color COk = Color.FromRgb(0x3D, 0xD6, 0x8C);
        static readonly Color CWarn = Color.FromRgb(0xF5, 0xB5, 0x47);
        static readonly Color CErr = Color.FromRgb(0xFF, 0x6B, 0x6B);
        static readonly Color CText = Color.FromRgb(0xE7, 0xE9, 0xEE);
        static readonly Color CStroke = Color.FromRgb(0x23, 0x28, 0x33);

        public readonly Window W;
        readonly bool startInTray;
        WinForms.NotifyIcon tray;
        Profile profile;
        Process proc;
        RunPlan plan;
        State state = State.Idle;
        DateTime engineStarted;
        string failedFamily;          // decoder family that crashed during this session
        bool restartWhenIdle;         // settings changed during mirroring: restart once it ends
        int autoRestarts;
        bool loading = true, quitting;
        UpdateInfo pendingUpdate;
        readonly DispatcherTimer volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        readonly StringBuilder logText = new StringBuilder();
        Storyboard pulse;

        // named elements
        Button PowerBtn, MinBtn, CloseBtn, SettingsBtn, LogBtn, HelpBtn, SettingsBack, LogBack, LogCopy, InfoBack, InfoAction,
               MuteBtn, ReprobeBtn, FirewallBtn, UpdateCheckBtn, AboutBtn, UpdateBtn, UpdateDismiss;
        TextBlock PowerGlyph, StatusTitle, StatusHint, QualityValue, GpuValue, NetValue, VolumeText, MuteGlyph, ToastText,
                  UpdateText, VersionText, InfoTitle, InfoActionText;
        StackPanel InfoBody;
        Ellipse Glow, Ring;
        Slider VolumeSlider;
        TextBox NameBox, LogBox;
        ComboBox QualityCombo, DecoderCombo;
        CheckBox LowLatSwitch, AudioSwitch, FullscreenSwitch, KeepSwitch, TakeoverSwitch, AutoStartSwitch, WithWindowsSwitch, AutoUpdateSwitch;
        Grid SettingsPage, LogPage, InfoPage;
        Border Toast, UpdateBanner;
        Image TitleIcon;
        Action infoAction;

        T F<T>(string name) where T : class
        {
            var o = W.FindName(name) as T;
            if (o == null) throw new Exception("UI element not found: " + name);
            return o;
        }

        public MainController(bool trayMode)
        {
            startInTray = trayMode;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeidoScreen.MainWindow.xaml"))
                W = (Window)XamlReader.Load(s);

            foreach (var f in GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                if (f.FieldType.IsSubclassOf(typeof(FrameworkElement)) && W.FindName(f.Name) != null) f.SetValue(this, W.FindName(f.Name));

            var icon = LoadIcon();
            if (icon != null) { W.Icon = icon; TitleIcon.Source = icon; }
            W.SourceInitialized += (s, e) => Native.StyleWindow(new WindowInteropHelper(W).Handle);
            W.Closing += (s, e) => { if (!quitting) { e.Cancel = true; Quit(); } };
            W.StateChanged += (s, e) => { if (W.WindowState == WindowState.Minimized && tray != null && Settings.Get("min_to_tray", false)) W.Hide(); };

            Wire();
            LoadSettings();
            BuildTray();
            SetState(State.Probing, "Подготовка…", "");
            VersionText.Text = AppInfo.Name + " " + AppInfo.VersionText;
            volumeTimer.Tick += (s, e) => ApplyVolume();
            loading = false;
        }

        static BitmapSource LoadIcon()
        {
            try
            {
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                    return Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { return null; }
        }

        public void Start()
        {
            if (!startInTray) { W.Show(); W.Activate(); }
            Task.Run(() => PrepareProfile(false));
            if (Settings.Get("autoupdate", true)) CheckUpdates(false);
        }

        // ------------------------------------------------------------ wiring
        void Wire()
        {
            MinBtn.Click += (s, e) => W.WindowState = WindowState.Minimized;
            CloseBtn.Click += (s, e) => Quit();
            PowerBtn.Click += (s, e) => Toggle();
            SettingsBtn.Click += (s, e) => ShowPage(SettingsPage, true);
            SettingsBack.Click += (s, e) => LeaveSettings();
            LogBtn.Click += (s, e) => { LogBox.Text = logText.ToString(); LogBox.ScrollToEnd(); ShowPage(LogPage, true); };
            LogBack.Click += (s, e) => ShowPage(LogPage, false);
            LogCopy.Click += (s, e) => { try { Clipboard.SetText(logText.ToString()); ShowToast("Журнал скопирован"); } catch { } };
            HelpBtn.Click += (s, e) => ShowHelp();
            InfoBack.Click += (s, e) => ShowPage(InfoPage, false);
            InfoAction.Click += (s, e) => { if (infoAction != null) infoAction(); };
            AboutBtn.Click += (s, e) => ShowAbout();
            FirewallBtn.Click += (s, e) => Firewall();
            ReprobeBtn.Click += (s, e) =>
            {
                ShowPage(SettingsPage, false);
                StopEngine();
                Task.Run(() => PrepareProfile(true));
            };
            UpdateCheckBtn.Click += (s, e) => CheckUpdates(true);
            UpdateBtn.Click += (s, e) => InstallUpdate();
            UpdateDismiss.Click += (s, e) => UpdateBanner.Visibility = Visibility.Collapsed;

            VolumeSlider.ValueChanged += (s, e) =>
            {
                VolumeText.Text = (int)VolumeSlider.Value + "%";
                if (!loading) { Settings.Set("volume", (int)VolumeSlider.Value); ApplyVolume(); }
            };
            VolumeSlider.PreviewMouseWheel += (s, e) => { VolumeSlider.Value += e.Delta > 0 ? 5 : -5; e.Handled = true; };
            VolumeSlider.LostMouseCapture += (s, e) => Settings.Save();
            MuteBtn.Click += (s, e) => { Settings.Set("muted", !Settings.Get("muted", false)); Settings.Save(); UpdateMuteGlyph(); ApplyVolume(); };

            WithWindowsSwitch.Click += (s, e) => SetRunAtStartup(WithWindowsSwitch.IsChecked == true);
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                    foreach (var p in new[] { SettingsPage, LogPage, InfoPage })
                        if (p.Visibility == Visibility.Visible) { if (p == SettingsPage) LeaveSettings(); else ShowPage(p, false); }
            };
        }

        void LeaveSettings()
        {
            ShowPage(SettingsPage, false);
            SaveSettings();
            RefreshSummary();
            if (proc == null || proc.HasExited || plan == null || profile == null) return;
            // apply changed settings right away; never cut off a live mirroring session
            if (string.Join(" ", BuildPlan().Args) == string.Join(" ", plan.Args)) return;
            if (state == State.Waiting) RestartEngine("Настройки применены");
            else { restartWhenIdle = true; ShowToast("Новые настройки применятся после отключения iPhone"); }
        }

        void ShowPage(Grid page, bool show)
        {
            var tt = (TranslateTransform)page.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (show)
            {
                page.Visibility = Visibility.Visible;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
            else
            {
                var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
                a.Completed += (s, e) => page.Visibility = Visibility.Collapsed;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
                page.BeginAnimation(UIElement.OpacityProperty, a);
            }
        }

        void ShowToast(string text)
        {
            ToastText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3600))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4000))));
            Toast.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ------------------------------------------------------------ settings
        void LoadSettings()
        {
            Settings.Load();
            NameBox.Text = Settings.Get("name", Native.HostName());
            QualityCombo.ItemsSource = Planner.Qualities();
            SelectQuality(Settings.Get("quality", "auto"));
            DecoderCombo.ItemsSource = new[] { "Авто" };
            DecoderCombo.SelectedIndex = 0;
            LowLatSwitch.IsChecked = Settings.Get("lowlat", true);
            AudioSwitch.IsChecked = Settings.Get("audio", true);
            FullscreenSwitch.IsChecked = Settings.Get("fullscreen", false);
            KeepSwitch.IsChecked = Settings.Get("keep_window", true);
            TakeoverSwitch.IsChecked = Settings.Get("takeover", true);
            AutoStartSwitch.IsChecked = Settings.Get("autostart", true);
            AutoUpdateSwitch.IsChecked = Settings.Get("autoupdate", true);
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) WithWindowsSwitch.IsChecked = k != null && k.GetValue(AppInfo.Name) != null;
            VolumeSlider.Value = Settings.Get("volume", 100);
            VolumeText.Text = (int)VolumeSlider.Value + "%";
            UpdateMuteGlyph();
            if (!Updater.Configured) { AutoUpdateSwitch.IsEnabled = false; UpdateCheckBtn.IsEnabled = false; }
        }

        void SelectQuality(string key)
        {
            foreach (Quality q in QualityCombo.Items) if (q.Key == key) { QualityCombo.SelectedItem = q; return; }
            QualityCombo.SelectedIndex = 0;
        }

        string SelectedFamilyKey()
        {
            var s = DecoderCombo.SelectedItem as string;
            if (s == null || s.StartsWith("Авто")) return "auto";
            var f = Hardware.Families.FirstOrDefault(x => x.Label == s);
            return f == null ? "auto" : f.Key;
        }

        void SaveSettings()
        {
            Settings.Set("name", CleanName());
            var q = QualityCombo.SelectedItem as Quality; if (q != null) Settings.Set("quality", q.Key);
            Settings.Set("decoder", SelectedFamilyKey());
            Settings.Set("lowlat", LowLatSwitch.IsChecked == true);
            Settings.Set("audio", AudioSwitch.IsChecked == true);
            Settings.Set("fullscreen", FullscreenSwitch.IsChecked == true);
            Settings.Set("keep_window", KeepSwitch.IsChecked == true);
            Settings.Set("takeover", TakeoverSwitch.IsChecked == true);
            Settings.Set("autostart", AutoStartSwitch.IsChecked == true);
            Settings.Set("autoupdate", AutoUpdateSwitch.IsChecked == true);
            Settings.Save();
        }

        string CleanName()
        {
            string n = (NameBox.Text ?? "").Trim().Replace("\"", "");
            return n.Length > 0 ? n : Native.HostName();
        }

        void SetRunAtStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(AppInfo.Name, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue(AppInfo.Name, false);
                }
            }
            catch (Exception ex) { ShowToast("Не удалось изменить автозапуск: " + ex.Message); }
        }

        // ------------------------------------------------------------ hardware profile
        void PrepareProfile(bool force)
        {
            Engine.Locate();
            if (!Engine.Present) { Ui(() => SetState(State.Error, "Движок не найден", "Переустановите KeidoScreen")); return; }
            var p = force ? null : Profile.Parse(Settings.Get("profile", ""));
            if (p == null || p.Signature != Hardware.Signature() || p.Ok264.Count == 0)
            {
                Ui(() => SetState(State.Probing, "Настраиваюсь под ваш ПК", "Проверяю видеокарту — это нужно только один раз"));
                p = Hardware.Probe(msg => Ui(() => StatusHint.Text = msg));
                Settings.Set("profile", p.Serialize());
                Settings.Save();
                Log("Подбор под ПК: H.264 — " + string.Join(", ", p.Ok264) + "; H.265 — " + (p.Ok265.Count > 0 ? string.Join(", ", p.Ok265) : "нет") +
                    (p.SoftwareFps > 0 ? string.Format("; процессор ≈ {0:0} к/с 1080p", p.SoftwareFps) : ""));
            }
            if (p.Ok264.Count == 0)
            {
                Ui(() => SetState(State.Error, "Видео не поддерживается", "Ни один декодер не заработал. Обновите драйвер видеокарты и нажмите «Заново подобрать настройки»."));
                return;
            }
            Ui(() =>
            {
                profile = p;
                var items = new List<string> { "Авто — " + Hardware.BestFamily(p).Label };
                items.AddRange(Hardware.Families.Where(f => p.Ok264.Contains(f.Key)).Select(f => f.Label));
                DecoderCombo.ItemsSource = items;
                string saved = Settings.Get("decoder", "auto");
                var fam = Hardware.Get(saved);
                DecoderCombo.SelectedItem = fam != null && p.Ok264.Contains(saved) ? (object)fam.Label : items[0];
                SetState(State.Idle, "Готов", "Нажмите кнопку, чтобы начать приём");
                RefreshSummary();
                if (Settings.Get("autostart", true) || startInTray) StartEngine();
            });
        }

        void RefreshSummary()
        {
            if (profile == null) return;
            var pl = BuildPlan();
            QualityValue.Text = pl.Summary;
            GpuValue.Text = pl.Family.Hardware ? (profile.GpuName.Length > 0 ? profile.GpuName : pl.Family.Label) + " · " + pl.Family.Label
                                               : "Процессор (видеокарта не подошла)";
            NetValue.Text = Native.LocalIp();
        }

        RunPlan BuildPlan()
        {
            var q = QualityCombo.SelectedItem as Quality;
            string fam = SelectedFamilyKey();
            if (fam == "auto" && failedFamily != null)
            {
                // a decoder crashed this session: continue with the next working one
                var next = Hardware.NextFamily(profile, failedFamily);
                if (next != null) fam = next.Key;
            }
            return Planner.Build(profile, CleanName(), q == null ? "auto" : q.Key, fam, LowLatSwitch.IsChecked == true,
                AudioSwitch.IsChecked == true, FullscreenSwitch.IsChecked == true, KeepSwitch.IsChecked == true, TakeoverSwitch.IsChecked == true);
        }

        // ------------------------------------------------------------ engine
        void Toggle()
        {
            if (state == State.Probing) return;
            if (proc != null && !proc.HasExited) StopEngine(); else { autoRestarts = 0; StartEngine(); }
        }

        void StartEngine()
        {
            if (profile == null || (proc != null && !proc.HasExited)) return;
            SaveSettings();
            plan = BuildPlan();
            string args = string.Join(" ", plan.Args.Select(Planner.Quote));
            Log("> uxplay " + args);
            var psi = new ProcessStartInfo(Engine.Uxplay, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Engine.Bin
            };
            Engine.Prepare(psi);
            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) Ui(() => OnLine(e.Data)); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) Ui(() => OnLine(e.Data)); };
                p.Exited += (s, e) => Ui(() => OnExited(p));
                p.Start();
                Native.KillWithParent(p);
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                proc = p;
                engineStarted = DateTime.Now;
            }
            catch (Exception ex) { SetState(State.Error, "Не удалось запустить", ex.Message); return; }
            SetState(State.Waiting, "Ожидание iPhone", WaitingHint());
            RefreshSummary();
            QualityValue.Text = plan.Summary;
            volumeTimer.Start();
        }

        string WaitingHint()
        {
            return "Пункт управления → Повтор экрана →\n«" + CleanName() + "»";
        }

        void StopEngine()
        {
            var p = proc; proc = null;
            volumeTimer.Stop();
            if (p != null) { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { } }
            Native.KeepAwake(false);
            if (profile != null) SetState(State.Idle, "Остановлено", "Нажмите кнопку, чтобы начать приём");
        }

        void RestartEngine(string why)
        {
            var p = proc; proc = null;
            if (p != null) { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { } }
            ShowToast(why);
            StartEngine();
        }

        void OnExited(Process p)
        {
            if (p != proc) return;
            proc = null;
            volumeTimer.Stop();
            Native.KeepAwake(false);
            uint code = unchecked((uint)p.ExitCode);
            Log("Движок завершился (код " + (code >= 0xC0000000 ? "0x" + code.ToString("X8") : p.ExitCode.ToString()) + ").");
            // Windows-level failures have nothing to do with the decoder: switching decoders won't help
            string fatal = null;
            switch (code)
            {
                case 0xC000001D: fatal = "Движок несовместим с этим процессором. Обновите KeidoScreen до последней версии."; break;
                case 0xC0000135: case 0xC0000139: case 0xC000007B:
                    fatal = "Файлы программы повреждены или неполные. Переустановите KeidoScreen."; break;
            }
            if (fatal != null)
            {
                Log(fatal);
                SetState(State.Error, "Движок не запускается", fatal);
                return;
            }
            // crashed right after start: try the next decoder automatically
            if (p.ExitCode != 0 && (DateTime.Now - engineStarted).TotalSeconds < 8 && TryFallback("Движок не запустился"))
                return;
            SetState(p.ExitCode == 0 ? State.Idle : State.Error, p.ExitCode == 0 ? "Остановлено" : "Движок остановился",
                     p.ExitCode == 0 ? "Нажмите кнопку, чтобы начать приём" : "Подробности — в журнале");
        }

        bool TryFallback(string reason)
        {
            if (SelectedFamilyKey() != "auto" || plan == null || autoRestarts >= 3) return false;
            var next = Hardware.NextFamily(profile, plan.Family.Key);
            if (next == null) return false;
            failedFamily = plan.Family.Key;
            autoRestarts++;
            Log(reason + " с декодером " + plan.Family.Label + " — переключаюсь на " + next.Label);
            RestartEngine(reason + ". Переключаюсь на " + next.Label);
            return true;
        }

        // strings come from UxPlay 1.74 sources (uxplay.cpp, video_renderer.c, httpd.c)
        void OnLine(string line)
        {
            Log(line);
            string low = line.ToLowerInvariant();
            int i = line.IndexOf("connection request from", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string who = line.Substring(i + 23);
                int j = who.IndexOf("with deviceID", StringComparison.OrdinalIgnoreCase);
                if (j > 0) who = who.Substring(0, j);
                who = who.Trim();
                int b = who.IndexOf(" (", StringComparison.Ordinal);
                SetState(State.Connected, "Подключено", b > 0 ? who.Substring(0, b) : who);
            }
            else if (low.Contains("begin streaming") || low.Contains("starting mirroring"))
            {
                SetState(State.Streaming, "Идёт трансляция", StatusHint.Text.Length > 0 && state == State.Connected ? StatusHint.Text : plan.Summary);
                Native.KeepAwake(true);
            }
            else if (low.Contains("lost connection with client"))
            {
                SetState(State.Waiting, "Связь потеряна", "Жду повторного подключения…");
                Native.KeepAwake(false);
            }
            else if (low.Contains("connection closed on socket") && state == State.Streaming)
            {
                SetState(State.Waiting, "Ожидание iPhone", WaitingHint());
                Native.KeepAwake(false);
            }
            else if (low.Contains("gstreamer error (video)") && !low.Contains("output window was closed"))
            {
                if (!TryFallback("Ошибка видео"))
                    SetState(State.Error, "Ошибка видео", "Подробности — в журнале");
            }
        }

        void Log(string line)
        {
            Ui(() =>
            {
                if (logText.Length > 300000) logText.Remove(0, 150000);
                logText.AppendLine(line);
                if (LogPage.Visibility == Visibility.Visible) { LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd(); }
            });
        }

        // ------------------------------------------------------------ visuals
        void SetState(State s, string title, string hint)
        {
            if (s == State.Waiting && restartWhenIdle && state != State.Waiting && proc != null)
            {
                restartWhenIdle = false;
                W.Dispatcher.BeginInvoke(new Action(() => { if (state == State.Waiting) RestartEngine("Настройки применены"); }));
            }
            if (s == State.Idle || s == State.Error) restartWhenIdle = false; // a fresh start uses the new settings anyway
            state = s;
            StatusTitle.Text = title;
            StatusHint.Text = hint ?? "";
            PowerBtn.IsEnabled = s != State.Probing;
            bool running = s == State.Waiting || s == State.Connected || s == State.Streaming;
            Color c = s == State.Streaming || s == State.Connected ? COk : s == State.Waiting ? CAccent : s == State.Error ? CErr : CStroke;
            Ring.Stroke = new SolidColorBrush(c);
            PowerGlyph.Foreground = new SolidColorBrush(running ? c : (s == State.Error ? CErr : CText));
            PowerGlyph.Text = s == State.Probing ? "" : "";
            Glow.Fill = new RadialGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(0x55, c.R, c.G, c.B), 0.45), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) });

            if (pulse != null) { pulse.Stop(W); pulse = null; }
            Glow.BeginAnimation(UIElement.OpacityProperty, null);
            if (s == State.Waiting || s == State.Probing)
            {
                var a = new DoubleAnimation(0.15, 0.75, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Glow.BeginAnimation(UIElement.OpacityProperty, a);
            }
            else Glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(running ? 0.9 : 0, TimeSpan.FromMilliseconds(300)));

            PowerBtn.ToolTip = running ? "Остановить приём" : "Начать приём";
            if (tray != null)
            {
                string t = AppInfo.Name + " — " + title;
                tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            }
        }

        void UpdateMuteGlyph()
        {
            bool m = Settings.Get("muted", false);
            MuteGlyph.Text = m ? "" : "";
            MuteGlyph.Foreground = new SolidColorBrush(m ? CErr : CText);
            MuteBtn.ToolTip = m ? "Включить звук" : "Выключить звук";
            VolumeSlider.Opacity = m ? 0.45 : 1;
        }

        void ApplyVolume()
        {
            var p = proc;
            if (p == null) return;
            int pid;
            try { if (p.HasExited) return; pid = p.Id; } catch { return; }
            AudioVolume.Apply(pid, (float)(VolumeSlider.Value / 100.0), Settings.Get("muted", false), AppInfo.Name,
                              Assembly.GetExecutingAssembly().Location + ",0");
        }

        // ------------------------------------------------------------ pages
        void ShowInfo(string title, string body, string actionText, Action action)
        {
            InfoTitle.Text = title;
            RenderBody(body);
            infoAction = action;
            InfoAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            InfoActionText.Text = actionText ?? "";
            ShowPage(InfoPage, true);
        }

        // Tiny markup for info pages, blocks separated by a blank line:
        //   "1. text"            numbered step with an accent badge
        //   "! Title\nbody"      tip card with an icon
        //   "# text"             large heading
        //   anything else        paragraph
        void RenderBody(string body)
        {
            InfoBody.Children.Clear();
            var text = (Brush)W.FindResource("Text");
            var muted = (Brush)W.FindResource("Muted");
            var accent = (Brush)W.FindResource("Accent");
            var icons = (FontFamily)W.FindResource("Icons");
            foreach (string raw in body.Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string b = raw.Trim();
                var m = System.Text.RegularExpressions.Regex.Match(b, @"^(\d+)\.\s+([\s\S]*)$");
                if (m.Success)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
                    var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = (Brush)W.FindResource("AccentSoft"),
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
                        Child = new TextBlock { Text = m.Groups[1].Value, Foreground = accent, FontWeight = FontWeights.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    row.Children.Add(badge);
                    row.Children.Add(new TextBlock { Text = m.Groups[2].Value, TextWrapping = TextWrapping.Wrap, Foreground = text,
                        LineHeight = 20, VerticalAlignment = VerticalAlignment.Center });
                    InfoBody.Children.Add(row);
                }
                else if (b.StartsWith("!"))
                {
                    int nl = b.IndexOf('\n');
                    string head = (nl < 0 ? b : b.Substring(0, nl)).TrimStart('!', ' ');
                    string rest = nl < 0 ? "" : b.Substring(nl + 1);
                    var card = new Border { Style = (Style)W.FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 4, 0, 10) };
                    var dp = new DockPanel();
                    var ic = new TextBlock { Text = "", FontFamily = icons, FontSize = 15, Foreground = accent,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
                    dp.Children.Add(ic);
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Foreground = text, TextWrapping = TextWrapping.Wrap });
                    if (rest.Length > 0)
                        sp.Children.Add(new TextBlock { Text = rest, Foreground = muted, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) });
                    dp.Children.Add(sp);
                    card.Child = dp;
                    InfoBody.Children.Add(card);
                }
                else if (b.StartsWith("#"))
                {
                    InfoBody.Children.Add(new TextBlock { Text = b.TrimStart('#', ' '), FontFamily = (FontFamily)W.FindResource("Display"),
                        FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 4, 0, 10) });
                }
                else
                {
                    InfoBody.Children.Add(new TextBlock { Text = b, Foreground = muted, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                        Margin = new Thickness(0, 2, 0, 12) });
                }
            }
        }

        void ShowHelp()
        {
            ShowInfo("Как подключить",
                "1. Подключите iPhone и компьютер к одному роутеру.\n\n" +
                "2. Нажмите большую кнопку — KeidoScreen начнёт ждать iPhone.\n\n" +
                "3. На iPhone откройте Пункт управления → «Повтор экрана» и выберите «" + CleanName() + "».\n\n" +
                "! Компьютера нет в списке?\nНастройки → «Разрешить в брандмауэре». И проверьте, что сеть в Windows не «Общедоступная».\n\n" +
                "! Минимальная задержка\nКомпьютер — кабелем к роутеру, iPhone — по Wi-Fi 5 ГГц.\n\n" +
                "! Полный экран\nAlt + Enter в окне трансляции.", null, null);
        }

        void ShowAbout()
        {
            ShowInfo("О программе",
                "# " + AppInfo.Name + " " + AppInfo.VersionText + "\n\n" +
                "Приёмник повтора экрана iPhone и iPad для Windows.\n\n" +
                "Работает на открытом движке UxPlay (GPL-3.0), GStreamer (LGPL) и FFmpeg (LGPL). " +
                "Программа распространяется по лицензии GPL-3.0.\n\n" +
                "iPhone, iPad и AirPlay — товарные знаки Apple Inc. KeidoScreen не связан с Apple.",
                "Открыть лицензии и исходный код", () =>
                {
                    string dir = Path.Combine(AppInfo.Dir, "licenses");
                    try { Process.Start("explorer.exe", "\"" + (Directory.Exists(dir) ? dir : AppInfo.Dir) + "\""); } catch { }
                });
        }

        void Firewall()
        {
            string ps =
                "Remove-NetFirewallRule -DisplayName 'KeidoScreen' -ErrorAction SilentlyContinue; " +
                "New-NetFirewallRule -DisplayName 'KeidoScreen' -Direction Inbound -Program '" + Engine.Uxplay.Replace("'", "''") + "' " +
                "-Action Allow -Profile Any -RemoteAddress LocalSubnet -Protocol Any | Out-Null";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command \"" + ps + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(30000);
                ShowToast("Готово: брандмауэр пропускает iPhone из локальной сети");
            }
            catch { ShowToast("Нужны права администратора"); }
        }

        // ------------------------------------------------------------ updates
        void CheckUpdates(bool manual)
        {
            if (!Updater.Configured) return;
            if (!manual)
            {
                DateTime last;
                if (DateTime.TryParse(Settings.Get("update_checked", ""), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out last) && (DateTime.UtcNow - last).TotalHours < 6) return;
            }
            if (manual) ShowToast("Проверяю обновления…");
            Task.Run(() =>
            {
                try
                {
                    var u = Updater.Check();
                    Ui(() =>
                    {
                        Settings.Set("update_checked", DateTime.UtcNow.ToString("o")); Settings.Save();
                        if (u == null) { if (manual) ShowToast("У вас последняя версия — " + AppInfo.VersionText); return; }
                        pendingUpdate = u;
                        UpdateText.Text = "Доступна версия " + u.Version.ToString(3);
                        UpdateBtn.IsEnabled = true;
                        UpdateBtn.Content = "Обновить";
                        UpdateBanner.Visibility = Visibility.Visible;
                        if (manual) ShowPage(SettingsPage, false);
                    });
                }
                catch (Exception ex) { if (manual) Ui(() => ShowToast("Не удалось проверить обновления: " + ex.Message)); }
            });
        }

        void InstallUpdate()
        {
            var u = pendingUpdate;
            if (u == null) return;
            if (!Updater.IsInstalled()) { Updater.OpenPage(u); return; } // portable copy: open the release page
            UpdateBtn.IsEnabled = false;
            Task.Run(() =>
            {
                try
                {
                    Updater.DownloadAndInstall(u, pct => Ui(() => UpdateText.Text = "Загружаю обновление… " + pct + "%"));
                    Ui(() => { UpdateText.Text = "Устанавливаю…"; Quit(); });
                }
                catch (Exception ex)
                {
                    Ui(() => { UpdateBtn.IsEnabled = true; UpdateText.Text = "Доступна версия " + u.Version.ToString(3); ShowToast("Обновление не удалось: " + ex.Message); });
                }
            });
        }

        // ------------------------------------------------------------ tray / exit
        void BuildTray()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => ShowWindow());
            menu.Items.Add("Начать / остановить приём", null, (s, e) => Toggle());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => Quit());
            System.Drawing.Icon ico = null;
            try { ico = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            tray = new WinForms.NotifyIcon { Icon = ico, Text = AppInfo.Name, ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowWindow(); };
        }

        public void ShowWindow()
        {
            W.Show();
            if (W.WindowState == WindowState.Minimized) W.WindowState = WindowState.Normal;
            W.Activate();
            W.Topmost = true; W.Topmost = false;
        }

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            try { SaveSettings(); } catch { }
            StopEngine();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Current.Shutdown();
        }

        void Ui(Action a)
        {
            var d = W.Dispatcher;
            if (d.CheckAccess()) a(); else d.BeginInvoke(a);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "KeidoScreen.SingleInstance", out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "KeidoScreen.Show"))
            {
                if (!created) { showEvent.Set(); return; } // bring the running copy to front

                Engine.Locate();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    try { File.AppendAllText(Path.Combine(AppInfo.DataDir, "crash.log"), DateTime.Now + " " + e.Exception + Environment.NewLine); } catch { }
                };
                MainController ui = null;
                app.Startup += (s, e) =>
                {
                    ui = new MainController(args.Contains("--tray"));
                    ui.Start();
                    var t = new Thread(() =>
                    {
                        while (true) { showEvent.WaitOne(); ui.W.Dispatcher.BeginInvoke((Action)ui.ShowWindow); }
                    }) { IsBackground = true };
                    t.Start();
                };
                app.Run();
            }
        }
    }
}
