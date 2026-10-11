using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>One captured frame, already in manifest conventions (OpenGL c2w, pixel intrinsics).</summary>
    public struct CapturedFrame
    {
        public string FilePath;          // relative to the bundle folder, e.g. images/000012.jpg
        public Matrix4x4 CameraToWorldGL;
        public float Fx, Fy, Cx, Cy;
        public int Width, Height;
        public double Timestamp;
        public float BlurScore;
        public float AngularVelocity;
        public int TargetId;
        public int TrackingEpoch;        // PoseGuard epoch: AR poses of different epochs may not agree
        public int AnchorId;             // spot whose anchor the photo belongs to (-1 = none)
        public bool Manual;              // taken with the shutter button (no automatic quality gate)
    }

    /// <summary>
    /// Writes manifest.json (schema v1.1, see worker/gs_pipeline/capture_manifest.schema.json)
    /// and points3d.ply for a capture bundle folder.
    /// </summary>
    public static class CaptureBundleWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ManifestJson(string captureId, string source, IDictionary<string, string> device,
                                          IReadOnlyList<CapturedFrame> frames, Vector3 boundsCenterGL,
                                          float boundsRadius, float boundsHeight, string pointsPath,
                                          IDictionary<string, int> stats, string captureMode = "object",
                                          string sceneExtensionJson = null)
        {
            bool scene = captureMode == "scene";
            var sb = new StringBuilder(4096 + frames.Count * 600);
            sb.Append("{\n");
            sb.Append("  \"schema_version\": \"1.1\",\n");
            sb.Append("  \"capture_mode\": ").Append(Str(scene ? "scene" : "object")).Append(",\n");
            sb.Append("  \"capture_id\": ").Append(Str(captureId)).Append(",\n");
            sb.Append("  \"source\": ").Append(Str(source)).Append(",\n");
            sb.Append("  \"device\": ").Append(Dict(device)).Append(",\n");
            sb.Append("  \"camera_model\": \"PINHOLE\",\n");
            if (!string.IsNullOrEmpty(pointsPath)) sb.Append("  \"points3d_path\": ").Append(Str(pointsPath)).Append(",\n");
            if (!scene) // a room has no single object; the pipeline uses extensions.scene instead
                sb.Append("  \"object_bounds\": {\"center\": ").Append(Vec(boundsCenterGL))
                  .Append(", \"radius\": ").Append(F(boundsRadius))
                  .Append(", \"height\": ").Append(F(boundsHeight)).Append("},\n");
            sb.Append("  \"extensions\": {\"gscapture_stats\": ").Append(IntDict(stats));
            if (scene && !string.IsNullOrEmpty(sceneExtensionJson)) sb.Append(", \"scene\": ").Append(sceneExtensionJson);
            sb.Append("},\n");
            sb.Append("  \"frames\": [\n");
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                sb.Append("    {\"file_path\": ").Append(Str(f.FilePath));
                sb.Append(", \"transform_matrix\": ").Append(Mat(f.CameraToWorldGL));
                sb.Append(", \"fl_x\": ").Append(F(f.Fx)).Append(", \"fl_y\": ").Append(F(f.Fy));
                sb.Append(", \"cx\": ").Append(F(f.Cx)).Append(", \"cy\": ").Append(F(f.Cy));
                sb.Append(", \"w\": ").Append(f.Width.ToString(Inv)).Append(", \"h\": ").Append(f.Height.ToString(Inv));
                sb.Append(", \"timestamp\": ").Append(f.Timestamp.ToString("R", Inv));
                sb.Append(", \"tracking_state\": \"tracking\"");
                sb.Append(", \"blur_score\": ").Append(F(f.BlurScore));
                sb.Append(", \"angular_velocity\": ").Append(F(f.AngularVelocity));
                sb.Append(", \"target_id\": ").Append(f.TargetId.ToString(Inv));
                sb.Append(", \"tracking_epoch\": ").Append(f.TrackingEpoch.ToString(Inv));
                sb.Append(", \"anchor_id\": ").Append(f.AnchorId.ToString(Inv));
                if (f.Manual) sb.Append(", \"manual\": true");
                sb.Append("}");
                sb.Append(i + 1 < frames.Count ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>Binary little-endian PLY with float x, y, z (what gs_pipeline.ply_io reads).</summary>
        public static void WritePointsPly(string path, IReadOnlyList<Vector3> pointsGL)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                string header = "ply\nformat binary_little_endian 1.0\nelement vertex " + pointsGL.Count.ToString(Inv) +
                                "\nproperty float x\nproperty float y\nproperty float z\nend_header\n";
                w.Write(Encoding.ASCII.GetBytes(header));
                foreach (var p in pointsGL)
                {
                    w.Write(p.x); // BinaryWriter is little-endian on all platforms
                    w.Write(p.y);
                    w.Write(p.z);
                }
            }
        }

        static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("R", Inv);
        }

        static string Vec(Vector3 v) => "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]";

        static string Mat(Matrix4x4 m)
        {
            var sb = new StringBuilder("[");
            for (int r = 0; r < 4; r++)
            {
                sb.Append(r == 0 ? "[" : ", [");
                for (int c = 0; c < 4; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(F(m[r, c]));
                }
                sb.Append("]");
            }
            return sb.Append("]").ToString();
        }

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string Dict(IDictionary<string, string> d)
        {
            if (d == null || d.Count == 0) return "{}";
            var parts = new List<string>();
            foreach (var kv in d) parts.Add(Str(kv.Key) + ": " + Str(kv.Value));
            return "{" + string.Join(", ", parts) + "}";
        }

        static string IntDict(IDictionary<string, int> d)
        {
            if (d == null || d.Count == 0) return "{}";
            var parts = new List<string>();
            foreach (var kv in d) parts.Add(Str(kv.Key) + ": " + kv.Value.ToString(Inv));
            return "{" + string.Join(", ", parts) + "}";
        }

        /// <summary>extensions.scene: floor height + stations (OpenGL world, metres) for the pipeline/viewer.</summary>
        public static string SceneExtensionJson(float floorY, IReadOnlyList<(Vector3 centerGL, int frames)> stations, int closeups)
        {
            var sb = new StringBuilder("{\"floor_y\": ").Append(F(floorY)).Append(", \"closeups\": ")
                .Append(closeups.ToString(Inv)).Append(", \"stations\": [");
            for (int i = 0; i < stations.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("{\"center\": ").Append(Vec(stations[i].centerGL))
                  .Append(", \"frames\": ").Append(stations[i].frames.ToString(Inv)).Append("}");
            }
            return sb.Append("]}").ToString();
        }

        public static string NewCaptureId()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", Inv) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
