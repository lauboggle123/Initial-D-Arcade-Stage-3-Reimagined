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

    // Scene shaders keep the geometry-stage SubShader (LOD 200) wherever the GPU
    // has one. Without it (many Mali GPUs) Unity selects the vertex-faces
    // SubShader (LOD 100), whose meshes carry per-triangle planes and final-vertex
    // lighting inputs. Force it for A/B checks with -idas3-vertex-faces or, on
    // Android, a "vertex-faces" flag file in the app's files directory.
    static bool? vertexFaces;
    public static bool SceneVertexFaces => vertexFaces ??= !SystemInfo.supportsGeometryShaders ||
        System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-idas3-vertex-faces") >= 0 ||
        IsAndroid && File.Exists(Path.Combine(Application.persistentDataPath, "vertex-faces"));
    public const int GeometryStageLod = 200, VertexFacesLod = 100;
    // Course packs ship DXT1/DXT5. GPUs without BC support get each stored mip
    // decoded to RGBA32 at load (Idas3DxtDecoder); the files are never transcoded.
    // Force with -idas3-decode-dxt or an Android "decode-dxt" flag file.
    static bool? decodeDxt;
    public static bool DecodeCompressedTextures => decodeDxt ??=
        !SystemInfo.SupportsTextureFormat(TextureFormat.DXT1) || !SystemInfo.SupportsTextureFormat(TextureFormat.DXT5) ||
        System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-idas3-decode-dxt") >= 0 ||
        IsAndroid && File.Exists(Path.Combine(Application.persistentDataPath, "decode-dxt"));
    // Call before drawing with a scene shader; also used by imported courses.
    public static void SelectSceneShader(Shader shader)
    {
        if (shader != null) shader.maximumLOD = SceneVertexFaces ? VertexFacesLod : -1;
    }
}
