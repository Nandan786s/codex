using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(SpineScrewSimulation))]
[RequireComponent(typeof(LeapPhysicalInputManager))]
public class SimulationUI : MonoBehaviour
{
    [Header("── Layout ──")]
    [Range(0.4f, 0.7f)] public float progressY = 0.55f;
    [Range(0.5f, 2f)] public float uiScale = 1f;

    [Header("── Drill UI Placement ──")]
    [Tooltip("Optional: tracked drill tip/tool transform used to place UI opposite to action.")]
    public Transform drillFocusTransform;
    [Tooltip("Optional: fallback model transform (bone/spine) if drill focus is not assigned.")]
    public Transform boneFocusTransform;
    [Range(0.05f, 0.25f)] public float sidePaddingNormalized = 0.08f;
    [Range(0.05f, 0.35f)] public float centerDeadZone = 0.14f;
    [Range(3f, 18f)] public float drillUiLerpSpeed = 10f;

    [Header("── Theme Colors ──")]
    public Color accentDrill = new Color(1f, 0.45f, 0.1f);
    public Color accentPlace = new Color(0.25f, 0.95f, 0.45f);
    public Color accentDrive = new Color(0.25f, 0.75f, 1f);
    public Color accentDone = new Color(0.3f, 1f, 0.5f);
    public Color barBgColor = new Color(0.08f, 0.06f, 0.04f, 0.7f);
    public Color panelBg = new Color(0.02f, 0.03f, 0.06f, 0.82f);
    public Color resistLow = new Color(0.15f, 0.75f, 0.25f);
    public Color resistHigh = new Color(1f, 0.18f, 0.1f);

    SpineScrewSimulation _sim;
    LeapPhysicalInputManager _input;
    Texture2D _w; bool _ready; float _t;
    readonly Dictionary<string, GUIStyle> _styleCache = new Dictionary<string, GUIStyle>();
    float _lastStyleScale = -1f;

    // Smoothed screen X anchor for drill panel.
    float _drillPanelCenterX = -1f;

    void Start() { _sim = GetComponent<SpineScrewSimulation>(); _input = GetComponent<LeapPhysicalInputManager>(); _w = new Texture2D(1, 1); _w.SetPixel(0, 0, Color.white); _w.Apply(); _ready = true; }
    void Update() { _t += Time.deltaTime; }
    void OnDestroy()
    {
        if (_w != null) Destroy(_w);
        _styleCache.Clear();
    }

    int S(float v) => Mathf.RoundToInt(v * uiScale);
    void R(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, _w); GUI.color = Color.white; }
    void Panel(Rect r) { R(r, panelBg); R(new Rect(r.x, r.y, r.width, 2), new Color(1, 1, 1, 0.08f)); R(new Rect(r.x, r.yMax - 1, r.width, 1), new Color(1, 1, 1, 0.04f)); }
    GUIStyle St(int sz, FontStyle fs, TextAnchor a, Color c)
    {
        if (!Mathf.Approximately(_lastStyleScale, uiScale))
        {
            _styleCache.Clear();
            _lastStyleScale = uiScale;
        }

        string k = $"{sz}_{(int)fs}_{(int)a}_{c.r:F3}_{c.g:F3}_{c.b:F3}_{c.a:F3}_{uiScale:F3}";
        if (_styleCache.TryGetValue(k, out var cached))
            return cached;

        var s = new GUIStyle
        {
            fontSize = S(sz),
            fontStyle = fs,
            alignment = a,
            wordWrap = true
        };
        s.normal.textColor = c;
        _styleCache[k] = s;
        return s;
    }

    void Bar(Rect r, float frac, Color c1, Color c2, string lbl, string val)
    {
        frac = Mathf.Clamp01(frac);
        R(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0, 0, 0, 0.9f));
        R(r, barBgColor);
        Color bc = Color.Lerp(c1, c2, frac);
        R(new Rect(r.x, r.y, r.width * frac, r.height), bc);
        R(new Rect(r.x, r.y, r.width * frac, r.height * 0.35f), new Color(1, 1, 1, 0.1f));
        if (lbl != "") GUI.Label(new Rect(r.x + 8, r.y, r.width - 16, r.height), lbl, St(11, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white));
        if (val != "") GUI.Label(new Rect(r.x + 8, r.y, r.width - 16, r.height), val, St(11, FontStyle.Bold, TextAnchor.MiddleRight, Color.white));
    }

    void OnGUI()
    {
        if (!_ready) return;
        float sw = Screen.width, sh = Screen.height;
        var phase = _input.currentPhase;
        Color accent = phase == LeapPhysicalInputManager.Phase.Drilling ? accentDrill :
                       phase == LeapPhysicalInputManager.Phase.Placing ? accentPlace :
                       phase == LeapPhysicalInputManager.Phase.Driving ? accentDrive : accentDone;

        DrawBanner(sw, accent, phase);
        DrawStatusPanel(sw, accent);

        switch (phase)
        {
            case LeapPhysicalInputManager.Phase.Drilling: DrawDrillUI(sw, sh); break;
            case LeapPhysicalInputManager.Phase.Placing: DrawPlaceUI(sw, sh); break;
            case LeapPhysicalInputManager.Phase.Driving: DrawDriveUI(sw, sh); break;
            case LeapPhysicalInputManager.Phase.Done: DrawDoneUI(sw, sh); break;
        }

        DrawFooter(sw, sh, phase);
    }

    Vector2 GetFocusViewportPoint()
    {
        Transform focus = drillFocusTransform != null ? drillFocusTransform :
                          (_input != null && _input.drillTipPoint != null ? _input.drillTipPoint :
                          (boneFocusTransform != null ? boneFocusTransform :
                          (_sim != null && _sim.GetBone() != null ? _sim.GetBone().transform : null)));
        Camera cam = Camera.main;

        if (focus == null || cam == null)
            return new Vector2(0.5f, 0.5f);

        Vector3 vp = cam.WorldToViewportPoint(focus.position);
        if (vp.z < 0f)
            return new Vector2(0.5f, 0.5f);

        return new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
    }

    bool ShouldPlaceDrillUiOnLeft(Vector2 focusViewport)
    {
        // If the drill/model is on right side of view, place UI on left side.
        if (focusViewport.x > 0.5f + centerDeadZone * 0.5f) return true;
        if (focusViewport.x < 0.5f - centerDeadZone * 0.5f) return false;

        // In center dead-zone, prefer left to avoid the right status panel overlap.
        return true;
    }

    float GetDrillUiTargetCenterX(float sw, float panelWidth, bool placeOnLeft)
    {
        float pad = sw * sidePaddingNormalized;
        float leftCenter = pad + panelWidth * 0.5f;

        // Keep clear of right status panel width + margin.
        float statusWidth = S(210) + S(16);
        float rightCenter = sw - statusWidth - pad - panelWidth * 0.5f;

        return placeOnLeft ? leftCenter : Mathf.Max(leftCenter, rightCenter);
    }

    float LerpDrillPanelX(float targetX)
    {
        if (_drillPanelCenterX < 0f)
            _drillPanelCenterX = targetX;

        _drillPanelCenterX = Mathf.Lerp(_drillPanelCenterX, targetX, 1f - Mathf.Exp(-drillUiLerpSpeed * Time.deltaTime));
        return _drillPanelCenterX;
    }

    // ── TOP BANNER ──
    void DrawBanner(float sw, Color accent, LeapPhysicalInputManager.Phase phase)
    {
        float h = S(56);
        Panel(new Rect(0, 0, sw, h));
        R(new Rect(0, h - 3, sw, 3), new Color(accent.r, accent.g, accent.b, 0.7f));

        // Step circles
        string[] names = { "DRILL", "PLACE", "DRIVE", "DONE" };
        string[] icons = { "⊕", "◎", "⟳", "✓" };
        Color[] cols = { accentDrill, accentPlace, accentDrive, accentDone };
        int pi = (int)phase;

        float dotX = S(20), dotY = S(8), dotW = S(70);
        for (int i = 0; i < 4; i++)
        {
            bool active = i == pi, done = i < pi;
            Color dc = done ? new Color(0.3f, 0.8f, 0.5f, 0.8f) : active ? cols[i] : new Color(0.3f, 0.3f, 0.35f, 0.5f);

            // Circle
            float cs = S(active ? 28 : 24);
            Rect cr = new Rect(dotX + i * dotW + (dotW - cs) * 0.5f, dotY, cs, cs);
            R(cr, new Color(dc.r, dc.g, dc.b, 0.15f));
            GUI.Label(cr, done ? "✓" : icons[i], St(active ? 16 : 13, active ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleCenter, dc));

            // Label
            GUI.Label(new Rect(dotX + i * dotW, dotY + cs + 1, dotW, S(12)),
                      names[i], St(9, FontStyle.Normal, TextAnchor.MiddleCenter,
                      active ? dc : new Color(0.5f, 0.5f, 0.55f)));

            // Connector line
            if (i < 3)
            {
                float lx = dotX + i * dotW + dotW * 0.5f + cs * 0.5f + 2;
                float lw = dotW - cs - 4;
                float ly = dotY + cs * 0.5f;
                R(new Rect(lx, ly, lw, 2), new Color(done || (i < pi) ? 0.3f : 0.2f, done || (i < pi) ? 0.8f : 0.2f, done || (i < pi) ? 0.5f : 0.25f, 0.4f));
            }
        }

        // Phase title
        float px = S(310);
        float pulse = 1 + 0.04f * Mathf.Sin(_t * 4);
        GUI.Label(new Rect(px, S(10), sw - px - S(220), S(28)),
                  $"{names[pi]} PHASE", St(Mathf.RoundToInt(20 * pulse), FontStyle.Bold, TextAnchor.MiddleLeft, accent));

        // Counts
        int holes = _sim.GetHoleCount(), placed = _sim.GetPlacedCount(), driven = _sim.GetFilledCount();
        GUI.Label(new Rect(sw - S(200), S(8), S(190), S(16)),
                  $"Holes: {holes}/{_sim.maxHoles}", St(12, FontStyle.Normal, TextAnchor.MiddleRight, new Color(0.7f, 0.75f, 0.8f)));
        GUI.Label(new Rect(sw - S(200), S(26), S(190), S(16)),
                  $"Screws: {placed} placed · {driven} driven", St(11, FontStyle.Normal, TextAnchor.MiddleRight, new Color(0.6f, 0.65f, 0.7f)));
    }

    // ── STATUS PANEL (right side) ──
    void DrawStatusPanel(float sw, Color accent)
    {
        float pw = S(210), ph = S(105), px = sw - pw - S(8), py = S(64);
        Panel(new Rect(px, py, pw, ph));
        R(new Rect(px, py, 3, ph), new Color(accent.r, accent.g, accent.b, 0.5f));

        float y = py + S(8), lx = px + S(12), vw = pw - S(24);

        // Hands
        bool hr = _input.HasRightHand, hl = _input.HasLeftHand;
        string ht = (hr && hl) ? "✓ Both Hands" : hr ? "✓ Right Hand" : hl ? "✓ Left Hand" : "✗ No Hands";
        GUI.Label(new Rect(lx, y, vw, S(16)), ht, St(12, FontStyle.Bold, TextAnchor.MiddleLeft,
                  (hr || hl) ? new Color(0.5f, 0.9f, 1) : new Color(1, 0.35f, 0.25f)));
        y += S(20);

        // Pinch meter
        float ps = _input.BestPinchStr;
        bool pp = _input.AnyPinch;
        GUI.Label(new Rect(lx, y, S(45), S(14)), pp ? "● Pinch" : "○ Pinch",
                  St(11, FontStyle.Normal, TextAnchor.MiddleLeft, pp ? new Color(0.3f, 1, 0.4f) : new Color(0.5f, 0.5f, 0.5f)));
        Bar(new Rect(lx + S(50), y + S(1), vw - S(58), S(12)), ps,
            new Color(0.3f, 0.6f, 0.3f), new Color(0.2f, 1, 0.3f), "", $"{ps:F2}");
        y += S(18);

        // Phase-specific status
        if (_input.currentPhase == LeapPhysicalInputManager.Phase.Drilling)
        {
            bool d = _sim.IsDrillActive();
            bool touch = _input.DrillTouching;
            string dt = d ? "● DRILLING" : touch ? "● CONTACT" : "○ No contact";
            Color dc = d ? new Color(1, 0.5f, 0.1f) : touch ? new Color(1, 0.85f, 0) : new Color(0.45f, 0.45f, 0.45f);
            GUI.Label(new Rect(lx, y, vw, S(16)), dt, St(12, FontStyle.Bold, TextAnchor.MiddleLeft, dc));
            y += S(18);
            if (_sim.GetHoleCount() > 0)
                GUI.Label(new Rect(lx, y, vw, S(14)), $"✓ {_sim.GetHoleCount()} hole(s)", St(11, FontStyle.Normal, TextAnchor.MiddleLeft, accentPlace));
        }
        else if (_input.currentPhase == LeapPhysicalInputManager.Phase.Driving)
        {
            bool at = _input.DriverAttached;
            GUI.Label(new Rect(lx, y, vw, S(16)), at ? "● Driver Attached" : "○ Bring to screw",
                      St(12, FontStyle.Bold, TextAnchor.MiddleLeft, at ? accentDrive : new Color(0.6f, 0.6f, 0.3f)));
            y += S(18);
            float tw = Mathf.Abs(_input.SmoothedTwist);
            if (at && tw > 0.1f)
                GUI.Label(new Rect(lx, y, vw, S(14)), $"Twist: {tw:F1}°", St(11, FontStyle.Normal, TextAnchor.MiddleLeft, new Color(0.6f, 0.8f, 1)));
        }
    }

    // ── DRILL UI ──
    void DrawDrillUI(float sw, float sh)
    {
        bool active = _sim.GetModeInt() == 1;
        int hc = _sim.GetHoleCount();

        if (active)
        {
            float bw = S(380);
            float pw = bw + S(40), ph = S(120);

            Vector2 focusVp = GetFocusViewportPoint();
            bool placeOnLeft = ShouldPlaceDrillUiOnLeft(focusVp);
            float targetCenterX = GetDrillUiTargetCenterX(sw, pw, placeOnLeft);
            float panelCenterX = LerpDrillPanelX(targetCenterX);

            float px = panelCenterX - pw * 0.5f;
            float py = sh * progressY - S(16);
            float cx = panelCenterX - bw * 0.5f;
            float cy = sh * progressY;

            Panel(new Rect(px, py, pw, ph));
            R(new Rect(px, py, pw, 3), new Color(accentDrill.r, accentDrill.g, accentDrill.b, 0.6f));

            float pulse = 0.7f + 0.3f * Mathf.Sin(_t * 5);
            GUI.Label(new Rect(cx, cy - S(10), bw, S(20)),
                      $"⬤ DRILLING HOLE #{hc + 1}", St(15, FontStyle.Bold, TextAnchor.MiddleCenter,
                      new Color(accentDrill.r, accentDrill.g, accentDrill.b, pulse)));

            float df = _sim.GetDrillDepthFraction();
            float by = cy + S(16);
            Bar(new Rect(cx, by, bw, S(26)), df, accentDrill, new Color(1, 0.15f, 0.05f),
                "DEPTH", $"{Mathf.RoundToInt(df * 100)}%");

            float rf = _sim.GetResistanceFraction();
            float ry = by + S(34);
            Bar(new Rect(cx, ry, bw, S(14)), rf, resistLow, resistHigh, "RESISTANCE", $"{Mathf.RoundToInt(rf * 100)}%");

            float mm = df * _sim.drillDepth * 1000, total = _sim.drillDepth * 1000;
            GUI.Label(new Rect(cx, ry + S(18), bw, S(14)), $"{mm:F1}mm / {total:F1}mm",
                      St(10, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.6f, 0.6f, 0.7f)));
        }
        else if (hc > 0)
        {
            // Waiting — show hole count + Done Drilling button
            float pw = S(320), ph = S(85), px = (sw - pw) * 0.5f, py = sh * progressY;
            Panel(new Rect(px, py, pw, ph));
            R(new Rect(px, py, pw, 3), accentPlace * 0.7f);

            GUI.Label(new Rect(px, py + S(8), pw, S(24)),
                      $"✓ {hc} Hole{(hc > 1 ? "s" : "")} Drilled", St(17, FontStyle.Bold, TextAnchor.MiddleCenter, accentPlace));
            GUI.Label(new Rect(px, py + S(32), pw, S(16)),
                      "Touch bone + Pinch = drill more", St(12, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.7f, 0.7f, 0.75f)));

            // Done Drilling button
            float btnW = S(140), btnH = S(28), btnX = px + (pw - btnW) * 0.5f, btnY = py + ph - btnH - S(6);
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.15f, 0.7f, 0.35f);
            if (GUI.Button(new Rect(btnX, btnY, btnW, btnH), "Done Drilling →"))
                _input.doneDrillingRequested = true;
            GUI.backgroundColor = old;
        }
    }

    // ── PLACE UI ──
    void DrawPlaceUI(float sw, float sh)
    {
        int total = _sim.GetHoleCount(), placed = _sim.GetPlacedCount();
        float pw = S(Mathf.Max(320, total * 40 + 60)), ph = S(85);
        float px = (sw - pw) * 0.5f, py = sh * progressY;
        Panel(new Rect(px, py, pw, ph));
        R(new Rect(px, py, pw, 3), accentPlace * 0.7f);

        GUI.Label(new Rect(px, py + S(6), pw, S(22)),
                  $"Place Screws  ({placed}/{total})", St(16, FontStyle.Bold, TextAnchor.MiddleCenter, accentPlace));

        // Dots
        float dotY = py + S(34), dotSz = S(28);
        float totalW = total * (dotSz + S(6));
        float dx = px + (pw - totalW) * 0.5f;

        for (int i = 0; i < total; i++)
        {
            var h = _sim.holes[i];
            Color dc = h.screwDriven ? accentDrive : h.screwPlaced ? accentPlace : new Color(0.3f, 0.3f, 0.35f);
            string dt = h.screwDriven ? "✓" : h.screwPlaced ? "●" : "○";
            float x = dx + i * (dotSz + S(6));

            R(new Rect(x, dotY, dotSz, dotSz), new Color(dc.r, dc.g, dc.b, 0.15f));
            GUI.Label(new Rect(x, dotY, dotSz, dotSz), dt,
                      St(15, h.screwPlaced ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleCenter, dc));
            GUI.Label(new Rect(x, dotY + dotSz, dotSz, S(12)), $"{i + 1}",
                      St(9, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.5f, 0.5f, 0.5f)));
        }
    }

    // ── DRIVE UI ──
    void DrawDriveUI(float sw, float sh)
    {
        int total = _sim.GetHoleCount();
        float progress = _sim.GetDriveProgress();

        float bw = S(380), pw = bw + S(40), ph = S(130);
        float px = (sw - pw) * 0.5f, py = sh * progressY;
        float cx = (sw - bw) * 0.5f;
        Panel(new Rect(px, py, pw, ph));
        R(new Rect(px, py, pw, 3), accentDrive * 0.7f);

        bool attached = _input.DriverAttached;
        float pulse = attached ? (0.7f + 0.3f * Mathf.Sin(_t * 4)) : 0.6f;
        string title = attached ? $"⬤ DRIVING SCREW #{_input.DrivingIndex + 1}" : "Bring screwdriver to screw head";
        GUI.Label(new Rect(cx, py + S(6), bw, S(22)), title,
                  St(15, FontStyle.Bold, TextAnchor.MiddleCenter,
                  new Color(accentDrive.r, accentDrive.g, accentDrive.b, pulse)));

        float by = py + S(32);
        Bar(new Rect(cx, by, bw, S(24)), progress, accentDrive, accentDone, "PENETRATION", $"{Mathf.RoundToInt(progress * 100)}%");

        float mm = progress * _sim.drillDepth * 1000, tot = _sim.drillDepth * 1000;
        GUI.Label(new Rect(cx, by + S(28), bw, S(14)), $"{mm:F1}mm / {tot:F1}mm",
                  St(10, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.6f, 0.6f, 0.7f)));

        // Mini dots
        float dotY = by + S(46), dotSz = S(20);
        float totalW = total * (dotSz + S(4));
        float dx = px + (pw - totalW) * 0.5f;
        for (int i = 0; i < total; i++)
        {
            var h = _sim.holes[i];
            Color dc = h.screwDriven ? accentDone : h.screwPlaced ? accentDrive : new Color(0.3f, 0.3f, 0.3f);
            GUI.Label(new Rect(dx + i * (dotSz + S(4)), dotY, dotSz, dotSz),
                      h.screwDriven ? "✓" : "●",
                      St(12, h.screwDriven ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleCenter, dc));
        }
    }

    // ── DONE UI ──
    void DrawDoneUI(float sw, float sh)
    {
        R(new Rect(0, 0, sw, sh), new Color(0, 0, 0, 0.4f));
        float pw = S(440), ph = S(280), px = (sw - pw) * 0.5f, py = (sh - ph) * 0.5f;
        Panel(new Rect(px, py, pw, ph));
        R(new Rect(px, py + ph - 4, pw, 4), accentDone);

        float pulse = 1 + 0.03f * Mathf.Sin(_t * 3);
        GUI.Label(new Rect(px, py + S(24), pw, S(40)),
                  "✓ Simulation Complete!", St(Mathf.RoundToInt(28 * pulse), FontStyle.Bold, TextAnchor.MiddleCenter, accentDone));

        int driven = _sim.GetFilledCount(), holes = _sim.GetHoleCount();
        GUI.Label(new Rect(px, py + S(72), pw, S(22)),
                  $"{driven} screw{(driven != 1 ? "s" : "")} placed and driven", St(15, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.8f, 0.9f, 0.85f)));

        // Summary dots
        float dotY = py + S(102), dotSz = S(26);
        float totalW = holes * (dotSz + S(6));
        float dx = px + (pw - totalW) * 0.5f;
        for (int i = 0; i < holes; i++)
            GUI.Label(new Rect(dx + i * (dotSz + S(6)), dotY, dotSz, dotSz), "✓",
                      St(16, FontStyle.Bold, TextAnchor.MiddleCenter, accentDone));

        GUI.Label(new Rect(px, py + S(138), pw, S(16)), "360° showcase in progress",
                  St(11, FontStyle.Italic, TextAnchor.MiddleCenter, new Color(0.5f, 0.7f, 0.6f)));

        // Buttons
        float btnW = S(120), btnH = S(38), gap = S(16);
        float totalBW = btnW * 3 + gap * 2;
        float bx = px + (pw - totalBW) * 0.5f, by = py + ph - btnH - S(28);

        Color old = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.15f, 0.5f, 0.3f);
        if (GUI.Button(new Rect(bx, by, btnW, btnH), "Drill More"))
        { _sim.GoFreeFromExternal(); _input.currentPhase = LeapPhysicalInputManager.Phase.Drilling; }

        GUI.backgroundColor = new Color(0.5f, 0.35f, 0.1f);
        if (GUI.Button(new Rect(bx + btnW + gap, by, btnW, btnH), "Retry"))
            UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);

        GUI.backgroundColor = new Color(0.5f, 0.15f, 0.15f);
        if (GUI.Button(new Rect(bx + (btnW + gap) * 2, by, btnW, btnH), "Exit"))
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
        GUI.backgroundColor = old;
    }

    // ── FOOTER INSTRUCTIONS ──
    void DrawFooter(float sw, float sh, LeapPhysicalInputManager.Phase phase)
    {
        if (phase == LeapPhysicalInputManager.Phase.Done) return;

        string txt = "";
        switch (phase)
        {
            case LeapPhysicalInputManager.Phase.Drilling:
                if (_sim.GetModeInt() == 1)
                    txt = "Hold pinch to drill  ·  Release = pause  ·  Move to new spot for next hole";
                else if (_sim.GetHoleCount() > 0)
                    txt = "Touch bone + Pinch = drill more  ·  Both fists or button = done drilling";
                else
                    txt = "Grab drill  →  Touch bone  →  Pinch to start drilling";
                break;
            case LeapPhysicalInputManager.Phase.Placing:
                txt = "Grab screw  →  Bring near gold ring  →  Auto-snaps to hole";
                break;
            case LeapPhysicalInputManager.Phase.Driving:
                if (_input.DriverAttached)
                    txt = "Pinch + Twist wrist slowly  ·  Smooth steady rotation for best results";
                else
                    txt = "Grab screwdriver  →  Bring near screw head  →  Auto-attaches";
                break;
        }
        if (txt == "") return;

        float iw = S(520), ih = S(28);
        float ix = (sw - iw) * 0.5f, iy = sh - S(44);
        Panel(new Rect(ix - S(10), iy - S(4), iw + S(20), ih + S(8)));

        float a = 0.55f + 0.35f * Mathf.Sin(_t * 2.5f);
        GUI.Label(new Rect(ix, iy, iw, ih), txt,
                  St(13, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(1, 0.95f, 0.75f, a)));
    }
}
