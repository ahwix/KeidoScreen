// Live volume for the mirrored sound: sets the Windows audio-session volume of
// the engine process (like the per-app slider in the Windows volume mixer).
// No engine restart needed and the system master volume is not touched.
using System;
using System.Runtime.InteropServices;

namespace KeidoScreen
{
    public static class AudioVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid ctx);
            [PreserveSig] int GetMute(out bool mute);
        }

        static Guid ctx = new Guid("6B0F5C2E-4A39-4C55-9D3E-2B8C1E7A9F10");

        /// Applies volume (0..1) and mute to every audio session of the process.
        /// Returns true if at least one session was found (audio already started).
        public static bool Apply(int pid, float level, bool mute, string displayName, string iconPath)
        {
            bool found = false;
            IMMDeviceEnumerator en = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDeviceCollection devs;
                if (en.EnumAudioEndpoints(0 /* render */, 1 /* active */, out devs) != 0) return false;
                int n; devs.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (devs.Item(i, out dev) != 0) continue;
                    object o;
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) { Marshal.ReleaseComObject(dev); continue; }
                    var mgr = (IAudioSessionManager2)o;
                    IAudioSessionEnumerator sessions;
                    if (mgr.GetSessionEnumerator(out sessions) == 0)
                    {
                        int c; sessions.GetCount(out c);
                        for (int j = 0; j < c; j++)
                        {
                            IAudioSessionControl2 s;
                            if (sessions.GetSession(j, out s) != 0) continue;
                            uint spid;
                            if (s.GetProcessId(out spid) == 0 && spid == (uint)pid)
                            {
                                found = true;
                                var vol = (ISimpleAudioVolume)s;
                                vol.SetMasterVolume(Math.Max(0f, Math.Min(1f, level)), ref ctx);
                                vol.SetMute(mute, ref ctx);
                                if (displayName != null) s.SetDisplayName(displayName, ref ctx);
                                if (iconPath != null) s.SetIconPath(iconPath, ref ctx);
                            }
                            Marshal.ReleaseComObject(s);
                        }
                        Marshal.ReleaseComObject(sessions);
                    }
                    Marshal.ReleaseComObject(mgr);
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(devs);
            }
            catch { }
            finally { if (en != null) Marshal.ReleaseComObject(en); }
            return found;
        }
    }
}
