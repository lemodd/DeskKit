using System;
using System.IO;

namespace MiniPix.Core;

/// <summary>Tiny append-only trace log used to diagnose startup / capture problems.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string? Path = Resolve();

    private static string? Resolve()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string full = System.IO.Path.Combine(dir, "DeskKit", "log.txt");
            Directory.CreateDirectory(System.IO.Path.Combine(dir, "DeskKit"));
            return full;
        }
        catch { return null; }
    }

    public static void Write(string message)
    {
        if (Path == null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* logging must never be fatal */ }
    }
}
