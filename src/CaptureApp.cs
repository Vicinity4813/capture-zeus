// Capture de la cite entiere de Zeus : Le Maitre de l'Olympe (programme autonome, transpose de ZeusCapture.ps1).
//   Capture Zeus.exe                         -> balayage complet + panorama (par defaut)
//   Capture Zeus.exe test                    -> verifie la capture et mesure le defilement
//   Capture Zeus.exe assembler <dossier> [echelle] -> reassemble un panorama deja capture
//   options : --delai <secondes> (3 par defaut), --silencieux (sans la musique de travail),
//             --lent (reglage de defilement prudent d'origine, sans essai du reglage rapide)
// Le programme bascule lui-meme sur le jeu, lui coupe le son pendant la capture et revient sur sa fenetre a la fin.
// INSTALL.wav (integre a l'exe) joue en boucle tant qu'il travaille : la musique s'arrete quand le panorama est fini. Alt+Tab pendant la capture : pause, reprise au retour dans le jeu.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ZeusCap;

[assembly: System.Reflection.AssemblyTitle("Capture Zeus - panorama de la cité entière")]
[assembly: System.Reflection.AssemblyDescription("Assemble un panorama de la cité entière de Zeus : Le Maître de l'Olympe / Poséidon à partir de captures d'écran (défilement de la vue au clavier). Aucune connexion réseau ; les images sont enregistrées dans le dossier du programme.")]
[assembly: System.Reflection.AssemblyCompany("Capture Zeus (fan project)")]
[assembly: System.Reflection.AssemblyProduct("Capture Zeus")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Vicinity4813 - MIT License")]
[assembly: System.Resources.NeutralResourcesLanguage("fr-FR")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]
[assembly: System.Reflection.AssemblyVersion("2.4.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.4.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("2.4.2")]

static class CaptureApp
{
    const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    const double STATIC_THRESHOLD = 0.002;
    const int TOP_BAR = 30, SKIP_TOP = 64, SIDEBAR = 186, MAX_FRAMES = 4000, RIGHT_TRIM = 4;
    const double OVERLAP = 0.25;
    // Reglages de defilement (appui, intervalle, attente apres le dernier appui, en ms) : du plus rapide au plus prudent.
    // Le plus rapide qui donne un defilement regulier est retenu au demarrage.
    static readonly int[][] PROFILES = { new[] { 20, 30, 150 }, new[] { 30, 50, 220 }, new[] { 40, 80, 300 } };
    static int TAP_MS = 40, TAP_GAP_MS = 80, SETTLE_MS = 300;

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlHandler h, bool add);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    delegate bool CtrlHandler(int type);
    static CtrlHandler ctrlHandler;   // garde le delegue en vie
    static IntPtr home;               // fenetre de la console, pour y revenir a la fin

    static Process proc;
    static int[] client, vp, park;
    static string outDir;

    class View { public Bitmap Bmp; public byte[] Gray, Side, Mini; public int[] Shift, Frame; }   // Shift : recalage net sur la precedente ; Mini, Frame : mini-carte et son cadre rouge

    // Mini-carte (x, y, largeur, hauteur dans la fenetre), sous le panneau des requetes. Sur une grande carte, elle
    // defile elle aussi : le deplacement de la vue = deplacement du cadre rouge - glissement du contenu.
    // Sert a placer les captures en pleine mer, que l'image seule ne permet pas de recaler.
    static int[] mini;
    static double[] miniPx = { 0, 0 }, miniCells = { 0, 0 };
    static int miniPlaced;
    const int MINI_W = 127, MINI_H = 108;

    // Deplacement de la vue lu sur la mini-carte, en px de mini-carte, ou null
    static int[] MiniMove(View a, View b, int axis)
    {
        if (a.Frame == null || b.Frame == null || a.Frame[2] - a.Frame[0] != b.Frame[2] - b.Frame[0]
            || a.Frame[3] - a.Frame[1] != b.Frame[3] - b.Frame[1]) return null;
        int[] cs = Img.MiniShift(a.Mini, a.Frame, b.Mini, b.Frame, MINI_W, MINI_H, axis, 40);
        if (cs == null) return null;
        return new[] { (b.Frame[0] - a.Frame[0]) - cs[0], (b.Frame[1] - a.Frame[1]) - cs[1] };
    }
    static void Free(View v) { if (v != null && v.Bmp != null) { v.Bmp.Dispose(); v.Bmp = null; } }

    // Panneau lateral (x, y, largeur, hauteur dans la fenetre) : le cadre de la mini-carte y bouge quand la vue defile.
    // Sert a distinguer le bord de la carte (rien ne bouge) d'une etendue uniforme comme la pleine mer (la vue
    // defile mais l'image semble identique). sideOk : verifie au demarrage (bouge au defilement, revient a l'identique).
    static int[] side; static bool sideOk;
    const int SIDE_MOVED = 6;
    static int SideDiff(View a, View b)
    {
        if (a.Side == null || b.Side == null) return 0;
        int n = 0;
        for (int i = 0; i < a.Side.Length; i++) if (Math.Abs(a.Side[i] - b.Side[i]) > 10) n++;
        return n;
    }
    // La vue a-t-elle vraiment refuse de bouger ? (sinon : mer ou terrain uniforme, on continue)
    static bool AtEdge(View prev, View cur) { return !(sideOk && SideDiff(prev, cur) >= SIDE_MOVED); }
    // Bord de la carte : l'image n'a pas bouge (ou ne tranche pas, si la mini-carte est suivie) et la mini-carte non plus
    static bool IsEdge(View prev, View cur, Move mv) { return (mv.Static || (sideOk && !mv.Ok)) && AtEdge(prev, cur); }

    // Enregistrement des PNG en arriere-plan, pendant que la vue defile (file limitee pour borner la memoire).
    static System.Collections.Concurrent.BlockingCollection<KeyValuePair<string, Bitmap>> saveQueue;
    static Thread saver;
    static Exception saveError;
    static void StartSaver()
    {
        saveError = null;
        saveQueue = new System.Collections.Concurrent.BlockingCollection<KeyValuePair<string, Bitmap>>(6);
        saver = new Thread(() =>
        {
            foreach (KeyValuePair<string, Bitmap> kv in saveQueue.GetConsumingEnumerable())
            {
                try { if (saveError == null) kv.Value.Save(kv.Key, ImageFormat.Png); }
                catch (Exception e) { saveError = e; }
                finally { kv.Value.Dispose(); }
            }
        });
        saver.IsBackground = true; saver.Start();
    }
    static void SaveAsync(string path, Bitmap bmp)
    {
        if (saveError != null) throw new Exception(L.T("Erreur d'enregistrement des images : ", "Image saving error: ") + saveError.Message);
        saveQueue.Add(new KeyValuePair<string, Bitmap>(path, bmp));
    }
    static void StopSaver()
    {
        saveQueue.CompleteAdding(); saver.Join();
        if (saveError != null) throw new Exception(L.T("Erreur d'enregistrement des images : ", "Image saving error: ") + saveError.Message);
    }
    class Move { public bool Static, Ok; public int Dx, Dy; public int[] Res; }

    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    public static string LastPanorama, LastDir;

    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        L.Load();
        GameAudio.RecoverAfterCrash();   // son du jeu reste coupe apres une capture interrompue
        if (args.Length == 0)
        {
            // Mode fenetre (par defaut)
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.Run(new CaptureForm());
            GameAudio.Restore();
            return 0;
        }
        AllocConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.Title = "Capture Zeus";
        int delay = 3; bool slow = false; string mode = "capture"; string stitchDir = null; double scale = 1.0;
        home = GetConsoleWindow();
        ctrlHandler = t => { GameAudio.Restore(); return false; };   // fermeture de la console : le son du jeu est remis
        SetConsoleCtrlHandler(ctrlHandler, true);
        List<string> rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--delai" && i + 1 < args.Length) delay = int.Parse(args[++i]);
            else if (args[i] == "--silencieux") Signal.Quiet = true;
            else if (args[i] == "--lent") slow = true;
            else rest.Add(args[i]);
        }
        if (rest.Count > 0) mode = rest[0].ToLowerInvariant();
        if (mode == "assembler" && rest.Count > 1) { stitchDir = rest[1]; if (rest.Count > 2) scale = double.Parse(rest[2], CultureInfo.InvariantCulture); }

        int code = 0;
        if (mode == "assembler")
        {
            try
            {
                if (stitchDir == null) throw new Exception(L.T("Indiquez le dossier des captures : Capture Zeus.exe assembler <dossier> [echelle]", "Give the capture folder: Capture Zeus.exe assembler <folder> [scale]"));
                Signal.StartLoop();
                BuildPanorama(Path.GetFullPath(stitchDir), scale);
                Signal.StopLoop();
            }
            catch (Exception e) { Signal.StopLoop(); Color(ConsoleColor.Red); Console.WriteLine(e.Message); ResetColor(); code = 1; }
        }
        else code = Execute(mode == "test", delay, scale, slow) ? 0 : 1;
        Console.WriteLine(); Console.WriteLine(L.T("Appuyez sur Entrée pour fermer.", "Press Enter to close."));
        try { Console.ReadLine(); } catch { }
        return code;
    }

    // Capture complete (ou test) avec gestion des erreurs : utilisee par la fenetre et par la ligne de commande.
    public static bool Execute(bool test, int delay, double scale, bool slow)
    {
        proc = null; LastPanorama = null; LastDir = null;
        try
        {
            Run(test, delay, scale, slow);
            return true;
        }
        catch (Exception e)
        {
            Signal.StopLoop();
            Color(ConsoleColor.Red); Console.WriteLine(); Console.WriteLine(e.Message); ResetColor();
            if (saveQueue != null && !saveQueue.IsAddingCompleted) saveQueue.CompleteAdding();
            if (proc != null) { GameAudio.Restore(); ReleaseArrows(); GoHome(); }
            Signal.Failed(); Signal.FlashConsole(); Signal.Status(L.T("ARRÊTÉE", "STOPPED"));
            Signal.Banner(L.T("CAPTURE ARRÊTÉE\n", "CAPTURE STOPPED\n") + e.Message, ConsoleColor.DarkRed);
            return false;
        }
        finally
        {
            GameAudio.Restore();
        }
    }

    // Couleurs de la console (sans effet en mode fenetre).
    static void Color(ConsoleColor c) { try { Console.ForegroundColor = c; } catch { } }
    static void ResetColor() { try { Console.ResetColor(); } catch { } }

    // Met une fenetre au premier plan malgre la protection de Windows (rattachement temporaire au fil qui a le focus).
    static bool ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        uint pid, target; GetWindowThreadProcessId(h, out target);
        uint fg = GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid), me = GetCurrentThreadId();
        bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
        Native.SetForegroundWindow(h); BringWindowToTop(h);
        if (attached) AttachThreadInput(me, fg, false);
        for (int i = 0; i < 30; i++)
        {
            GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid == target) return true;
            if (i == 10) { Native.Key(0x12, false); Native.Key(0x12, true); Native.SetForegroundWindow(h); }   // 2e essai : appui sur Alt
            Thread.Sleep(100);
        }
        return false;
    }

    // Retour sur la fenetre de la console (le jeu en plein ecran se reduit).
    static void GoHome()
    {
        IntPtr h = Signal.Home != IntPtr.Zero ? Signal.Home : home;
        if (h == IntPtr.Zero || (proc != null && Native.ForegroundPid() == (uint)proc.Id && h == proc.MainWindowHandle))
        {
            h = GetConsoleWindow();
            IntPtr owner = GetAncestor(h, 3 /* GA_ROOTOWNER */);   // Terminal Windows : fenetre hote de la console
            if (owner != IntPtr.Zero) h = owner;
        }
        ForceForeground(h);
    }

    static void Run(bool test, int delay, double scale, bool slow)
    {
        string only = Environment.GetEnvironmentVariable("CAPTURE_ZEUS_JEU");   // tests : nom exact d'un autre programme
        foreach (Process p in Process.GetProcesses())
            if ((only != null ? p.ProcessName == only : p.ProcessName.StartsWith("Zeus", StringComparison.OrdinalIgnoreCase)) && p.MainWindowHandle != IntPtr.Zero) { proc = p; break; }
        if (proc == null) throw new Exception(L.T("Le jeu ne tourne pas. Lancez-le, chargez la partie et mettez-la en pause (touche P).", "The game is not running. Start it, load your game and pause it (P key)."));
        Console.WriteLine(L.T("Jeu trouvé : ", "Game found: ") + proc.ProcessName + ".exe");
        Signal.StartLoop();

        string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        outDir = Path.Combine(exeDir, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);
        LastDir = outDir;

        Console.WriteLine(L.T("La musique joue tant que le programme travaille : quand elle s'arrête, le panorama est prêt.", "The music plays while the program is working: when it stops, the panorama is ready."));
        Console.WriteLine(L.T("Un Alt+Tab trop tôt ne casse rien : la capture se met en pause et reprend quand vous revenez dans le jeu.", "An early Alt+Tab breaks nothing: the capture pauses and resumes when you go back to the game."));
        for (int i = delay; i > 0; i--)
        {
            Console.WriteLine(L.T("  Démarrage dans ", "  Starting in ") + i + " s...");
            Signal.Status(L.T("démarrage dans ", "starting in ") + i + " s");
            Signal.Banner(L.T("NE TOUCHEZ PLUS À LA SOURIS NI AU CLAVIER\nPassage sur le jeu dans ", "DO NOT TOUCH THE MOUSE OR KEYBOARD\nSwitching to the game in ") + i + " s", ConsoleColor.DarkRed);
            Thread.Sleep(1000);
        }

        IntPtr hwnd = proc.MainWindowHandle;
        Console.WriteLine(L.T("Passage sur le jeu...", "Switching to the game..."));
        Signal.Banner(L.T("CAPTURE EN COURS\nNe touchez ni à la souris ni au clavier tant que la musique joue", "CAPTURE IN PROGRESS\nDo not touch the mouse or keyboard while the music is playing"), ConsoleColor.DarkRed);
        if (!ForceForeground(hwnd)) throw new Exception(L.T("Impossible de mettre le jeu au premier plan : cliquez dans le jeu puis relancez la capture.", "Could not bring the game to the front: click in the game, then start the capture again."));
        GameAudio.RecoverAfterCrash();
        int nm = GameAudio.Mute(proc.Id);
        Console.WriteLine(nm > 0 ? L.T("Son du jeu coupé pendant la capture.", "Game sound muted during the capture.") : L.T("Son du jeu : aucune sortie audio trouvée à couper.", "Game sound: no audio output found to mute."));
        Thread.Sleep(2000);   // le jeu repasse en plein ecran et se redessine
        Signal.Status(L.T("EN COURS : réglages", "RUNNING: calibration"));

        if (Native.ForegroundPid() != (uint)proc.Id && !ForceForeground(hwnd))
            throw new Exception(L.T("La fenêtre du jeu n'est pas au premier plan.", "The game window is not in front."));
        client = Native.ClientArea(hwnd);
        Console.WriteLine(L.T("Fenêtre du jeu : ", "Game window: ") + client[2] + " x " + client[3] + " px");
        // Premier point de repos : au milieu de la carte, a gauche du panneau lateral. Si le jeu confine la souris
        // (image du jeu plus petite que la fenetre), on se cale sur cette zone : un curseur pose sur le panneau
        // lateral y fait apparaitre des surbrillances qui faussent la mesure de la vue.
        int ax0 = client[0], ay0 = client[1], ax1 = client[0] + client[2], ay1 = client[1] + client[3];
        int[] clip0 = Native.CursorClip();
        if (clip0 != null)
        {
            ax0 = Math.Max(ax0, clip0[0]); ay0 = Math.Max(ay0, clip0[1]);
            ax1 = Math.Min(ax1, clip0[2]); ay1 = Math.Min(ay1, clip0[3]);
            if (ax1 - ax0 < 2 * SIDEBAR || ay1 - ay0 < 200) { ax0 = client[0]; ay0 = client[1]; ax1 = client[0] + client[2]; ay1 = client[1] + client[3]; }
        }
        park = new[] { ax0 + (ax1 - ax0 - SIDEBAR) / 2, (ay0 + ay1) / 2 };
        ParkCursor();

        // Verification de la capture et de la pause
        Bitmap full0 = GrabFull();
        byte[] g0 = Img.Gray(full0);
        if (Img.DarkFraction(g0) > 0.97)
        {
            full0.Save(Path.Combine(outDir, "test_noir.png"), ImageFormat.Png);
            throw new Exception(L.T("La capture est noire : lancez le jeu en mode fenêtré (cnc-ddraw ou DxWnd) puis réessayez.", "The capture is black: run the game in windowed mode (cnc-ddraw or DxWnd) and try again."));
        }
        Thread.Sleep(400);
        using (Bitmap f2 = GrabFull())
        {
            double still = Img.ChangedFraction(g0, Img.Gray(f2));
            if (still > 0.01) { Color(ConsoleColor.Yellow); Console.WriteLine(string.Format(L.T("Attention : l'image bouge sans défilement ({0:P1}) : la partie n'est peut-être pas en pause.", "Warning: the picture moves without scrolling ({0:P1}): the game may not be paused."), still)); ResetColor(); }
        }

        // Zone de la carte. D'abord la geometrie : l'image du jeu (barre du haut et panneau lateral, jamais noirs ;
        // le reste de la fenetre est noir si le jeu est plus petit que l'ecran), puis la largeur de vue du jeu,
        // un multiple de 60 px moins 2 (1024 -> 838, 1920 -> 1738, 2560 -> 2338). La mesure au defilement ne sert
        // qu'a affiner de quelques pixels (selon l'exe) : seule, elle se trompait quand la mer occupait la vue.
        int[] pic = Img.PictureRect(g0, client[2], client[3]);
        if (pic == null) pic = new[] { 0, 0, client[2], client[3] };
        int viewL = pic[0], picTop = pic[1], bottom = pic[3];
        int viewW = 60 * ((pic[2] - pic[0] - 180) / 60) - 2;
        int[] dv = DetectView();
        if (dv != null && Math.Abs(dv[0] - viewL) <= 8 && Math.Abs(dv[1] - (viewL + viewW)) <= 8) viewW = dv[1] - viewL;
        Console.WriteLine(L.T("Largeur de la vue : ", "View width: ") + viewW + " px");
        if (pic[0] > 0 || pic[1] > 0 || pic[2] < client[2] || pic[3] < client[3])
            Console.WriteLine(string.Format(L.T("Le jeu n'occupe qu'une partie de la fenêtre : image de {0},{1} à {2},{3}", "The game only fills part of the window: picture from {0},{1} to {2},{3}"), pic[0], pic[1], pic[2], pic[3]));
        int top = picTop + TOP_BAR + SKIP_TOP;
        // Le jeu laisse une bordure de 2 px (couleur sable) au bord droit de la vue, juste avant le panneau lateral :
        // elle formait un lisere dans le panorama la ou une capture en recouvre une autre. On rogne 4 px.
        viewW -= RIGHT_TRIM;
        vp = new[] { viewL, top, viewW, bottom - top };
        Console.WriteLine(string.Format(L.T("Zone de la carte : x={0} y={1} {2} x {3} px", "Map area: x={0} y={1} {2} x {3} px"), vp[0], vp[1], vp[2], vp[3]));
        park = new[] { client[0] + vp[0] + vp[2] / 2, client[1] + vp[1] + vp[3] / 2 };
        ParkCursor();
        Thread.Sleep(200);

        // Panneau lateral, pour reconnaitre le bord de la carte en pleine mer (voir AtEdge)
        side = null; sideOk = false;   // nouvelle capture dans la meme fenetre
        int sideX = vp[0] + vp[2] + RIGHT_TRIM, sideW = Math.Min(SIDEBAR - 4, pic[2] - sideX);
        if (sideW >= 60)
        {
            side = new[] { sideX, picTop, sideW, bottom - picTop };
            int[][] probe = { new[] { VK_RIGHT, VK_LEFT }, new[] { VK_LEFT, VK_RIGHT }, new[] { VK_DOWN, VK_UP } };
            foreach (int[] pr in probe)
            {
                AssertSafe();
                View a = GrabView(); TapSafe(pr[0], 4); Thread.Sleep(SETTLE_MS);
                View b = GrabView(); TapSafe(pr[1], 4); Thread.Sleep(SETTLE_MS);
                View c = GrabView();
                int moved = SideDiff(a, b), back = SideDiff(a, c);
                Free(a); Free(b); Free(c);
                if (back > 2) break;                                   // le panneau change tout seul : inutilisable
                if (moved >= SIDE_MOVED) { sideOk = true; break; }
            }
            if (!sideOk) side = null;
        }
        // Mini-carte : position fixe dans le panneau lateral (relevee en 1024x768), validee par la presence du cadre rouge
        mini = null; miniPx = new double[] { 0, 0 }; miniCells = new double[] { 0, 0 }; miniPlaced = 0;
        if (sideOk && pic[2] - 136 >= 0 && pic[1] + 319 + MINI_H <= client[3])
        {
            mini = new[] { pic[2] - 136, pic[1] + 319, MINI_W, MINI_H };
            View t = GrabView();
            if (t.Frame == null) mini = null;
            Free(t);
        }
        Console.WriteLine(mini != null ? L.T("Cadre de la vue trouvé sur la mini-carte.", "View frame found on the minimap.")
                                       : L.T("Cadre de la vue introuvable sur la mini-carte.", "View frame not found on the minimap."));
        Console.WriteLine(sideOk ? L.T("Mini-carte suivie : la pleine mer ne sera pas prise pour le bord de la carte.", "Minimap tracked: open sea will not be mistaken for the map edge.")
                                 : L.T("Mini-carte non suivie : le bord de la carte est reconnu à l'image seule.", "Minimap not tracked: the map edge is recognised from the picture only."));

        // Cran de defilement, mesure au centre de la ville, avec le reglage le plus rapide qui reste regulier
        double[] tapStep = { 0, 0 }; int[] taps = { 1, 1 };
        double[] target = { (1 - OVERLAP) * vp[2], (1 - OVERLAP) * vp[3] };
        int[][] tries = { new[] { 0, VK_RIGHT, VK_LEFT, 1 }, new[] { 0, VK_LEFT, VK_RIGHT, -1 }, new[] { 1, VK_DOWN, VK_UP, 1 }, new[] { 1, VK_UP, VK_DOWN, -1 } };
        for (int pi = slow ? PROFILES.Length - 1 : 0; pi < PROFILES.Length; pi++)
        {
            TAP_MS = PROFILES[pi][0]; TAP_GAP_MS = PROFILES[pi][1]; SETTLE_MS = PROFILES[pi][2];
            tapStep = new double[] { 0, 0 };
            int[][] okTry = new int[2][];   // sens qui a fonctionne pour chaque axe (l'autre peut buter sur un bord)
            foreach (int[] m in tries)
            {
                if (tapStep[m[0]] > 0) continue;
                int along = MeasureTaps(m, 4, 0);
                if (along > 0) { tapStep[m[0]] = along / 4.0; okTry[m[0]] = m; }
            }
            bool last = pi == PROFILES.Length - 1;
            if (tapStep[0] <= 0 || tapStep[1] <= 0) { if (last) break; continue; }
            if (last) break;
            // Controle : 8 appuis doivent donner 2 fois le deplacement de 4 (sinon des appuis sont perdus)
            bool regular = true;
            for (int axis = 0; axis < 2 && regular; axis++)
            {
                int along = MeasureTaps(okTry[axis], 8, 8 * tapStep[axis]);
                regular = along > 0 && Math.Abs(along - 8 * tapStep[axis]) <= Math.Max(4, tapStep[axis] / 2);
            }
            if (regular) break;
            Console.WriteLine(L.T("Défilement irrégulier avec le réglage ", "Irregular scrolling with the ") + (pi == 0 ? L.T("rapide", "fast") : L.T("intermédiaire", "medium")) + L.T(" : essai d'un réglage plus prudent.", " setting: trying a safer one."));
        }
        if (tapStep[0] <= 0 || tapStep[1] <= 0)
            throw new Exception(L.T("Impossible de mesurer le défilement. Placez la vue au milieu de la ville et vérifiez que les flèches font défiler la vue.", "Could not measure the scrolling. Put the view in the middle of the city and check that the arrow keys scroll the view."));
        Console.WriteLine(string.Format(L.T("Réglage du défilement : appui {0} ms, intervalle {1} ms, attente {2} ms", "Scroll setting: key press {0} ms, gap {1} ms, wait {2} ms"), TAP_MS, TAP_GAP_MS, SETTLE_MS));
        for (int axis = 0; axis < 2; axis++)
        {
            taps[axis] = Math.Max(1, (int)Math.Floor(target[axis] / tapStep[axis]));
            Console.WriteLine(string.Format(L.T("Défilement {0} : {1:N0} px par appui ; {2} appuis par pas ({3:N0} px)", "Scrolling {0}: {1:N0} px per key press; {2} presses per step ({3:N0} px)"),
                axis == 0 ? L.T("horizontal", "horizontally") : L.T("vertical", "vertically"), tapStep[axis], taps[axis], taps[axis] * tapStep[axis]));
        }

        if (test)
        {
            full0.Save(Path.Combine(outDir, "test_full.png"), ImageFormat.Png);
            View v = GrabView(); v.Bmp.Save(Path.Combine(outDir, "test_vue.png"), ImageFormat.Png); Free(v);
            GameAudio.Restore(); ReleaseArrows();
            GoHome(); Signal.StopLoop(); Signal.FlashConsole(); Signal.Status(L.T("TEST TERMINÉ", "TEST DONE"));
            Signal.Banner(L.T("TEST RÉUSSI\nImages de contrôle dans le dossier de la capture", "TEST PASSED\nCheck images are in the capture folder"), ConsoleColor.DarkGreen);
            Color(ConsoleColor.Green); Console.WriteLine(L.T("Test réussi. Images de contrôle dans ", "Test passed. Check images in ") + outDir); ResetColor();
            Console.WriteLine(L.T("  test_full.png : la fenêtre entière ; test_vue.png : la zone de la carte capturée.", "  test_full.png: the whole window; test_vue.png: the captured map area."));
            return;
        }
        full0.Dispose();

        // Coin haut-gauche
        Console.WriteLine(L.T("Placement dans le coin haut-gauche de la carte...", "Moving to the top-left corner of the map..."));
        Signal.Status(L.T("EN COURS : placement dans le coin", "RUNNING: moving to the corner"));
        GoEdge(VK_UP, 1, -1, tapStep[1]);
        GoEdge(VK_LEFT, 0, -1, tapStep[0]);

        // Balayage en serpentin
        StartSaver();
        Tracker tracker = new Tracker(vp[2], vp[3]);
        string csv = Path.Combine(outDir, "positions.csv");
        File.WriteAllText(csv, "file,x,y\r\n", Encoding.ASCII);
        List<string> names = new List<string>();
        List<int> rowOf = new List<int>();
        int row = 1;
        Action<View, int, int, bool> saveFrame = (f, pdx, pdy, newRow) =>
        {
            string name = string.Format("frame_{0:D4}.png", names.Count + 1);
            SaveAsync(Path.Combine(outDir, name), f.Bmp); f.Bmp = null;   // l'image appartient desormais a l'enregistreur
            names.Add(name); rowOf.Add(newRow ? row + 1 : row);   // la capture apres le pas vers le bas ouvre la rangee suivante
            Signal.Status(string.Format(L.T("EN COURS : rangée {0}, {1} images", "RUNNING: row {0}, {1} images"), row, names.Count));
            int[] r = tracker.Add(f.Gray, pdx, pdy, newRow, f.Shift);
            File.AppendAllText(csv, name + "," + r[0] + "," + r[1] + "\r\n", Encoding.ASCII);
        };
        Func<View, int, int, int, bool, View> step = (prev, vk, axis, sign, newRow) =>
        {
            AssertSafe();
            TapSafe(vk, taps[axis]); Thread.Sleep(SETTLE_MS);
            View cur = GrabView();
            double expected = taps[axis] * tapStep[axis];
            Move mv = MeasureMove(prev, cur, axis, sign, expected);
            if (IsEdge(prev, cur, mv))
            {
                // Le jeu met parfois plus longtemps a redessiner : la capture montrait encore l'ancienne vue,
                // prise pour le bord de la carte (et le retard se reportait sur la rangee suivante). Second avis.
                Thread.Sleep(Math.Max(500, 3 * SETTLE_MS));
                View again = GrabView();
                Move m2 = MeasureMove(prev, again, axis, sign, expected);
                if (IsEdge(prev, again, m2)) { Free(again); Free(cur); return null; }
                Free(cur); cur = again; mv = m2;
            }
            if (!mv.Ok) mv = new Move();   // l'image ne tranche pas (pleine mer) mais la vue a bouge : position estimee
            int pdx = mv.Ok ? mv.Dx : (axis == 0 ? (int)(sign * expected) : 0);
            int pdy = mv.Ok ? mv.Dy : (axis == 1 ? (int)(sign * expected) : 0);
            int[] mm = mini != null ? MiniMove(prev, cur, axis) : null;
            if (mm != null && mv.Ok)
            {
                // Echelle de la mini-carte (px de carte par px de mini-carte), apprise sur les deplacements mesures
                if (Math.Abs(mm[axis]) >= 3) { miniPx[axis] += Math.Abs(axis == 0 ? mv.Dx : mv.Dy); miniCells[axis] += Math.Abs(mm[axis]); }
            }
            else if (mm != null)
            {
                // Pleine mer : deplacement lu sur la mini-carte, arrondi au cran de defilement,
                // dans le sens appuye et au plus le pas complet
                double sc = miniCells[axis] >= 30 ? miniPx[axis] / miniCells[axis]
                          : axis == 0 ? (vp[2] + RIGHT_TRIM) / (double)(cur.Frame[2] - cur.Frame[0]) : (vp[3] + SKIP_TOP) / (double)(cur.Frame[3] - cur.Frame[1]);
                double est = Math.Round(mm[axis] * sc / tapStep[axis]) * tapStep[axis];
                est = sign * Math.Max(tapStep[axis], Math.Min(taps[axis] * tapStep[axis], sign * est));
                if (axis == 0) pdx = (int)est; else pdy = (int)est;
                miniPlaced++;
            }
            cur.Shift = mv.Ok ? mv.Res : null;
            saveFrame(cur, pdx, pdy, newRow);
            return cur;
        };

        int dir = 1;
        View p0 = GrabView();
        saveFrame(p0, 0, 0, false);
        DateTime started = DateTime.Now;
        Console.WriteLine(L.T("Balayage en cours (bouger la souris pour arrêter)...", "Sweeping (move the mouse to stop)..."));
        View pv = p0;
        while (names.Count < MAX_FRAMES)
        {
            int vk = dir > 0 ? VK_RIGHT : VK_LEFT;
            while (names.Count < MAX_FRAMES)
            {
                View cur = step(pv, vk, 0, dir, false);
                if (cur == null) break;
                Free(pv); pv = cur;
            }
            Console.WriteLine(string.Format(L.T("  Rangée {0} terminée ({1} captures au total)", "  Row {0} done ({1} captures in total)"), row, names.Count));
            View down = step(pv, VK_DOWN, 1, 1, true);
            if (down == null) break;
            Free(pv); pv = down;
            dir = -dir; row++;
        }
        Free(pv);
        Console.WriteLine(string.Format(L.T("Balayage terminé : {0} captures en {1:N0} s.", "Sweep done: {0} captures in {1:N0} s."), names.Count, (DateTime.Now - started).TotalSeconds));
        if (miniPlaced > 0) Console.WriteLine(string.Format(L.T("{0} captures en pleine mer placées grâce à la mini-carte (échelle {1:F1} x {2:F1}).", "{0} open-sea captures placed using the minimap (scale {1:F1} x {2:F1})."),
            miniPlaced, miniCells[0] > 0 ? miniPx[0] / miniCells[0] : 0, miniCells[1] > 0 ? miniPx[1] / miniCells[1] : 0));
        GameAudio.Restore(); ReleaseArrows();
        GoHome(); Signal.Status(L.T("EN COURS : assemblage du panorama", "RUNNING: assembling the panorama"));
        Signal.Banner(L.T("BALAYAGE TERMINÉ : vous pouvez reprendre la main\nAssemblage du panorama en cours (la musique s'arrêtera à la fin)", "SWEEP DONE: you can use your computer again\nAssembling the panorama (the music will stop at the end)"), ConsoleColor.DarkYellow);
        Console.WriteLine(L.T("Fin de l'enregistrement des images...", "Finishing saving the images..."));
        StopSaver();

        int[][] sol = tracker.Solve();
        int anchored = Rows.Anchor(sol[0], rowOf.ToArray());
        if (anchored > 0) Console.WriteLine(string.Format(L.T("{0} capture(s) recalée(s) sur les bords de la carte.", "{0} capture(s) realigned on the map edges."), anchored));
        StringBuilder sb = new StringBuilder("file,x,y\r\n");
        for (int i = 0; i < names.Count; i++) sb.Append(names[i] + "," + sol[0][i] + "," + sol[1][i] + "\r\n");
        File.WriteAllText(csv, sb.ToString(), Encoding.ASCII);
        if (tracker.Groups() > 1)
        {
            Color(ConsoleColor.Yellow);
            Console.WriteLine(tracker.Fallbacks + L.T(" captures n'ont pas pu être recalées (en général au bord de la carte) ; leur position est estimée.", " captures could not be aligned (usually at the map edge); their position is estimated."));
            ResetColor();
        }
        LastPanorama = BuildPanorama(outDir, scale);
        Signal.StopLoop(); Signal.FlashConsole(); Signal.Status(L.T("TERMINÉ", "DONE"));
        Signal.Banner(L.T("PANORAMA TERMINÉ\n", "PANORAMA DONE\n") + Path.GetFileName(outDir) + "\\" + Path.GetFileName(LastPanorama), ConsoleColor.DarkGreen);
    }

    // Deplacement mesure (en px, 0 si echec) pour n appuis dans le sens m, puis retour au point de depart.
    static int MeasureTaps(int[] m, int n, double expected)
    {
        AssertSafe();
        View a = GrabView();
        TapSafe(m[1], n); Thread.Sleep(SETTLE_MS);
        View b = GrabView();
        TapSafe(m[2], n); Thread.Sleep(SETTLE_MS);
        Move mv = MeasureMove(a, b, m[0], m[3], expected);
        a.Bmp.Dispose(); b.Bmp.Dispose();
        return mv.Ok ? Math.Abs(m[0] == 0 ? mv.Dx : mv.Dy) : 0;
    }

    // Zone de la vue de la carte, mesuree a l'ecran : on fait defiler de 4 crans et on cherche les lignes et colonnes
    // qui changent (le panneau lateral, a droite, ne bouge pas). Renvoie { gauche, droite, bas } ou null si la mesure echoue.
    // Le jeu ne remplit pas toujours la fenetre : un exe 1920x1080 sur un ecran 3440x1440 dessine dans un coin
    // (ou au centre) et laisse le reste noir et immobile.
    static int[] DetectView()
    {
        int w = client[2], h = client[3];
        int[][] moves = { new[] { VK_RIGHT, VK_LEFT }, new[] { VK_LEFT, VK_RIGHT } };   // l'autre sens si la vue bute sur un bord
        foreach (int[] mv in moves)
        {
            AssertSafe();
            byte[] a, b;
            using (Bitmap f = GrabFull()) a = Img.Gray(f);
            TapSafe(mv[0], 4); Thread.Sleep(SETTLE_MS);
            using (Bitmap f = GrabFull()) b = Img.Gray(f);
            TapSafe(mv[1], 4); Thread.Sleep(SETTLE_MS);
            Func<int, int, bool> diff = (x, y) => Math.Abs(a[y * w + x] - b[y * w + x]) > 10;

            // Bas : derniere ligne qui change
            int cols = (w + 1) / 2, bottom = -1;
            for (int y = h - 1; y >= h / 4 && bottom < 0; y--)
            {
                int n = 0;
                for (int x = 0; x < w; x += 2) if (diff(x, y)) n++;
                if (n >= cols / 100 + 2) bottom = y + 1;
            }
            if (bottom < 0) continue;

            // Gauche et droite : premiere et derniere colonne qui changent, dans la moitie basse de l'image du jeu
            int y0 = bottom / 2, rows = 0, left = -1, right = -1;
            for (int y = y0; y < bottom; y += 2) rows++;
            for (int x = w - 1; x >= 0 && right < 0; x--)
            {
                int n = 0;
                for (int y = y0; y < bottom; y += 2) if (diff(x, y)) n++;
                if (n >= rows / 50 + 2) right = x + 1;
            }
            for (int x = 0; x < right && left < 0; x++)
            {
                int n = 0;
                for (int y = y0; y < bottom; y += 2) if (diff(x, y)) n++;
                if (n >= rows / 50 + 2) left = x;
            }
            if (left < 8) left = 0;   // image du jeu calee a gauche : la carte commence au bord
            if (right - left >= 320 && bottom >= 300) return new[] { left, right, bottom };
        }
        return null;
    }
    static Bitmap GrabFull() { return Img.Capture(client[0], client[1], client[2], client[3]); }

    // Capture de la zone de la carte ; refaite si le jeu a quitte le premier plan pendant la capture.
    static View GrabView()
    {
        while (true)
        {
            WaitForGame();
            View v = new View();
            v.Bmp = Img.Capture(client[0] + vp[0], client[1] + vp[1], vp[2], vp[3]);   // seulement la zone de la carte
            if (Native.ForegroundPid() != (uint)proc.Id) { v.Bmp.Dispose(); continue; }
            v.Gray = Img.Gray(v.Bmp);
            if (side != null) using (Bitmap s = Img.Capture(client[0] + side[0], client[1] + side[1], side[2], side[3])) v.Side = Img.Rgb(s);
            if (mini != null) using (Bitmap s = Img.Capture(client[0] + mini[0], client[1] + mini[1], mini[2], mini[3])) { v.Mini = Img.Rgb(s); v.Frame = Img.FindRedFrame(v.Mini, mini[2], mini[3]); }
            return v;
        }
    }

    public static void ReleaseArrows() { foreach (int k in new[] { VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN }) Native.Key(k, true); }

    // Appuis sur une fleche, un par un, chacun seulement quand le jeu est au premier plan (aucun appui perdu).
    static void TapSafe(int vk, int count)
    {
        for (int i = 0; i < count; i++)
        {
            WaitForGame();
            Native.Key(vk, false); Thread.Sleep(TAP_MS);
            Native.Key(vk, true); Thread.Sleep(TAP_GAP_MS);
        }
    }

    // Alt+Tab pendant la capture : pause jusqu'au retour du jeu au premier plan, puis reprise.
    static void WaitForGame()
    {
        if (Native.ForegroundPid() == (uint)proc.Id) return;
        ReleaseArrows();
        Signal.FlashConsole(); Signal.Status(L.T("EN PAUSE : revenez dans le jeu", "PAUSED: go back to the game"));
        Signal.Banner(L.T("CAPTURE EN PAUSE : le jeu n'est plus au premier plan", "CAPTURE PAUSED: the game is no longer in front"), ConsoleColor.DarkYellow);
        Console.WriteLine(L.T("Revenez dans le jeu par Alt+Tab (sans cliquer sur la carte ni la faire défiler) :", "Go back to the game with Alt+Tab (without clicking on the map or scrolling it):"));
        Console.WriteLine(L.T("la capture reprendra 3 secondes après.", "the capture will resume 3 seconds later."));
        while (true)
        {
            while (Native.ForegroundPid() != (uint)proc.Id) Thread.Sleep(200);
            bool lost = false;
            for (int i = 3; i > 0 && !lost; i--)
            {
                Console.WriteLine(L.T("  Reprise dans ", "  Resuming in ") + i + " s...");
                Thread.Sleep(1000);
                lost = Native.ForegroundPid() != (uint)proc.Id;
            }
            if (!lost) break;
        }
        ReleaseArrows();
        Thread.Sleep(800);   // le jeu redessine l'ecran apres sa restauration
        ParkCursor();
        Signal.Status(L.T("EN COURS", "RUNNING"));
        Color(ConsoleColor.Green); Console.WriteLine(L.T("Reprise de la capture.", "Capture resumed.")); ResetColor();
    }

    // Place le curseur au point de repos et attend qu'il y reste : en revenant au premier plan, le jeu (ou Windows)
    // peut deplacer le curseur lui-meme juste apres, ce qui passait pour un mouvement de l'utilisateur.
    static DateTime cursorGrace;
    static void ParkCursor()
    {
        for (int i = 0; i < 10; i++)
        {
            // Zeus confine la souris a sa zone d'origine (1024x768) meme dans une resolution plus grande : un point
            // de repos hors de cette zone etait ramene au bord par Windows (1023), ce qui passait pour un mouvement.
            int[] clip = Native.CursorClip();
            if (clip != null)
                for (int k = 0; k < 2; k++)
                {
                    int lo = clip[k], hi = clip[k + 2] - 1, m = Math.Min(40, (hi - lo) / 4);
                    int p = Math.Max(lo + m, Math.Min(hi - m, park[k]));
                    if (p != park[k])
                    {
                        Console.WriteLine(string.Format(L.T("Souris confinée par le jeu ({0},{1} - {2},{3}) : point de repos déplacé.", "Mouse confined by the game ({0},{1} - {2},{3}): rest point moved."), clip[0], clip[1], clip[2], clip[3]));
                        park[k] = p;
                    }
                }
            Native.SetCursorPos(park[0], park[1]);
            Thread.Sleep(300);
            if (CursorParked()) break;
        }
        cursorGrace = DateTime.Now.AddSeconds(4);
    }
    static bool CursorParked()
    {
        int[] c = Native.CursorPos();
        return Math.Abs(c[0] - park[0]) <= 8 && Math.Abs(c[1] - park[1]) <= 8;
    }

    static void AssertSafe()
    {
        WaitForGame();
        if (CursorParked()) return;
        // Juste apres le retour sur le jeu, un deplacement isole fait par le jeu est corrige (deuxieme avis) ;
        // ensuite, tout mouvement arrete la capture comme avant.
        int[] c1 = Native.CursorPos();
        if (DateTime.Now > cursorGrace)
            throw new Exception(string.Format(L.T("Arrêt : la souris a bougé (attendue en {0},{1}, trouvée en {2},{3}).", "Stopped: the mouse moved (expected at {0},{1}, found at {2},{3})."), park[0], park[1], c1[0], c1[1]));
        Native.SetCursorPos(park[0], park[1]);
        Thread.Sleep(400);
        if (CursorParked()) { Console.WriteLine(string.Format(L.T("Curseur déplacé par le jeu ({0},{1}) : remis en place.", "Cursor moved by the game ({0},{1}): put back."), c1[0], c1[1])); return; }
        int[] c = Native.CursorPos();
        throw new Exception(string.Format(L.T("Arrêt : la souris a bougé (attendue en {0},{1}, trouvée en {2},{3}).", "Stopped: the mouse moved (expected at {0},{1}, found at {2},{3})."), park[0], park[1], c[0], c[1]));
    }

    // Deplacement de la vue entre deux captures (la mer s'anime meme en pause : decision sur le decalage mesure).
    static Move MeasureMove(View a, View b, int axis, int sign, double expected)
    {
        Move r = new Move();
        if (Img.ChangedFraction(a.Gray, b.Gray) < STATIC_THRESHOLD) { r.Static = true; return r; }
        int w = vp[2], h = vp[3];
        int[] s0 = Img.FindShiftNear(a.Gray, b.Gray, w, h, 0, 0, 6);
        int[] sp = null;
        if (expected > 0)
        {
            int pdx = axis == 0 ? (int)(sign * expected) : 0, pdy = axis == 1 ? (int)(sign * expected) : 0;
            int margin = (int)(32 + 0.2 * expected);   // incertitude dans le sens du defilement seulement
            sp = Img.FindShiftNear(a.Gray, b.Gray, w, h, pdx, pdy, axis == 0 ? margin : 16, axis == 1 ? margin : 16);
        }
        if (sp != null && s0 != null && s0[2] <= 600 && s0[2] <= sp[2]) { r.Static = true; return r; }
        int[] best = null;
        if (Img.IsReliable(sp, 6, 2)) best = sp;
        else
        {
            int[] sd = Img.FindShift(a.Gray, b.Gray, w, h, axis, sign);
            if (Img.IsReliable(sd, 6, 2)) best = sd;
        }
        if (best == null) return r;
        if (Math.Abs(best[axis]) < 2) { r.Static = true; return r; }
        r.Ok = true; r.Dx = best[0]; r.Dy = best[1]; r.Res = best;
        return r;
    }

    static void GoEdge(int vk, int axis, int sign, double tapStep)
    {
        View prev = GrabView();
        for (int n = 0; n < 300; n++)
        {
            AssertSafe();
            TapSafe(vk, 10); Thread.Sleep(SETTLE_MS);
            View cur = GrabView();
            Move mv = MeasureMove(prev, cur, axis, sign, 10 * tapStep);
            bool edge = IsEdge(prev, cur, mv);
            if (edge)
            {
                Thread.Sleep(Math.Max(500, 3 * SETTLE_MS));   // second avis (redessin lent)
                View again = GrabView();
                edge = IsEdge(prev, again, MeasureMove(prev, again, axis, sign, 10 * tapStep));
                cur.Bmp.Dispose(); cur = again;
            }
            prev.Bmp.Dispose(); prev = cur;
            if (edge) break;
        }
        prev.Bmp.Dispose();
    }

    static string BuildPanorama(string dir, double scale)
    {
        string csv = Path.Combine(dir, "positions.csv");
        if (!File.Exists(csv)) throw new Exception(L.T("positions.csv introuvable dans ", "positions.csv not found in ") + dir);
        List<string> files = new List<string>(); List<int> xs = new List<int>(), ys = new List<int>();
        foreach (string line in File.ReadAllLines(csv))
        {
            string[] p = line.Split(',');
            if (p.Length != 3 || p[0] == "file") continue;
            files.Add(Path.Combine(dir, p[0])); xs.Add(int.Parse(p[1])); ys.Add(int.Parse(p[2]));
        }
        if (files.Count == 0) throw new Exception(L.T("Aucune capture listée dans ", "No capture listed in ") + csv);
        string suffix = scale == 1.0 ? "" : "_x" + scale.ToString(CultureInfo.InvariantCulture);
        string outPath = Path.Combine(dir, "panorama" + suffix + ".png");
        Console.WriteLine(L.T("Assemblage de ", "Assembling ") + files.Count + " captures...");
        int[] size = Compose.Composite(files.ToArray(), xs.ToArray(), ys.ToArray(), scale, outPath);
        Color(ConsoleColor.Green); Console.WriteLine(L.T("Panorama : ", "Panorama: ") + outPath + " (" + size[0] + " x " + size[1] + " px)"); ResetColor();
        return outPath;
    }
}
