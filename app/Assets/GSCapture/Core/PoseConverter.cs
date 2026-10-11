using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>
    /// Converts Unity/AR Foundation poses into the capture-manifest convention:
    /// camera-to-world, OpenGL / nerfstudio (right-handed, +y up, camera looks down -z).
    ///
    /// Unity is left-handed (+y up, camera looks down +z). Flipping the world z axis (left)
    /// and the camera's own z axis (right) gives the manifest pose: M_gl = S * M_unity * S,
    /// S = diag(1, 1, -1, 1). Mirrored in Python by gs_pipeline/cameras.py; the EditMode
    /// tests use golden values generated there.
    /// </summary>
    public static class PoseConverter
    {
        static readonly Matrix4x4 FlipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

        public static Matrix4x4 ToOpenGLCameraToWorld(Matrix4x4 unityCameraToWorld)
        {
            return FlipZ * unityCameraToWorld * FlipZ;
        }

        public static Matrix4x4 ToOpenGLCameraToWorld(Vector3 position, Quaternion rotation)
        {
            return ToOpenGLCameraToWorld(Matrix4x4.TRS(position, rotation, Vector3.one));
        }

        /// <summary>
        /// Optional correction when the saved image is rotated relative to the AR camera frame
        /// (e.g. a device whose sensor is mounted at 270 degrees). Rotates about the viewing axis.
        /// </summary>
        public static Matrix4x4 ApplyImageRoll(Matrix4x4 cameraToWorldGL, int rollDegrees)
        {
            if (rollDegrees % 360 == 0) return cameraToWorldGL;
            return cameraToWorldGL * Matrix4x4.Rotate(Quaternion.AngleAxis(rollDegrees, Vector3.forward));
        }

        public static Vector3 ToOpenGLPoint(Vector3 unityWorldPoint)
        {
            return new Vector3(unityWorldPoint.x, unityWorldPoint.y, -unityWorldPoint.z);
        }
    }
}
