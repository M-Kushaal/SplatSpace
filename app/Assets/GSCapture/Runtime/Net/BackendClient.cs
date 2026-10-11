using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GSCapture.Net
{
    /// <summary>
    /// Talks to the FastAPI backend (backend/app/routers.py). Coroutine based so it runs on the main
    /// thread with UnityWebRequest. Uses an automatic per-device account (no login screen).
    /// </summary>
    public sealed class BackendClient
    {
        const string PrefsEmail = "gs.device.email", PrefsPassword = "gs.device.password", PrefsServer = "gs.server";
        public const string DefaultServer = "http://192.168.1.10:8000";

        public string BaseUrl { get; private set; }
        string token;

        public BackendClient(string baseUrl)
        {
            BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        }

        public static string SavedServer
        {
            get => PlayerPrefs.GetString(PrefsServer, DefaultServer);
            set
            {
                PlayerPrefs.SetString(PrefsServer, value.Trim().TrimEnd('/'));
                PlayerPrefs.Save();
            }
        }

        public static string DeviceEmail
        {
            get
            {
                // Accounts made by early builds used a reserved ".local" domain that the backend rejects.
                if (!PlayerPrefs.HasKey(PrefsEmail) || PlayerPrefs.GetString(PrefsEmail).EndsWith(".local"))
                {
                    PlayerPrefs.SetString(PrefsEmail, $"device-{Guid.NewGuid():N}@devices.gscapture.app");
                    PlayerPrefs.SetString(PrefsPassword, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
                    PlayerPrefs.Save();
                }
                return PlayerPrefs.GetString(PrefsEmail);
            }
        }

        static string DevicePassword
        {
            get
            {
                _ = DeviceEmail; // creates the pair on first use
                return PlayerPrefs.GetString(PrefsPassword);
            }
        }

        // ------------------------------------------------------------------ public API

        public IEnumerator Health(Action<bool, string> done)
        {
            using (var req = UnityWebRequest.Get(BaseUrl + "/health"))
            {
                req.timeout = 6;
                yield return Await(req);
                bool ok = req.result == UnityWebRequest.Result.Success;
                done(ok, ok ? "Connected" : Describe(req));
            }
        }

        public IEnumerator CreateCapture(CaptureCreate body, Action<CaptureCreated, string> done)
        {
            yield return Send("POST", "/captures", JsonUtility.ToJson(body), (code, text, err) =>
                done(err == null ? JsonUtility.FromJson<CaptureCreated>(text) : null, err));
        }

        public IEnumerator Complete(string captureId, Action<CaptureOut, long, string> done)
        {
            yield return Send("POST", $"/captures/{captureId}/complete", "{}", (code, text, err) =>
                done(err == null ? JsonUtility.FromJson<CaptureOut>(text) : null, code, err ?? null));
        }

        /// <summary>Ask the server to process an already-uploaded capture again (after a server-side failure).</summary>
        public IEnumerator Retrain(string captureId, Action<CaptureOut, string> done)
        {
            yield return Send("POST", $"/captures/{captureId}/retrain", "{\"params\": {}}", (code, text, err) =>
                done(err == null ? JsonUtility.FromJson<CaptureOut>(text) : null, err));
        }

        public IEnumerator GetCapture(string captureId, Action<CaptureOut, string> done)
        {
            yield return Send("GET", $"/captures/{captureId}", null, (code, text, err) =>
                done(err == null ? JsonUtility.FromJson<CaptureOut>(text) : null, err));
        }

        /// <summary>PUT a file to a signed URL, streaming from disk.</summary>
        public static IEnumerator PutFile(string url, string filePath, Action<float> progress, Action<string> done)
        {
            using (var req = new UnityWebRequest(url, "PUT"))
            {
                req.uploadHandler = new UploadHandlerFile(filePath);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 120;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    progress?.Invoke(req.uploadProgress);
                    yield return null;
                }
                done(req.result == UnityWebRequest.Result.Success ? null : Describe(req));
            }
        }

        public static IEnumerator Download(string url, string destPath, Action<string> done)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.downloadHandler = new DownloadHandlerFile(destPath) { removeFileOnAbort = true };
                req.timeout = 300;
                yield return Await(req);
                done(req.result == UnityWebRequest.Result.Success ? null : Describe(req));
            }
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// Poll instead of yielding the AsyncOperation: works in the player and in edit-mode tests (where the
        /// test runner does not wait on AsyncOperations).
        /// </summary>
        static IEnumerator Await(UnityWebRequest req)
        {
            var op = req.SendWebRequest();
            while (!op.isDone) yield return null;
        }

        IEnumerator EnsureToken(Action<string> done)
        {
            if (!string.IsNullOrEmpty(token))
            {
                done(null);
                yield break;
            }
            string creds = JsonUtility.ToJson(new Credentials { email = DeviceEmail, password = DevicePassword });
            long code = 0;
            string text = null, err = null;
            yield return Raw("POST", "/auth/login", creds, null, (c, t, e) => { code = c; text = t; err = e; });
            if (code == 401) // first time on this server: create the device account
                yield return Raw("POST", "/auth/register", creds, null, (c, t, e) => { code = c; text = t; err = e; });
            if (err == null) token = JsonUtility.FromJson<TokenOut>(text).access_token;
            done(err);
        }

        IEnumerator Send(string method, string path, string json, Action<long, string, string> done)
        {
            string authErr = null;
            yield return EnsureToken(e => authErr = e);
            if (authErr != null)
            {
                done(0, null, authErr);
                yield break;
            }
            long code = 0;
            string text = null, err = null;
            yield return Raw(method, path, json, token, (c, t, e) => { code = c; text = t; err = e; });
            if (code == 401) // expired token: log in again once
            {
                token = null;
                yield return EnsureToken(e => authErr = e);
                if (authErr == null)
                    yield return Raw(method, path, json, token, (c, t, e) => { code = c; text = t; err = e; });
            }
            done(code, text, err);
        }

        IEnumerator Raw(string method, string path, string json, string bearer, Action<long, string, string> done)
        {
            using (var req = new UnityWebRequest(BaseUrl + path, method))
            {
                if (json != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                if (bearer != null) req.SetRequestHeader("Authorization", "Bearer " + bearer);
                req.timeout = 30;
                yield return Await(req);
                string body = req.downloadHandler?.text;
                done(req.responseCode, body, req.result == UnityWebRequest.Result.Success ? null : Describe(req, body));
            }
        }

        /// <summary>Human-readable error: backend "detail" if present, else the transport error.</summary>
        public static string Describe(UnityWebRequest req, string body = null)
        {
            body = body ?? req.downloadHandler?.text;
            if (!string.IsNullOrEmpty(body))
            {
                string msg = ErrorMessage(body);
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            if (req.result == UnityWebRequest.Result.ConnectionError)
                return "Can't reach the server - check the address and that the PC is on the same Wi-Fi";
            return $"{req.responseCode} {req.error}";
        }

        public static string ErrorMessage(string body)
        {
            try
            {
                var obj = JsonUtility.FromJson<ErrorObjectEnvelope>(body);
                if (obj?.detail != null)
                {
                    if (obj.detail.validation != null && obj.detail.validation.errors.Count > 0)
                        return obj.detail.message + ": " + string.Join("; ", obj.detail.validation.errors);
                    if (!string.IsNullOrEmpty(obj.detail.message)) return obj.detail.message;
                }
            }
            catch (ArgumentException) { }
            try
            {
                var list = JsonUtility.FromJson<ErrorListEnvelope>(body); // FastAPI request validation (422)
                if (list?.detail != null && list.detail.Count > 0 && !string.IsNullOrEmpty(list.detail[0].msg))
                    return list.detail[0].msg;
            }
            catch (ArgumentException) { }
            try
            {
                var str = JsonUtility.FromJson<ErrorStringEnvelope>(body);
                if (!string.IsNullOrEmpty(str?.detail)) return str.detail;
            }
            catch (ArgumentException) { }
            return null;
        }
    }
}
