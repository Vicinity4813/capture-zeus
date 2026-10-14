// Helpers natifs pour ZeusCapture.ps1 : capture d'ecran, envoi de touches,
// recalage de deux captures successives et assemblage du panorama.
// Ecrit en C# 5 pour compiler avec le Add-Type de Windows PowerShell 5.1.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZeusCap
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        const uint KEYEVENTF_KEYUP = 0x2;
        const uint KEYEVENTF_SCANCODE = 0x8;

        static bool IsExtended(int vk)
        {
            // Fleches, Inser/Suppr, Origine/Fin, PgPrec/PgSuiv.
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;
        }

        // Envoi par scancode : les vieux jeux DirectInput ignorent souvent les codes virtuels.
        public static void Key(int vk, bool up)
        {
            INPUT[] inp = new INPUT[1];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki.wScan = (ushort)MapVirtualKey((uint)vk, 0);
            uint flags = KEYEVENTF_SCANCODE;
            if (up) flags |= KEYEVENTF_KEYUP;
            if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            inp[0].U.ki.dwFlags = flags;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Hold(int vk, int ms)
        {
            Key(vk, false);
            Thread.Sleep(ms);
            Key(vk, true);
        }

        // Zeus avance d'un cran fixe par appui (une touche simulee ne se repete pas) : on compte les appuis.
        public static void Tap(int vk, int count, int downMs, int gapMs)
        {
            for (int i = 0; i < count; i++)
            {
                Key(vk, false);
                Thread.Sleep(downMs);
                Key(vk, true);
                Thread.Sleep(gapMs);
            }
        }

        public static uint ForegroundPid()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid;
        }

        // Zone client de la fenetre en coordonnees ecran : x, y, largeur, hauteur.
        public static int[] ClientArea(IntPtr hwnd)
        {
            RECT r;
            GetClientRect(hwnd, out r);
            POINT p = new POINT();
            ClientToScreen(hwnd, ref p);
            return new int[] { p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top };
        }

        public static int[] CursorPos()
        {
            POINT p;
            GetCursorPos(out p);
            return new int[] { p.X, p.Y };
        }
    }

    public static class Img
    {
        public static Bitmap Capture(int x, int y, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        public static Bitmap Crop(Bitmap src, int x, int y, int w, int h)
        {
            // Copie explicite : Clone() peut partager la memoire de la source, qui est liberee juste apres.
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            }
            return dst;
        }

        public static byte[] Gray(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            byte[] raw = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, raw, 0, raw.Length);
            bmp.UnlockBits(d);
            byte[] g = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * d.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 3;
                    g[y * w + x] = (byte)((raw[i] * 29 + raw[i + 1] * 150 + raw[i + 2] * 77) >> 8);
                }
            }
            return g;
        }

        // Part des pixels qui different de plus de 10 niveaux de gris.
        public static double ChangedFraction(byte[] a, byte[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 10) n++;
            return (double)n / a.Length;
        }

        // Pourcentage de pixels quasi noirs, pour detecter une capture ratee.
        public static double DarkFraction(byte[] a)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] < 8) n++;
            return (double)n / a.Length;
        }

        static int[] LongestRun(double[] v, double threshold)
        {
            int bestStart = -1, bestLen = 0, start = -1;
            for (int i = 0; i <= v.Length; i++)
            {
                bool on = i < v.Length && v[i] > threshold;
                if (on && start < 0) start = i;
                if (!on && start >= 0)
                {
                    if (i - start > bestLen) { bestLen = i - start; bestStart = start; }
                    start = -1;
                }
            }
            return new int[] { bestStart, bestLen };
        }

        // Zone de la carte = la partie de l'ecran qui change quand on fait defiler la vue.
        // L'interface (barre du haut, panneau lateral) reste fixe, sauf la minicarte qui est trop petite pour peser.
        public static int[] DetectViewport(byte[] a, byte[] b, int w, int h)
        {
            bool[] changed = new bool[w * h];
            for (int i = 0; i < changed.Length; i++) changed[i] = Math.Abs(a[i] - b[i]) > 10;

            double[] col = new double[w];
            for (int x = 0; x < w; x++)
            {
                int n = 0;
                for (int y = 0; y < h; y++) if (changed[y * w + x]) n++;
                col[x] = (double)n / h;
            }
            int[] cr = LongestRun(col, 0.25);
            if (cr[0] < 0 || cr[1] < w / 4) return null;

            double[] row = new double[h];
            for (int y = 0; y < h; y++)
            {
                int n = 0;
                for (int x = cr[0]; x < cr[0] + cr[1]; x++) if (changed[y * w + x]) n++;
                row[y] = (double)n / cr[1];
            }
            // Dans Zeus, la carte va du dessous de la barre de menus jusqu'au bas de l'ecran : on prend la premiere
            // rangee qui bouge et on descend jusqu'en bas (une mer unie ne change pas au defilement et
            // tronquerait sinon la zone).
            int top = -1;
            for (int y = 0; y < h; y++) if (row[y] > 0.25) { top = y; break; }
            if (top < 0 || h - top < h / 4) return null;

            return new int[] { cr[0], top, cr[1], h - top };
        }

        // Images reduites deja calculees (une capture sert de reference a plusieurs recalages).
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> small =
            new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]>();

        static byte[] Downsample(byte[] g, int w, int h, int f, out int w2, out int h2)
        {
            w2 = w / f; h2 = h / f;
            byte[] cached;
            if (f == 4 && small.TryGetValue(g, out cached)) return cached;
            byte[] r0 = DownsampleRaw(g, w, h, f, w2, h2);
            if (f == 4) { try { small.Add(g, r0); } catch (ArgumentException) { } }
            return r0;
        }

        static byte[] DownsampleRaw(byte[] g, int w, int h, int f, int w2, int h2)
        {
            byte[] r = new byte[w2 * h2];
            for (int y = 0; y < h2; y++)
                for (int x = 0; x < w2; x++)
                {
                    int s = 0;
                    for (int j = 0; j < f; j++)
                        for (int i = 0; i < f; i++) s += g[(y * f + j) * w + x * f + i];
                    r[y * w2 + x] = (byte)(s / (f * f));
                }
            return r;
        }

        // Ecart moyen entre A et B decale de (dx, dy), avec B(x, y) qui correspond a A(x + dx, y + dy).
        // Seuls les pixels non noirs comptent, pour que le vide hors carte ne donne pas de faux accords.
        // step > 1 : un pixel sur step dans chaque sens (l'ecart moyen reste representatif, calcul step^2 fois plus court).
        static double Score(byte[] a, byte[] b, int w, int h, int dx, int dy, int minN, out int n, int step = 1)
        {
            n = 0;
            int x0 = Math.Max(0, -dx), x1 = Math.Min(w, w - dx);
            int y0 = Math.Max(0, -dy), y1 = Math.Min(h, h - dy);
            if (x1 <= x0 || y1 <= y0) return double.MaxValue;
            long sum = 0;
            for (int y = y0; y < y1; y += step)
            {
                int ib = y * w, ia = (y + dy) * w + dx;
                for (int x = x0; x < x1; x += step)
                {
                    int va = a[ia + x], vb = b[ib + x];
                    if (va > 16 || vb > 16)
                    {
                        sum += Math.Abs(va - vb);
                        n++;
                    }
                }
            }
            if (n < minN) return double.MaxValue;
            return (double)sum / n;
        }

        // Cherche le decalage dans une fenetre [dxMin, dxMax] x [dyMin, dyMax] (en pixels).
        // Un petit malus (wx, wy par pixel d'ecart a la prediction) departage les zones sans motif.
        // Resultat : { dx, dy, ecart*100, pixels compares } ou null.
        static int[] Search(byte[] a, byte[] b, int w, int h, int dxMin, int dxMax, int dyMin, int dyMax,
                            double pdx, double pdy, double wx, double wy)
        {
            const int F = 4;
            int w4, h4;
            byte[] a4 = Downsample(a, w, h, F, out w4, out h4);
            byte[] b4 = Downsample(b, w, h, F, out w4, out h4);
            int minN4 = Math.Max(30, w4 * h4 / 200);
            int minN = Math.Max(500, w * h / 200);

            // Recherche grossiere sur image reduite ; on garde plusieurs candidats car la reduction
            // peut defavoriser le vrai decalage quand il n'est pas multiple de F.
            // (calcul reparti sur tous les coeurs du processeur)
            List<double[]> cands = new List<double[]>();
            int cy0 = dyMin / F, cy1 = dyMax / F;
            System.Threading.Tasks.Parallel.For(cy0, cy1 + 1, cy =>
            {
                List<double[]> local = new List<double[]>();
                int nn;
                for (int cx = dxMin / F; cx <= dxMax / F; cx++)
                {
                    double s = Score(a4, b4, w4, h4, cx, cy, minN4, out nn);
                    if (s == double.MaxValue) continue;
                    s += wx * Math.Abs(cx * F - pdx) + wy * Math.Abs(cy * F - pdy);
                    local.Add(new double[] { s, cx, cy });
                }
                lock (cands) cands.AddRange(local);
            });
            if (cands.Count == 0) return null;
            cands.Sort(delegate(double[] p, double[] q) { int c = p[0].CompareTo(q[0]); if (c != 0) return c; c = p[2].CompareTo(q[2]); return c != 0 ? c : p[1].CompareTo(q[1]); });

            List<double[]> picked = new List<double[]>();
            foreach (double[] c in cands)
            {
                bool near = false;
                foreach (double[] p in picked) if (Math.Abs(p[1] - c[1]) <= 2 && Math.Abs(p[2] - c[2]) <= 2) near = true;
                if (!near) picked.Add(c);
                if (picked.Count >= 6) break;
            }

            // Affinage a pleine resolution autour de chaque candidat ; on retient aussi le meilleur ecart
            // trouve ailleurs (a plus de F pixels), qui mesure l'ambiguite : sur de l'eau, plusieurs
            // positions collent presque aussi bien.
            // Affinage sur un pixel sur 2 dans chaque sens : decalage toujours au pixel pres, 4 fois moins de calcul.
            const int RS = 2;
            double[][] slots = new double[picked.Count][];
            System.Threading.Tasks.Parallel.For(0, picked.Count, k =>
            {
                double[] c = picked[k];
                double candS = double.MaxValue, candRaw = 0;
                int cfx = 0, cfy = 0, cfn = 0, nn;
                int cx = (int)c[1] * F, cy = (int)c[2] * F;
                for (int dy = cy - F; dy <= cy + F; dy++)
                    for (int dx = cx - F; dx <= cx + F; dx++)
                    {
                        double raw = Score(a, b, w, h, dx, dy, minN / (RS * RS), out nn, RS);
                        if (raw == double.MaxValue) continue;
                        double s = raw + wx * Math.Abs(dx - pdx) + wy * Math.Abs(dy - pdy);
                        if (s < candS) { candS = s; candRaw = raw; cfx = dx; cfy = dy; cfn = nn * RS * RS; }
                    }
                if (candS != double.MaxValue) slots[k] = new double[] { candS, candRaw, cfx, cfy, cfn };
            });
            List<double[]> refined = new List<double[]>();
            foreach (double[] r in slots) if (r != null) refined.Add(r);
            if (refined.Count == 0) return null;
            refined.Sort(delegate(double[] p, double[] q) { return p[0].CompareTo(q[0]); });
            double[] best = refined[0];
            double second = double.MaxValue;
            for (int i = 1; i < refined.Count; i++)
                if (Math.Abs(refined[i][2] - best[2]) > F || Math.Abs(refined[i][3] - best[3]) > F)
                    second = Math.Min(second, refined[i][1]);
            int secondInt = second == double.MaxValue ? int.MaxValue : (int)(second * 100);
            return new int[] { (int)best[2], (int)best[3], (int)(best[1] * 100), (int)best[4], secondInt };
        }

        // Recalage net et sans ambiguite : ecart faible, et nettement meilleur que toute autre position.
        public static bool IsReliable(int[] res, double maxScore, double minGap)
        {
            if (res == null || res[2] > maxScore * 100) return false;
            return res[4] == int.MaxValue || res[4] - res[2] >= minGap * 100;
        }

        // Deplacement de la camera sans prediction, dans le sens de defilement attendu.
        // axis 0 = horizontal, 1 = vertical ; sign = +1 (droite/bas) ou -1 (gauche/haut).
        public static int[] FindShift(byte[] a, byte[] b, int w, int h, int axis, int sign)
        {
            int ext = axis == 0 ? w : h;
            int lo = sign > 0 ? 0 : -(int)(ext * 0.92);
            int hi = sign > 0 ? (int)(ext * 0.92) : 0;
            double wc = 0.5 / 16.0;
            if (axis == 0) return Search(a, b, w, h, lo, hi, -16, 16, 0, 0, 0, wc);
            return Search(a, b, w, h, -16, 16, lo, hi, 0, 0, wc, 0);
        }

        // Deplacement de la camera autour d'une prediction (pdx, pdy), a +/- radius pixels.
        public static int[] FindShiftNear(byte[] a, byte[] b, int w, int h, int pdx, int pdy, int radius)
        {
            return FindShiftNear(a, b, w, h, pdx, pdy, radius, radius);
        }

        // Idem avec une marge differente par axe : les fleches font defiler la vue exactement a l'horizontale
        // ou a la verticale, l'incertitude n'est donc que dans le sens du defilement (recherche bien plus courte,
        // surtout en haute resolution ou le pas, donc la marge, est grand).
        public static int[] FindShiftNear(byte[] a, byte[] b, int w, int h, int pdx, int pdy, int rx, int ry)
        {
            double wx = 1.0 / Math.Max(16, rx), wy = 1.0 / Math.Max(16, ry);
            return Search(a, b, w, h, pdx - rx, pdx + rx, pdy - ry, pdy + ry, pdx, pdy, wx, wy);
        }
    }

    // Suit la position de chaque capture pendant un balayage en serpentin.
    // Chaque capture est recalee sur la precedente et sur celles de la rangee du dessus qu'elle chevauche.
    // Les recalages nets deviennent des liens exacts ; faute de mieux, un lien estime (vitesse de defilement)
    // relie la capture a la precedente. Les positions finales sont calculees a la fin en n'utilisant les liens
    // estimes que pour raccrocher des groupes de captures qui n'ont aucun lien exact entre eux.
    public class Tracker
    {
        class Entry { public int Index, X, Y, Drift; public byte[] G; }
        class Link { public int A, B, Dx, Dy, Cost; }

        readonly int w, h;
        List<Entry> prevRow = new List<Entry>();
        List<Entry> curRow = new List<Entry>();
        List<Link> links = new List<Link>();
        Entry last;
        int count;

        // Ecart moyen maximal (niveaux de gris) pour croire un recalage ; 0 sur un jeu en pause.
        public double MaxScore = 6.0;
        // Avance minimale du meilleur recalage sur toute autre position (niveaux de gris).
        public double MinGap = 2.0;
        public int Fallbacks;

        public Tracker(int w, int h) { this.w = w; this.h = h; }

        // Ajoute une capture ; predDx/predDy = deplacement attendu depuis la precedente.
        // knownLast : recalage sur la precedente deja fait (et net) pendant le balayage, pour ne pas le refaire.
        // Resultat : { x, y provisoires, nombre de liens exacts trouves }.
        public int[] Add(byte[] g, int predDx, int predDy, bool newRow, int[] knownLast = null)
        {
            Entry e = new Entry();
            e.G = g;
            e.Index = count++;
            if (last == null)
            {
                curRow.Add(e);
                last = e;
                return new int[] { 0, 0, 1 };
            }
            if (newRow)
            {
                prevRow = curRow;
                curRow = new List<Entry>();
            }

            int px = last.X + predDx, py = last.Y + predDy;
            int along = Math.Max(Math.Abs(predDx), Math.Abs(predDy));

            // Incertitude de la position provisoire : la marge de la mesure du pas, plus la derive accumulee
            // par la chaine de captures placees sans repere (appuis perdus sur une mer sans motif).
            int stepMargin = 32 + (int)(0.2 * along);
            List<Entry> refs = new List<Entry>();
            refs.Add(last);
            foreach (Entry r in prevRow)
                if (r != last && Overlap(r.X, r.Y, px, py) >= 0.05 * w * h) refs.Add(r);

            int found = 0, bestScore = int.MaxValue, bestDrift = int.MaxValue;
            int bx = px, by = py;
            foreach (Entry r in refs)
            {
                // Marge dans le sens du defilement (pas mesure + derive), etroite en travers (defilement droit).
                bool horiz = Math.Abs(predDx) >= Math.Abs(predDy);
                int rAlong = r == last ? stepMargin : Math.Min(300, 32 + stepMargin + last.Drift + r.Drift);
                int rAcross = r == last ? 16 + last.Drift : Math.Min(300, 32 + last.Drift + r.Drift);
                int[] res = r == last && knownLast != null ? knownLast
                    : Img.FindShiftNear(r.G, g, w, h, px - r.X, py - r.Y, horiz ? rAlong : rAcross, horiz ? rAcross : rAlong);
                if (!Img.IsReliable(res, MaxScore, MinGap)) continue;
                Link l = new Link();
                l.A = r.Index; l.B = e.Index; l.Dx = res[0]; l.Dy = res[1]; l.Cost = res[2];
                links.Add(l);
                found++;
                // Position provisoire tiree de la reference la mieux ancree, puis du recalage le plus net.
                if (r.Drift < bestDrift || (r.Drift == bestDrift && res[2] < bestScore))
                {
                    bestDrift = r.Drift; bestScore = res[2];
                    bx = r.X + res[0]; by = r.Y + res[1];
                }
            }
            if (found == 0)
            {
                Link l = new Link();
                l.A = last.Index; l.B = e.Index; l.Dx = predDx; l.Dy = predDy; l.Cost = int.MaxValue;
                links.Add(l);
                Fallbacks++;
                bestDrift = last.Drift + stepMargin;
            }

            e.Drift = bestDrift;
            e.X = bx; e.Y = by;
            curRow.Add(e);
            last = e;
            return new int[] { bx, by, found };
        }

        // Positions finales { xs[], ys[] } : arbre couvrant qui prefere les liens exacts les plus nets.
        public int[][] Solve()
        {
            int[] parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            List<Link> sorted = new List<Link>(links);
            sorted.Sort(delegate(Link p, Link q) { return p.Cost.CompareTo(q.Cost); });

            List<Link>[] adj = new List<Link>[count];
            for (int i = 0; i < count; i++) adj[i] = new List<Link>();
            foreach (Link l in sorted)
            {
                int ra = Find(parent, l.A), rb = Find(parent, l.B);
                if (ra == rb) continue;
                parent[ra] = rb;
                adj[l.A].Add(l);
                adj[l.B].Add(l);
            }

            int[] xs = new int[count], ys = new int[count];
            bool[] seen = new bool[count];
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(0);
            seen[0] = true;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (Link l in adj[i])
                {
                    int j = l.A == i ? l.B : l.A;
                    if (seen[j]) continue;
                    if (l.A == i) { xs[j] = xs[i] + l.Dx; ys[j] = ys[i] + l.Dy; }
                    else { xs[j] = xs[i] - l.Dx; ys[j] = ys[i] - l.Dy; }
                    seen[j] = true;
                    queue.Enqueue(j);
                }
            }
            return new int[][] { xs, ys };
        }

        // Nombre de groupes de captures relies seulement par des liens estimes.
        public int Groups()
        {
            int[] parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            foreach (Link l in links)
                if (l.Cost != int.MaxValue) parent[Find(parent, l.A)] = Find(parent, l.B);
            int n = 0;
            for (int i = 0; i < count; i++) if (Find(parent, i) == i) n++;
            return n;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        long Overlap(int ax, int ay, int bx, int by)
        {
            long ox = Math.Max(0, w - Math.Abs(ax - bx));
            long oy = Math.Max(0, h - Math.Abs(ay - by));
            return ox * oy;
        }
    }

    public static class Compose
    {
        // Colle les captures a leurs positions sur une grande image et l'enregistre en PNG.
        public static int[] Composite(string[] files, int[] xs, int[] ys, double scale, string outPath)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int fw, fh;
            using (Bitmap first = new Bitmap(files[0])) { fw = first.Width; fh = first.Height; }
            for (int i = 0; i < files.Length; i++)
            {
                minX = Math.Min(minX, xs[i]); minY = Math.Min(minY, ys[i]);
                maxX = Math.Max(maxX, xs[i] + fw); maxY = Math.Max(maxY, ys[i] + fh);
            }
            int cw = (int)Math.Ceiling((maxX - minX) * scale);
            int ch = (int)Math.Ceiling((maxY - minY) * scale);
            using (Bitmap canvas = new Bitmap(cw, ch, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = scale == 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    // Le noir pur (vide hors carte) est colle en transparent : une capture du bord, mal placee
                    // faute de repere, ne peut pas recouvrir de noir une partie de la ville deja posee.
                    using (ImageAttributes attrs = new ImageAttributes())
                    {
                        attrs.SetColorKey(Color.FromArgb(0, 0, 0), Color.FromArgb(3, 3, 3));
                        // En reduction, le bord de chaque image se melangeait avec le vide autour (liseré clair visible
                        // sur l'eau la ou une image en recouvre une autre) : bords prolonges en miroir.
                        attrs.SetWrapMode(WrapMode.TileFlipXY);
                        for (int i = 0; i < files.Length; i++)
                        {
                            using (Bitmap f = new Bitmap(files[i]))
                            {
                                // Coins arrondis separement : pas de trou ni de decalage d'un pixel entre voisines.
                                int x = (int)Math.Round((xs[i] - minX) * scale);
                                int y = (int)Math.Round((ys[i] - minY) * scale);
                                int dw = (int)Math.Round((xs[i] - minX + fw) * scale) - x;
                                int dh = (int)Math.Round((ys[i] - minY + fh) * scale) - y;
                                g.DrawImage(f, new Rectangle(x, y, dw, dh), 0, 0, fw, fh, GraphicsUnit.Pixel, attrs);
                            }
                        }
                    }
                }
                canvas.Save(outPath, ImageFormat.Png);
            }
            return new int[] { cw, ch };
        }
    }
}
