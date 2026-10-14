// Fenetre principale : consignes, deux boutons de capture (pleine resolution / reduite de moitie),
// bandeau d'etat en couleur (alerte rouge pendant le compte a rebours et la capture) et journal.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class CaptureForm : Form
{
    readonly Button btnFull, btnHalf, btnOpenPano, btnOpenDir;
    readonly CheckBox chkQuiet;
    readonly ComboBox cboLang;
    readonly Label banner, lblTitle, lblHelp, lblLang;
    bool idle = true;   // bandeau « Pret » (a retraduire au changement de langue)
    readonly TextBox log;
    volatile bool busy;

    public CaptureForm()
    {
        Text = "Capture Zeus";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(640, 560);
        Size = new Size(760, 720);
        BackColor = Color.FromArgb(245, 240, 228);

        TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // Titre a gauche, choix de la langue a droite
        TableLayoutPanel head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lblTitle = new Label { AutoSize = true, Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = Color.FromArgb(90, 50, 20), Anchor = AnchorStyles.Left };
        FlowLayoutPanel langBox = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false };
        lblLang = new Label { AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        cboLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        cboLang.Items.AddRange(new object[] { "Français", "English" });
        cboLang.SelectedIndex = L.En ? 1 : 0;
        cboLang.SelectedIndexChanged += (s, e) => { L.En = cboLang.SelectedIndex == 1; L.Save(); ApplyLanguage(); };
        langBox.Controls.Add(lblLang); langBox.Controls.Add(cboLang);
        head.Controls.Add(lblTitle, 0, 0); head.Controls.Add(langBox, 1, 0);
        root.Controls.Add(head);
        lblHelp = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(lblHelp);

        FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        btnFull = BigButton();
        btnHalf = BigButton();
        btnFull.Click += (s, e) => Start(1.0);
        btnHalf.Click += (s, e) => Start(0.5);
        buttons.Controls.Add(btnFull); buttons.Controls.Add(btnHalf);
        root.Controls.Add(buttons);

        chkQuiet = new CheckBox { AutoSize = true, Margin = new Padding(4, 0, 0, 8) };
        root.Controls.Add(chkQuiet);

        TableLayoutPanel statusRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 8) };
        banner = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 88, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.FromArgb(110, 110, 110)
        };
        statusRow.Controls.Add(banner);
        FlowLayoutPanel open = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        btnOpenPano = new Button { AutoSize = true, Visible = false };
        btnOpenDir = new Button { AutoSize = true, Visible = false };
        btnOpenPano.Click += (s, e) => OpenPath(CaptureApp.LastPanorama);
        btnOpenDir.Click += (s, e) => OpenPath(CaptureApp.LastDir);
        open.Controls.Add(btnOpenPano); open.Controls.Add(btnOpenDir);
        statusRow.Controls.Add(open);
        root.Controls.Add(statusRow);

        log = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = Color.White
        };
        root.Controls.Add(log);
        ApplyLanguage();

        Resize += (s, e) => banner.Width = statusRow.ClientSize.Width;
        Shown += (s, e) =>
        {
            Signal.Home = Handle;
            Console.SetOut(new LogWriter(this));
            Signal.OnBanner = (text, c) => UI(() => SetBanner(text, c));
            Signal.OnStatus = text => UI(() => Text = "Capture Zeus - " + text);
        };
        FormClosing += OnClosing;
    }

    // Tous les textes de la fenetre dans la langue choisie.
    void ApplyLanguage()
    {
        lblTitle.Text = L.T("Capture de la cité entière", "Whole-city capture");
        lblLang.Text = L.T("Langue :", "Language:");
        lblHelp.Text = L.T(
            "Avant de lancer la capture :\r\n"
            + "  1.  Lancez Zeus et ouvrez la cité à capturer.\r\n"
            + "  2.  Placez la vue à peu près au milieu de la ville.\r\n"
            + "  3.  Mettez la partie en pause (touche P).\r\n"
            + "  4.  Revenez sur cette fenêtre et choisissez une capture ci-dessous.\r\n\r\n"
            + "Le programme passe tout seul sur le jeu, lui coupe le son et fait défiler la vue. "
            + "Tant que la musique joue, ne touchez ni à la souris ni au clavier (bouger la souris arrête la capture). "
            + "Quand la musique s'arrête, le panorama est prêt.\r\n"
            + "Un Alt+Tab par erreur ne casse rien : la capture attend que vous reveniez dans le jeu.",
            "Before starting the capture:\r\n"
            + "  1.  Start Zeus and open the city to capture.\r\n"
            + "  2.  Put the view roughly in the middle of the city.\r\n"
            + "  3.  Pause the game (P key).\r\n"
            + "  4.  Come back to this window and choose a capture below.\r\n\r\n"
            + "The program switches to the game by itself, mutes it and scrolls the view. "
            + "While the music is playing, do not touch the mouse or the keyboard (moving the mouse stops the capture). "
            + "When the music stops, the panorama is ready.\r\n"
            + "An accidental Alt+Tab breaks nothing: the capture waits until you go back to the game.");
        btnFull.Text = L.T("Capture pleine résolution\r\nPanorama à la taille réelle du jeu", "Full-resolution capture\r\nPanorama at the game's real size");
        btnHalf.Text = L.T("Capture réduite de moitié\r\nPanorama 2 fois plus petit et plus léger", "Half-size capture\r\nPanorama 2 times smaller and lighter");
        chkQuiet.Text = L.T("Sans musique", "No music");
        btnOpenPano.Text = L.T("Voir le panorama", "View the panorama");
        btnOpenDir.Text = L.T("Ouvrir le dossier", "Open the folder");
        if (idle) banner.Text = L.T("Prêt", "Ready");
    }

    static Button BigButton()
    {
        return new Button
        {
            Size = new Size(330, 76), Margin = new Padding(0, 0, 12, 0),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold), BackColor = Color.FromArgb(232, 220, 196), FlatStyle = FlatStyle.Flat
        };
    }

    void UI(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

    void SetBanner(string text, ConsoleColor c)
    {
        idle = false;
        banner.Text = text;
        banner.BackColor = c == ConsoleColor.DarkRed ? Color.FromArgb(180, 20, 20)
            : c == ConsoleColor.DarkYellow ? Color.FromArgb(210, 130, 0)
            : c == ConsoleColor.DarkGreen ? Color.FromArgb(30, 130, 50)
            : Color.FromArgb(110, 110, 110);
    }

    void Start(double scale)
    {
        if (busy) return;
        busy = true;
        btnFull.Enabled = btnHalf.Enabled = chkQuiet.Enabled = cboLang.Enabled = false;
        btnOpenPano.Visible = btnOpenDir.Visible = false;
        log.Clear();
        Signal.Quiet = chkQuiet.Checked;
        SetBanner(L.T("Recherche du jeu...", "Looking for the game..."), ConsoleColor.DarkYellow);
        Thread t = new Thread(() =>
        {
            bool ok = CaptureApp.Execute(false, 3, scale, false);
            UI(() =>
            {
                busy = false;
                btnFull.Enabled = btnHalf.Enabled = chkQuiet.Enabled = cboLang.Enabled = true;
                btnOpenPano.Visible = ok && CaptureApp.LastPanorama != null;
                btnOpenDir.Visible = CaptureApp.LastDir != null && Directory.Exists(CaptureApp.LastDir);
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    static void OpenPath(string p)
    {
        if (string.IsNullOrEmpty(p) || !(File.Exists(p) || Directory.Exists(p))) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (!busy) return;
        if (MessageBox.Show(this, L.T("Une capture est en cours. L'arrêter et fermer le programme ?", "A capture is in progress. Stop it and close the program?"), "Capture Zeus",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
        Signal.StopLoop();
        GameAudio.Restore();
        Environment.Exit(1);
    }

    // Recoit tout ce que le programme ecrit (Console.WriteLine) et l'ajoute au journal de la fenetre.
    class LogWriter : TextWriter
    {
        readonly CaptureForm f;
        readonly StringBuilder line = new StringBuilder();
        public LogWriter(CaptureForm f) { this.f = f; }
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void Write(char c)
        {
            if (c == '\r') return;
            if (c != '\n') { line.Append(c); return; }
            string s = line.ToString(); line.Clear();
            f.UI(() => f.log.AppendText(s + Environment.NewLine));
        }
    }
}
