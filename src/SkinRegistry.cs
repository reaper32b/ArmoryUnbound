using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ArmoryUnbound
{
    public class SkinInfo
    {
        public string Id;          // "auSkin<CleanFileName>"
        public string DisplayName; // "Tiger Stripe"
        public string ImagePath;   // null for the identity test skin
        public string Hash;        // first 16 hex chars of the file's MD5 (cache key for step 7.3)
        public bool Custom;        // from Skins\Custom
        public bool Hidden;        // not counted, not offered as an item (identity test skin)
        public bool Identity;      // builder copies the gun's texture unchanged
        public string AvgHex;      // average colour "rrggbb" - tints the skin item's paint icon
    }

    // Scans Mods\0-ArmoryUnbound\Skins\Starter and Skins\Custom once at startup (scope item 2).
    // Each readable .png/.jpg/.jpeg = one skin. Unreadable files are skipped with one warning each.
    // Adding or removing an image takes a game restart.
    public static class SkinRegistry
    {
        public const string IdentityId = "auSkinIdentity";

        private static readonly Dictionary<string, SkinInfo> byId = new Dictionary<string, SkinInfo>(StringComparer.Ordinal);
        private static readonly List<SkinInfo> ordered = new List<SkinInfo>();
        public static int StarterCount;
        public static int CustomCount;
        public static string Root;

        public static void Load(string modPath)
        {
            byId.Clear();
            ordered.Clear();
            StarterCount = 0;
            CustomCount = 0;
            Root = Path.Combine(modPath, "Skins");

            // Hidden test skin first, so an image called "identity" can't take its id.
            SkinInfo ident = new SkinInfo();
            ident.Id = IdentityId;
            ident.DisplayName = "Identity (test: copies the stock texture)";
            ident.Hash = "identity";
            ident.Hidden = true;
            ident.Identity = true;
            byId[ident.Id] = ident;

            StarterCount = Scan(Path.Combine(Root, "Starter"), false);
            CustomCount = Scan(Path.Combine(Root, "Custom"), true);
            Log.Out("[ArmoryUnbound] skins: " + StarterCount + " starter, " + CustomCount + " custom");
        }

        private static int Scan(string dir, bool custom)
        {
            if (!Directory.Exists(dir))
            {
                if (!custom) Log.Warning("[ArmoryUnbound] skin folder missing: " + dir);
                return 0;
            }
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            int n = 0;
            foreach (string f in files)
            {
                string why, avg;
                byte[] bytes;
                if (!Validate(f, out bytes, out avg, out why))
                {
                    Log.Warning("[ArmoryUnbound] skipped skin image " + f + ": " + why);
                    continue;
                }
                string baseName = Path.GetFileNameWithoutExtension(f);
                SkinInfo s = new SkinInfo();
                s.Id = UniqueId("auSkin" + CleanName(baseName));
                s.DisplayName = Readable(baseName);
                s.ImagePath = f;
                s.Hash = Md5(bytes);
                s.Custom = custom;
                s.AvgHex = avg;
                byId[s.Id] = s;
                ordered.Add(s);
                n++;
            }
            return n;
        }

        private static bool Validate(string path, out byte[] bytes, out string avgHex, out string why)
        {
            bytes = null;
            why = null;
            avgHex = "ffffff";
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                why = "can't read the file (" + e.Message + ")";
                return false;
            }
            Texture2D t = null;
            try
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!ImageConversion.LoadImage(t, bytes, false))
                {
                    why = "not a readable PNG or JPG image";
                    return false;
                }
                if (t.width < 8 || t.height < 8)
                {
                    why = "image is too small (" + t.width + "x" + t.height + ")";
                    return false;
                }
                Color32[] px = t.GetPixels32();
                long r = 0, g = 0, b = 0;
                for (int i = 0; i < px.Length; i++) { r += px[i].r; g += px[i].g; b += px[i].b; }
                int n = Math.Max(1, px.Length);
                avgHex = ((int)(r / n)).ToString("x2") + ((int)(g / n)).ToString("x2") + ((int)(b / n)).ToString("x2");
                return true;
            }
            catch (Exception e)
            {
                why = "image decode failed (" + e.Message + ")";
                return false;
            }
            finally
            {
                if (t != null) UnityEngine.Object.Destroy(t);
            }
        }

        // Decodes a skin's image (main thread). Row 0 = bottom, like every Unity texture.
        public static Color32[] LoadPixels(SkinInfo s, out int w, out int h)
        {
            w = 0;
            h = 0;
            Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(t, File.ReadAllBytes(s.ImagePath), false)) return null;
                w = t.width;
                h = t.height;
                return t.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.Destroy(t);
            }
        }

        // "tiger_stripe" -> "TigerStripe" (letters and digits only)
        public static string CleanName(string fileName)
        {
            StringBuilder sb = new StringBuilder();
            bool up = true;
            foreach (char c in fileName)
            {
                if (char.IsLetterOrDigit(c) && c < 128)
                {
                    sb.Append(up ? char.ToUpperInvariant(c) : c);
                    up = false;
                }
                else up = true;
            }
            return sb.Length > 0 ? sb.ToString() : "Custom";
        }

        // "tiger_stripe" -> "Tiger Stripe"
        public static string Readable(string fileName)
        {
            StringBuilder sb = new StringBuilder();
            bool up = true;
            foreach (char c in fileName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (up && sb.Length > 0) sb.Append(' ');
                    sb.Append(up ? char.ToUpperInvariant(c) : c);
                    up = false;
                }
                else up = true;
            }
            return sb.Length > 0 ? sb.ToString() : fileName;
        }

        private static string UniqueId(string id)
        {
            if (!byId.ContainsKey(id)) return id;
            int i = 2;
            while (byId.ContainsKey(id + i)) i++;
            return id + i;
        }

        private static string Md5(byte[] bytes)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static SkinInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            SkinInfo s;
            return byId.TryGetValue(id, out s) ? s : null;
        }

        public static bool IsSkin(string id)
        {
            return Get(id) != null;
        }

        // Visible skins in load order (starter first, then custom); the hidden test skin is not included.
        public static List<SkinInfo> All()
        {
            return new List<SkinInfo>(ordered);
        }
    }
}
