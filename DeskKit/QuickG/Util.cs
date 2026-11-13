using System;
using System.IO;

namespace CtrlG
{
    internal static class Util
    {
        private static readonly object Gate = new object();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskKit", "log.txt");

        public static void Roll()
        {
            try
            {
                if (!File.Exists(LogPath)) return;
                string old = LogPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(LogPath, old);
            }
            catch { }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
