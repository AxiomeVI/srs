using System;
using System.IO;

namespace Celeste.Mod.SpeedrunSheet;

/// A file replaced whole or not at all; callers serialise their own writes.
internal static class AtomicFile {
    /// Where TryWrite writes before it moves: left behind when the process
    /// dies between the two, or when its own cleanup fails.
    public static string TempOf(string path) => path + ".tmp";

    /// Writes beside the file, then moves over it. Never throws: a failure comes
    /// back in error, and the temporary file is deleted, since it may hold the
    /// export URL. leftover is why that delete failed too, null when it did not.
    public static bool TryWrite(string path, string text, out Exception error, out Exception leftover) {
        string tmp = TempOf(path);
        leftover = null;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            error = null;
            return true;
        } catch (Exception e) {
            error = e;
            try {
                File.Delete(tmp);
            } catch (Exception cleanup) {
                leftover = cleanup;
            }

            return false;
        }
    }
}
