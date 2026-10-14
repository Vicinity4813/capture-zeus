// Langue de l'interface (francais / anglais). Le choix est memorise dans %LOCALAPPDATA%\Capture Zeus\langue.txt ;
// par defaut, la langue de Windows (francais si Windows est en francais, anglais sinon).
using System;
using System.Globalization;
using System.IO;

static class L
{
    public static bool En;

    static string SettingsFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capture Zeus", "langue.txt"); }
    }

    public static void Load()
    {
        En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "fr";
        try
        {
            if (File.Exists(SettingsFile)) En = File.ReadAllText(SettingsFile).Trim() == "en";
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            File.WriteAllText(SettingsFile, En ? "en" : "fr");
        }
        catch { }
    }

    // Texte dans la langue choisie.
    public static string T(string fr, string en) { return En ? en : fr; }
}
