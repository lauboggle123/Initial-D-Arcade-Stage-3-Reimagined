using System.Collections.Generic;
using UnityEngine;

// On-screen controls for touch devices. They act as a virtual Xbox-layout pad
// merged into the physical pad sample, so controller bindings, menu navigation
// and the original steering conditioning apply unchanged. While visible the
// overlay owns every touch: touches must not also become the mouse "click"
// that the host translates to Enter or a save-menu pointer.
public sealed class Idas3TouchControls : MonoBehaviour
{
    const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    const ushort Start = 0x0010, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    enum Control { None, Up, Down, Left, Right, Confirm, Back, View, Pause, Steer, Gas, Brake, ShiftUp, ShiftDown }
    struct Button { public Control control; public Rect rect; public string label; public bool round; }

    static Idas3TouchControls active;
    // True while the overlay is on screen; the host ignores mouse emulation then.
    internal static bool OwnsPointer => active != null && active.Visible;
    internal static bool FreeMouseHeld => !OwnsPointer && Input.GetMouseButton(0);
    public static bool Supported => Idas3Platform.IsAndroid || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-idas3-touch") >= 0;

    public bool Visible { get; private set; }
    public bool Driving { get; private set; }
    // Full steering lock at this many screen-height percent of horizontal drag.
    public float steeringTravel = 15f;

    readonly Dictionary<int, Control> fingers = new Dictionary<int, Control>();
    readonly List<Button> buttons = new List<Button>();
    readonly HashSet<Control> held = new HashSet<Control>();
    int steerFinger = -1;
    Vector2 steerAnchor, steerPoint;
    float steering;
    Texture2D disk, ring;
    GUIStyle labelStyle;
    int layoutWidth, layoutHeight;
    Rect layoutSafe, steerZone;
    bool layoutDriving;

    void Awake() { active = this; }
    void OnDestroy() { if (active == this) active = null; if (disk != null) Destroy(disk); if (ring != null) Destroy(ring); }

    // Called once per host frame before the pad sample is used.
    public void Tick(bool show, bool driving)
    {
        if (!show) { Release(); Visible = false; return; }
        if (!Visible || driving != Driving) Release();
        Visible = true; Driving = driving;
        Layout();
        for (int i = 0; i < Input.touchCount; ++i)
        {
            var touch = Input.GetTouch(i);
            var point = new Vector2(touch.position.x, Screen.height - touch.position.y);
            if (touch.phase == TouchPhase.Began)
            {
                var control = HitTest(point);
                if (control == Control.None) continue;
                fingers[touch.fingerId] = control;
                if (control == Control.Steer) { steerFinger = touch.fingerId; steerAnchor = steerPoint = point; }
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                fingers.Remove(touch.fingerId);
                if (touch.fingerId == steerFinger) { steerFinger = -1; steering = 0; }
            }
            else if (touch.fingerId == steerFinger)
            {
                steerPoint = point;
                // Floating stick: past full lock the anchor follows the finger,
                // so reversing direction responds immediately.
                float travel = steeringTravel * Screen.height / 100f;
                float offset = steerPoint.x - steerAnchor.x;
                if (offset > travel) steerAnchor.x = steerPoint.x - travel;
                else if (offset < -travel) steerAnchor.x = steerPoint.x + travel;
            }
        }
        // A finger lifted while the app missed its Ended phase must not stay held.
        if (Input.touchCount == 0) { fingers.Clear(); steerFinger = -1; }
        held.Clear();
        foreach (var control in fingers.Values) held.Add(control);
        steering = steerFinger < 0 ? 0 : Mathf.Clamp((steerPoint.x - steerAnchor.x) / (steeringTravel * Screen.height / 100f), -1, 1);
    }

    void Release() { fingers.Clear(); held.Clear(); steerFinger = -1; steering = 0; }

    // Merge into the physical pad sample (a connected controller hides the overlay).
    public void Apply(ref Idas3ControlBindings.PadState pad)
    {
        if (!Visible) return;
        pad.connected = true;
        ushort bits = 0;
        if (held.Contains(Control.Up)) bits |= DpadUp;
        if (held.Contains(Control.Down)) bits |= DpadDown;
        if (held.Contains(Control.Left)) bits |= DpadLeft;
        if (held.Contains(Control.Right)) bits |= DpadRight;
        if (held.Contains(Control.Confirm)) bits |= A;
        if (held.Contains(Control.Back) || held.Contains(Control.ShiftUp)) bits |= B;
        if (held.Contains(Control.ShiftDown)) bits |= X;
        if (held.Contains(Control.View)) bits |= Y;
        if (held.Contains(Control.Pause)) bits |= Start;
        pad.buttons |= bits;
        if (held.Contains(Control.Gas)) pad.rightTrigger = 255;
        if (held.Contains(Control.Brake)) pad.leftTrigger = 255;
        if (steerFinger >= 0) pad.thumbLX = (short)Mathf.RoundToInt(steering * (steering < 0 ? 32768f : 32767f));
    }

    Control HitTest(Vector2 point)
    {
        float slop = Screen.height * .02f;
        foreach (var button in buttons)
        {
            var area = new Rect(button.rect.x - slop, button.rect.y - slop, button.rect.width + 2 * slop, button.rect.height + 2 * slop);
            if (area.Contains(point)) return button.control;
        }
        return Driving && steerZone.Contains(point) ? Control.Steer : Control.None;
    }

    void Layout()
    {
        var safe = Screen.safeArea;
        var guiSafe = new Rect(safe.x, Screen.height - safe.yMax, safe.width, safe.height);
        if (layoutWidth == Screen.width && layoutHeight == Screen.height && layoutSafe == guiSafe && layoutDriving == Driving) return;
        layoutWidth = Screen.width; layoutHeight = Screen.height; layoutSafe = guiSafe; layoutDriving = Driving;
        buttons.Clear();
        float u = Screen.height / 100f;
        float left = guiSafe.xMin + 3 * u, right = guiSafe.xMax - 3 * u, top = guiSafe.yMin + 3 * u, bottom = guiSafe.yMax - 3 * u;
        if (Driving)
        {
            // Keep clear of the default race HUD: the record panel (top right,
            // to ~41% height) and the tachometer (bottom right, ~42u wide).
            Add(Control.Pause, new Rect(right - 17 * u, guiSafe.yMin + 43 * u, 17 * u, 9 * u), "PAUSE", false);
            Add(Control.View, new Rect(right - 17 * u, guiSafe.yMin + 54 * u, 17 * u, 9 * u), "VIEW", false);
            // Pedals sit just left of the tachometer so chase cameras keep the
            // player's car (centre bottom) visible.
            var gas = new Rect(right - 48 * u, bottom - 40 * u, 17 * u, 40 * u);
            var brake = new Rect(gas.x - 18 * u, bottom - 28 * u, 15 * u, 28 * u);
            Add(Control.Gas, gas, "GAS", false);
            Add(Control.Brake, brake, "BRAKE", false);
            Add(Control.ShiftUp, new Rect(gas.x + 2 * u, gas.y - 15 * u, 13 * u, 13 * u), "▲", true);
            Add(Control.ShiftDown, new Rect(brake.x + 1 * u, brake.y - 15 * u, 13 * u, 13 * u), "▼", true);
            steerZone = new Rect(guiSafe.xMin, guiSafe.yMin + 16 * u, Mathf.Min(guiSafe.width * .5f, brake.x - 8 * u - guiSafe.xMin), guiSafe.height - 16 * u);
        }
        else
        {
            Add(Control.Pause, new Rect(right - 20 * u, top, 20 * u, 10 * u), "START", false);
            float size = 13 * u;
            var center = new Vector2(left + 1.5f * size, bottom - 1.5f * size);
            Add(Control.Up, new Rect(center.x - size / 2, center.y - 1.5f * size, size, size), "▲", false);
            Add(Control.Down, new Rect(center.x - size / 2, center.y + .5f * size, size, size), "▼", false);
            Add(Control.Left, new Rect(center.x - 1.5f * size, center.y - size / 2, size, size), "◀", false);
            Add(Control.Right, new Rect(center.x + .5f * size, center.y - size / 2, size, size), "▶", false);
            Add(Control.Confirm, new Rect(right - 19 * u, bottom - 25 * u, 19 * u, 19 * u), "A", true);
            Add(Control.Back, new Rect(right - 39 * u, bottom - 15 * u, 15 * u, 15 * u), "B", true);
            Add(Control.View, new Rect(right - 39 * u, bottom - 35 * u, 15 * u, 15 * u), "VIEW", true);
            steerZone = Rect.zero;
        }
    }
    void Add(Control control, Rect rect, string label, bool round) => buttons.Add(new Button { control = control, rect = rect, label = label, round = round });

    void OnGUI()
    {
        if (!Visible || Event.current.type != EventType.Repaint) return;
        GUI.depth = -100;
        if (disk == null) disk = CreateDisk(128, 0);
        if (ring == null) ring = CreateDisk(128, 5);
        if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        labelStyle.fontSize = Mathf.RoundToInt(Screen.height * .035f);
        var old = GUI.color;
        // Dark fill + light outline stays readable on white logos and night roads.
        float line = Mathf.Max(2f, Screen.height * .003f);
        foreach (var button in buttons)
        {
            bool down = held.Contains(button.control);
            GUI.color = down ? new Color(.9f, .1f, .16f, .6f) : new Color(0, 0, 0, .32f);
            GUI.DrawTexture(button.rect, button.round ? disk : Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, down ? .9f : .55f);
            if (button.round) GUI.DrawTexture(button.rect, ring);
            else Outline(button.rect, line);
            Label(button.rect, button.label, down ? 1f : .85f);
        }
        if (Driving)
        {
            float travel = steeringTravel * Screen.height / 100f, knob = Screen.height * .09f;
            var anchor = steerFinger >= 0 ? steerAnchor : new Vector2(steerZone.center.x, steerZone.yMax - 2.2f * travel);
            var track = new Rect(anchor.x - travel - knob / 2, anchor.y - knob * .3f, 2 * travel + knob, knob * .6f);
            GUI.color = new Color(0, 0, 0, .3f);
            GUI.DrawTexture(track, Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, .45f);
            Outline(track, line);
            var knobRect = new Rect(anchor.x + steering * travel - knob / 2, anchor.y - knob / 2, knob, knob);
            GUI.color = steerFinger >= 0 ? new Color(.9f, .1f, .16f, .65f) : new Color(0, 0, 0, .35f);
            GUI.DrawTexture(knobRect, disk);
            GUI.color = new Color(1, 1, 1, .8f);
            GUI.DrawTexture(knobRect, ring);
            if (steerFinger < 0) Label(new Rect(anchor.x - travel, anchor.y + knob * .6f, 2 * travel, knob * .6f), "◀  STEER  ▶", .85f);
        }
        GUI.color = old;
    }

    static void Outline(Rect r, float w)
    {
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y + w, w, r.height - 2 * w), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - w, r.y + w, w, r.height - 2 * w), Texture2D.whiteTexture);
    }

    void Label(Rect r, string text, float alpha)
    {
        float shadow = Mathf.Max(1f, Screen.height * .002f);
        GUI.color = new Color(0, 0, 0, alpha * .8f);
        GUI.Label(new Rect(r.x + shadow, r.y + shadow, r.width, r.height), text, labelStyle);
        GUI.color = new Color(1, 1, 1, alpha);
        GUI.Label(r, text, labelStyle);
    }

    // Antialiased filled disk (ringWidth 0) or ring of the given texel width.
    static Texture2D CreateDisk(int size, float ringWidth)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Touch control disk", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        float radius = size / 2f;
        for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
            {
                float distance = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(radius, radius));
                float coverage = Mathf.Clamp01(radius - distance);
                if (ringWidth > 0) coverage *= Mathf.Clamp01(distance - (radius - ringWidth));
                byte alpha = (byte)Mathf.RoundToInt(coverage * 255);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        texture.SetPixels32(pixels); texture.Apply(false, true);
        return texture;
    }
}
