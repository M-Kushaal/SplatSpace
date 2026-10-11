using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace GSCapture.EditorTools
{
    /// <summary>
    /// Phase-1 dev builds talk to the backend on the user's PC over plain http:// on the LAN. Android blocks
    /// cleartext HTTP by default (API 28+) and Unity's "Allow downloads over HTTP" setting does not add the
    /// manifest flag, so add android:usesCleartextTraffic="true" here. Android cannot scope this to private IP
    /// ranges; the app only contacts the server address the user types in Settings.
    /// Remove this (and PlayerSettings.insecureHttpOption) once the backend is served over HTTPS.
    /// </summary>
    public sealed class AndroidManifestCleartext : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var doc = new XmlDocument();
            doc.Load(manifest);
            var app = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (app == null) return;
            app.SetAttribute("usesCleartextTraffic", AndroidNs, "true");
            doc.Save(manifest);
        }
    }
}
