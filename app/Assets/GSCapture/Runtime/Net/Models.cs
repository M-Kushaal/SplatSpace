using System;
using System.Collections.Generic;

namespace GSCapture.Net
{
    // JsonUtility models mirroring backend/app/schemas.py. Field names are the JSON keys.
    // JsonUtility turns JSON null objects into default instances, so check ids/strings for emptiness.

    [Serializable] public class Credentials { public string email; public string password; }
    [Serializable] public class TokenOut { public string access_token; }

    [Serializable] public class FileIn { public string path; public long size; }
    [Serializable] public class CaptureCreate { public string title; public List<FileIn> files = new List<FileIn>(); }
    [Serializable] public class UploadTarget { public string path; public string url; public string method; }
    [Serializable] public class CaptureCreated { public string capture_id; public List<UploadTarget> uploads = new List<UploadTarget>(); }

    [Serializable] public class ArtifactOut { public string kind; public string name; public long bytes; public string url; }

    [Serializable]
    public class JobMetrics
    {
        public float psnr_object_mean;
        public float psnr_test_mean;
        public float train_seconds;
        public int num_gaussians;
    }

    [Serializable]
    public class JobOut
    {
        public string id;
        public string pipeline;
        public string status;
        public string stage;
        public string message;
        public int attempts;
        public string error;
        public JobMetrics metrics;
        public List<ArtifactOut> artifacts = new List<ArtifactOut>();
    }

    [Serializable]
    public class ValidationOut
    {
        public bool ok;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
    }

    [Serializable]
    public class CaptureOut
    {
        public string id;
        public string title;
        public string status;
        public int frame_count;
        public string created_at;
        public ValidationOut validation;
        public JobOut job;

        public bool HasJob => job != null && !string.IsNullOrEmpty(job.id);
    }

    [Serializable] public class ErrorDetail { public string message; public ValidationOut validation; public List<string> paths = new List<string>(); }
    [Serializable] public class ErrorObjectEnvelope { public ErrorDetail detail; }
    [Serializable] public class ErrorStringEnvelope { public string detail; }
    [Serializable] public class ErrorItem { public string msg; }
    [Serializable] public class ErrorListEnvelope { public List<ErrorItem> detail = new List<ErrorItem>(); }
}
