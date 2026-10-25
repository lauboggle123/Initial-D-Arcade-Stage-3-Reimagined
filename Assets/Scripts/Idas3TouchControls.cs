using System;
using System.Collections.Generic;
using System.Text;
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
    enum Control { None, Up, Down, Left, Right, Confirm, Back, View, Pause, Steer, Gas, Brake, ShiftUp, ShiftDown, SteerLeft, SteerRight }
    struct Button { public Control control; public Rect rect; public string label; public bool round; public int slot; }

    // Layout slots saved in Idas3GameOptions.Values.touch*: race 0-5, menu 6-10, race
    // steering d-pad 11-12 (appended so older saves keep their slots). The menu D-pad is one slot.
    const int SlotCount = Idas3GameOptions.Values.TouchControlCount;
    static readonly string[] SlotNames = { "PAUSE", "VIEW", "GAS", "BRAKE", "SHIFT UP", "SHIFT DOWN", "START", "D-PAD", "A", "B", "VIEW", "STEER LEFT", "STEER RIGHT" };
    const int SteerLeftKey = 65, SteerRightKey = 68; // Canonical A / D driving keys.
    static readonly string[] ModeNames = { "DRAG", "TILT", "D-PAD" };
    // Default layout widths in units of screen height/100, margins included: the left group
    // (drag track, BRAKE in TILT, d-pad), an 8u gap and the right group (BRAKE..PAUSE, 69u).
    static readonly float[] RaceWidth = { 120, 100, 136 };
    const float MenuWidth = 92, MinimumButtonMm = 7;

    static Idas3TouchControls active;
    internal static Idas3TouchControls Instance => active;
    // True while the overlay or its editor is on screen; the host ignores mouse emulation then.
    internal static bool OwnsPointer => active != null && (active.Visible || active.editing);
    internal static bool FreeMouseHeld => !OwnsPointer && Input.GetMouseButton(0);
    // Test environment: each rebuilt layout is logged for the device scripts (IdasDevice.ps1).
    internal static bool LogLayout;
    public static bool Supported => Idas3Platform.IsAndroid || Array.IndexOf(Environment.GetCommandLineArgs(), "-idas3-touch") >= 0;

    public bool Visible { get; private set; }
    public bool Driving { get; private set; }
    public bool Editing => editing;

    // Each finger keeps the control it began on; over = the shift button it is sliding across.
    readonly Dictionary<int, Control> fingers = new Dictionary<int, Control>(), over = new Dictionary<int, Control>();
    readonly Dictionary<int, Vector2> points = new Dictionary<int, Vector2>();
    readonly List<Button> buttons = new List<Button>();
    readonly HashSet<Control> held = new HashSet<Control>();
    readonly Rect[] slotHome = new Rect[SlotCount], slotRect = new Rect[SlotCount];
    Idas3GameOptions.Values settings = new Idas3GameOptions.Values();
    int steerFinger = -1;
    Vector2 steerAnchor, steerPoint;
    float steering;
    Texture2D disk, ring;
    GUIStyle labelStyle, toolStyle;
    int layoutWidth, layoutHeight;
    Rect layoutSafe, steerZone;
    bool layoutDriving, layoutDirty = true;
    int layoutMode;
    float layoutScale = 1;

    // Editor state: a working copy of the options, committed only by SAVE.
    bool editing, editDriving, dragging;
    Idas3GameOptions editOwner;
    Idas3GameOptions.Values working;
    Action<bool> editClosed;
    int selectedSlot, editMode;
    string editNotice = "";

    Idas3GameOptions.Values Layout => editing ? working : settings;
    // Each TOUCH STEERING mode has its own race layout; the editor shows the pending mode's.
    int Mode => editing ? editMode : Mathf.Clamp(settings.touchSteering, 0, 2);
    bool Tilt => settings.touchSteering == 1 && !editing;
    bool Dpad => Mode == 2;
    bool Slide => settings.touchSlideShift && Driving;
    float Travel => settings.touchSteeringTravel * Screen.height / 100f;

    void Awake() { active = this; }
    void OnDestroy() { if (active == this) active = null; if (disk != null) Destroy(disk); if (ring != null) Destroy(ring); }

    public void Configure(Idas3GameOptions.Values values)
    {
        if (values == null) return;
        settings = values.Clone(); layoutDirty = true;
        if (!editing) Release(); // A finger on a control the new mode lacks must not stay held.
    }

    // Called once per host frame before the pad sample is used.
    public void Tick(bool show, bool driving)
    {
        if (!show) { Release(); Visible = false; return; }
        if (!Visible || driving != Driving) Release();
        Visible = true; Driving = driving;
        RebuildLayout();
        for (int i = 0; i < Input.touchCount; ++i)
        {
            var touch = Input.GetTouch(i);
            var point = new Vector2(touch.position.x, Screen.height - touch.position.y);
            if (touch.phase == TouchPhase.Began)
            {
                var control = HitTest(point);
                // A race finger on empty screen is kept (as None) so it can slide onto ▲/▼.
                if (control == Control.None && !Slide) continue;
                fingers[touch.fingerId] = control; points[touch.fingerId] = point;
                over[touch.fingerId] = control == Control.ShiftUp || control == Control.ShiftDown ? control : Control.None;
                if (control == Control.Steer) { steerFinger = touch.fingerId; steerAnchor = steerPoint = point; }
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                fingers.Remove(touch.fingerId); over.Remove(touch.fingerId); points.Remove(touch.fingerId);
                if (touch.fingerId == steerFinger) { steerFinger = -1; steering = 0; }
            }
            else if (touch.fingerId == steerFinger)
            {
                steerPoint = point;
                // Floating stick: past full lock the anchor follows the finger,
                // so reversing direction responds immediately.
                float travel = Travel;
                float offset = steerPoint.x - steerAnchor.x;
                if (offset > travel) steerAnchor.x = steerPoint.x - travel;
                else if (offset < -travel) steerAnchor.x = steerPoint.x + travel;
            }
            else if (fingers.ContainsKey(touch.fingerId))
            {
                points[touch.fingerId] = point;
                over[touch.fingerId] = ShiftUnder(point, over.TryGetValue(touch.fingerId, out var shift) ? shift : Control.None);
            }
        }
        // A finger lifted while the app missed its Ended phase must not stay held.
        if (Input.touchCount == 0) { fingers.Clear(); over.Clear(); points.Clear(); steerFinger = -1; }
        held.Clear();
        foreach (var finger in fingers)
        {
            var control = finger.Value;
            var shift = over.TryGetValue(finger.Key, out var slid) ? slid : Control.None;
            // A d-pad finger steers toward the nearer arrow until lifted, so a thumb
            // rolling across reverses without letting go. Shift is level-based per
            // tick and acts on the press, so a finger over ▲/▼ counts as holding it:
            // sliding in shifts once, out and back in shifts again. GAS and BRAKE
            // keep their pedal while their finger passes over a shift button.
            if (control == Control.SteerLeft || control == Control.SteerRight) control = NearestDpad(points[finger.Key], control);
            else if (Slide && (control == Control.ShiftUp || control == Control.ShiftDown || control == Control.None)) control = shift;
            else if (Slide && (control == Control.Gas || control == Control.Brake)) held.Add(shift);
            held.Add(control);
        }
        held.Remove(Control.None);
        if (Driving && Tilt) steering = Mathf.Lerp(steering, TiltSteering(), 1 - Mathf.Exp(-Time.unscaledDeltaTime * 15));
        else steering = steerFinger < 0 ? 0 : Mathf.Clamp((steerPoint.x - steerAnchor.x) / Travel, -1, 1);
    }

    // Roll of the phone in the screen plane, from gravity: turning it clockwise
    // like a wheel steers right. Unity reports acceleration in the current
    // screen's axes (measured on device: held level in landscape, gravity is
    // -y; clockwise moves it to +x), so tilting the screen back changes only
    // y/z and does not steer.
    float TiltSteering()
    {
        var g = Input.acceleration;
        if (g.x * g.x + g.y * g.y < .04f) return 0; // Phone lying flat: no usable roll.
        return Mathf.Clamp(Mathf.Atan2(g.x, -g.y) * Mathf.Rad2Deg / settings.touchTiltDegrees, -1, 1);
    }

    void Release() { fingers.Clear(); over.Clear(); points.Clear(); held.Clear(); steerFinger = -1; steering = 0; }

    // Entering needs the drawn disk (a drifting gas thumb must not graze ▲); staying
    // only needs the wider tap area, so jitter at the edge cannot repeat a shift.
    Control ShiftUnder(Vector2 point, Control current)
    {
        if (!Slide) return Control.None;
        float slop = Screen.height * .02f, best = float.MaxValue;
        var hit = Control.None;
        foreach (var button in buttons)
        {
            if (button.control != Control.ShiftUp && button.control != Control.ShiftDown) continue;
            var area = new Rect(button.rect.x - slop, button.rect.y - slop, button.rect.width + 2 * slop, button.rect.height + 2 * slop);
            if (button.control == current && area.Contains(point)) return current;
            float distance = (point - button.rect.center).sqrMagnitude, radius = Mathf.Min(button.rect.width, button.rect.height) / 2;
            if (distance <= radius * radius && distance < best) { best = distance; hit = button.control; }
        }
        return hit;
    }

    Control NearestDpad(Vector2 point, Control fallback)
    {
        float best = float.MaxValue;
        foreach (var button in buttons)
        {
            if (button.control != Control.SteerLeft && button.control != Control.SteerRight) continue;
            float distance = (point - button.rect.center).sqrMagnitude;
            if (distance < best) { best = distance; fallback = button.control; }
        }
        return fallback;
    }

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
        // D-pad steering goes in as keys (ApplyKeys): with original handling a full
        // stick value reaches the wheel at once, keys use the digital-steering ramp.
        if (steerFinger >= 0 || Driving && Tilt) pad.thumbLX = (short)Mathf.RoundToInt(steering * (steering < 0 ? 32768f : 32767f));
    }

    // After the driving bindings have placed their keys.
    internal void ApplyKeys(ref Idas3Native.FrameInput frame)
    {
        if (!Visible || !Driving) return;
        if (held.Contains(Control.SteerLeft)) frame.SetKey(SteerLeftKey);
        if (held.Contains(Control.SteerRight)) frame.SetKey(SteerRightKey); // Both held cancel out natively.
    }

    // Overlapping (moved or enlarged) controls: the touch goes to the nearest centre.
    Control HitTest(Vector2 point)
    {
        float slop = Screen.height * .02f, best = float.MaxValue;
        var hit = Control.None;
        foreach (var button in buttons)
        {
            var area = new Rect(button.rect.x - slop, button.rect.y - slop, button.rect.width + 2 * slop, button.rect.height + 2 * slop);
            float distance = (point - button.rect.center).sqrMagnitude;
            if (area.Contains(point) && distance < best) { best = distance; hit = button.control; }
        }
        if (hit != Control.None) return hit;
        return Driving && !Tilt && !Dpad && steerZone.Contains(point) ? Control.Steer : Control.None;
    }

    static Rect SafeRect()
    {
        var safe = Screen.safeArea;
        return safe.width > 0 && safe.height > 0 ? new Rect(safe.x, Screen.height - safe.yMax, safe.width, safe.height) : new Rect(0, 0, Screen.width, Screen.height);
    }

    void RebuildLayout()
    {
        var guiSafe = SafeRect();
        bool driving = editing ? editDriving : Driving;
        int mode = Mode;
        if (!layoutDirty && layoutWidth == Screen.width && layoutHeight == Screen.height && layoutSafe == guiSafe && layoutDriving == driving && layoutMode == mode) return;
        layoutDirty = false; layoutWidth = Screen.width; layoutHeight = Screen.height; layoutSafe = guiSafe; layoutDriving = driving; layoutMode = mode;
        buttons.Clear();
        layoutScale = FitScale(guiSafe, driving ? RaceWidth[mode] : MenuWidth);
        float u = Screen.height / 100f * layoutScale;
        float left = guiSafe.xMin + 3 * u, right = guiSafe.xMax - 3 * u, top = guiSafe.yMin + 3 * u, bottom = guiSafe.yMax - 3 * u;
        if (driving)
        {
            // Keep clear of the default race HUD: the record panel (top right,
            // to ~41% height) and the tachometer (bottom right, ~42u wide).
            Add(Control.Pause, Place(0, new Rect(right - 17 * u, guiSafe.yMin + 43 * u, 17 * u, 9 * u), guiSafe), "PAUSE", false, 0);
            Add(Control.View, Place(1, new Rect(right - 17 * u, guiSafe.yMin + 54 * u, 17 * u, 9 * u), guiSafe), "VIEW", false, 1);
            // Pedals sit just left of the tachometer so chase cameras keep the
            // player's car (centre bottom) visible.
            var gas = new Rect(right - 48 * u, bottom - 40 * u, 17 * u, 40 * u);
            var brake = new Rect(gas.x - 18 * u, bottom - 28 * u, 15 * u, 28 * u);
            // Tilt has no on-screen steering, so BRAKE takes the left thumb; ▼ keeps its place.
            var placedBrake = Place(3, mode == 1 ? new Rect(left, bottom - 34 * u, 20 * u, 34 * u) : brake, guiSafe);
            Add(Control.Gas, Place(2, gas, guiSafe), "GAS", false, 2);
            Add(Control.Brake, placedBrake, "BRAKE", false, 3);
            Add(Control.ShiftUp, Place(4, new Rect(gas.x + 2 * u, gas.y - 15 * u, 13 * u, 13 * u), guiSafe), "▲", true, 4);
            Add(Control.ShiftDown, Place(5, new Rect(brake.x + 1 * u, brake.y - 15 * u, 13 * u, 13 * u), guiSafe), "▼", true, 5);
            // D-pad steering replaces the drag zone: two thumb-sized arrows bottom left.
            if (Dpad)
            {
                Add(Control.SteerLeft, Place(11, new Rect(left, bottom - 30 * u, 26 * u, 30 * u), guiSafe), "◀", false, 11);
                Add(Control.SteerRight, Place(12, new Rect(left + 30 * u, bottom - 30 * u, 26 * u, 30 * u), guiSafe), "▶", false, 12);
            }
            // Buttons hit-test before the zone, so one moved into it still wins.
            float reach = placedBrake.x - 8 * u - guiSafe.xMin;
            if (mode == 1 && placedBrake.center.x < guiSafe.center.x)
            {
                // The TILT TO STEER indicator sits between the left BRAKE and the right-hand group.
                float from = placedBrake.xMax + 4 * u, to = Mathf.Min(slotRect[2].x, slotRect[5].x) - 4 * u;
                steerZone = new Rect(from, guiSafe.yMin + 16 * u, Mathf.Max(0, to - from), guiSafe.height - 16 * u);
            }
            else steerZone = new Rect(guiSafe.xMin, guiSafe.yMin + 16 * u, reach >= 25 * u ? Mathf.Min(guiSafe.width * .5f, reach) : guiSafe.width * .5f, guiSafe.height - 16 * u);
        }
        else
        {
            Add(Control.Pause, Place(6, new Rect(right - 20 * u, top, 20 * u, 10 * u), guiSafe), "START", false, 6);
            float size = 13 * u;
            var center = new Vector2(left + 1.5f * size, bottom - 1.5f * size);
            var pad = Place(7, new Rect(center.x - 1.5f * size, center.y - 1.5f * size, 3 * size, 3 * size), guiSafe);
            size = pad.width / 3; center = pad.center;
            Add(Control.Up, new Rect(center.x - size / 2, center.y - 1.5f * size, size, size), "▲", false, 7);
            Add(Control.Down, new Rect(center.x - size / 2, center.y + .5f * size, size, size), "▼", false, 7);
            Add(Control.Left, new Rect(center.x - 1.5f * size, center.y - size / 2, size, size), "◀", false, 7);
            Add(Control.Right, new Rect(center.x + .5f * size, center.y - size / 2, size, size), "▶", false, 7);
            Add(Control.Confirm, Place(8, new Rect(right - 19 * u, bottom - 25 * u, 19 * u, 19 * u), guiSafe), "A", true, 8);
            Add(Control.Back, Place(9, new Rect(right - 39 * u, bottom - 15 * u, 15 * u, 15 * u), guiSafe), "B", true, 9);
            Add(Control.View, Place(10, new Rect(right - 39 * u, bottom - 35 * u, 15 * u, 15 * u), guiSafe), "VIEW", true, 10);
            steerZone = Rect.zero;
        }
        if (LogLayout && !editing) LogControls(driving);
    }

    // Narrow safe areas (4:3 tablets, foldables) shrink the whole layout uniformly until the
    // default fits, but never below thumb-sized ▲/▼ (13u at full size); past that the controls
    // are only clamped inside the safe area. Wider screens keep the edge anchoring.
    static float FitScale(Rect safe, float width)
    {
        float u = Screen.height / 100f;
        float floor = Screen.dpi > 0 ? MinimumButtonMm / 25.4f * Screen.dpi / (13 * u) : .75f;
        return Mathf.Clamp(safe.width / (width * u), Mathf.Min(floor, 1), 1);
    }

    void LogControls(bool driving)
    {
        var text = new StringBuilder();
        text.AppendFormat("IDAS3 touch layout {0} {1} {2}x{3} scale {4:0.00}:", driving ? "race" : "menu", ModeNames[Mode], Screen.width, Screen.height, layoutScale);
        foreach (var button in buttons)
        {
            var c = button.control;
            string name = c == Control.Confirm ? "A" : c == Control.Back ? "B" : c == Control.Pause && !driving ? "Start" : c.ToString();
            text.AppendFormat(" {0}={1},{2}", name, Mathf.RoundToInt(button.rect.center.x), Mathf.RoundToInt(button.rect.center.y));
        }
        if (driving && !Tilt && !Dpad) text.AppendFormat(" Steer={0},{1}", Mathf.RoundToInt(steerZone.center.x), Mathf.RoundToInt(steerZone.yMax - 2.2f * Travel));
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", text);
    }

    // Saved offset and size around the default centre, kept wholly inside the safe area.
    Rect Place(int slot, Rect home, Rect bounds)
    {
        var layout = Layout;
        var size = home.size * (layout.TouchSizePercent(slot, Mode) / 100f);
        size = new Vector2(Mathf.Min(size.x, bounds.width), Mathf.Min(size.y, bounds.height));
        var center = home.center + layout.TouchOffset(slot, Mode) * (Screen.height * layoutScale);
        var rect = new Rect(center - size / 2, size);
        rect.x = Mathf.Clamp(rect.x, bounds.xMin, bounds.xMax - rect.width);
        rect.y = Mathf.Clamp(rect.y, bounds.yMin, bounds.yMax - rect.height);
        slotHome[slot] = home; slotRect[slot] = rect;
        return rect;
    }
    void Add(Control control, Rect rect, string label, bool round, int slot) => buttons.Add(new Button { control = control, rect = rect, label = label, round = round, slot = slot });

    // ---- Layout editor (opened from Game Settings; the pause menu stays hidden meanwhile) ----

    public void BeginEdit(Idas3GameOptions owner, bool driving, Action<bool> closed)
    {
        if (editing || owner == null) return;
        // The pending TOUCH STEERING row decides which race layout is edited.
        Release(); editOwner = owner; working = owner.Current.Clone(); editClosed = closed; editMode = Mathf.Clamp(owner.Draft.touchSteering, 0, 2);
        editing = true; editDriving = driving; dragging = false; editNotice = "";
        selectedSlot = driving ? 2 : 8; layoutDirty = true;
    }

    public void EndEdit(bool save)
    {
        if (!editing) return;
        if (save && !editOwner.ApplyTouchLayout(working)) { editNotice = editOwner.LastError ?? "Could not save the touch layout."; return; }
        editing = false; dragging = false; layoutDirty = true;
        var closed = editClosed; editClosed = null; editOwner = null; working = null;
        closed?.Invoke(save);
    }

    void ShowEditLayout(bool driving)
    {
        if (editDriving == driving) return;
        editDriving = driving; dragging = false; selectedSlot = driving ? 2 : 8; layoutDirty = true;
    }

    void MoveSelected(Vector2 delta)
    {
        int slot = selectedSlot;
        working.SetTouchOffset(slot, editMode, (slotRect[slot].center + delta - slotHome[slot].center) / (Screen.height * layoutScale));
        SyncSelected();
    }

    // Re-derive the stored offset from the clamped rectangle so a control
    // pinned at an edge never carries an offset that keeps it stuck there.
    void SyncSelected()
    {
        layoutDirty = true; RebuildLayout();
        int slot = selectedSlot;
        working.SetTouchOffset(slot, editMode, (slotRect[slot].center - slotHome[slot].center) / (Screen.height * layoutScale));
        layoutDirty = true; RebuildLayout();
    }

    void StepSelected(bool opacity, int step, int minimum, int maximum)
    {
        int now = opacity ? working.TouchOpacityPercent(selectedSlot, editMode) : working.TouchSizePercent(selectedSlot, editMode);
        working.SetTouchPercent(selectedSlot, editMode, opacity, Mathf.Clamp(now + step, minimum, maximum));
        SyncSelected();
    }

    void ResetSlot(int slot) { working.SetTouchOffset(slot, editMode, Vector2.zero); working.SetTouchPercent(slot, editMode, false, 100); working.SetTouchPercent(slot, editMode, true, 100); }

    // Approximate areas of the default race HUD and the chase-camera car.
    static Rect[] KeepOut(Rect safe, float u) => new[] {
        new Rect(safe.xMax - 38 * u, safe.yMin, 38 * u, 41 * u),
        new Rect(safe.xMax - 44 * u, safe.yMax - 36 * u, 44 * u, 36 * u),
        new Rect(safe.center.x - 18 * u, safe.yMin + 50 * u, 36 * u, 34 * u) };

    void DrawEditor()
    {
        float u = Screen.height / 100f;
        var safe = SafeRect();
        var e = Event.current;
        float toolWidth = Mathf.Min(150 * u, Screen.width - 50 * u), row = 9 * u, gap = u;
        var tools = new Rect(safe.center.x - toolWidth / 2, safe.yMin + 2 * u, toolWidth, 2 * row + 3 * gap);
        toolStyle.fontSize = Mathf.RoundToInt(3.2f * u);

        if (e.type == EventType.Repaint)
        {
            GUI.color = new Color(0, 0, 0, .45f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        }
        // Overlaps still work (nearest centre wins) but leave less room for each control.
        bool overlaps = false, covers = false;
        for (int i = 0; i < buttons.Count; ++i)
            for (int j = i + 1; j < buttons.Count; ++j)
                if (buttons[i].slot != buttons[j].slot && buttons[i].rect.Overlaps(buttons[j].rect)) overlaps = true;
        if (editDriving)
        {
            foreach (var zone in KeepOut(safe, u * layoutScale))
            {
                foreach (var button in buttons) if (button.rect.Overlaps(zone)) covers = true;
                if (e.type != EventType.Repaint) continue;
                GUI.color = new Color(1, .85f, .2f, .14f); GUI.DrawTexture(zone, Texture2D.whiteTexture);
                GUI.color = new Color(1, .85f, .2f, .5f); Outline(zone, Mathf.Max(1f, .2f * u));
            }
        }
        if (e.type == EventType.Repaint) DrawButtons(true);

        // Toolbar first: its buttons win over a control lying underneath.
        if (e.type == EventType.Repaint) { GUI.color = new Color(.07f, .08f, .1f, .92f); GUI.DrawTexture(tools, Texture2D.whiteTexture); }
        GUI.color = Color.white;
        float x = tools.x + gap, y = tools.y + gap, w = (toolWidth - 6 * gap) / 5;
        if (Tool(new Rect(x, y, w, row), "RACE · " + ModeNames[editMode], editDriving)) ShowEditLayout(true);
        if (Tool(new Rect(x + (w + gap), y, w, row), "MENU", !editDriving)) ShowEditLayout(false);
        if (Tool(new Rect(x + 2 * (w + gap), y, w, row), "RESET ALL")) { for (int i = 0; i < SlotCount; ++i) if (Idas3GameOptions.Values.IsRaceTouchSlot(i) == editDriving) ResetSlot(i); layoutDirty = true; editNotice = ""; }
        if (Tool(new Rect(x + 3 * (w + gap), y, w, row), "CANCEL")) { EndEdit(false); return; }
        if (Tool(new Rect(x + 4 * (w + gap), y, w, row), "SAVE", true)) { EndEdit(true); if (!editing) return; }
        y += row + gap; w = (toolWidth - 7 * gap) / 6;
        GUI.Label(new Rect(x, y, w, row), SlotNames[selectedSlot], toolStyle);
        if (Tool(new Rect(x + (w + gap), y, w, row), "SIZE −")) StepSelected(false, -10, 50, 200);
        if (Tool(new Rect(x + 2 * (w + gap), y, w, row), "SIZE +")) StepSelected(false, 10, 50, 200);
        if (Tool(new Rect(x + 3 * (w + gap), y, w, row), "FADE −")) StepSelected(true, -20, 20, 100);
        if (Tool(new Rect(x + 4 * (w + gap), y, w, row), "FADE +")) StepSelected(true, 20, 20, 100);
        if (Tool(new Rect(x + 5 * (w + gap), y, w, row), "RESET")) { ResetSlot(selectedSlot); SyncSelected(); }

        bool warning = editNotice.Length == 0 && (overlaps || covers);
        string status = editNotice.Length > 0 ? editNotice : overlaps ? "Two controls overlap: a tap there presses the nearer one." :
            covers ? "A control covers part of the default race HUD or the car." : "Drag a control to move it. SIZE and FADE change the selected one.";
        var statusRect = new Rect(safe.center.x - toolWidth / 2, tools.yMax + gap, toolWidth, 6 * u);
        if (e.type == EventType.Repaint) { GUI.color = new Color(0, 0, 0, .6f); GUI.DrawTexture(statusRect, Texture2D.whiteTexture); }
        GUI.color = warning ? new Color(1, .85f, .2f) : Color.white;
        int toolFont = toolStyle.fontSize; toolStyle.fontSize = Mathf.RoundToInt(2.6f * u);
        GUI.Label(statusRect, status, toolStyle);
        toolStyle.fontSize = toolFont;
        GUI.color = Color.white;

        // Dragging: IMGUI receives the first touch as the mouse on Android.
        if (e.type == EventType.MouseDown && !tools.Contains(e.mousePosition) && !statusRect.Contains(e.mousePosition))
        {
            float best = float.MaxValue;
            foreach (var button in buttons)
            {
                float distance = (e.mousePosition - button.rect.center).sqrMagnitude;
                if (button.rect.Contains(e.mousePosition) && distance < best) { best = distance; selectedSlot = button.slot; dragging = true; }
            }
            if (dragging) { editNotice = ""; e.Use(); }
        }
        else if (e.type == EventType.MouseDrag && dragging) { MoveSelected(e.delta); e.Use(); }
        else if (e.type == EventType.MouseUp && dragging) { dragging = false; e.Use(); }
    }

    bool Tool(Rect rect, string text, bool active = false)
    {
        if (Event.current.type == EventType.Repaint)
        {
            GUI.color = active ? new Color(.87f, .14f, .19f, .95f) : new Color(.16f, .17f, .2f, .95f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, .35f); Outline(rect, 1);
            GUI.color = Color.white; GUI.Label(rect, text, toolStyle);
        }
        return GUI.Button(rect, GUIContent.none, GUIStyle.none);
    }

    void OnGUI()
    {
        if (!Visible && !editing) return;
        if (!editing && Event.current.type != EventType.Repaint) return;
        GUI.depth = -100;
        if (disk == null) disk = CreateDisk(128, 0);
        if (ring == null) ring = CreateDisk(128, 5);
        if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        if (toolStyle == null) toolStyle = new GUIStyle(labelStyle) { wordWrap = true };
        labelStyle.fontSize = Mathf.RoundToInt(Screen.height * .035f * layoutScale);
        var old = GUI.color;
        if (editing) { RebuildLayout(); DrawEditor(); GUI.color = old; return; }
        DrawButtons(false);
        if (Driving && !Dpad)
        {
            float line = Mathf.Max(2f, Screen.height * .003f);
            float travel = Travel, knob = Screen.height * .09f;
            var anchor = steerFinger >= 0 ? steerAnchor : new Vector2(steerZone.center.x, steerZone.yMax - 2.2f * travel);
            if (Tilt) { travel = Screen.height * .15f * layoutScale; anchor = new Vector2(steerZone.center.x, steerZone.yMax - 2.2f * travel); }
            var track = new Rect(anchor.x - travel - knob / 2, anchor.y - knob * .3f, 2 * travel + knob, knob * .6f);
            GUI.color = new Color(0, 0, 0, .3f);
            GUI.DrawTexture(track, Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, .45f);
            Outline(track, line);
            var knobRect = new Rect(anchor.x + steering * travel - knob / 2, anchor.y - knob / 2, knob, knob);
            GUI.color = steerFinger >= 0 || Tilt ? new Color(.9f, .1f, .16f, .65f) : new Color(0, 0, 0, .35f);
            GUI.DrawTexture(knobRect, disk);
            GUI.color = new Color(1, 1, 1, .8f);
            GUI.DrawTexture(knobRect, ring);
            if (Tilt) Label(new Rect(anchor.x - travel, anchor.y + knob * .6f, 2 * travel, knob * .6f), "TILT TO STEER", .85f);
            else if (steerFinger < 0) Label(new Rect(anchor.x - travel, anchor.y + knob * .6f, 2 * travel, knob * .6f), "◀  STEER  ▶", .85f);
        }
        GUI.color = old;
    }

    void DrawButtons(bool editor)
    {
        // Dark fill + light outline stays readable on white logos and night roads.
        float line = Mathf.Max(2f, Screen.height * .003f);
        foreach (var button in buttons)
        {
            bool down = held.Contains(button.control);
            float alpha = Layout.TouchOpacityPercent(button.slot, Mode) / 100f;
            GUI.color = down ? new Color(.9f, .1f, .16f, .6f * alpha) : new Color(0, 0, 0, .32f * alpha);
            GUI.DrawTexture(button.rect, button.round ? disk : Texture2D.whiteTexture);
            bool selected = editor && button.slot == selectedSlot;
            GUI.color = selected ? new Color(.95f, .15f, .2f, 1) : new Color(1, 1, 1, (down ? .9f : .55f) * alpha);
            if (button.round) GUI.DrawTexture(button.rect, ring);
            else Outline(button.rect, selected ? 2.5f * line : line);
            if (selected && button.round) Outline(button.rect, line);
            Label(button.rect, button.label, (down ? 1f : .85f) * Mathf.Max(alpha, editor ? .6f : 0));
        }
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
