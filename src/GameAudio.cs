// Coupe le son d'un seul programme (comme le melangeur de volume de Windows), via les interfaces Core Audio.
// Seules les sessions audio qui n'etaient pas deja muettes sont coupees, et ce sont elles qui sont retablies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

static class GameAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
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
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(string name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Sessions coupees, reperees par leur identifiant d'instance : la remise du son refait l'inventaire,
    // elle marche donc depuis n'importe quel fil (fenetre, fermeture de la console...).
    static readonly HashSet<string> muted = new HashSet<string>();
    static int mutedPid;

    // Parcourt les sessions audio du processus sur toutes les sorties actives.
    static void ForEachSession(int pid, Action<string, ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devs;
        if (en.EnumAudioEndpoints(0 /* rendu */, 1 /* actif */, out devs) != 0) return;
        int nd; devs.GetCount(out nd);
        Guid iidMgr = typeof(IAudioSessionManager2).GUID;
        for (int d = 0; d < nd; d++)
        {
            IMMDevice dev; if (devs.Item(d, out dev) != 0) continue;
            object o; if (dev.Activate(ref iidMgr, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) continue;
            IAudioSessionEnumerator ses; if (((IAudioSessionManager2)o).GetSessionEnumerator(out ses) != 0) continue;
            int ns; ses.GetCount(out ns);
            for (int i = 0; i < ns; i++)
            {
                IAudioSessionControl2 s; if (ses.GetSession(i, out s) != 0) continue;
                uint p; if (s.GetProcessId(out p) != 0 || p != (uint)pid) continue;
                IntPtr idp; if (s.GetSessionInstanceIdentifier(out idp) != 0) continue;
                string id = Marshal.PtrToStringUni(idp); Marshal.FreeCoTaskMem(idp);
                action(id, (ISimpleAudioVolume)s);
            }
        }
    }

    // Coupe le son du processus (sessions pas deja muettes) ; renvoie le nombre de sessions coupees.
    public static int Mute(int pid)
    {
        lock (muted)
        {
            try
            {
                Guid ctx = Guid.Empty;
                mutedPid = pid;
                ForEachSession(pid, (id, v) =>
                {
                    bool m; v.GetMute(out m);
                    if (!m && v.SetMute(true, ref ctx) == 0) muted.Add(id);
                });
            }
            catch { }
            if (muted.Count > 0) WriteMarker(pid);
            return muted.Count;
        }
    }

    // Remet le son des sessions coupees par Mute (sans effet si rien n'a ete coupe).
    public static void Restore()
    {
        lock (muted)
        {
            if (muted.Count == 0) return;
            try
            {
                Guid ctx = Guid.Empty;
                ForEachSession(mutedPid, (id, v) => { if (muted.Contains(id)) v.SetMute(false, ref ctx); });
            }
            catch { }
            muted.Clear();
            DeleteMarker();
        }
    }

    // ---- Securite en cas de plantage ou de fermeture forcee pendant une capture
    // Windows memorise la coupure du son de chaque programme : si Capture Zeus s'arrete brutalement apres avoir
    // coupe le jeu, le jeu resterait muet, meme relance. Une note est donc ecrite quand le son est coupe, effacee
    // quand il est remis ; au lancement suivant, si la note est encore la, le son du jeu est remis.
    static string MarkerFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capture Zeus", "son_coupe.txt"); }
    }

    static void WriteMarker(int pid)
    {
        try
        {
            string name = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerFile));
            File.WriteAllText(MarkerFile, name);
        }
        catch { }
    }

    static void DeleteMarker() { try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); } catch { } }

    // A appeler au demarrage : remet le son du jeu coupe lors d'une capture interrompue.
    // Renvoie le nombre de sessions remises ; la note est gardee tant que le jeu n'est pas lance.
    public static int RecoverAfterCrash()
    {
        int n = 0;
        try
        {
            if (!File.Exists(MarkerFile)) return 0;
            string name = File.ReadAllText(MarkerFile).Trim();
            if (name.Length == 0) { DeleteMarker(); return 0; }
            System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName(name);
            if (procs.Length == 0) return 0;
            Guid ctx = Guid.Empty;
            foreach (System.Diagnostics.Process p in procs)
                ForEachSession(p.Id, (id, v) => { bool m; v.GetMute(out m); if (m && v.SetMute(false, ref ctx) == 0) n++; });
            DeleteMarker();
        }
        catch { }
        return n;
    }
}