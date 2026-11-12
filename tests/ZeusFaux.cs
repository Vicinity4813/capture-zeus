// Faux jeu pour tester Capture Zeus sans le jeu : un monde procedural de 2000 x 1300 px vu par une fenetre de jeu
// de 1024x768 (barre du haut de 30 px, panneau de 186 px, mini-carte avec cadre rouge, fleches a 60 px par appui dans les deux sens, comme le vrai jeu en 1024x768),
// affichee en plein ecran selon le mode :
//   1        1024x768 non agrandi, dans le coin d'une fenetre plus grande ; souris confinee a l'image (comme Zeus)
//   aspect   agrandi en gardant les proportions (bilineaire), centre, bandes noires
//   etire    agrandi a tout l'ecran (echelles X et Y differentes, au plus proche)
//   curseur  comme aspect, et le jeu replace le curseur a 0,8 x sa position (comme en 1280x1024)
// Usage : ZeusFaux.exe <mode> "<Capture Zeus.exe>"
// Le faux jeu lance la capture (CAPTURE_ZEUS_JEU=ZeusFaux, --delai 1 --silencieux), attend la fin, compare le panorama
// au monde (taille exacte, ecart moyen faible), ecrit resultat_<mode>.txt et journal_<mode>.txt a cote de la capture
// et renvoie 0 si c'est bon. Prend la souris et le clavier environ 30 s. Echap : quitter.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class ZeusFaux : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT r);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool UnclipCursor(IntPtr none);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const int GW = 1024, GH = 768, TOP = 30, VIEW_W = 838, SIDE = 186;
    const int WORLD_W = 2000, WORLD_H = 1300;
    const int MINI_X = GW - 136, MINI_Y = 319, MINI_W = 127, MINI_H = 108;
    const int SKIP_TOP = 64, RIGHT_TRIM = 4;   // ce que Capture Zeus laisse de cote dans chaque capture

    static string mode, captureExe;
    static int result = 2;
    readonly Bitmap world, thumb, frame;
    int camX = (WORLD_W - VIEW_W) / 2, camY = (WORLD_H - (GH - TOP)) / 2;
    Rectangle dest;
    readonly System.Windows.Forms.Timer cursorTimer = new System.Windows.Forms.Timer();
    Point lastSeen, lastOwn = new Point(-1, -1);

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware();
        if (args.Length < 2) { Console.WriteLine("Usage : ZeusFaux.exe <1|aspect|etire|curseur> \"<Capture Zeus.exe>\""); return 2; }
        mode = args[0].ToLowerInvariant(); captureExe = Path.GetFullPath(args[1]);
        if (mode != "1" && mode != "aspect" && mode != "etire" && mode != "curseur") { Console.WriteLine("Mode inconnu : " + mode); return 2; }
        Application.Run(new ZeusFaux());
        return result;
    }

    // Monde facon jeu : fond en degrade, tuiles aux couleurs de terre (jamais rouges ni noires), traits fins.
    public static Bitmap MakeWorld()
    {
        Bitmap b = new Bitmap(WORLD_W, WORLD_H, PixelFormat.Format24bppRgb);
        Random r = new Random(1234);
        using (Graphics g = Graphics.FromImage(b))
        {
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, WORLD_W, WORLD_H), Color.FromArgb(110, 130, 80), Color.FromArgb(150, 140, 100), 30f))
                g.FillRectangle(lg, 0, 0, WORLD_W, WORLD_H);
            for (int i = 0; i < 9000; i++)
            {
                int s = 8 + r.Next(17), rr = 70 + r.Next(130);
                Color c = Color.FromArgb(rr, Math.Min(255, Math.Max(40, rr - 50 + r.Next(70))), 40 + r.Next(110));
                using (SolidBrush br = new SolidBrush(c)) g.FillRectangle(br, r.Next(WORLD_W), r.Next(WORLD_H), s, s + r.Next(9) - 4);
            }
            for (int i = 0; i < 600; i++)
                using (Pen p = new Pen(Color.FromArgb(40 + r.Next(50), 40 + r.Next(50), 35 + r.Next(50)), 2))
                    g.DrawLine(p, r.Next(WORLD_W), r.Next(WORLD_H), r.Next(WORLD_W), r.Next(WORLD_H));
        }
        return b;
    }

    ZeusFaux()
    {
        Text = "ZeusFaux"; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen.Bounds; BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        world = MakeWorld();
        thumb = new Bitmap(MINI_W, MINI_H, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(thumb)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(world, new Rectangle(0, 0, MINI_W, MINI_H)); }
        frame = new Bitmap(GW, GH, PixelFormat.Format24bppRgb);
        int sw = Bounds.Width, sh = Bounds.Height;
        if (mode == "1") dest = new Rectangle(0, 0, GW, GH);
        else if (mode == "etire") dest = new Rectangle(0, 0, sw, sh);
        else
        {
            double s = Math.Min(sw / (double)GW, sh / (double)GH);
            int dw = (int)Math.Round(GW * s), dh = (int)Math.Round(GH * s);
            dest = new Rectangle((sw - dw) / 2, (sh - dh) / 2, dw, dh);
        }
        Render();
        cursorTimer.Interval = 30; cursorTimer.Tick += OnCursorTick;
        Shown += (o, e) => { Activate(); new Thread(RunCapture) { IsBackground = true }.Start(); };
        Activated += (o, e) => { if (mode == "1") { RECT rc = new RECT { Left = 0, Top = 0, Right = GW, Bottom = GH }; ClipCursor(ref rc); } if (mode == "curseur") cursorTimer.Start(); };
        Deactivate += (o, e) => { UnclipCursor(IntPtr.Zero); cursorTimer.Stop(); };
        FormClosed += (o, e) => UnclipCursor(IntPtr.Zero);
    }

    // Image du jeu en 1024x768
    void Render()
    {
        using (Graphics g = Graphics.FromImage(frame))
        {
            g.DrawImage(world, new Rectangle(0, TOP, VIEW_W, GH - TOP), new Rectangle(camX, camY, VIEW_W, GH - TOP), GraphicsUnit.Pixel);
            using (SolidBrush bar = new SolidBrush(Color.FromArgb(185, 165, 120))) g.FillRectangle(bar, 0, 0, GW, TOP);
            using (SolidBrush mark = new SolidBrush(Color.FromArgb(90, 70, 40)))
                for (int x = 20; x < GW - 40; x += 97) g.FillRectangle(mark, x, 9, 50, 12);
            using (SolidBrush side = new SolidBrush(Color.FromArgb(150, 120, 80))) g.FillRectangle(side, VIEW_W, TOP, SIDE, GH - TOP);
            using (SolidBrush btn = new SolidBrush(Color.FromArgb(110, 90, 60)))
            {
                for (int y = 60; y < 300; y += 48) g.FillRectangle(btn, VIEW_W + 20, y, SIDE - 40, 36);
                for (int y = 450; y < GH - 20; y += 40) g.FillRectangle(btn, VIEW_W + 15, y, SIDE - 30, 28);
            }
            g.DrawImageUnscaled(thumb, MINI_X, MINI_Y);
            int fx = MINI_X + camX * MINI_W / WORLD_W, fy = MINI_Y + camY * MINI_H / WORLD_H;
            int fw = VIEW_W * MINI_W / WORLD_W, fh = (GH - TOP) * MINI_H / WORLD_H;
            using (Pen red = new Pen(Color.FromArgb(255, 0, 0))) g.DrawRectangle(red, fx, fy, fw, fh);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (Region black = new Region(ClientRectangle)) { black.Exclude(dest); g.FillRegion(Brushes.Black, black); }
        if (mode == "1") { g.DrawImageUnscaled(frame, 0, 0); return; }
        g.InterpolationMode = mode == "etire" ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (ImageAttributes at = new ImageAttributes())
        {
            at.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(frame, dest, 0, 0, GW, GH, GraphicsUnit.Pixel, at);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int dx = 0, dy = 0;
        if (keyData == Keys.Left) dx = -60; else if (keyData == Keys.Right) dx = 60;
        else if (keyData == Keys.Up) dy = -60; else if (keyData == Keys.Down) dy = 60;   // comme le vrai jeu en 1024x768 (et non 30 px)
        else if (keyData == Keys.Escape) { Close(); return true; }
        else return base.ProcessCmdKey(ref msg, keyData);
        camX = Math.Max(0, Math.Min(WORLD_W - VIEW_W, camX + dx));
        camY = Math.Max(0, Math.Min(WORLD_H - (GH - TOP), camY + dy));
        Render(); Invalidate();
        return true;
    }

    // Mode curseur : une position posee par quelqu'un d'autre (stable depuis un tour) est ramenee a 0,8 x.
    void OnCursorTick(object sender, EventArgs e)
    {
        Point p = Cursor.Position;
        if (p == lastOwn) return;
        if (p == lastSeen)
        {
            lastOwn = new Point((int)(p.X * 0.8), (int)(p.Y * 0.8));
            SetCursorPos(lastOwn.X, lastOwn.Y);
        }
        lastSeen = p;
    }

    void RunCapture()
    {
        string dir = Path.GetDirectoryName(captureExe);
        string log = Path.Combine(dir, "journal_" + mode + ".txt"), res = Path.Combine(dir, "resultat_" + mode + ".txt");
        StringBuilder sb = new StringBuilder();
        string verdict;
        try
        {
            Thread.Sleep(1500);
            DateTime start = DateTime.Now;
            ProcessStartInfo si = new ProcessStartInfo(captureExe, "--delai 1 --silencieux");
            si.UseShellExecute = false; si.WorkingDirectory = dir;
            si.RedirectStandardOutput = true; si.StandardOutputEncoding = Encoding.UTF8;
            si.RedirectStandardInput = true;   // entree fermee : une attente de la touche Entree se termine aussitot
            si.EnvironmentVariables["CAPTURE_ZEUS_JEU"] = "ZeusFaux";
            Process p = Process.Start(si);
            p.StandardInput.Close();
            p.OutputDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            bool done = p.WaitForExit(240000);
            if (!done) try { p.Kill(); } catch { }
            p.WaitForExit();
            lock (sb) File.WriteAllText(log, sb.ToString(), Encoding.UTF8);
            if (!done) throw new Exception("capture trop longue (4 min) : arretee");
            verdict = "code de sortie " + p.ExitCode + " ; " + (DateTime.Now - start).TotalSeconds.ToString("F0") + " s ; ";
            string cap = null;
            foreach (string d in Directory.GetDirectories(dir, "capture_*"))
                if (Directory.GetCreationTime(d) >= start.AddSeconds(-2) && (cap == null || string.CompareOrdinal(d, cap) > 0)) cap = d;
            string pano = cap != null ? Path.Combine(cap, "panorama.png") : null;
            if (p.ExitCode != 0 || pano == null || !File.Exists(pano)) { verdict += "pas de panorama"; result = 1; }
            else
            {
                string cmp; bool ok = Compare(world, pano, mode == "1" ? 0.5 : 4.0, out cmp);
                verdict += cmp; result = ok ? 0 : 1;
            }
        }
        catch (Exception e) { verdict = "erreur : " + e.Message; result = 1; }
        verdict = (result == 0 ? "OK     " : "ECHEC  ") + "mode " + mode + " : " + verdict;
        File.WriteAllText(res, verdict + "\r\n", new UTF8Encoding(false));   // sans BOM : lu par findstr dans run.cmd
        BeginInvoke(new Action(Close));
    }

    // Le panorama doit etre exactement la partie du monde vue par les captures : x de 0 a 2000 - RIGHT_TRIM,
    // y de SKIP_TOP a 1300 (Capture Zeus saute les 64 lignes sous la barre du haut).
    static bool Compare(Bitmap world, string pano, double maxMean, out string msg)
    {
        int ew = WORLD_W - RIGHT_TRIM, eh = WORLD_H - SKIP_TOP;
        using (Bitmap pb = new Bitmap(pano))
        {
            if (pb.Width != ew || pb.Height != eh) { msg = "panorama " + pb.Width + " x " + pb.Height + " au lieu de " + ew + " x " + eh; return false; }
            byte[] a = Raw(world, 0, SKIP_TOP, ew, eh), b = Raw(pb, 0, 0, ew, eh);
            long sum = 0; int bad = 0;
            for (int i = 0; i < a.Length; i++) { int d = Math.Abs(a[i] - b[i]); sum += d; if (d > 40) bad++; }
            double mean = sum / (double)a.Length;
            msg = string.Format("panorama {0} x {1}, ecart moyen {2:F2} (limite {3}), {4:P2} de valeurs a plus de 40", ew, eh, mean, maxMean, bad / (double)a.Length);
            return mean < maxMean;
        }
    }

    static byte[] Raw(Bitmap bmp, int x, int y, int w, int h)
    {
        BitmapData d = bmp.LockBits(new Rectangle(x, y, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] o = new byte[w * h * 3];
        for (int j = 0; j < h; j++) Marshal.Copy(d.Scan0 + j * d.Stride, o, j * w * 3, w * 3);
        bmp.UnlockBits(d);
        return o;
    }
}
