using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmoryUnbound
{
    // Builds a skinned copy of one gun texture (scope items 4 and 5).
    // Per texture, spread over frames - the main thread does at most ONE GPU step and at most ONE
    // upload per frame; recolour and disk I/O run on worker threads:
    //   a. main: Blit the source into a 16x16 RenderTexture + AsyncGPUReadback -> fingerprint
    //      (part of the disk cache key, so two different textures with the same name/size never mix).
    //   b. worker: cache file present and valid -> c'. Otherwise:
    //   c. main: Blit the full source into a RenderTexture in the source's colour space (sRGB for colour
    //      textures, so the bytes come back unchanged) + AsyncGPUReadback (ReadPixels fallback).
    //   d. worker: make_skins.py apply_pattern - pattern tiled ~4x across (tile = max(256, W/4),
    //      resampled with wrap-around), times 0.40 + 0.85 x luminance normalised to its 2nd-98th
    //      percentile over the whole texture; original alpha kept. Identity skin: bytes unchanged.
    //   e. main: Texture2D (same colour space), mipmaps, Compress (DXT5), upload; worker saves the
    //      compressed bytes to the disk cache.
    //   c'. main: Texture2D from the cached bytes (LoadRawTextureData), upload. A bad file -> c.
    // Shared in memory per (skin, source texture): two guns of one type with the same skin cost one copy.
    public static class SkinBuilder
    {
        public const int Version = 2;          // bump when the output changes: old cache files are dropped
        private const int FingerprintSize = 16;
        private const bool HighQualityCompress = false; // Unity's dithered mode is slower; see build log 7.3

        private enum Stage { Fingerprint, Readback }

        private class Job
        {
            public string Key;
            public SkinInfo Skin;
            public Texture Source;
            public string SourceName, FormatName;
            public int W, H;
            public bool Srgb;
            public TextureWrapMode Wrap;
            public FilterMode Filter;
            public int Aniso;
            public Stage Next;
            public string CachePath;
            public SkinCache.Data Cached;   // set by the worker when the cache file was good
            public string CacheWhy;         // set by the worker when a cache file was bad
            public byte[] Pixels;           // readback, then recoloured in place
            public string Error;
            public string ReadbackPath;
            public double MsReadback, MsRecolour, MsLoad;
            public Stopwatch Clock;
            public readonly List<Action<Texture2D>> Callbacks = new List<Action<Texture2D>>();
        }

        private class Pattern
        {
            public Color32[] Px;
            public int W, H;
        }

        private static readonly Dictionary<string, Texture2D> built = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Job> pending = new Dictionary<string, Job>();
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static readonly Queue<Job> gpuQueue = new Queue<Job>();                                     // main only
        private static readonly Dictionary<string, Pattern> patterns = new Dictionary<string, Pattern>();   // locked
        private static readonly Dictionary<string, Color32[]> tiles = new Dictionary<string, Color32[]>(); // locked
        private static readonly Queue<Job> finished = new Queue<Job>();                                     // worker -> main, locked
        private static readonly Queue<Job> cacheMiss = new Queue<Job>();                                    // worker -> main, locked
        private static readonly Queue<KeyValuePair<bool, string>> logs = new Queue<KeyValuePair<bool, string>>(); // any -> main, locked
        private static readonly object gate = new object();

        // batch stats for the "skin work finished" line
        public static int BatchBuilt, BatchCached;
        public static double BatchMaxMainMs;

        public static bool Busy
        {
            get
            {
                if (pending.Count > 0 || gpuQueue.Count > 0) return true;
                lock (gate) { return finished.Count > 0 || cacheMiss.Count > 0; }
            }
        }

        private static string KeyOf(string skinId, Texture src)
        {
            return skinId + "|" + src.GetInstanceID();
        }

        public static void MainLog(bool warning, string msg)
        {
            lock (gate) { logs.Enqueue(new KeyValuePair<bool, string>(warning, msg)); }
        }

        public static Texture2D TryGet(string skinId, Texture src)
        {
            if (src == null) return null;
            Texture2D t;
            if (built.TryGetValue(KeyOf(skinId, src), out t) && t != null) return t;
            return null;
        }

        // ready is called on the main thread when the texture exists (at once if it's already built).
        // If the build fails it is never called; the gun just stays stock (one warning).
        public static void Request(SkinInfo skin, Texture src, Action<Texture2D> ready)
        {
            if (skin == null || src == null) return;
            string key = KeyOf(skin.Id, src);
            Texture2D t;
            if (built.TryGetValue(key, out t) && t != null)
            {
                if (ready != null) ready(t);
                return;
            }
            if (failed.Contains(key)) return;
            Job job;
            if (pending.TryGetValue(key, out job))
            {
                if (ready != null) job.Callbacks.Add(ready);
                return;
            }

            job = new Job();
            job.Key = key;
            job.Skin = skin;
            job.Source = src;
            job.SourceName = src.name;
            job.FormatName = src.graphicsFormat.ToString();
            job.W = src.width;
            job.H = src.height;
            job.Srgb = src.isDataSRGB;
            job.Wrap = src.wrapMode;
            job.Filter = src.filterMode;
            job.Aniso = src.anisoLevel;
            job.Clock = Stopwatch.StartNew();
            job.Next = Stage.Fingerprint;
            if (ready != null) job.Callbacks.Add(ready);
            pending[key] = job;
            gpuQueue.Enqueue(job);
            SkinRuntime.Ensure();
        }

        private static void Fail(Job job, string why)
        {
            pending.Remove(job.Key);
            failed.Add(job.Key);
            Log.Warning("[ArmoryUnbound] skin " + job.Skin.Id + " on texture '" + job.SourceName + "': " + why + " - this part stays stock");
        }

        // ---------------------------------------------------------------- main-thread pump (SkinRuntime.Update)
        // Returns the milliseconds spent on the main thread this frame.
        public static double Pump()
        {
            Stopwatch sw = Stopwatch.StartNew();
            for (;;)
            {
                KeyValuePair<bool, string> m;
                lock (gate)
                {
                    if (logs.Count == 0) break;
                    m = logs.Dequeue();
                }
                if (m.Key) Log.Warning("[ArmoryUnbound] " + m.Value); else Log.Out("[ArmoryUnbound] " + m.Value);
            }
            // cache misses / bad cache files go back for a full readback (cheap: just a queue move)
            for (;;)
            {
                Job miss = null;
                lock (gate) { if (cacheMiss.Count > 0) miss = cacheMiss.Dequeue(); }
                if (miss == null) break;
                if (miss.CacheWhy != null)
                    Log.Warning("[ArmoryUnbound] skin cache file for " + miss.Skin.Id + " on '" + miss.SourceName + "' was bad (" + miss.CacheWhy + ") - rebuilding it");
                miss.Next = Stage.Readback;
                gpuQueue.Enqueue(miss);
            }

            // at most one upload per frame
            Job done = null;
            lock (gate) { if (finished.Count > 0) done = finished.Dequeue(); }
            if (done != null) Finish(done);

            // at most one GPU step per frame
            while (gpuQueue.Count > 0)
            {
                Job job = gpuQueue.Dequeue();
                if (job.Source == null)   // texture unloaded before its turn (world unload): drop quietly
                {
                    pending.Remove(job.Key);
                    continue;
                }
                try
                {
                    if (job.Next == Stage.Fingerprint) StartFingerprint(job);
                    else StartReadback(job);
                }
                catch (Exception e)
                {
                    Fail(job, "readback failed: " + e.Message);
                }
                break;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > BatchMaxMainMs) BatchMaxMainMs = ms;
            return ms;
        }

        // ---------------------------------------------------------------- a. fingerprint
        private static void StartFingerprint(Job job)
        {
            RenderTexture rt = Blit(job, FingerprintSize, FingerprintSize);
            ReadBack(job, rt, FingerprintSize, FingerprintSize, delegate (byte[] px) { OnFingerprint(job, px); });
        }

        private static void OnFingerprint(Job job, byte[] px)
        {
            uint h = 2166136261;   // FNV-1a
            for (int i = 0; i < px.Length; i++) { h ^= px[i]; h *= 16777619; }
            job.CachePath = SkinCache.Enabled ? SkinCache.PathFor(job.Skin, job.SourceName, job.W, job.H, job.FormatName, h.ToString("x8")) : null;
            if (job.CachePath == null)
            {
                job.Next = Stage.Readback;
                gpuQueue.Enqueue(job);
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate (object o) { LoadCached((Job)o); }, job);
        }

        // ---------------------------------------------------------------- b. cache read (worker)
        private static void LoadCached(Job job)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string why;
            SkinCache.Data d = null;
            try { d = SkinCache.TryRead(job.CachePath, job.W, job.H, out why); }
            catch (Exception e) { why = e.Message; }
            job.MsLoad = sw.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                if (d != null)
                {
                    job.Cached = d;
                    finished.Enqueue(job);
                }
                else
                {
                    job.CacheWhy = why;
                    cacheMiss.Enqueue(job);
                }
            }
        }

        // ---------------------------------------------------------------- c. full readback
        private static void StartReadback(Job job)
        {
            if (!job.Skin.Identity && !EnsurePattern(job.Skin))
            {
                Fail(job, "can't read the skin image " + job.Skin.ImagePath);
                return;
            }
            RenderTexture rt = Blit(job, job.W, job.H);
            ReadBack(job, rt, job.W, job.H, delegate (byte[] px)
            {
                job.Pixels = px;
                job.MsReadback = job.Clock.Elapsed.TotalMilliseconds;
                if (px.Length < job.W * job.H * 4)
                {
                    Fail(job, "readback returned " + px.Length + " bytes for " + job.W + "x" + job.H);
                    return;
                }
                ThreadPool.QueueUserWorkItem(delegate (object o) { Recolour((Job)o); }, job);
            });
        }

        private static RenderTexture Blit(Job job, int w, int h)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                job.Srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(job.Source, rt);
            RenderTexture.active = prev;
            return rt;
        }

        // Async readback (released in the callback); ReadPixels fallback if async is unsupported or fails.
        private static void ReadBack(Job job, RenderTexture rt, int w, int h, Action<byte[]> then)
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                job.ReadbackPath = "async";
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, delegate (AsyncGPUReadbackRequest req)
                {
                    try
                    {
                        byte[] px;
                        if (req.hasError)
                        {
                            job.ReadbackPath = "ReadPixels (async failed)";
                            px = ReadPixelsNow(job, rt, w, h);
                        }
                        else
                        {
                            px = req.GetData<byte>(0).ToArray();
                            RenderTexture.ReleaseTemporary(rt);
                        }
                        then(px);
                    }
                    catch (Exception e)
                    {
                        Fail(job, "readback failed: " + e.Message);
                    }
                });
            }
            else
            {
                job.ReadbackPath = "ReadPixels";
                then(ReadPixelsNow(job, rt, w, h));
            }
        }

        private static byte[] ReadPixelsNow(Job job, RenderTexture rt, int w, int h)
        {
            Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, !job.Srgb);
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                return tmp.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tmp);
            }
        }

        private static bool EnsurePattern(SkinInfo skin)
        {
            lock (gate)
            {
                if (patterns.ContainsKey(skin.Id)) return true;
            }
            int w, h;
            Color32[] px = null;
            try { px = SkinRegistry.LoadPixels(skin, out w, out h); }
            catch (Exception) { w = h = 0; }
            if (px == null) return false;
            Pattern p = new Pattern();
            p.Px = px;
            p.W = w;
            p.H = h;
            lock (gate) { patterns[skin.Id] = p; }
            return true;
        }

        // ---------------------------------------------------------------- d. recolour (worker thread)
        private static void Recolour(Job job)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (!job.Skin.Identity) ApplyPattern(job);
            }
            catch (Exception e)
            {
                job.Error = e.GetType().Name + ": " + e.Message;
            }
            job.MsRecolour = sw.Elapsed.TotalMilliseconds;
            lock (gate) { finished.Enqueue(job); }
        }

        // Port of make_skins.py apply_pattern + the whole-texture luminance normalisation.
        private static void ApplyPattern(Job job)
        {
            byte[] a = job.Pixels;
            int W = job.W, H = job.H, N = W * H;

            // luminance of the original (sRGB byte values / 255, as make_skins.py), 2nd-98th percentile
            const int Bins = 4096;
            int[] hist = new int[Bins];
            float[] lum = new float[N];
            for (int i = 0, p = 0; i < N; i++, p += 4)
            {
                float l = (0.299f * a[p] + 0.587f * a[p + 1] + 0.114f * a[p + 2]) / 255f;
                lum[i] = l;
                int b = (int)(l * (Bins - 1) + 0.5f);
                if (b < 0) b = 0; else if (b >= Bins) b = Bins - 1;
                hist[b]++;
            }
            float lo = Percentile(hist, N, 0.02f, Bins);
            float hi = Percentile(hist, N, 0.98f, Bins);
            float range = Math.Max(hi - lo, 1e-3f);

            int tile = Math.Max(256, W / 4);
            Color32[] t = Tile(job.Skin.Id, tile);

            for (int y = 0, i = 0; y < H; y++)
            {
                int trow = (y % tile) * tile;
                for (int x = 0; x < W; x++, i++)
                {
                    float n = (lum[i] - lo) / range;
                    if (n < 0f) n = 0f; else if (n > 1f) n = 1f;
                    float shade = 0.40f + 0.85f * n;
                    Color32 c = t[trow + (x % tile)];
                    int p = i * 4;
                    a[p] = Clamp(c.r * shade);
                    a[p + 1] = Clamp(c.g * shade);
                    a[p + 2] = Clamp(c.b * shade);
                    // a[p + 3] (alpha) kept
                }
            }
        }

        private static byte Clamp(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)v; // truncate, like numpy astype(uint8) after clip
        }

        // value at fraction q of the sorted luminances (numpy-style linear rank), from a fine histogram
        private static float Percentile(int[] hist, int n, float q, int bins)
        {
            double rank = q * (n - 1);
            long cum = 0;
            for (int b = 0; b < bins; b++)
            {
                cum += hist[b];
                if (cum > rank) return (float)b / (bins - 1);
            }
            return 1f;
        }

        // The pattern resampled to tile x tile with wrap-around (box filter when shrinking,
        // bilinear when growing), so tile edges stay sharp and seamless.
        private static Color32[] Tile(string skinId, int size)
        {
            string key = skinId + "|" + size;
            Pattern p;
            lock (gate)
            {
                Color32[] cached;
                if (tiles.TryGetValue(key, out cached)) return cached;
                p = patterns[skinId];
            }
            Color32[] o = new Color32[size * size];
            float sx = (float)p.W / size, sy = (float)p.H / size;
            for (int ty = 0; ty < size; ty++)
                for (int tx = 0; tx < size; tx++)
                    o[ty * size + tx] = (sx >= 1f && sy >= 1f) ? Box(p, tx, ty, sx, sy) : Bilinear(p, (tx + 0.5f) * sx - 0.5f, (ty + 0.5f) * sy - 0.5f);
            lock (gate) { tiles[key] = o; }
            return o;
        }

        private static Color32 Box(Pattern p, int tx, int ty, float sx, float sy)
        {
            int x0 = (int)(tx * sx), x1 = Math.Max(x0 + 1, (int)((tx + 1) * sx));
            int y0 = (int)(ty * sy), y1 = Math.Max(y0 + 1, (int)((ty + 1) * sy));
            int r = 0, g = 0, b = 0, n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = Wrap(y, p.H) * p.W;
                for (int x = x0; x < x1; x++)
                {
                    Color32 c = p.Px[row + Wrap(x, p.W)];
                    r += c.r; g += c.g; b += c.b; n++;
                }
            }
            return new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
        }

        private static Color32 Bilinear(Pattern p, float fx, float fy)
        {
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float ax = fx - x0, ay = fy - y0;
            Color32 c00 = p.Px[Wrap(y0, p.H) * p.W + Wrap(x0, p.W)];
            Color32 c10 = p.Px[Wrap(y0, p.H) * p.W + Wrap(x0 + 1, p.W)];
            Color32 c01 = p.Px[Wrap(y0 + 1, p.H) * p.W + Wrap(x0, p.W)];
            Color32 c11 = p.Px[Wrap(y0 + 1, p.H) * p.W + Wrap(x0 + 1, p.W)];
            return new Color32(
                Lerp2(c00.r, c10.r, c01.r, c11.r, ax, ay),
                Lerp2(c00.g, c10.g, c01.g, c11.g, ax, ay),
                Lerp2(c00.b, c10.b, c01.b, c11.b, ax, ay), 255);
        }

        private static byte Lerp2(byte a, byte b, byte c, byte d, float ax, float ay)
        {
            float top = a + (b - a) * ax, bot = c + (d - c) * ax;
            return Clamp(top + (bot - top) * ay + 0.5f);
        }

        private static int Wrap(int v, int n)
        {
            v %= n;
            return v < 0 ? v + n : v;
        }

        // ---------------------------------------------------------------- e / c'. upload (main thread, 1 per frame)
        private static void Finish(Job job)
        {
            if (job.Error != null)
            {
                Fail(job, "recolour failed: " + job.Error);
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            Texture2D t = null;
            bool fromCache = job.Cached != null;
            try
            {
                if (fromCache)
                {
                    SkinCache.Data d = job.Cached;
                    t = new Texture2D(d.W, d.H, (TextureFormat)d.Format, d.Mips > 1, d.Linear);
                    Setup(t, job);
                    t.LoadRawTextureData(d.Bytes);   // throws if the size doesn't fit the format
                    t.Apply(false, true);
                    job.Cached = null;
                }
                else
                {
                    t = new Texture2D(job.W, job.H, TextureFormat.RGBA32, true, !job.Srgb);
                    Setup(t, job);
                    t.SetPixelData<byte>(job.Pixels, 0, 0);
                    t.Apply(true, false);                // build mipmaps
                    t.Compress(HighQualityCompress);     // DXT5 (alpha kept), ~4x less VRAM than RGBA32
                    SkinCache.Data d = null;
                    if (job.CachePath != null)
                    {
                        d = new SkinCache.Data();
                        d.W = t.width;
                        d.H = t.height;
                        d.Format = (int)t.format;
                        d.Mips = t.mipmapCount;
                        d.Linear = !job.Srgb;
                        d.Bytes = t.GetRawTextureData();
                    }
                    t.Apply(false, true);                // drop the CPU copy
                    job.Pixels = null;
                    if (d != null)
                    {
                        string path = job.CachePath;
                        ThreadPool.QueueUserWorkItem(delegate (object o) { SkinCache.Write(path, (SkinCache.Data)o); }, d);
                    }
                }
            }
            catch (Exception e)
            {
                if (t != null) UnityEngine.Object.Destroy(t);
                if (fromCache)
                {
                    // the file looked fine but didn't fit the texture: delete it and rebuild from the GPU
                    SkinCache.Delete(job.CachePath);
                    job.Cached = null;
                    job.CacheWhy = e.Message;
                    lock (gate) { cacheMiss.Enqueue(job); }
                    return;
                }
                Fail(job, "texture upload failed: " + e.Message);
                return;
            }
            pending.Remove(job.Key);
            built[job.Key] = t;
            double ms = sw.Elapsed.TotalMilliseconds;
            StringBuilder sb = new StringBuilder("[ArmoryUnbound] skin ");
            sb.Append(fromCache ? "from cache: " : "built: ").Append(job.Skin.Id).Append(" on '").Append(job.SourceName).Append("' ")
              .Append(job.W).Append('x').Append(job.H).Append(job.Srgb ? " sRGB" : " linear").Append(" -> ").Append(t.format).Append(" (");
            if (fromCache)
            {
                BatchCached++;
                sb.Append("file read ").Append(job.MsLoad.ToString("0")).Append(" ms worker, upload ").Append(ms.ToString("0")).Append(" ms main");
            }
            else
            {
                BatchBuilt++;
                sb.Append("readback ").Append(job.MsReadback.ToString("0")).Append(" ms via ").Append(job.ReadbackPath)
                  .Append(", recolour ").Append(job.MsRecolour.ToString("0")).Append(" ms worker, mips+compress+upload ")
                  .Append(ms.ToString("0")).Append(" ms main").Append(job.CachePath == null ? ", not cached" : "");
            }
            sb.Append(", ").Append(job.Clock.Elapsed.TotalMilliseconds.ToString("0")).Append(" ms from request)");
            Log.Out(sb.ToString());
            foreach (Action<Texture2D> cb in job.Callbacks)
            {
                try { cb(t); }
                catch (Exception e) { Log.Warning("[ArmoryUnbound] skin ready callback failed: " + e.Message); }
            }
        }

        private static void Setup(Texture2D t, Job job)
        {
            t.name = job.SourceName + " (" + job.Skin.Id + ")";
            t.wrapMode = job.Wrap;
            t.filterMode = job.Filter;
            t.anisoLevel = job.Aniso;
        }
    }
}
