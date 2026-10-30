using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PackPilot.Core;

public static class Util
{
    /// <summary>A name that is safe in file names and registry keys: letters, digits, dot, dash, underscore.</summary>
    public static string SafeId(string name)
    {
        var s = Regex.Replace(name ?? "", @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return string.IsNullOrEmpty(s) ? "App" : (s.Length > 60 ? s[..60] : s);
    }

    /// <summary>A PowerShell single-quoted string literal.</summary>
    public static string PsQuote(string? s) => "'" + (s ?? "").Replace("'", "''") + "'";

    /// <summary>Splits a Windows path into folder and file name, on any OS: "C:\\A\\b.exe" -> ("C:\\A", "b.exe").</summary>
    public static (string Folder, string Name) SplitWindowsPath(string path)
    {
        var p = (path ?? "").Trim().TrimEnd('\\', '/');
        int i = p.LastIndexOfAny(new[] { '\\', '/' });
        return i < 0 ? ("", p) : (p[..i], p[(i + 1)..]);
    }

    public static string QuoteIfNeeded(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    /// <summary>Writes UTF-8 with BOM and CRLF: Windows PowerShell 5.1 reads a BOM-less file as ANSI.</summary>
    public static void WritePs1(string path, string content)
    {
        var text = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(true));
    }

    public static void WriteText(string path, string content)
    {
        var text = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    /// <summary>Total size and count of everything under a folder, for the "include the whole folder" warning.</summary>
    public static (long Bytes, int Files) FolderStats(string dir)
    {
        long b = 0; int n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { b += new FileInfo(f).Length; n++; }
        return (b, n);
    }

    /// <summary>Normalizes a version string so [version] in PowerShell can parse it: "1.2.3 beta" -> "1.2.3".</summary>
    public static string CleanVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        var m = Regex.Match(v, @"\d+(\.\d+){0,3}");
        return m.Success ? m.Value : "";
    }

    /// <summary>
    /// A display name that carries its version ("7-Zip 24.08 (x64 edition)") becomes "7-Zip*",
    /// so the rule still matches after the app updates itself.
    /// </summary>
    public static string NamePattern(string displayName, string version)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "";
        var parts = (version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int n = parts.Length; n >= 2; n--)
        {
            var v = string.Join('.', parts.Take(n));
            int at = displayName.IndexOf(v, StringComparison.Ordinal);
            if (at > 0) return displayName[..at].TrimEnd(' ', '-', '_', 'v', 'V') + "*";
        }
        return displayName;
    }
}
