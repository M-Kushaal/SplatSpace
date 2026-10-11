using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GSCapture.UI;
using UnityEngine;

namespace GSCapture.Net
{
    /// <summary>Per-capture sync state, saved as upload_state.json next to the capture (resumable).</summary>
    [Serializable]
    public class UploadState
    {
        public string server;
        public string remote_id;
        public long urls_issued_unix;       // signed upload URLs expire; re-create the capture if old
        public List<UploadTarget> targets = new List<UploadTarget>();
        public List<string> uploaded = new List<string>();
        public string phase = "local";       // local | uploading | uploaded | done | failed
        public string status;                // backend capture status: queued/training/ready/failed/invalid
        public string stage;                 // backend job stage, e.g. "refining poses", "training"
        public string message;               // last error / validator message for the user
        public float psnr_object;
        public float train_seconds;
        public string splat_url;
        public string metrics_url;
        public string refine_url;
        public string splat_local;

        public bool NeedsPolling => phase == "uploaded" && status != "ready" && status != "failed" && status != "invalid";
    }

    public sealed class CaptureEntry
    {
        public string Id;
        public string Dir;
        public bool Complete;  // manifest written (capture finished)
        public bool Scene;     // scene (room) capture rather than an object orbit
        public int Frames;
        public long Bytes;
        public DateTime Created;
        public UploadState State;
        public string ThumbPath;
    }

    public static class CaptureLibrary
    {
        public const string StateFile = "upload_state.json";

        public static string Root => Path.Combine(Application.persistentDataPath, "captures");

        public static List<CaptureEntry> Scan(string root)
        {
            var list = new List<CaptureEntry>();
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string manifest = Path.Combine(dir, "manifest.json");
                var e = new CaptureEntry
                {
                    Id = Path.GetFileName(dir),
                    Dir = dir,
                    Complete = File.Exists(manifest),
                    State = LoadState(dir),
                };
                e.Created = ParseId(e.Id) ?? Directory.GetCreationTimeUtc(dir);
                if (e.Complete)
                {
                    string text = File.ReadAllText(manifest);
                    e.Frames = CountFrames(text);
                    e.Scene = text.Contains("\"capture_mode\": \"scene\"");
                }
                e.Bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                string thumb = Path.Combine(dir, "images", "000000.jpg");
                e.ThumbPath = File.Exists(thumb) ? thumb : null;
                list.Add(e);
            }
            return list.OrderByDescending(e => e.Created).ToList();
        }

        public static int CountFrames(string manifestJson)
        {
            int n = 0, i = 0;
            while ((i = manifestJson.IndexOf("\"file_path\"", i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += 11;
            }
            return n;
        }

        static DateTime? ParseId(string id)
        {
            if (id.Length >= 15 && DateTime.TryParseExact(id.Substring(0, 15), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                return t;
            return null;
        }

        public static UploadState LoadState(string dir)
        {
            string p = Path.Combine(dir, StateFile);
            if (!File.Exists(p)) return new UploadState();
            try
            {
                return JsonUtility.FromJson<UploadState>(File.ReadAllText(p)) ?? new UploadState();
            }
            catch (ArgumentException)
            {
                return new UploadState();
            }
        }

        public static void SaveState(string dir, UploadState s)
        {
            string p = Path.Combine(dir, StateFile), tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(s, true));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }

        /// <summary>Files to upload (relative, forward slashes), manifest.json last; local-only files skipped.</summary>
        public static List<FileIn> UploadFiles(string dir)
        {
            var files = new List<FileIn>();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(dir.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (rel == StateFile || rel.EndsWith(".tmp") || rel.StartsWith("local/")) continue;
                files.Add(new FileIn { path = rel, size = new FileInfo(f).Length });
            }
            files.Sort((a, b) =>
            {
                bool am = a.path == "manifest.json", bm = b.path == "manifest.json";
                return am == bm ? string.CompareOrdinal(a.path, b.path) : (am ? 1 : -1);
            });
            return files;
        }

        public static void Delete(CaptureEntry e)
        {
            if (Directory.Exists(e.Dir)) Directory.Delete(e.Dir, true);
        }

        public static string FormatBytes(long b) =>
            b >= 1 << 30 ? $"{b / (double)(1 << 30):F1} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):F0} MB" : $"{b / 1024.0:F0} KB";
    }

    /// <summary>What a capture card shows: label, colour and optional progress (0..1).</summary>
    public static class StatusView
    {
        public static (string text, Color color) For(CaptureEntry e, float uploadProgress = -1f)
        {
            var s = e.State ?? new UploadState();
            if (!e.Complete) return ("Incomplete", Theme.TextFaint);
            if (s.phase == "uploading" || uploadProgress >= 0f)
                return (uploadProgress >= 0f ? $"Uploading {Mathf.RoundToInt(uploadProgress * 100)}%" : "Upload paused", Theme.Info);
            if (s.phase == "failed") return ("Upload failed", Theme.Danger);
            switch (s.status)
            {
                case "ready": return ("Ready", Theme.Success);
                case "invalid": return ("Rejected", Theme.Danger);
                case "failed": return ("Failed", Theme.Danger);
                case "training":
                    string st = (s.stage ?? "").ToLowerInvariant();
                    if (st.Contains("refin")) return ("Refining poses", Theme.Warning);
                    if (st.Contains("upload")) return ("Finishing", Theme.Warning);
                    if (st.Contains("download") || st.Contains("valid") || st.Contains("claim")) return ("Preparing", Theme.Warning);
                    return ("Training", Theme.Warning);
                case "queued": return ("Queued", Theme.Warning);
            }
            return s.phase == "uploaded" ? ("Sent", Theme.Info) : ("On device", Theme.TextDim);
        }
    }
}
