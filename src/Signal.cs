// Signaux d'avancement de la capture. Le jeu tourne en DirectDraw plein ecran exclusif : rien ne peut s'afficher
// par-dessus, d'ou la musique de travail en boucle (arretee a la fin du panorama) + titre et clignotement de la console.
using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;

static class Signal
{
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

    public static bool Quiet;
    static readonly Dictionary<string, SoundPlayer> cache = new Dictionary<string, SoundPlayer>();

    // Son synthetise (notes en Hz, duree de chaque note en ms), joue sans bloquer, par la carte son.
    static void Play(string key, int ms, double volume, params int[] notes)
    {
        try
        {
            SoundPlayer p;
            if (!cache.TryGetValue(key, out p))
            {
                const int rate = 22050;
                int per = rate * ms / 1000, n = per * notes.Length;
                MemoryStream m = new MemoryStream();
                BinaryWriter w = new BinaryWriter(m);
                w.Write(0x46464952); w.Write(36 + n * 2); w.Write(0x45564157); w.Write(0x20746D66); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(0x61746164); w.Write(n * 2);
                for (int k = 0; k < notes.Length; k++)
                    for (int i = 0; i < per; i++)
                    {
                        double env = Math.Min(1.0, Math.Min(i, per - i) / (rate * 0.005)); // fondu de 5 ms : pas de clic
                        w.Write((short)(Math.Sin(2 * Math.PI * notes[k] * i / rate) * env * volume * 32767));
                    }
                m.Position = 0;
                p = new SoundPlayer(m); p.Load();
                cache[key] = p;
            }
            p.Play();
        }
        catch { }
    }

    // Musique de travail, jouee en boucle du debut de la capture jusqu'a la fin du panorama :
    // un fichier .wav place a cote de l'exe (music.wav en priorite) s'il existe, sinon une melodie generee par le programme.
    // Windows ne joue qu'un son a la fois par programme : tout autre son de Signal arrete la boucle (reserve aux erreurs).
    static SoundPlayer loop;
    public static void StartLoop()
    {
        if (Quiet) return;
        try
        {
            if (loop == null)
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                // music.wav ou musique.wav d'abord, sinon le premier fichier .wav du dossier (ordre alphabetique)
                List<string> files = new List<string>();
                foreach (string n in new[] { "music.wav", "musique.wav" })
                    if (File.Exists(Path.Combine(dir, n))) files.Add(Path.Combine(dir, n));
                string[] others = Directory.GetFiles(dir, "*.wav");
                Array.Sort(others, StringComparer.OrdinalIgnoreCase);
                files.AddRange(others);
                foreach (string f in files)
                {
                    try { SoundPlayer p = new SoundPlayer(f); p.Load(); loop = p; break; } catch { }   // fichier illisible : suivant
                }
                if (loop == null) { loop = new SoundPlayer(Melody()); loop.Load(); }
            }
            loop.PlayLooping();
        }
        catch { }
    }

    // Melodie de travail (composee par le programme, libre de droits) : arpege lent en mode dorien,
    // notes douces qui s'eteignent doucement, 9,6 s qui bouclent sans coupure.
    static Stream Melody()
    {
        const int rate = 22050;
        double[] notes = { 293.66, 349.23, 440.00, 523.25, 493.88, 440.00, 392.00, 349.23,
                           329.63, 392.00, 493.88, 587.33, 523.25, 440.00, 392.00, 329.63 };
        double beat = 0.6;
        int n = (int)(rate * beat * notes.Length);
        double[] mix = new double[n];
        for (int k = 0; k < notes.Length; k++)
        {
            int start = (int)(k * beat * rate), len = (int)(beat * 2.2 * rate);   // chaque note deborde sur la suivante
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1.0, t / 0.02) * Math.Exp(-t * 2.2);
                double v = (Math.Sin(2 * Math.PI * notes[k] * t) + 0.3 * Math.Sin(4 * Math.PI * notes[k] * t)) * env;
                mix[(start + i) % n] += v;   // la fin repasse au debut : boucle sans coupure
            }
            // basse tenue toutes les 4 notes
            if (k % 4 == 0)
                for (int i = 0; i < (int)(beat * 4 * rate); i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Min(1.0, t / 0.05) * Math.Exp(-t * 0.6);
                    mix[(start + i) % n] += 0.5 * Math.Sin(2 * Math.PI * notes[k] / 2 * t) * env;
                }
        }
        double peak = 0;
        foreach (double v in mix) peak = Math.Max(peak, Math.Abs(v));
        MemoryStream m = new MemoryStream();
        BinaryWriter w = new BinaryWriter(m);
        w.Write(0x46464952); w.Write(36 + n * 2); w.Write(0x45564157); w.Write(0x20746D66); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(0x61746164); w.Write(n * 2);
        foreach (double v in mix) w.Write((short)(v / peak * 0.35 * 32767));
        m.Position = 0;
        return m;
    }
    public static void StopLoop() { try { if (loop != null) loop.Stop(); } catch { } }
    public static void Failed() { Play("fail", 300, 0.6, 440, 330, 220); }

    // Fenetre du programme (mode fenetre) : l'etat et les bandeaux y sont affiches au lieu de la console.
    public static IntPtr Home;
    public static Action<string> OnStatus;
    public static Action<string, ConsoleColor> OnBanner;

    public static void Status(string text)
    {
        if (OnStatus != null) { OnStatus(text); return; }
        try { Console.Title = "Capture Zeus - " + text; } catch { }
    }

    // Fait clignoter la fenetre du programme dans la barre des taches jusqu'a ce qu'on la regarde.
    public static void FlashConsole()
    {
        FLASHWINFO fi = new FLASHWINFO { hwnd = Home != IntPtr.Zero ? Home : GetConsoleWindow(), dwFlags = 3 | 0x0C };
        fi.cbSize = (uint)Marshal.SizeOf(fi);
        FlashWindowEx(ref fi);
    }

    // Grande ligne d'etat en couleur (bandeau de la fenetre, ou console).
    public static void Banner(string text, ConsoleColor back)
    {
        if (OnBanner != null) { OnBanner(text, back); return; }
        Console.WriteLine();
        Console.BackgroundColor = back; Console.ForegroundColor = ConsoleColor.White;
        Console.Write("  " + text.Replace("\n", " : ") + "  "); Console.ResetColor(); Console.WriteLine();
        Console.WriteLine();
    }
}