using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GSCapture.Net
{
    /// <summary>
    /// Uploads captures to the backend (resumable: progress lives in upload_state.json) and polls the
    /// processing status. Lives on the app object so uploads continue while the user moves between screens.
    /// </summary>
    public sealed class UploadManager : MonoBehaviour
    {
        const int MaxRetries = 3;
        const long UrlMaxAgeSeconds = 5 * 3600; // backend signs URLs for 6 h

        public event Action Changed;
        readonly Dictionary<string, float> progress = new Dictionary<string, float>();

        public bool IsUploading(string id) => progress.ContainsKey(id);
        public float Progress(string id) => progress.TryGetValue(id, out var p) ? p : -1f;

        BackendClient client;
        BackendClient Client
        {
            get
            {
                string server = BackendClient.SavedServer;
                if (client == null || client.BaseUrl != server.TrimEnd('/')) client = new BackendClient(server);
                return client;
            }
        }

        public void Upload(CaptureEntry e)
        {
            if (!e.Complete || IsUploading(e.Id)) return;
            StartCoroutine(UploadRoutine(e));
        }

        IEnumerator UploadRoutine(CaptureEntry e)
        {
            progress[e.Id] = 0f;
            Changed?.Invoke();
            var s = CaptureLibrary.LoadState(e.Dir);
            if (s.phase == "uploaded" && s.status == "failed" && s.server == Client.BaseUrl && !string.IsNullOrEmpty(s.remote_id))
            {
                // Files are already on the server: just process again.
                CaptureOut retrained = null;
                string rerr = null;
                yield return Client.Retrain(s.remote_id, (c, er) => { retrained = c; rerr = er; });
                if (rerr != null) s.message = rerr;
                else
                {
                    s.message = null;
                    Apply(s, retrained);
                }
                Finish(e, s);
                yield break;
            }
            var files = CaptureLibrary.UploadFiles(e.Dir);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool fresh = string.IsNullOrEmpty(s.remote_id) || s.server != Client.BaseUrl ||
                         now - s.urls_issued_unix > UrlMaxAgeSeconds || s.phase == "failed";
            if (fresh)
            {
                CaptureCreated created = null;
                string err = null;
                yield return Client.CreateCapture(new CaptureCreate { title = e.Id, files = files }, (c, er) => { created = c; err = er; });
                if (err != null)
                {
                    Fail(e, s, err);
                    yield break;
                }
                s = new UploadState { server = Client.BaseUrl, remote_id = created.capture_id, urls_issued_unix = now,
                                      targets = created.uploads, phase = "uploading" };
                CaptureLibrary.SaveState(e.Dir, s);
            }

            s.phase = "uploading";
            s.message = null;
            var urls = s.targets.ToDictionary(t => t.path, t => t.url);
            long total = files.Sum(f => f.size), done = files.Where(f => s.uploaded.Contains(f.path)).Sum(f => f.size);
            foreach (var f in files)
            {
                if (s.uploaded.Contains(f.path)) continue;
                if (!urls.TryGetValue(f.path, out var url))
                {
                    Fail(e, s, $"server did not return an upload URL for {f.path}");
                    yield break;
                }
                string err = "not started";
                for (int attempt = 0; attempt < MaxRetries && err != null; attempt++)
                {
                    if (attempt > 0) yield return new WaitForSecondsRealtime(2 << attempt);
                    long before = done;
                    yield return BackendClient.PutFile(url, Path.Combine(e.Dir, f.path),
                        p => { progress[e.Id] = (before + p * f.size) / Mathf.Max(1, total); Changed?.Invoke(); },
                        r => err = r);
                }
                if (err != null)
                {
                    Fail(e, s, err);
                    yield break;
                }
                done += f.size;
                s.uploaded.Add(f.path);
                CaptureLibrary.SaveState(e.Dir, s);
            }

            CaptureOut cap = null;
            long code = 0;
            string cerr = null;
            yield return Client.Complete(s.remote_id, (c, k, er) => { cap = c; code = k; cerr = er; });
            if (cerr != null)
            {
                if (code == 422) // the server-side validator rejected the bundle; re-uploading won't help
                {
                    s.phase = "uploaded";
                    s.status = "invalid";
                    s.message = cerr;
                    Finish(e, s);
                }
                else Fail(e, s, cerr);
                yield break;
            }
            s.phase = "uploaded";
            Apply(s, cap);
            Finish(e, s);
        }

        void Fail(CaptureEntry e, UploadState s, string err)
        {
            s.phase = "failed";
            s.message = err;
            Finish(e, s);
        }

        void Finish(CaptureEntry e, UploadState s)
        {
            CaptureLibrary.SaveState(e.Dir, s);
            e.State = s;
            progress.Remove(e.Id);
            Changed?.Invoke();
        }

        /// <summary>Refresh statuses of captures being processed on the server.</summary>
        public IEnumerator Poll(List<CaptureEntry> entries)
        {
            foreach (var e in entries)
            {
                var s = e.State;
                if (s == null || !s.NeedsPolling || IsUploading(e.Id) || s.server != Client.BaseUrl) continue;
                CaptureOut cap = null;
                string err = null;
                yield return Client.GetCapture(s.remote_id, (c, er) => { cap = c; err = er; });
                if (err != null) continue; // offline etc.: keep the last known status
                Apply(s, cap);
                CaptureLibrary.SaveState(e.Dir, s);
                Changed?.Invoke();
            }
        }

        static void Apply(UploadState s, CaptureOut cap)
        {
            if (cap == null) return;
            s.status = cap.status;
            if (cap.HasJob)
            {
                s.stage = cap.job.stage;
                s.message = cap.status == "failed" ? cap.job.error : null;
                if (cap.job.metrics != null)
                {
                    s.psnr_object = cap.job.metrics.psnr_object_mean;
                    s.train_seconds = cap.job.metrics.train_seconds;
                }
                var splat = cap.job.artifacts.FirstOrDefault(a => a.kind == "splat_object_ply") ??
                            cap.job.artifacts.FirstOrDefault(a => a.kind == "splat_ply");
                if (splat != null) s.splat_url = splat.url;
                s.metrics_url = cap.job.artifacts.FirstOrDefault(a => a.kind == "metrics")?.url;
                s.refine_url = cap.job.artifacts.FirstOrDefault(a => a.kind == "refine_report")?.url;
            }
        }

        /// <summary>
        /// Link to the web viewer served by the backend (/viewer) for this capture's splat. Fetches the capture
        /// first because the signed artifact URLs expire (6 h).
        /// </summary>
        public IEnumerator ViewerLink(CaptureEntry e, Action<string, string> done)
        {
            var s = e.State;
            if (s == null || string.IsNullOrEmpty(s.remote_id))
            {
                done(null, "this capture hasn't been processed yet");
                yield break;
            }
            CaptureOut cap = null;
            string err = null;
            yield return Client.GetCapture(s.remote_id, (c, er) => { cap = c; err = er; });
            if (err != null)
            {
                done(null, err);
                yield break;
            }
            Apply(s, cap);
            CaptureLibrary.SaveState(e.Dir, s);
            if (string.IsNullOrEmpty(s.splat_url))
            {
                done(null, "no splat available yet");
                yield break;
            }
            done(ViewerUrl(Client.BaseUrl, s.splat_url, s.metrics_url, s.refine_url, e.Created.ToLocalTime().ToString("MMM d HH:mm")), null);
        }

        public static string ViewerUrl(string server, string splat, string metrics, string refine, string name)
        {
            var q = new System.Text.StringBuilder(server.TrimEnd('/') + "/viewer/?url=" + Uri.EscapeDataString(splat));
            if (!string.IsNullOrEmpty(metrics)) q.Append("&metrics=").Append(Uri.EscapeDataString(metrics));
            if (!string.IsNullOrEmpty(refine)) q.Append("&refine=").Append(Uri.EscapeDataString(refine));
            if (!string.IsNullOrEmpty(name)) q.Append("&name=").Append(Uri.EscapeDataString("Capture " + name));
            return q.ToString();
        }

        /// <summary>Download the finished object splat into the capture's local/ folder.</summary>
        public IEnumerator DownloadSplat(CaptureEntry e, Action<string> done)
        {
            var s = e.State;
            if (string.IsNullOrEmpty(s?.splat_url))
            {
                done("no splat available yet");
                yield break;
            }
            string dir = Path.Combine(e.Dir, "local");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "splat_object.ply");
            string err = null;
            yield return BackendClient.Download(s.splat_url, dest, r => err = r);
            if (err == null)
            {
                s.splat_local = dest;
                CaptureLibrary.SaveState(e.Dir, s);
                Changed?.Invoke();
            }
            done(err);
        }
    }
}
