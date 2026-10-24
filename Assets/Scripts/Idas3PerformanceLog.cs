using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// Per-second frame-time log for manual device testing. Each row records the
// frame rate, frame-time spread, Unity CPU/GPU timings, native simulation +
// scene submission time and Android thermal state, with the course and phase.
// Rows go to <persistentDataPath>/perf/perf-<time>.csv and logcat (tag Unity,
// prefix IDAS3PERF); every race also logs a summary when it ends.
public sealed class Idas3PerformanceLog : MonoBehaviour
{
    // Opt-in: -idas3-perf-log, or on Android a "perf-log" flag file in the app's files directory.
    public static bool Enabled => Array.IndexOf(Environment.GetCommandLineArgs(), "-idas3-perf-log") >= 0 ||
        Idas3Platform.IsAndroid && File.Exists(Path.Combine(Application.persistentDataPath, "perf-log"));
    const string Header = "time_s,scene,phase,fps,avg_ms,p99_ms,max_ms,frames_over_33ms,cpu_main_ms,cpu_render_ms,gpu_ms,sim_submit_ms,speed_kmh,meshes,uploaded_vertices,thermal_status,thermal_headroom,width,height,face_ranges";
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    Idas3SceneGame host;
    StreamWriter writer;
    readonly List<float> second = new List<float>(), race = new List<float>();
    readonly FrameTiming[] timing = new FrameTiming[1];
    double windowStart, cpuMain, cpuRender, gpu, submit;
    int timingSamples, over33, raceOver33;
    float raceStart;
    string raceScene;
    bool racing;
    int lastStage = int.MinValue;
    // Native FrontendStage order (frontend.h).
    static readonly string[] Stages = { "Title", "SaveSelect", "Make", "Car", "Transmission", "Mode", "Course", "Route", "Weather", "Time", "Rival", "Name", "TuningCourse" };
    static string StageName(int stage) => stage >= 0 && stage < Stages.Length ? Stages[stage] : "Stage" + stage;
#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject power;
#endif

    public string FilePath { get; private set; }

    public void Initialize(Idas3SceneGame owner)
    {
        host = owner;
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, "perf");
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "perf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Invariant) + ".csv");
            writer = new StreamWriter(FilePath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("# " + SystemInfo.deviceModel + " | " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType +
                " | " + SystemInfo.systemMemorySize + " MB | " + Application.version + " | scene shaders " + (Idas3Platform.SceneVertexFaces ? "vertex-faces" : "geometry-stage"));
            writer.WriteLine(Header);
            Log("IDAS3PERF log: " + FilePath + " | " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType);
        }
        catch (Exception error) { Debug.LogWarning("IDAS3PERF log unavailable: " + error.Message); writer = null; }
#if UNITY_ANDROID && !UNITY_EDITOR
        try { power = UnityEngine.Android.AndroidApplication.currentActivity.Call<AndroidJavaObject>("getSystemService", "power"); }
        catch (Exception) { power = null; }
#endif
        windowStart = Time.realtimeSinceStartupAsDouble;
    }

    void LateUpdate()
    {
        if (host == null || !host.Ready || !Application.isFocused) { windowStart = Time.realtimeSinceStartupAsDouble; second.Clear(); return; }
        float ms = Time.unscaledDeltaTime * 1000f;
        second.Add(ms); if (ms > 33.4f) ++over33;
        submit += host.SubmissionMilliseconds;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, timing) == 1)
        {
            cpuMain += timing[0].cpuMainThreadFrameTime; cpuRender += timing[0].cpuRenderThreadFrameTime;
            gpu += timing[0].gpuFrameTime; ++timingSamples;
        }
        var status = host.Status;
        bool driving = (status.flags & (1u | 32u | 512u | 1024u | 4096u)) == 0 && status.racePhase < 3;
        // Immediate state markers let scripted runs wait for a screen instead of sleeping.
        int stage = (status.flags & 1u) != 0 ? status.frontendStage : -1;
        if (stage != lastStage) { lastStage = stage; Log("IDAS3PERF stage=" + (stage >= 0 ? StageName(stage) : "none")); }
        string scene = (status.flags & 1u) != 0 ? "menu" : Idas3CourseCatalog.SceneName(status);
        if (driving && !racing) { racing = true; race.Clear(); raceOver33 = 0; raceStart = Time.realtimeSinceStartup; raceScene = scene; Log("IDAS3PERF race-start scene=" + scene); }
        if (driving) { race.Add(ms); if (ms > 33.4f) ++raceOver33; }
        if (!driving && racing) { racing = false; LogRaceSummary(); }
        double now = Time.realtimeSinceStartupAsDouble;
        if (now - windowStart < 1.0) return;
        WriteRow(now, scene, driving ? "race" : (status.flags & 1u) != 0 ? "menu" : "race-other", status);
        windowStart = now; second.Clear(); over33 = 0; cpuMain = cpuRender = gpu = submit = 0; timingSamples = 0;
    }

    void WriteRow(double now, string scene, string phase, Idas3Native.Status status)
    {
        if (second.Count == 0) return;
        float total = 0, max = 0; foreach (float f in second) { total += f; if (f > max) max = f; }
        var sorted = new List<float>(second); sorted.Sort();
        float p99 = sorted[Mathf.Clamp(Mathf.CeilToInt(sorted.Count * .99f) - 1, 0, sorted.Count - 1)];
        float avg = total / second.Count, frames = second.Count;
        double t = timingSamples > 0 ? 1.0 / timingSamples : 0;
        ReadThermal(out int thermalStatus, out float headroom);
        var renderer = host.SceneRenderer;
        string row = string.Join(",",
            now.ToString("F1", Invariant), scene.Replace(',', ' '), phase,
            (1000f / avg).ToString("F1", Invariant), avg.ToString("F2", Invariant), p99.ToString("F2", Invariant), max.ToString("F2", Invariant),
            over33.ToString(Invariant), (cpuMain * t).ToString("F2", Invariant), (cpuRender * t).ToString("F2", Invariant), (gpu * t).ToString("F2", Invariant),
            (submit / frames).ToString("F2", Invariant), (status.speedMetresPerSecond * 3.6f).ToString("F0", Invariant),
            (renderer != null ? renderer.ActiveMeshCount : 0).ToString(Invariant), (renderer != null ? renderer.UploadedVertexCount : 0).ToString(Invariant),
            thermalStatus.ToString(Invariant), float.IsNaN(headroom) ? "" : headroom.ToString("F2", Invariant),
            Screen.width.ToString(Invariant), Screen.height.ToString(Invariant),
            (renderer != null ? renderer.WideRangeCount : 0).ToString(Invariant));
        try { writer?.WriteLine(row); } catch (Exception) { writer = null; }
        Log("IDAS3PERF " + row);
    }

    void LogRaceSummary()
    {
        if (race.Count < 30) return;
        var sorted = new List<float>(race); sorted.Sort();
        float total = 0; foreach (float f in race) total += f;
        int worst = Mathf.Max(1, sorted.Count / 100); float worstTotal = 0;
        for (int i = sorted.Count - worst; i < sorted.Count; ++i) worstTotal += sorted[i];
        string summary = string.Format(Invariant, "IDAS3PERF race-summary scene={0} seconds={1:F0} frames={2} avg_fps={3:F1} one_percent_low_fps={4:F1} max_ms={5:F1} frames_over_33ms={6}",
            raceScene, Time.realtimeSinceStartup - raceStart, race.Count, 1000f * race.Count / total, 1000f * worst / worstTotal, sorted[sorted.Count - 1], raceOver33);
        try { writer?.WriteLine("# " + summary); } catch (Exception) { writer = null; }
        Log(summary);
    }

    // One logcat line per message, without the managed stack trace.
    static void Log(string message) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message);

    void ReadThermal(out int status, out float headroom)
    {
        status = -1; headroom = float.NaN;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (power == null) return;
        try { status = power.Call<int>("getCurrentThermalStatus"); } catch (Exception) { }
        try { headroom = power.Call<float>("getThermalHeadroom", 10); } catch (Exception) { }
#endif
    }

    void OnDestroy()
    {
        if (racing) LogRaceSummary();
        try { writer?.Dispose(); } catch (Exception) { }
        writer = null;
#if UNITY_ANDROID && !UNITY_EDITOR
        power?.Dispose(); power = null;
#endif
    }
}
