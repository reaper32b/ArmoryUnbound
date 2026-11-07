using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ArmoryUnbound
{
    // 1.1.0 (AJ 2026-11-02): on melee weapons, tools and bows a skin covers the HEAD only - not the handle, not the
    // cutting edge. Those models are one mesh and one texture (UL steel axe: material 'Axe', _Color tint, no tint mask;
    // vanilla fire axe likewise), so the split has to be per pixel. Per material slot, in this order:
    //   1. Mask file Skins\Masks\<texture name>.png - white = skin, black = stock, grey = blend. Exact (head only, edge
    //      excluded). Matched by the _MainTex name, case-insensitive; any size (resampled). "au mask export" writes the
    //      held item's textures and their automatic masks to Skins\Masks\_export\ as a starting point for painting one.
    //   2. No mask file: the whole material (the dye rule). A metallic-map fallback was tried and dropped (see For).
    // Guns never use masks (they keep the stock_parts.txt word list).
    public sealed class MaskSpec
    {
        public string Id;          // part of the build/cache key
        public string Kind;        // "file" or "metal" (for au parts)
        public Texture Metal;      // metal mode
        public Color32[] FilePx;   // file mode
        public int FW, FH;
        public string Source;      // file path or metallic texture name
    }

    public static class SkinMask
    {
        public const string MetalProp = "_MetallicGlossMap";
        public static string Dir;   // <mod>\Skins\Masks
        private class FileMask { public Color32[] Px; public int W, H; public string Hash; public string Path; }
        private static readonly Dictionary<string, FileMask> files = new Dictionary<string, FileMask>(StringComparer.OrdinalIgnoreCase);
        private static bool scanned;

        public static void Init(string modPath)
        {
            Dir = Path.Combine(Path.Combine(modPath, "Skins"), "Masks");
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                string readme = Path.Combine(Dir, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme,
                        "Armory Unbound - skin masks for melee weapons, tools and bows.\r\n" +
                        "A PNG named after a texture (e.g. Axe_A.png) decides where a skin goes on that texture:\r\n" +
                        "white = skin, black = stock, grey = blend. Any size. Without a mask the whole texture is skinned.\r\n" +
                        "<item name>.<texture>.png applies to that item only; <texture>.png to every item using it.\r\n" +
                        "Hold the item and type 'au mask export' (F1): its textures and automatic masks land in _export\\.\r\n" +
                        "Paint the head white, everything else black, save it here, then 'au parts reload'.\r\n");
            }
            catch (Exception e) { Log.Warning("[ArmoryUnbound] masks folder: " + e.Message); }
            Reload();
        }

        // Re-read every mask file (main thread: LoadImage). Called at startup and by "au parts reload".
        public static void Reload()
        {
            files.Clear();
            scanned = true;
            if (Dir == null || !Directory.Exists(Dir)) return;
            foreach (string f in Directory.GetFiles(Dir, "*.png"))
            {
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    if (!ImageConversion.LoadImage(t, bytes, false)) { Log.Warning("[ArmoryUnbound] mask " + Path.GetFileName(f) + " isn't a readable PNG - ignored"); continue; }
                    FileMask m = new FileMask();
                    m.Px = t.GetPixels32(); m.W = t.width; m.H = t.height; m.Path = f;
                    uint h = 2166136261; foreach (byte b in bytes) { h ^= b; h *= 16777619; }
                    m.Hash = h.ToString("x8");
                    files[Path.GetFileNameWithoutExtension(f)] = m;
                }
                catch (Exception e) { Log.Warning("[ArmoryUnbound] mask " + Path.GetFileName(f) + ": " + e.Message); }
                finally { UnityEngine.Object.Destroy(t); }
            }
            Log.Out("[ArmoryUnbound] skin masks: " + files.Count + " file(s) in " + Dir);
        }

        // The mask for one material slot under the dye rule, or null (whole material).
        // File names: "<item name>.<texture>.png" (this item only - textures like vanilla's toolA_d are atlases shared
        // by many tools) wins over "<texture>.png" (every item using that texture).
        public static MaskSpec For(Material m)
        {
            return For(m, null);
        }

        public static MaskSpec For(Material m, string itemName)
        {
            if (m == null || !m.HasProperty("_MainTex")) return null;
            Texture main = m.GetTexture("_MainTex");
            if (main == null) return null;
            if (!scanned) Reload();
            FileMask f;
            if ((itemName != null && files.TryGetValue(itemName + "." + main.name, out f)) || files.TryGetValue(main.name, out f))
            {
                MaskSpec s = new MaskSpec();
                s.Kind = "file"; s.FilePx = f.Px; s.FW = f.W; s.FH = f.H; s.Source = f.Path;
                s.Id = "f" + f.Hash;
                return s;
            }
            // No automatic fallback (AJ 2026-11-02): the metallic-map mask was tested on 28 UL and vanilla tools and
            // clubs and was right on one - UL's metallic maps mark leather wraps and handles as metal, vanilla's tool
            // atlas has none, and bone clubs came out with no skin at all. Without a mask file, the whole texture is
            // skinned (the dye rule). Masks for every UL / vanilla melee weapon and tool ship in Skins\Masks.
            return null;
        }

        // Metallic (R, 0..255, linear) -> skin weight 0..1: below ~0.25 stock, above ~0.6 fully skinned.
        public static float FromMetal(byte r)
        {
            float x = (r / 255f - 0.25f) / 0.35f;
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x * x * (3f - 2f * x);
        }

        // File mask (any size) sampled at (x, y) of a W x H texture: luminance, nearest pixel.
        public static float FromFile(MaskSpec s, int x, int y, int W, int H)
        {
            int fx = (int)((x + 0.5f) * s.FW / W), fy = (int)((y + 0.5f) * s.FH / H);
            if (fx >= s.FW) fx = s.FW - 1;
            if (fy >= s.FH) fy = s.FH - 1;
            Color32 c = s.FilePx[fy * s.FW + fx];
            return (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
        }

        // "au mask export": the held item's dye-rule textures and their automatic masks, as PNGs, for painting a mask.
        public static List<string> Export(Transform model)
        {
            List<string> written = new List<string>();
            if (model == null || Dir == null) return written;
            string outDir = Path.Combine(Dir, "_export");
            Directory.CreateDirectory(outDir);
            HashSet<int> seen = new HashSet<int>();
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                AuSkinMark mark = SkinApply.FindMark(r);
                Material[] mats = mark != null ? mark.Originals : r.sharedMaterials;
                foreach (Material m in mats)
                {
                    if (m == null || !m.HasProperty("_MainTex")) continue;
                    Texture main = m.GetTexture("_MainTex");
                    if (main == null || !seen.Add(main.GetInstanceID())) continue;
                    written.Add(Save(main, Path.Combine(outDir, Safe(main.name) + ".png"), main.isDataSRGB, false));
                    Texture metal = m.HasProperty(MetalProp) ? m.GetTexture(MetalProp) : null;
                    if (metal != null)
                        written.Add(Save(metal, Path.Combine(outDir, Safe(main.name) + "_automask.png"), false, true));
                }
            }
            return written;
        }

        private static string Safe(string n)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        private static string Save(Texture src, string path, bool srgb, bool asMask)
        {
            int w = src.width, h = src.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                if (asMask)
                {
                    Color32[] px = tmp.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        byte v = (byte)(FromMetal(px[i].r) * 255f);
                        px[i] = new Color32(v, v, v, 255);
                    }
                    tmp.SetPixels32(px);
                }
                tmp.Apply(false, false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tmp));
                return path;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tmp);
            }
        }
    }
}
