using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ArmoryUnbound
{
    // Disk cache of built skin textures (scope item 5): the raw compressed bytes (all mips) that
    // Texture2D.GetRawTextureData returns, loaded next session with LoadRawTextureData.
    // Folder: %APPDATA%\7DaysToDie\ArmoryUnbound\SkinCache\ - safe to delete at any time.
    // Key (in the file name): skin id + skin image hash + source texture name + size + format
    // + a 16x16 fingerprint of the source texture + builder version. A file from an older builder
    // version is removed at startup; a bad file is deleted and rebuilt with one warning.
    // Nothing here throws: if the folder can't be used, the cache turns off with one warning.
    public static class SkinCache
    {
        private const int Magic = 0x4B535541; // "AUSK"
        private const int FileVersion = 1;
        public static string Dir;
        private static bool inited, disabled, warned;
        private static readonly object gate = new object();

        public class Data
        {
            public int W, H, Format, Mips;
            public bool Linear;
            public byte[] Bytes;
        }

        private static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                Dir = Path.Combine(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie"), "ArmoryUnbound"), "SkinCache");
                Directory.CreateDirectory(Dir);
                int stale = 0;
                string mine = "_v" + SkinBuilder.Version + ".bin";
                foreach (string f in Directory.GetFiles(Dir, "*.bin"))
                {
                    if (f.EndsWith(mine, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); stale++; } catch (Exception) { }
                }
                foreach (string f in Directory.GetFiles(Dir, "*.tmp"))
                {
                    try { File.Delete(f); } catch (Exception) { }
                }
                Log.Out("[ArmoryUnbound] skin cache: " + Dir + (stale > 0 ? " (removed " + stale + " files from an older builder version)" : ""));
            }
            catch (Exception e)
            {
                Disable("can't use " + Dir + ": " + e.Message);
            }
        }

        public static bool Enabled
        {
            get
            {
                Init();
                return !disabled;
            }
        }

        // Safe from any thread; the warning is logged on the main thread.
        public static void Disable(string why)
        {
            lock (gate)
            {
                disabled = true;
                if (warned) return;
                warned = true;
            }
            SkinBuilder.MainLog(true, "skin disk cache is off (" + why + ") - skins are rebuilt in memory each session");
        }

        public static string PathFor(SkinInfo skin, string srcName, int w, int h, string format, string fingerprint)
        {
            Init();
            if (Dir == null) return null;
            string name = Clean(skin.Id) + "_" + Clean(skin.Hash) + "_" + Clean(srcName) + "_" + w + "x" + h + "_" + Clean(format)
                + "_" + fingerprint + "_v" + SkinBuilder.Version + ".bin";
            return Path.Combine(Dir, name);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= 48) break;
                sb.Append((char.IsLetterOrDigit(c) && c < 128) || c == '-' ? c : '-');
            }
            return sb.ToString();
        }

        // Worker thread. Returns null when there is no file. A bad file -> deleted, why set (caller warns + rebuilds).
        public static Data TryRead(string path, int w, int h, out string why)
        {
            why = null;
            if (disabled || path == null || !File.Exists(path)) return null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 40) throw new InvalidDataException("file too short");
                    if (br.ReadInt32() != Magic) throw new InvalidDataException("not a skin cache file");
                    if (br.ReadInt32() != FileVersion) throw new InvalidDataException("old file format");
                    if (br.ReadInt32() != SkinBuilder.Version) throw new InvalidDataException("old builder version");
                    Data d = new Data();
                    d.W = br.ReadInt32();
                    d.H = br.ReadInt32();
                    d.Format = br.ReadInt32();
                    d.Mips = br.ReadInt32();
                    d.Linear = br.ReadInt32() != 0;
                    int len = br.ReadInt32();
                    if (d.W != w || d.H != h) throw new InvalidDataException("size " + d.W + "x" + d.H + " doesn't match " + w + "x" + h);
                    if (d.Mips < 1 || d.Mips > 16) throw new InvalidDataException("bad mip count " + d.Mips);
                    if (len <= 0 || len != fs.Length - fs.Position) throw new InvalidDataException("data length " + len + " doesn't match the file");
                    d.Bytes = br.ReadBytes(len);
                    if (d.Bytes.Length != len) throw new InvalidDataException("truncated");
                    return d;
                }
            }
            catch (Exception e)
            {
                why = e.Message;
                Delete(path);
                return null;
            }
        }

        public static void Delete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        // Worker thread. Writes to .tmp then renames, so a quit mid-write never leaves a half file.
        public static void Write(string path, Data d)
        {
            if (disabled || path == null) return;
            string tmp = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));   // the folder may have been deleted mid-session
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(Magic);
                    bw.Write(FileVersion);
                    bw.Write(SkinBuilder.Version);
                    bw.Write(d.W);
                    bw.Write(d.H);
                    bw.Write(d.Format);
                    bw.Write(d.Mips);
                    bw.Write(d.Linear ? 1 : 0);
                    bw.Write(d.Bytes.Length);
                    bw.Write(d.Bytes);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Delete(tmp);
                Disable("can't write " + path + ": " + e.Message);
            }
        }
    }
}
