// Verification hors ecran de Geo (src\ZeusCapture.cs) : image du jeu agrandie par la carte graphique.
// Une image procedurale de 1024x768 est agrandie par GDI+ (bilineaire et au plus proche) a plusieurs echelles ;
// Geo.Resample doit retrouver l'original (exact au plus proche, ecart moyen sous 4 en bilineaire : 3,6 a x2,
// ou le centre de chaque pixel du jeu tombe entre deux pixels de l'ecran), Snap et Geo.FromWidth la taille du jeu. Quelques secondes, sans ecran.
//   VerifEchelle.exe   -> code 0 si tout est bon
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZeusCap;

static class VerifEchelle
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  ECHEC ") + what);
        if (!ok) fails++;
    }

    // Image facon jeu : tuiles de 8 a 24 px aux couleurs douces, degrade, quelques traits fins.
    static Bitmap World(int w, int h)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        Random r = new Random(7);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(90, 120, 80));
            for (int i = 0; i < 2500; i++)
            {
                int s = 8 + r.Next(17);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(60 + r.Next(150), 60 + r.Next(150), 40 + r.Next(150))))
                    g.FillRectangle(br, r.Next(w), r.Next(h), s, s);
            }
            for (int i = 0; i < 150; i++)
                using (Pen p = new Pen(Color.FromArgb(30 + r.Next(60), 30 + r.Next(60), 30 + r.Next(60)), 2))
                    g.DrawLine(p, r.Next(w), r.Next(h), r.Next(w), r.Next(h));
        }
        return b;
    }

    static Bitmap Enlarge(Bitmap src, int w, int h, InterpolationMode mode)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(b))
        using (ImageAttributes at = new ImageAttributes())
        {
            at.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = mode;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, at);
        }
        return b;
    }

    static byte[] Raw(Bitmap b, out int stride)
    {
        BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] raw = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, raw, 0, raw.Length);
        stride = d.Stride; b.UnlockBits(d);
        return raw;
    }

    // Ecart moyen par composante (0-255) et ecart maximal
    static double MeanDiff(Bitmap a, Bitmap b, out int max)
    {
        int sa, sb; byte[] ra = Raw(a, out sa), rb = Raw(b, out sb);
        long sum = 0; max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width * 3; x++)
            {
                int d = Math.Abs(ra[y * sa + x] - rb[y * sb + x]);
                sum += d; if (d > max) max = d;
            }
        return sum / (double)(a.Width * a.Height * 3);
    }

    static int Main()
    {
        const int W = 1024, H = 768;
        Console.WriteLine("Geo.Resample : image 1024x768 agrandie puis ramenee a la taille du jeu");
        using (Bitmap world = World(W, H))
        {
            double[][] scales = { new[] { 1.25, 1.25 }, new[] { 1.25, 1024 / 768.0 }, new[] { 1.875, 1.875 }, new[] { 2.0, 2.0 }, new[] { 2.8125, 2.8125 }, new[] { 3.75, 2.8125 } };
            foreach (double[] s in scales)
            {
                int ew = (int)Math.Round(W * s[0]), eh = (int)Math.Round(H * s[1]);
                foreach (InterpolationMode mode in new[] { InterpolationMode.NearestNeighbor, InterpolationMode.Bilinear })
                    using (Bitmap big = Enlarge(world, ew, eh, mode))
                    {
                        Geo g = Geo.Scaled(0, 0, ew, eh, s[0], s[1]);
                        int max;
                        using (Bitmap back = Geo.Resample(big, 0, 0, 0, 0, g.W, g.H, g.Sx, g.Sy))
                        {
                            double md = MeanDiff(world, back, out max);
                            bool nn = mode == InterpolationMode.NearestNeighbor;
                            Check(g.W == W && g.H == H && (nn ? max == 0 : md < 4),
                                string.Format("{0} x {1} ({2}) : taille {3} x {4}, ecart moyen {5:F2}, max {6}", ew, eh, nn ? "au plus proche" : "bilineaire", g.W, g.H, md, max));
                        }
                        // Morceau (comme une capture de la vue) : identique a la meme zone de l'image entiere
                        using (Bitmap full = Geo.Resample(big, 0, 0, 0, 0, g.W, g.H, g.Sx, g.Sy))
                        {
                            int gx = 123, gy = 94, gw = 600, gh = 500;
                            int x0 = (int)Math.Floor(gx * g.Sx), y0 = (int)Math.Floor(gy * g.Sy);
                            int x1 = (int)Math.Ceiling((gx + gw) * g.Sx), y1 = (int)Math.Ceiling((gy + gh) * g.Sy);
                            using (Bitmap crop = Img.Crop(big, x0, y0, x1 - x0, y1 - y0))
                            using (Bitmap part = Geo.Resample(crop, x0, y0, gx, gy, gw, gh, g.Sx, g.Sy))
                            using (Bitmap same = Img.Crop(full, gx, gy, gw, gh))
                            {
                                double md = MeanDiff(same, part, out max);
                                if (mode == InterpolationMode.Bilinear) Check(max == 0, string.Format("  morceau {0},{1} {2} x {3} identique a l'image entiere (max {4})", gx, gy, gw, gh, max));
                            }
                        }
                    }
            }
        }

        Console.WriteLine("Geo.FromWidth : taille du jeu d'apres le seul defilement horizontal (4 appuis = 240 px du jeu)");
        int[][] cases = {
            new[] { 1280, 1024, 300, 1024, 768 },    // 1024x768 etire en 1280x1024
            new[] { 1280, 960, 300, 1024, 768 },     // proportions gardees
            new[] { 1280, 1024, 299, 1024, 768 },    // mesure a 1 px pres
            new[] { 2880, 2160, 675, 1024, 768 },    // 4K, proportions gardees
            new[] { 3840, 2160, 900, 1024, 768 },    // 4K etire
            new[] { 1920, 1440, 450, 1024, 768 },    // 1440p, proportions gardees (faux jeu, mode aspect)
            new[] { 2560, 1440, 600, 1024, 768 },    // 1440p etire (faux jeu, mode etire)
            new[] { 1920, 1080, 450, 1024, 768 },    // 1080p etire
            new[] { 3840, 2160, 360, 2560, 1440 },   // jeu 2560x1440 agrandi x1,5
            new[] { 1600, 1200, 300, 1280, 960 },    // largeur sans hauteur attitree : proportions gardees
        };
        foreach (int[] c in cases)
        {
            Geo g = Geo.FromWidth(0, 0, c[0], c[1], c[2] / 240.0);
            Check(g.W == c[3] && g.H == c[4], string.Format("{0} x {1}, decalage {2} -> {3} x {4} (attendu {5} x {6}), echelle {7:0.###} x {8:0.###}",
                c[0], c[1], c[2], g.W, g.H, c[3], c[4], g.Sx, g.Sy));
        }
        Check(Geo.Snap(1019.5, Geo.WIDTHS) == 1024 && Geo.Snap(1001, Geo.WIDTHS) == 1002 && Geo.Snap(767, Geo.HEIGHTS) == 768, "Snap : 1019,5 -> 1024 ; 1001 -> 1002 ; 767 -> 768");

        Console.WriteLine("ToScreen / ToGame");
        Geo q = Geo.Scaled(480, 0, 2880, 2160, 2.8125, 2.8125);
        bool round = true;
        for (int x = 0; x < q.W; x += 7) for (int y = 0; y < q.H; y += 11)
            {
                int[] s = q.ToScreen(x, y), b = q.ToGame(s[0], s[1]);
                if (b[0] != x || b[1] != y) round = false;
            }
        Check(round, "aller-retour jeu -> ecran -> jeu sur toute l'image (x2,8125, decalage 480)");
        Geo u = Geo.Plain(100, 50, 1024, 768);
        Check(u.Unit && u.ToScreen(10, 20)[0] == 110 && u.ToScreen(10, 20)[1] == 70 && u.ToGame(110, 70)[0] == 10, "echelle 1 : simple decalage");

        Console.WriteLine(fails == 0 ? "VerifEchelle : tout est bon." : "VerifEchelle : " + fails + " echec(s).");
        return fails == 0 ? 0 : 1;
    }
}
