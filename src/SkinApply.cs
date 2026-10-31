using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace ArmoryUnbound
{
    // Puts a gun's skin on the model in your hands (scope item 6). Two paths, as proven by AJ_UniqueSkins:
    //  a) Harmony postfix on ItemClass.CloneModel(World, ItemValue, Vector3, Transform, MeshPurpose, TextureFullArray)
    //     for MeshPurpose.Hold: Inventory.createHeldItem passes the slot's own ItemValue, so this is per gun.
    //     It walks every renderer incl. inactive ones, so EFTX's ItemFpv/ and 3RDPV/ copies are caught
    //     before KF re-parents ItemFpv under the camera (AnimationTargetsAbs.Init runs after CloneModel).
    //  b) a re-apply pass ~0.05/0.3/1.05/2.55 s after every draw (and when a skin finishes building),
    //     on the held model and every active ItemFpv under the player, driven by the HELD ItemValue's au.skin.
    //     If the held gun has no skin, any of our materials found there are put back to stock.
    // Material rules: renderer name on the built-in keep-list (bullet|patron|shell|cartridge|round_|spring) is never
    // touched; nor is anything matching a word in Skins\stock_parts.txt (StockParts: renderer name, any parent up to
    // the model root, or - per material slot - the slot's texture name); a material is skinned only if it is opaque (render queue below transparent, not a
    // glass/transparent/particle/reflex shader) and has a _MainTex. We always swap in our OWN material copy
    // (never edit a material in place: renderers with KF's IgnoreTint keep the shared asset material).
    // The copy keeps the original's _Color, so vanilla dyes still tint the skin.
    public static class SkinApply
    {
        private static readonly Regex Keep = new Regex("bullet|patron|shell|cartridge|round_|spring", RegexOptions.IgnoreCase);
        private static readonly Regex SkipShader = new Regex("glass|transparent|particle|reflex|lens|reticle|scope", RegexOptions.IgnoreCase);
        private const string MainTex = "_MainTex";
        private static readonly HashSet<string> noted = new HashSet<string>();
        private static readonly HashSet<string> unknownWarned = new HashSet<string>();

        public static void Note(string key, string msg)
        {
            if (noted.Add(key)) Log.Out("[ArmoryUnbound] " + msg);
        }

        // The skin to show for this gun, or null. Unknown ids (image removed) -> stock + one warning.
        public static SkinInfo SkinFor(ItemValue iv)
        {
            string id = GunData.GetSkin(iv);
            if (id == null || !GunData.IsGun(iv)) return null;
            SkinInfo s = SkinRegistry.Get(id);
            if (s == null && unknownWarned.Add(id))
                Log.Warning("[ArmoryUnbound] a gun has skin '" + id + "', which isn't loaded (image removed?) - it shows stock");
            return s;
        }

        public static bool Eligible(Material m)
        {
            if (m == null || !m.HasProperty(MainTex)) return false;
            if (m.GetTexture(MainTex) == null) return false;
            if (m.renderQueue >= 2501) return false; // transparent queue (3000) and anything after geometry/alpha-test
            string rt = m.GetTag("RenderType", false, "");
            if (rt == "Transparent") return false;
            if (m.shader != null && SkipShader.IsMatch(m.shader.name)) return false;
            return true;
        }

        // Why this whole renderer stays stock, or null: built-in rule on its name, a stock-parts word in its name
        // or in any parent's name up to (not including) the model root - EFTX attachments sit under mod_* parents.
        public static string KeepReason(Renderer r, Transform root)
        {
            if (Keep.IsMatch(r.name)) return "built-in rule (bullet/spring) on '" + r.name + "'";
            string w = StockParts.Match(r.name);
            if (w != null) return "name '" + r.name + "' has '" + w + "'";
            for (Transform t = r.transform.parent; t != null && t != root; t = t.parent)
            {
                w = StockParts.Match(t.name);
                if (w != null) return "parent '" + t.name + "' has '" + w + "'";
            }
            return null;
        }

        // Why one material slot stays stock even though its renderer is skinned: its texture name has a word.
        public static string SlotKeepReason(Material m)
        {
            if (m == null || !m.HasProperty(MainTex)) return null;
            Texture t = m.GetTexture(MainTex);
            if (t == null) return null;
            string w = StockParts.Match(t.name);
            return w != null ? "texture '" + t.name + "' has '" + w + "'" : null;
        }

        // Returns how many material slots now show the skin (newly or already). Textures still building are
        // requested and the slot stays stock until a later pass.
        public static int Apply(Transform root, SkinInfo skin, out int pendingSlots, out int keptRenderers)
        {
            pendingSlots = 0;
            keptRenderers = 0;
            if (root == null || skin == null) return 0;
            SkinRuntime.Ensure();   // the re-apply passes need the runner from the first skinned gun on
            int shown = 0;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rs.Length; r++)
            {
                Renderer rend = rs[r];
                if (rend == null) continue;
                if (KeepReason(rend, root) != null)
                {
                    keptRenderers++;
                    AuSkinMark old = FindMark(rend);
                    if (old != null) old.RestoreAndRemove();   // skinned before the word list changed
                    continue;
                }
                shown += ApplyRenderer(rend, skin, ref pendingSlots, ref keptRenderers);
            }
            return shown;
        }

        private static int ApplyRenderer(Renderer rend, SkinInfo skin, ref int pendingSlots, ref int keptParts)
        {
            AuSkinMark mark = FindMark(rend);
            if (mark != null && mark.SkinId == skin.Id && mark.Complete) return mark.Shown;

            Material[] originals = mark != null ? mark.Originals : rend.sharedMaterials;
            Material[] current = rend.sharedMaterials;
            Material[] next = new Material[originals.Length];
            int shown = 0, waiting = 0;
            for (int i = 0; i < originals.Length; i++)
            {
                Material o = originals[i];
                next[i] = o;
                if (!Eligible(o)) continue;
                if (SlotKeepReason(o) != null)
                {
                    keptParts++;
                    continue;
                }
                Texture src = o.GetTexture(MainTex);
                Texture2D tex = SkinBuilder.TryGet(skin.Id, src);
                if (tex == null)
                {
                    SkinBuilder.Request(skin, src, delegate (Texture2D t) { SkinRuntime.RequestPass(); });
                    waiting++;
                    continue;
                }
                if (mark == null)
                {
                    mark = rend.gameObject.AddComponent<AuSkinMark>();
                    mark.Init(rend, originals, skin.Id);
                }
                else if (mark.SkinId != skin.Id)
                {
                    mark.Reset(skin.Id);
                }
                Material made = mark.Made[i];
                if (made == null)
                {
                    made = new Material(o);
                    made.name = o.name + " (AuSkin " + skin.Id + ")";
                    made.SetTexture(MainTex, tex);
                    mark.Made[i] = made;
                }
                next[i] = made;
                shown++;
            }
            if (mark != null)
            {
                mark.Complete = waiting == 0;
                mark.Shown = shown;
                if (!SameMaterials(current, next)) rend.sharedMaterials = next;
            }
            pendingSlots += waiting;
            return shown;
        }

        public static AuSkinMark FindMark(Renderer rend)
        {
            AuSkinMark[] marks = rend.GetComponents<AuSkinMark>();
            for (int i = 0; i < marks.Length; i++)
                if (marks[i] != null && marks[i].Owner == rend && marks[i].Originals != null) return marks[i];
            return null;
        }

        private static bool SameMaterials(Material[] a, Material[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Puts every renderer we skinned under root back to its stock materials. Returns how many.
        public static int Restore(Transform root)
        {
            if (root == null) return 0;
            int n = 0;
            AuSkinMark[] marks = root.GetComponentsInChildren<AuSkinMark>(true);
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i] == null || marks[i].Originals == null) continue;
                marks[i].RestoreAndRemove();
                n++;
            }
            return n;
        }
    }

    // On each renderer we skinned: its stock materials and the copies we made (destroyed with it).
    public class AuSkinMark : MonoBehaviour
    {
        public Renderer Owner;
        public Material[] Originals;
        public Material[] Made;
        public string SkinId;
        public bool Complete;
        public int Shown;

        public void Init(Renderer owner, Material[] originals, string skinId)
        {
            Owner = owner;
            Originals = (Material[])originals.Clone();
            Made = new Material[originals.Length];
            SkinId = skinId;
        }

        public void Reset(string skinId)
        {
            DestroyMade();
            Made = new Material[Originals.Length];
            SkinId = skinId;
            Complete = false;
            Shown = 0;
        }

        public void RestoreAndRemove()
        {
            if (Owner != null && Originals != null) Owner.sharedMaterials = Originals;
            DestroyMade();
            Originals = null;   // Destroy is deferred to the end of the frame; FindMark skips us from now on
            Destroy(this);
        }

        private void DestroyMade()
        {
            if (Made == null) return;
            for (int i = 0; i < Made.Length; i++)
                if (Made[i] != null) Destroy(Made[i]);   // textures are shared and cached, not destroyed here
            Made = null;
        }

        private void OnDestroy()
        {
            DestroyMade();
        }
    }

    [HarmonyPatch(typeof(ItemClass), "CloneModel", new Type[] { typeof(World), typeof(ItemValue), typeof(Vector3), typeof(Transform), typeof(BlockShape.MeshPurpose), typeof(TextureFullArray) })]
    public static class Patch_ItemClass_CloneModel
    {
        // Last, so we run after KFCommonUtilityLib's postfix (which may swap whole materials via KFMaterialReplacer).
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ItemClass __instance, Transform __result, ItemValue _itemValue, BlockShape.MeshPurpose _purpose)
        {
            try
            {
                if (__result == null || _purpose != BlockShape.MeshPurpose.Hold) return;
                SkinInfo skin = SkinApply.SkinFor(_itemValue);
                if (skin == null) return;
                int pend, kept;
                int n = SkinApply.Apply(__result, skin, out pend, out kept);
                SkinApply.Note("clone|" + skin.Id + "|" + __instance.Name,
                    "skin " + skin.Id + " on " + __instance.Name + " (new held model '" + __result.name + "'): " + n + " materials skinned, "
                    + pend + " waiting for their texture, " + kept + " parts kept stock (keep rules / stock_parts.txt)");
            }
            catch (Exception e)
            {
                SkinApply.Note("cloneerr|" + e.GetType().Name, "skin postfix on CloneModel failed (gun stays stock): " + e);
            }
        }
    }

    // Main-thread runner: pumps the builder and runs the re-apply passes after each draw.
    public class SkinRuntime : MonoBehaviour
    {
        private static SkinRuntime instance;
        private static readonly float[] Offsets = { 0.05f, 0.30f, 1.05f, 2.55f };   // after each draw, as AJ_UniqueSkins
        private readonly List<float> due = new List<float>();
        private bool passNextFrame;
        private ItemValue lastIv;
        private Transform lastHeld;
        private int lastIdx = -1;

        public static void Ensure()
        {
            if (instance != null) return;
            GameObject go = new GameObject("ArmoryUnbound.SkinRuntime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            instance = go.AddComponent<SkinRuntime>();
        }

        public static void RequestPass()
        {
            Ensure();
            instance.passNextFrame = true;
        }

        public static void ScheduleDrawPasses()
        {
            Ensure();
            instance.Schedule();
        }

        private void Schedule()
        {
            float now = Time.time;
            due.Clear();
            for (int i = 0; i < Offsets.Length; i++) due.Add(now + Offsets[i]);
        }

        private void Update()
        {
            try
            {
                bool busy = SkinBuilder.Busy;
                SkinBuilder.Pump();
                WatchFrames(busy);
                EntityPlayerLocal player = (GameManager.Instance == null || GameManager.Instance.World == null) ? null : GameManager.Instance.World.GetPrimaryPlayer();
                if (player == null || player.inventory == null)
                {
                    lastIv = null;
                    lastHeld = null;
                    lastIdx = -1;
                    due.Clear();
                    return;
                }
                Inventory inv = player.inventory;
                ItemValue iv = inv.holdingItemItemValue;
                Transform held = inv.GetHoldingItemTransform();
                int idx = inv.holdingItemIdx;
                if (!ReferenceEquals(iv, lastIv) || held != lastHeld || idx != lastIdx)
                {
                    lastIv = iv;
                    lastHeld = held;
                    lastIdx = idx;
                    Schedule();
                }
                bool run = passNextFrame;
                passNextFrame = false;
                float now = Time.time;
                for (int i = due.Count - 1; i >= 0; i--)
                {
                    if (now >= due[i])
                    {
                        due.RemoveAt(i);
                        run = true;
                    }
                }
                if (run) Pass(player, inv, iv, held);
            }
            catch (Exception e)
            {
                SkinApply.Note("passerr|" + e.GetType().Name, "skin re-apply pass failed: " + e);
            }
        }

        // While skin work is going on, track the longest whole frame (the game's own work included) and
        // log one summary line when it's done - the 50 ms acceptance check (scope item 4).
        private bool inBatch;
        private int batchFrames, batchSlow;
        private float batchMaxFrameMs, batchStart;

        private void WatchFrames(bool busyBefore)
        {
            if (busyBefore)
            {
                if (!inBatch)
                {
                    inBatch = true;
                    batchFrames = batchSlow = 0;
                    batchMaxFrameMs = 0f;
                    batchStart = Time.realtimeSinceStartup;
                    SkinBuilder.BatchBuilt = SkinBuilder.BatchCached = 0;
                    SkinBuilder.BatchMaxMainMs = 0;
                    return;   // this frame's delta was before the work started
                }
                float ms = Time.unscaledDeltaTime * 1000f;
                batchFrames++;
                if (ms > batchMaxFrameMs) batchMaxFrameMs = ms;
                if (ms > 50f) batchSlow++;
                return;
            }
            if (!inBatch) return;
            inBatch = false;
            Log.Out("[ArmoryUnbound] skin work finished: " + SkinBuilder.BatchBuilt + " textures built, " + SkinBuilder.BatchCached
                + " from cache in " + (Time.realtimeSinceStartup - batchStart).ToString("0.00") + " s over " + batchFrames
                + " frames; longest frame " + batchMaxFrameMs.ToString("0") + " ms (" + batchSlow + " over 50 ms); longest skin work in one frame "
                + SkinBuilder.BatchMaxMainMs.ToString("0") + " ms");
        }

        // The held model plus every active first-person copy (EFTX/KF re-parent ItemFpv under the camera).
        public static List<Transform> Targets(EntityPlayerLocal player, Transform held)
        {
            List<Transform> targets = new List<Transform>();
            if (held != null) targets.Add(held);
            foreach (Transform t in player.transform.GetComponentsInChildren<Transform>(false))
                if (t.name == "ItemFpv" && !targets.Contains(t)) targets.Add(t);
            return targets;
        }

        // After the word list is reloaded: put everything back to stock, then re-skin with the new rules.
        public static void Reskin(EntityPlayerLocal player, Transform held)
        {
            foreach (Transform t in Targets(player, held)) SkinApply.Restore(t);
            ScheduleDrawPasses();
        }

        private static void Pass(EntityPlayerLocal player, Inventory inv, ItemValue iv, Transform held)
        {
            SkinInfo skin = SkinApply.SkinFor(iv);
            List<Transform> targets = Targets(player, held);

            if (skin == null)
            {
                int restored = 0;
                foreach (Transform t in targets) restored += SkinApply.Restore(t);
                if (restored > 0) Log.Out("[ArmoryUnbound] held gun has no skin: put " + restored + " renderers back to stock");
                return;
            }
            int shown = 0, pend = 0, kept = 0;
            foreach (Transform t in targets)
            {
                int p, k;
                shown += SkinApply.Apply(t, skin, out p, out k);
                pend += p;
                kept += k;
            }
            string itemName = iv.ItemClass != null ? iv.ItemClass.Name : "?";
            SkinApply.Note("pass|" + skin.Id + "|" + itemName + "|" + shown + "|" + pend,
                "skin " + skin.Id + " on held " + itemName + ": " + shown + " materials showing the skin, " + pend + " waiting, "
                + kept + " parts kept stock, across " + targets.Count + " model(s) (held + first-person copies)");
        }
    }

    // The stock-parts word list (AJ, 2026-10-28): parts of a skinned gun that keep their stock look. One word per
    // line in Skins\stock_parts.txt, matched case-insensitively anywhere in a renderer name, a parent name or a
    // texture name. Edit the file, then "au parts reload" in game (or restart) - no rebuild. Missing file = defaults
    // (and the file is written with them). On vanilla guns the barrel, iron sights and grip are part of the one body
    // mesh and texture, so only separate attachments can stay stock there; EFTX guns have a renderer per part.
    public static class StockParts
    {
        public static readonly string[] Defaults = { "barrel", "sight", "scope", "grip", "laser", "light", "suppressor", "silencer", "muzzle", "magazine", "mag_", "drum" };
        private static string[] words = Defaults;
        public static string FilePath;

        public static string[] Words { get { return words; } }

        public static void Load(string modPath)
        {
            FilePath = Path.Combine(Path.Combine(modPath, "Skins"), "stock_parts.txt");
            try
            {
                if (!File.Exists(FilePath))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("# Armory Unbound - parts of a skinned gun that stay stock.");
                    sb.AppendLine("# One word per line. A part stays stock if the word appears anywhere in its name, a parent's name");
                    sb.AppendLine("# or its texture name (not case-sensitive). Lines starting with # are ignored. Delete a word to skin");
                    sb.AppendLine("# that part. After editing: \"au parts reload\" in the console (F1), or restart the game.");
                    sb.AppendLine("# \"au parts\" lists every part of the gun in your hands and why it is skinned or stock.");
                    sb.AppendLine("# Note: on vanilla guns the barrel, iron sights and grip are part of the main body, so only");
                    sb.AppendLine("# separate attachments (scopes, lasers, lights, suppressors) can stay stock there.");
                    foreach (string w in Defaults) sb.AppendLine(w);
                    File.WriteAllText(FilePath, sb.ToString());
                    words = Defaults;
                    Log.Out("[ArmoryUnbound] stock parts: wrote the default list (" + words.Length + " words) to " + FilePath);
                    return;
                }
                List<string> list = new List<string>();
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string w = raw.Trim().ToLowerInvariant();
                    if (w.Length == 0 || w.StartsWith("#") || list.Contains(w)) continue;
                    list.Add(w);
                }
                words = list.ToArray();
                Log.Out("[ArmoryUnbound] stock parts: " + words.Length + " words (" + (words.Length > 0 ? string.Join(", ", words) : "none - every part is skinned") + ")");
            }
            catch (Exception e)
            {
                words = Defaults;
                Log.Warning("[ArmoryUnbound] stock parts: couldn't read " + FilePath + " (" + e.Message + ") - using the defaults");
            }
        }

        // The first word found in s, or null.
        public static string Match(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string l = s.ToLowerInvariant();
            for (int i = 0; i < words.Length; i++)
                if (l.IndexOf(words[i], StringComparison.Ordinal) >= 0) return words[i];
            return null;
        }
    }
}
