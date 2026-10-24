using System;
using UnityEngine;

// C# side of Assets/Plugins/Android/Idas3FilePicker.java: Android's document
// picker copies chosen files into an app-owned folder; callers poll State.
public static class Idas3AndroidFiles
{
    public enum PickState { Idle = 0, Picking = 1, Copying = 2, Done = 3, Cancelled = 4, Failed = 5, Unavailable = 6 }
    const string PickerClass = "com.idas3.android.Idas3FilePicker";

    public static bool Available => Idas3Platform.IsAndroid;

    // mimeTypes: e.g. "audio/mpeg;audio/ogg", or empty for any file type.
    public static bool Pick(string mimeTypes, string destination, bool multiple)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var picker = new AndroidJavaClass(PickerClass))
                return picker.CallStatic<bool>("pick", UnityEngine.Android.AndroidApplication.currentActivity, mimeTypes ?? "", destination, multiple);
        }
        catch (Exception error) { Debug.LogWarning("Android file picker unavailable: " + error.Message); return false; }
#else
        return false;
#endif
    }

    public static PickState State => (PickState)Call("getState", (int)PickState.Unavailable);
    public static long CopiedBytes => Call("getCopiedBytes", 0L);
    public static long TotalBytes => Call("getTotalBytes", 0L);
    public static string Error => Call("getError", "");
    public static string[] Results
    {
        get { string joined = Call("getResults", ""); return string.IsNullOrEmpty(joined) ? Array.Empty<string>() : joined.Split('\n'); }
    }
    public static void Reset()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { using (var picker = new AndroidJavaClass(PickerClass)) picker.CallStatic("reset"); } catch (Exception) { }
#endif
    }

    static T Call<T>(string method, T fallback)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { using (var picker = new AndroidJavaClass(PickerClass)) return picker.CallStatic<T>(method); }
        catch (Exception) { return fallback; }
#else
        return fallback;
#endif
    }
}
