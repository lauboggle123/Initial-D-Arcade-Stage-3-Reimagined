using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-10000)]
public sealed class Idas3RomGate : MonoBehaviour
{
    public static bool Verified { get; private set; }
    internal static Idas3RomGate Instance { get; private set; }
    internal bool Checking => validation != null;
    internal string Message => message;
    internal string RomFolder => Path.Combine(gameRoot, "rom");
    private string gameRoot, message = "Checking GDS-0033…";
    private Task<Idas3RomValidation.Result> validation;
    private CancellationTokenSource cancellation;
    private volatile float progress;
    private Camera background;
    private int selected;
    private GUIStyle titleStyle, textStyle, pathStyle, buttonStyle;
    private RenderTexture diagnosticTarget;
    internal bool DiagnosticCaptureReady { get; private set; }

    internal void RequestDiagnosticCapture(RenderTexture target)
    {
        if (Verified || target == null || Array.IndexOf(Environment.GetCommandLineArgs(), "-idas3-rom-smoke") < 0)
            throw new InvalidOperationException("ROM capture requires the blocked startup diagnostic.");
        diagnosticTarget = target; DiagnosticCaptureReady = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState() { Verified = false; Instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // Deliberately applies to editor play, diagnostic flags, replay viewing,
        // legacy host and -idas3-skip-update-once as well as ordinary startup.
        Verified = false;
        Idas3MenuLocalization.SetLanguage(Idas3MenuLocalization.LoadLanguage(Path.Combine(Application.persistentDataPath,"userdata-unity-scene")));
        var go = new GameObject("GDS-0033 startup check");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<Idas3RomGate>();
        Instance.gameRoot = Idas3Platform.GameRoot;
        Instance.background = go.AddComponent<Camera>();
        Instance.background.clearFlags = CameraClearFlags.SolidColor;
        Instance.background.backgroundColor = Color.black;
        Instance.background.cullingMask = 0;
        Instance.background.depth = 10000;
        Instance.background.allowHDR = false;
        Instance.background.allowMSAA = false;
        Application.runInBackground = true;
        Instance.CheckAgain();
    }

    internal void CheckAgain()
    {
        if (Verified || Checking) return;
        try
        {
            Directory.CreateDirectory(RomFolder);
            string readme = Path.Combine(RomFolder, "README.txt");
            if (!File.Exists(readme))
            {
                try { File.WriteAllText(readme, Idas3RomValidation.ReadmeText); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Debug.LogWarning("Could not write ROM instructions: " + error.Message); }
            }
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            progress = 0;
            message = "Checking GDS-0033…";
            validation = Task.Run(() => Idas3RomValidation.Validate(gameRoot, value => progress = value, token), token);
        }
        catch (Exception error)
        {
            message = "Could not access the rom folder. Check the game folder permissions.";
            Debug.LogWarning("GDS-0033 startup check: " + error.Message);
        }
    }

    private void Update()
    {
        if (Verified) return;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (validation != null && validation.IsCompleted)
        {
            try
            {
                var result = validation.GetAwaiter().GetResult();
                Verified = result.Verified;
                message = result.Message;
                // The shared validator speaks of the desktop rom folder; Android players import through the picker.
                if (Idas3Platform.IsAndroid && !Verified && message == "Place gds-0033.chd in the rom folder.") message = "No GDS-0033 dump found yet. Tap SELECT ROM FILE to import it.";
                Debug.Log("GDS-0033 startup check: " + (Verified ? "verified " + result.Format : "blocked: " + message));
            }
            catch (Exception error) { message = "Could not verify GDS-0033. Try again."; Debug.LogWarning(error.Message); }
            validation = null;
            if (Verified) { background.enabled = false; return; }
        }
        if (importing) { PollImport(); return; }
        var key = Keyboard.current; var pad = Gamepad.current;
        if (key?.escapeKey.wasPressedThisFrame == true || pad?.buttonEast.wasPressedThisFrame == true) { Quit(); return; }
        if (Checking) return;
        int count = Actions.Length;
        if (key?.leftArrowKey.wasPressedThisFrame == true || pad?.dpad.left.wasPressedThisFrame == true) selected = (selected + count - 1) % count;
        if (key?.rightArrowKey.wasPressedThisFrame == true || pad?.dpad.right.wasPressedThisFrame == true) selected = (selected + 1) % count;
        if (key?.enterKey.wasPressedThisFrame == true || key?.numpadEnterKey.wasPressedThisFrame == true || pad?.buttonSouth.wasPressedThisFrame == true) Activate(Actions[selected]);
    }

    // Android cannot open an app-private folder in another app; the document
    // picker copies the dump into it instead.
    private static readonly int[] DesktopActions = { 0, 1, 2 }, AndroidActions = { 3, 0, 2 };
    private static readonly string[] ActionLabels = { "CHECK AGAIN", "OPEN ROM FOLDER", "QUIT", "SELECT ROM FILE" };
    private static int[] Actions => Idas3Platform.IsAndroid ? AndroidActions : DesktopActions;
    private bool importing;

    private void Activate(int action)
    {
        if (action == 0) CheckAgain();
        else if (action == 1) Application.OpenURL(new Uri(RomFolder + Path.DirectorySeparatorChar).AbsoluteUri);
        else if (action == 3) StartImport();
        else Quit();
    }

    private void StartImport()
    {
        if (Checking || importing) return;
        Directory.CreateDirectory(RomFolder);
        if (!Idas3AndroidFiles.Pick("", RomFolder, true)) { message = "The file picker could not open. Try again."; return; }
        importing = true; progress = 0; message = "Choose gds-0033.chd, or the .cue file and its three .bin tracks.";
    }

    private void PollImport()
    {
        var state = Idas3AndroidFiles.State;
        if (state == Idas3AndroidFiles.PickState.Picking) return;
        if (state == Idas3AndroidFiles.PickState.Copying)
        {
            long copied = Idas3AndroidFiles.CopiedBytes, total = Idas3AndroidFiles.TotalBytes;
            progress = total > 0 ? Mathf.Clamp01((float)copied / total) : 0;
            message = total > 0 ? string.Format("Copying ROM… {0:N0} / {1:N0} MB", copied >> 20, total >> 20) : "Copying ROM…";
            return;
        }
        importing = false;
        if (state == Idas3AndroidFiles.PickState.Done)
        {
            try { NormalizeImportedNames(Idas3AndroidFiles.Results); }
            catch (Exception error) { Debug.LogWarning("ROM import naming: " + error.Message); }
            Idas3AndroidFiles.Reset();
            CheckAgain();
            return;
        }
        message = state == Idas3AndroidFiles.PickState.Cancelled ? "No file was selected." : "The ROM could not be copied: " + Idas3AndroidFiles.Error;
        Idas3AndroidFiles.Reset();
    }

    // The validator checks fixed names; content is still verified by hash.
    private void NormalizeImportedNames(string[] paths)
    {
        var bins = new System.Collections.Generic.List<string>();
        foreach (string path in paths)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".chd") Rename(path, "gds-0033.chd");
            else if (extension == ".cue") Rename(path, "gds-0033.cue");
            else if (extension == ".bin") bins.Add(path);
        }
        if (bins.Count != 3) return;
        bins.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 3; ++i) Rename(bins[i], "gds-0033-track" + (i + 1) + ".bin");
    }

    private void Rename(string path, string name)
    {
        string target = Path.Combine(RomFolder, name);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.Ordinal)) return;
        if (File.Exists(target)) File.Delete(target);
        File.Move(path, target);
    }

    private static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private static void Fill(Rect rect, Color color)
    { GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = Color.white; }

    private void OnGUI()
    {
        if (Verified) return;
        if (Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp) Event.current.Use();
        var oldMatrix = GUI.matrix; var oldColor = GUI.color;
        GUI.depth = -10000;
        bool diagnostic = diagnosticTarget != null && Event.current.type == EventType.Repaint;
        var previousTarget = RenderTexture.active;
        if (diagnostic) { RenderTexture.active = diagnosticTarget; GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); }
        try { DrawWindow(); }
        finally
        {
            GUI.matrix = oldMatrix; GUI.color = oldColor;
            if (diagnostic) { GL.PopMatrix(); RenderTexture.active = previousTarget; diagnosticTarget = null; DiagnosticCaptureReady = true; }
        }
    }

    private void DrawWindow()
    {
        float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
        GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1280 * scale) / 2, (Screen.height - 720 * scale) / 2, 0), Quaternion.identity, Vector3.one * scale);
        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 36, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true, normal = { textColor = Color.white } };
            pathStyle = new GUIStyle(textStyle) { fontSize = 17, normal = { textColor = new Color(.66f, .67f, .71f) } };
            buttonStyle = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        }
        Fill(new Rect(0, 0, 1280, 720), Color.black);
        Fill(new Rect(160, 170, 960, 380), new Color(.04f, .045f, .05f));
        Fill(new Rect(160, 170, 960, 5), new Color(.89f, .08f, .16f));
        Idas3MenuLocalization.Label(new Rect(195, 194, 880, 54), importing ? "IMPORTING ROM" : Checking ? "CHECKING GDS-0033" : "ROM REQUIRED", titleStyle);
        Idas3MenuLocalization.Label(new Rect(198, 273, 875, 72), message, textStyle);
        // A path inside app storage means nothing to an Android player.
        if (Idas3Platform.IsAndroid)
            Idas3MenuLocalization.Label(new Rect(198, 351, 875, 62), "Select your own GDS-0033 dump: gds-0033.chd, or the .cue file with its three .bin tracks.", pathStyle);
        else Idas3MenuLocalization.Label(new Rect(198, 351, 875, 62), RomFolder, pathStyle, false);
        if (Checking || importing)
        {
            Fill(new Rect(198, 439, 875, 8), new Color(.18f, .19f, .21f));
            Fill(new Rect(198, 439, 875 * Mathf.Clamp01(progress), 8), new Color(.89f, .08f, .16f));
            if (!importing) Idas3MenuLocalization.Label(new Rect(198, 470, 875, 30), "ESC / B  QUIT", pathStyle);
        }
        else
        {
            var actions = Actions;
            for (int i = 0; i < actions.Length; ++i)
            {
                var rect = new Rect(198 + i * 295, 445, 280, 56);
                if (Event.current.type == EventType.MouseMove && rect.Contains(Event.current.mousePosition)) selected = i;
                Fill(rect, selected == i ? new Color(.89f, .08f, .16f) : new Color(.11f, .12f, .14f));
                if (GUI.Button(rect,Idas3MenuLocalization.T(ActionLabels[actions[i]]),Idas3MenuLocalization.Style(buttonStyle))) { selected = i; Activate(actions[i]); }
            }
        }
    }

    private void OnDestroy()
    {
        cancellation?.Cancel(); cancellation?.Dispose();
        if (Instance == this) { Verified = false; Instance = null; }
    }
}
