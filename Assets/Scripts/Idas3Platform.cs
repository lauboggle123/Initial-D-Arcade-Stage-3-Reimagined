using System.IO;
using UnityEngine;

// One place for platform-dependent roots and features. Windows keeps its
// install-folder layout. Android cannot read files packed inside the APK
// through System.IO, so game data, course packs and the ROM live in the app's
// external files directory, where the data installer (or adb push) puts them.
public static class Idas3Platform
{
#if UNITY_ANDROID && !UNITY_EDITOR
    public static bool IsAndroid => true;
#else
    public static bool IsAndroid => false;
#endif
    // Online play, leaderboard submission, Discord, wheel force feedback and
    // the self-installing updater are Windows-only for the first Android release.
    public static bool OnlineEnabled => !IsAndroid;
    public static bool LeaderboardSubmitEnabled => !IsAndroid;
    public static bool DiscordEnabled => !IsAndroid;
    public static bool WheelFeedbackEnabled => !IsAndroid;
    public static bool UpdaterEnabled => !IsAndroid;

    // Directory holding rom/, Custom Music/ and (on Android) every data pack.
    public static string GameRoot => IsAndroid
        ? Application.persistentDataPath
        : Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    // Original runtime data (data/original_physics, ...).
    public static string NativeAssets => Application.isEditor
        ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Native"))
        : Path.Combine(IsAndroid ? Application.persistentDataPath : Application.streamingAssetsPath, "IDAS3");

    // Parent of HAKONE/, SADAMINE/, ... and SoundRoom/.
    public static string ContentRoot => IsAndroid ? Application.persistentDataPath : Application.streamingAssetsPath;
    public static string CoursePack(string name) => Path.Combine(ContentRoot, name);
}
