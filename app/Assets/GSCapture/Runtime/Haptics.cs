using UnityEngine;

namespace GSCapture
{
    /// <summary>
    /// A short vibration "tick" on every kept photo, so the user can keep turning without watching the screen.
    /// Handheld.Vibrate is ~0.5 s (too long for a tick), so Android's Vibrator is called directly for 20 ms;
    /// referencing Handheld.Vibrate also makes Unity add the (normal, no-prompt) VIBRATE permission.
    /// </summary>
    public static class Haptics
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject vibrator;
        static bool failed;
#endif

        public static void Tick(long milliseconds = 20)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (failed) return;
            try
            {
                if (vibrator == null)
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                vibrator?.Call("vibrate", milliseconds);
            }
            catch (System.Exception e)
            {
                failed = true;
                Debug.LogWarning($"[GSCapture] haptics unavailable: {e.Message}");
            }
#elif UNITY_IOS && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }

        // Never called; keeps Handheld.Vibrate referenced so the Android build declares VIBRATE.
        static void DeclarePermission() => Handheld.Vibrate();
    }
}
