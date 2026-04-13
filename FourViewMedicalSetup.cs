using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class FourViewMedicalSetup : MonoBehaviour
{
    [Header("Scene")]
    public Transform volumeTarget;
    public Transform toolTransform;
    public LayerMask criticalStructureMask;

    [Header("X-Ray")]
    public Shader xRayShader;
    public string[] xRayShaderCandidates = { "Medical/XRayOrtho_URP", "Medical/XRayOrtho" };
    public bool[] quadXRay = { true, true, true, false };
    public bool useMaterialXRayFallback = true;

    [Header("Reslicing")]
    public int resliceQuadrant = 2;
    public bool enableResliceScroll = true;

    [Header("Replay")]
    public int maxReplayFrames = 5000;

    [Header("Tool Training")]
    public bool planningMode = true;
    public bool alignmentAssist = true;
    public float alignmentSnapAngleDeg = 6f;
    public float insertionSpeed = 0.15f;
    public float toolCollisionRadius = 0.004f;

    [Header("Camera")]
    [Range(20f, 90f)] public float perspectiveFov = 60f;
    [Range(0.05f, 5f)] public float zoomSpeed = 0.15f;

    readonly string[] _quadLabels = { "Trajectory 1", "Trajectory 2", "Probe Eye View", "3D View" };
    readonly Rect[] _defaultVP =
    {
        new Rect(0f,   0.5f, 0.5f, 0.5f),
        new Rect(0.5f, 0.5f, 0.5f, 0.5f),
        new Rect(0f,   0f,   0.5f, 0.5f),
        new Rect(0.5f, 0f,   0.5f, 0.5f),
    };

    Rect[] _quadVP = new Rect[4];
    int _maxQuad = -1;

    Camera[] _baseCams = new Camera[4];
    Camera[] _overlayCams = new Camera[4];
    Camera _cam3D;

    Shader _resolvedXRayShader;
    bool _xRayAvailable;

    Vector3 _pivot;
    float _orbitDist;
    float _minOrbitDist;
    float _maxOrbitDist;
    float _yaw = 45f;
    float _pitch = 30f;

    bool _orbitDrag;
    bool _panDrag;
    bool _sliderDrag;
    bool _crosshairDrag;
    int _crosshairDragQuad = -1;
    Vector2 _lastMouse;
    float _sliderOffsetY;

    Texture2D _px;
    GUIStyle _sLabel, _sTitle, _sValue, _sBtn;
    bool _stylesBuilt;

    Material _xrayFallbackMat;
    Dictionary<Renderer, Material[]> _originalMats = new Dictionary<Renderer, Material[]>();

    // crosshair normalized in volume bounds [0..1]
    Vector3 _crosshair = new Vector3(0.5f, 0.5f, 0.5f);

    [Serializable]
    public class Trajectory
    {
        public bool hasEntry;
        public bool hasTarget;
        public Vector3 entry;
        public Vector3 target;
        public Color color;

        public float LengthMm => hasEntry && hasTarget ? Vector3.Distance(entry, target) * 1000f : 0f;
        public Vector3 Direction => hasEntry && hasTarget ? (target - entry).normalized : Vector3.forward;
    }

    [Serializable]
    public class Marker
    {
        public Vector3 world;
        public string label;
        public Color color;
    }

    [Serializable]
    public class MeasureSegment
    {
        public bool valid;
        public Vector3 a;
        public Vector3 b;
        public float DistanceMm => valid ? Vector3.Distance(a, b) * 1000f : 0f;
    }

    [Serializable]
    class ReplayFrame
    {
        public float t;
        public Vector3 toolPos;
        public Quaternion toolRot;
    }

    Trajectory _trajectory = new Trajectory { color = new Color(0.2f, 0.9f, 1f) };
    List<Marker> _markers = new List<Marker>();
    MeasureSegment _measure = new MeasureSegment();
    bool _measurePickA;

    bool _setEntryNext;
    bool _setTargetNext;

    bool _recording;
    bool _replaying;
    float _replayT;
    List<ReplayFrame> _replay = new List<ReplayFrame>();

    bool _collisionWarning;
    string _collisionMessage = "";

    float _elapsedTime;
    float _insertedDistanceMm;

    bool _isSRP;

    bool[] _steps = new bool[4];

    const float SNAP_DURATION = 0.2f;
    bool _snapping;
    float _snapT;
    float _sy0, _sp0, _sy1, _sp1;

    // ── Spine Screw Simulation (embedded in 3D quadrant) ──
    [Header("── Spine Screw Simulation ──")]
    [Tooltip("Optional: assign an existing SpineScrewSimulation. If empty, one will be auto-created on the same GameObject.")]
    public SpineScrewSimulation spineSimulation;
    [Tooltip("Enable/disable the screw simulation in the 3D view.")]
    public bool enableSpineSim = true;
    bool _spineSimActive;
    bool _spineSimInitialized;

    float UIScale => Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) / 1080f, 0.8f, 1.6f);
    int S(int v) => Mathf.RoundToInt(v * UIScale);

    void Start()
    {
        if (volumeTarget == null)
        {
            Debug.LogError("[FourView] Assign volumeTarget.");
            enabled = false;
            return;
        }

        if (quadXRay == null || quadXRay.Length != 4) quadXRay = new[] { true, true, true, false };

        _px = new Texture2D(1, 1, TextureFormat.ARGB32, false);
        _px.SetPixel(0, 0, Color.white);
        _px.Apply();

        for (int i = 0; i < 4; i++) _quadVP[i] = _defaultVP[i];

        CleanupManagedCameras();
        DisableMainCam();
        _isSRP = GraphicsSettings.currentRenderPipeline != null;
        ResolveXRayShader();

        Bounds b = GetBounds(volumeTarget);
        Vector3 c = b.center;
        float ms = Mathf.Max(b.size.x, b.size.y, b.size.z);

        _baseCams[0] = MakeOrtho("T1", c + Vector3.up * ms, Quaternion.Euler(90, 0, 0), _quadVP[0], ms, 0, 0f);
        _baseCams[1] = MakeOrtho("T2", c + Vector3.left * ms, Quaternion.Euler(0, 90, 0), _quadVP[1], ms, 1, 1f);
        _baseCams[2] = MakeOrtho("PEV", c + Vector3.forward * ms, Quaternion.Euler(0, 180, 0), _quadVP[2], ms, 2, 2f);
        _cam3D = MakePersp("3DV", c, _quadVP[3], ms, 3f);
        _baseCams[3] = _cam3D;

        _pivot = c;
        _orbitDist = ms * 2f;
        _minOrbitDist = Mathf.Max(0.2f, ms * 0.2f);
        _maxOrbitDist = Mathf.Max(_minOrbitDist + 0.1f, ms * 6f);

        _cam3D.fieldOfView = perspectiveFov;
        OrbitApply();
        ApplyXRayToQuadrants();
        ApplyResliceView();

        // ── Init Spine Screw Simulation in 3D quadrant ──
        InitSpineSimulation();
    }

    void Update()
    {
        _elapsedTime += Time.deltaTime;

        // ── Sync spine sim viewport rect every frame (handles resize / maximize) ──
        UpdateSpineSimViewport();

        if (_snapping)
        {
            _snapT += Time.deltaTime / SNAP_DURATION;
            if (_snapT >= 1f) { _snapT = 1f; _snapping = false; }
            float e = 1f - Mathf.Pow(1f - _snapT, 3f);
            _yaw = Mathf.LerpAngle(_sy0, _sy1, e);
            _pitch = Mathf.Lerp(_sp0, _sp1, e);
            OrbitApply();
        }

        if (_replaying)
        {
            TickReplay();
            return;
        }

        if (_cam3D == null) return;

        Vector2 mp = MousePos();
        bool in3D = Is3DQuadActive() && In3DArea(mp);

        if (!MouseHeld(0))
        {
            _sliderDrag = false;
            _crosshairDrag = false;
            _crosshairDragQuad = -1;
        }

        HandleCrosshairDrag(mp);
        HandleResliceScroll(mp);
        if (in3D) Handle3DControls(mp);
        HandleKeyboardShortcuts();
        HandleToolControl();
        UpdateTrainingState();
        UpdateCollisionWarning();

        if (_recording) RecordFrame();
    }

    void HandleResliceScroll(Vector2 mp)
    {
        if (!enableResliceScroll) return;
        int q = QuadFromMouse(mp);
        if (q != resliceQuadrant || q < 0 || q > 2) return;

        float scroll = MouseScrollY();
        if (Mathf.Abs(scroll) < 0.001f) return;

        Camera rc = _baseCams[q];
        if (rc == null) return;

        float step = Mathf.Sign(scroll) * 0.02f * Mathf.Max(0.05f, rc.orthographicSize);
        rc.transform.position += rc.transform.forward * step;
        if (_overlayCams[q] != null)
            _overlayCams[q].transform.position = rc.transform.position;
    }

    void HandleCrosshairDrag(Vector2 mousePos)
    {
        int q = QuadFromMouse(mousePos);
        if (q < 0 || q > 2) return;

        Rect pr = PixelRectFromViewport(_quadVP[q]);
        Vector2 guiMouse = new Vector2(mousePos.x, Screen.height - mousePos.y);
        bool inside = pr.Contains(guiMouse);

        if (!_crosshairDrag && inside && MouseDown(0) && !SliderRect().Contains(guiMouse))
        {
            _crosshairDrag = true;
            _crosshairDragQuad = q;
        }

        if (_crosshairDrag && _crosshairDragQuad == q && MouseHeld(0))
        {
            float nx = Mathf.Clamp01((guiMouse.x - pr.x) / Mathf.Max(1f, pr.width));
            float nyTop = Mathf.Clamp01((guiMouse.y - pr.y) / Mathf.Max(1f, pr.height));

            if (q == 0)
            {
                _crosshair.x = nx;
                _crosshair.z = 1f - nyTop;
            }
            else if (q == 1)
            {
                _crosshair.z = nx;
                _crosshair.y = 1f - nyTop;
            }
            else // q==2
            {
                _crosshair.x = nx;
                _crosshair.y = 1f - nyTop;
            }
        }
    }

    void Handle3DControls(Vector2 mp)
    {
        bool lDown = MouseDown(0);
        bool lUp = MouseUp(0);
        bool lHeld = MouseHeld(0);
        bool rDown = MouseDown(1);
        bool rUp = MouseUp(1);
        bool rHeld = MouseHeld(1);

        bool inSlider = SliderRect().Contains(new Vector2(mp.x, Screen.height - mp.y));

        if (!_crosshairDrag && !inSlider && lDown) { _orbitDrag = true; _lastMouse = mp; }
        if (lUp) _orbitDrag = false;

        if (_orbitDrag && lHeld)
        {
            Vector2 d = mp - _lastMouse;
            float orbitSpeed = 0.35f * UIScale;
            _yaw += d.x * orbitSpeed;
            _pitch = Mathf.Clamp(_pitch - d.y * orbitSpeed, -89f, 89f);
            _lastMouse = mp;
            OrbitApply();
        }

        if (rDown) { _panDrag = true; _lastMouse = mp; }
        if (rUp) _panDrag = false;

        if (_panDrag && rHeld)
        {
            Vector2 d = mp - _lastMouse;
            float s = _orbitDist * 0.0018f;
            _pivot -= _cam3D.transform.right * d.x * s;
            _pivot -= _cam3D.transform.up * d.y * s;
            _lastMouse = mp;
            OrbitApply();
        }

        float scroll = MouseScrollY();
        if (Mathf.Abs(scroll) > 0.001f)
        {
            _orbitDist = Mathf.Clamp(_orbitDist * (1f - scroll * zoomSpeed * 0.05f), _minOrbitDist, _maxOrbitDist);
            OrbitApply();
        }

        if (planningMode && lDown && !_crosshairDrag)
            TryPickPoint(mp);
    }

    void TryPickPoint(Vector2 mousePos)
    {
        if (_cam3D == null) return;
        Ray ray = _cam3D.ScreenPointToRay(mousePos);
        Bounds b = GetBounds(volumeTarget);
        float enter;
        if (!b.IntersectRay(ray, out enter)) return;
        Vector3 hit = ray.GetPoint(enter);

        if (_setEntryNext)
        {
            _trajectory.entry = hit;
            _trajectory.hasEntry = true;
            _setEntryNext = false;
            return;
        }

        if (_setTargetNext)
        {
            _trajectory.target = hit;
            _trajectory.hasTarget = true;
            _setTargetNext = false;
            return;
        }

        if (_measurePickA)
        {
            _measure.a = hit;
            _measure.valid = false;
            _measurePickA = false;
        }
        else if (KeyHeld(KeyCode.LeftShift) || KeyHeld(KeyCode.RightShift))
        {
            _measure.b = hit;
            _measure.valid = true;
        }
        else
        {
            _crosshair = WorldToNormalized(hit);
        }
    }

    void HandleToolControl()
    {
        if (toolTransform == null || !_trajectory.hasEntry || !_trajectory.hasTarget) return;

        Vector3 pathDir = _trajectory.Direction;

        if (alignmentAssist)
        {
            float ang = Vector3.Angle(toolTransform.forward, pathDir);
            if (ang <= alignmentSnapAngleDeg)
            {
                Quaternion targetRot = Quaternion.LookRotation(pathDir, Vector3.up);
                toolTransform.rotation = Quaternion.Slerp(toolTransform.rotation, targetRot, Time.deltaTime * 8f);
            }
        }

        if (KeyHeld(KeyCode.UpArrow))
            toolTransform.position += pathDir * insertionSpeed * Time.deltaTime;
        if (KeyHeld(KeyCode.DownArrow))
            toolTransform.position -= pathDir * insertionSpeed * Time.deltaTime;

        _insertedDistanceMm = Vector3.Dot(toolTransform.position - _trajectory.entry, pathDir) * 1000f;
    }

    void UpdateTrainingState()
    {
        _steps[0] = _trajectory.hasEntry;
        _steps[1] = _trajectory.hasEntry && _trajectory.hasTarget;

        bool aligned = false;
        if (toolTransform != null && _trajectory.hasEntry && _trajectory.hasTarget)
            aligned = Vector3.Angle(toolTransform.forward, _trajectory.Direction) < alignmentSnapAngleDeg;

        _steps[2] = aligned;

        float targetDepth = _trajectory.LengthMm;
        _steps[3] = _insertedDistanceMm >= targetDepth * 0.9f;
    }

    void UpdateCollisionWarning()
    {
        _collisionWarning = false;
        _collisionMessage = "";

        if (toolTransform == null) return;

        if (Physics.CheckSphere(toolTransform.position, toolCollisionRadius, criticalStructureMask))
        {
            _collisionWarning = true;
            _collisionMessage = "WARNING: Critical Structure";
        }
    }

    void HandleKeyboardShortcuts()
    {
        if (KeyPressed(KeyCode.E)) _setEntryNext = true;
        if (KeyPressed(KeyCode.T)) _setTargetNext = true;
        if (KeyPressed(KeyCode.G)) _measurePickA = true;

        if (KeyPressed(KeyCode.X))
        {
            Vector2 mp = MousePos();
            if (IsMouseInsideScreen(mp))
            {
                int q = Mathf.Clamp(QuadFromMouse(mp), 0, 3);
                quadXRay[q] = !quadXRay[q];
                ApplyXRayToQuadrants();
            }
        }

        // Unity-like axis snaps in 3D view
        if (KeyPressed(KeyCode.Alpha7)) SnapTo(0f, -89f);     // Top
        if (KeyPressed(KeyCode.Alpha1)) SnapTo(0f, 0f);       // Front
        if (KeyPressed(KeyCode.Alpha3)) SnapTo(90f, 0f);      // Right

        if (KeyPressed(KeyCode.M)) ToggleMaximize(3);

        if (KeyPressed(KeyCode.C))
        {
            Vector3 world = NormalizedToWorld(_crosshair);
            _markers.Add(new Marker { world = world, label = "Marker " + (_markers.Count + 1), color = Color.yellow });
        }

        if (KeyPressed(KeyCode.R))
        {
            _recording = !_recording;
            if (_recording) { _replaying = false; _replay.Clear(); }
        }
        if (KeyPressed(KeyCode.P) && _replay.Count > 1)
        {
            _replaying = !_replaying;
            _recording = false;
            _replayT = 0f;
        }

        if (KeyPressed(KeyCode.F8)) CaptureScreenshot();

        if (KeyPressed(KeyCode.LeftBracket))
        {
            resliceQuadrant = Mathf.Clamp(resliceQuadrant - 1, 0, 2);
            ApplyResliceView();
        }
        if (KeyPressed(KeyCode.RightBracket))
        {
            resliceQuadrant = Mathf.Clamp(resliceQuadrant + 1, 0, 2);
            ApplyResliceView();
        }

        if (KeyPressed(KeyCode.F)) SnapTo(0f, 0f);
        if (KeyPressed(KeyCode.L)) SnapTo(90f, 0f);
        if (KeyPressed(KeyCode.K)) SnapTo(180f, 0f);
        if (KeyPressed(KeyCode.U)) SnapTo(0f, -89f);
    }

    void RecordFrame()
    {
        if (toolTransform == null) return;
        _replay.Add(new ReplayFrame
        {
            t = _elapsedTime,
            toolPos = toolTransform.position,
            toolRot = toolTransform.rotation
        });

        if (_replay.Count > Mathf.Max(100, maxReplayFrames))
            _replay.RemoveAt(0);
    }

    void TickReplay()
    {
        if (_replay.Count < 2 || toolTransform == null) return;
        _replayT += Time.deltaTime;

        ReplayFrame start = _replay[0];
        ReplayFrame end = _replay[_replay.Count - 1];
        float duration = Mathf.Max(0.01f, end.t - start.t);
        float rt = Mathf.Repeat(_replayT, duration) + start.t;

        int hi = 1;
        while (hi < _replay.Count && _replay[hi].t < rt) hi++;
        int lo = Mathf.Max(0, hi - 1);
        hi = Mathf.Min(_replay.Count - 1, hi);

        ReplayFrame a = _replay[lo];
        ReplayFrame b = _replay[hi];
        float d = Mathf.Max(0.0001f, b.t - a.t);
        float t = Mathf.Clamp01((rt - a.t) / d);

        toolTransform.position = Vector3.Lerp(a.toolPos, b.toolPos, t);
        toolTransform.rotation = Quaternion.Slerp(a.toolRot, b.toolRot, t);
    }

    void OnGUI()
    {
        EventType t = Event.current.type;
        if (t != EventType.Layout && t != EventType.Repaint && t != EventType.MouseDown && t != EventType.MouseUp && t != EventType.MouseDrag)
            return;

        BuildStyles();

        DrawLayout();
        DrawCrosshairs();
        DrawTrajectory();
        DrawZoomSlider();
        DrawViewAxisButtons();
        DrawReslicePanel();
        DrawTrainingPanel();
        DrawMetricsPanel();
        DrawWarningsPanel();
        DrawSpineSimToggle();
    }

    void DrawViewAxisButtons()
    {
        int x = _maxQuad == 3 ? S(12) : Screen.width / 2 + S(12);
        int y = Screen.height - S(96);
        int w = S(34);
        int h = S(24);

        Box(new Rect(x - S(4), y - S(4), w * 3 + S(12), h + S(8)), new Color(0f, 0f, 0f, 0.35f));
        if (GUI.Button(new Rect(x, y, w, h), "X", _sBtn)) SnapTo(90f, 0f);
        if (GUI.Button(new Rect(x + w + S(4), y, w, h), "Y", _sBtn)) SnapTo(0f, -89f);
        if (GUI.Button(new Rect(x + (w + S(4)) * 2, y, w, h), "Z", _sBtn)) SnapTo(0f, 0f);
    }

    void DrawReslicePanel()
    {
        int x = Screen.width - S(240);
        int y = Screen.height - S(96);
        int w = S(228);
        int h = S(84);
        Box(new Rect(x, y, w, h), new Color(0f, 0f, 0f, 0.35f));
        GUI.Label(new Rect(x + S(8), y + S(6), w - S(16), S(20)), "Reslice Quadrant", _sTitle);
        GUI.Label(new Rect(x + S(8), y + S(30), w - S(16), S(18)), $"Q{resliceQuadrant + 1}: {_quadLabels[resliceQuadrant]}", _sValue);
        if (GUI.Button(new Rect(x + S(8), y + S(52), S(28), S(22)), "<", _sBtn))
        {
            resliceQuadrant = Mathf.Clamp(resliceQuadrant - 1, 0, 2);
            ApplyResliceView();
        }
        if (GUI.Button(new Rect(x + S(42), y + S(52), S(28), S(22)), ">", _sBtn))
        {
            resliceQuadrant = Mathf.Clamp(resliceQuadrant + 1, 0, 2);
            ApplyResliceView();
        }
    }

    void DrawLayout()
    {
        if (_maxQuad == -1)
        {
            Box(new Rect(0, Screen.height / 2f, Screen.width, 1), new Color(0.12f, 0.15f, 0.2f));
            Box(new Rect(Screen.width / 2f, 0, 1, Screen.height), new Color(0.12f, 0.15f, 0.2f));
            for (int i = 0; i < 4; i++) DrawLabel(i);
        }
        else
        {
            DrawLabel(_maxQuad);
        }

        DrawMaxButtons();
    }

    void DrawLabel(int i)
    {
        Rect pr = PixelRectFromViewport(_quadVP[i]);
        int x = Mathf.RoundToInt(pr.x) + S(8);
        int y = Mathf.RoundToInt(pr.y) + S(8);
        int h = S(24);
        _sLabel.fontSize = S(11);
        int w = Mathf.CeilToInt(_sLabel.CalcSize(new GUIContent(_quadLabels[i])).x) + S(18);

        Box(new Rect(x, y, w, h), new Color(0.04f, 0.06f, 0.1f, 0.92f));
        Box(new Rect(x, y, S(2), h), quadXRay[i] ? new Color(0.25f, 0.52f, 0.88f) : new Color(0f, 0.78f, 0.68f));
        GUI.Label(new Rect(x + S(6), y, w - S(6), h), _quadLabels[i], _sLabel);
    }

    void DrawCrosshairs()
    {
        for (int q = 0; q < 3; q++)
        {
            if (_maxQuad != -1 && _maxQuad != q) continue;
            Rect pr = PixelRectFromViewport(_quadVP[q]);

            float x, y;
            if (q == 0)
            {
                x = pr.x + pr.width * _crosshair.x;
                y = pr.y + pr.height * (1f - _crosshair.z);
            }
            else if (q == 1)
            {
                x = pr.x + pr.width * _crosshair.z;
                y = pr.y + pr.height * (1f - _crosshair.y);
            }
            else
            {
                x = pr.x + pr.width * _crosshair.x;
                y = pr.y + pr.height * (1f - _crosshair.y);
            }

            Box(new Rect(pr.x, y, pr.width, 1), new Color(1f, 0.2f, 0.2f, 0.8f));
            Box(new Rect(x, pr.y, 1, pr.height), new Color(1f, 0.2f, 0.2f, 0.8f));
            Box(new Rect(x - 2, y - 2, 4, 4), new Color(1f, 0.9f, 0.2f, 1f));
        }
    }

    void DrawTrajectory()
    {
        if (!_trajectory.hasEntry || !_trajectory.hasTarget) return;

        Vector3 ep = _cam3D != null ? _cam3D.WorldToScreenPoint(_trajectory.entry) : Vector3.zero;
        Vector3 tp = _cam3D != null ? _cam3D.WorldToScreenPoint(_trajectory.target) : Vector3.zero;
        if (ep.z > 0f && tp.z > 0f)
        {
            Vector2 a = new Vector2(ep.x, Screen.height - ep.y);
            Vector2 b = new Vector2(tp.x, Screen.height - tp.y);
            DrawLine(a, b, _trajectory.color, 2f);
            Vector2 dir = (b - a).normalized;
            Vector2 left = new Vector2(-dir.y, dir.x);
            DrawLine(b, b - dir * 12f + left * 5f, _trajectory.color, 2f);
            DrawLine(b, b - dir * 12f - left * 5f, _trajectory.color, 2f);
        }
    }

    void DrawZoomSlider()
    {
        if (_maxQuad != -1 && _maxQuad != 3) return;

        Rect r = SliderRect();
        int btnH = S(24);
        int gap = S(4);
        int thumbH = S(14);

        Box(r, new Color(0.05f, 0.07f, 0.1f, 0.9f));

        Rect plus = new Rect(r.x, r.y, r.width, btnH);
        Rect minus = new Rect(r.x, r.yMax - btnH, r.width, btnH);
        Rect track = new Rect(r.x, plus.yMax + gap, r.width, r.height - btnH * 2 - gap * 2);

        float t = Mathf.InverseLerp(_minOrbitDist, _maxOrbitDist, _orbitDist);
        float ty = Mathf.Lerp(track.y, track.yMax - thumbH, t);
        Rect thumb = new Rect(r.x + S(3), ty, r.width - S(6), thumbH);

        if (GUI.Button(plus, "+", _sBtn)) { _orbitDist = Mathf.Max(_minOrbitDist, _orbitDist - (_maxOrbitDist - _minOrbitDist) * 0.03f); OrbitApply(); }
        if (GUI.Button(minus, "-", _sBtn)) { _orbitDist = Mathf.Min(_maxOrbitDist, _orbitDist + (_maxOrbitDist - _minOrbitDist) * 0.03f); OrbitApply(); }

        Box(thumb, new Color(0f, 0.8f, 0.7f));

        Event e = Event.current;
        if (e.type == EventType.MouseDown && track.Contains(e.mousePosition))
        {
            _sliderDrag = true;
            _sliderOffsetY = e.mousePosition.y - (thumb.y + thumb.height * 0.5f);
            e.Use();
        }
        if (e.type == EventType.MouseUp) _sliderDrag = false;
        if (_sliderDrag && e.type == EventType.MouseDrag)
        {
            float usable = Mathf.Max(10f, track.height - thumbH);
            float p = Mathf.Clamp01((e.mousePosition.y - _sliderOffsetY - track.y - thumbH * 0.5f) / usable);
            _orbitDist = Mathf.Lerp(_minOrbitDist, _maxOrbitDist, p);
            OrbitApply();
            e.Use();
        }
    }

    void DrawTrainingPanel()
    {
        int x = S(10), y = S(10), w = S(360), h = S(170);
        Box(new Rect(x, y, w, h), new Color(0f, 0f, 0f, 0.45f));
        GUI.Label(new Rect(x + S(8), y + S(6), w - S(16), S(20)), "Step Training", _sTitle);

        GUI.Label(new Rect(x + S(8), y + S(30), w - S(16), S(18)), (_steps[0] ? "[✓]" : "[ ]") + " Step 1 - Select entry", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(50), w - S(16), S(18)), (_steps[1] ? "[✓]" : "[ ]") + " Step 2 - Select target", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(70), w - S(16), S(18)), (_steps[2] ? "[✓]" : "[ ]") + " Step 3 - Align tool", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(90), w - S(16), S(18)), (_steps[3] ? "[✓]" : "[ ]") + " Step 4 - Insert probe", _sValue);

        GUI.Label(new Rect(x + S(8), y + S(118), w - S(16), S(18)), "E/T: pick entry/target, Shift+Click: measure", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(138), w - S(16), S(18)), "R/P: record/replay  C: marker  F8: screenshot", _sValue);
    }

    void DrawMetricsPanel()
    {
        int x = Screen.width - S(390), y = S(10), w = S(380), h = S(220);
        Box(new Rect(x, y, w, h), new Color(0f, 0f, 0f, 0.45f));
        GUI.Label(new Rect(x + S(8), y + S(6), w - S(16), S(20)), "Trajectory & Scoring", _sTitle);

        float length = _trajectory.LengthMm;
        float depthRemaining = Mathf.Max(0f, length - Mathf.Max(0f, _insertedDistanceMm));

        float angleToVertical = _trajectory.hasEntry && _trajectory.hasTarget
            ? Vector3.Angle(_trajectory.Direction, Vector3.up)
            : 0f;

        float angleToSurface = 0f;
        if (_trajectory.hasEntry && _trajectory.hasTarget)
        {
            Vector3 radial = (_trajectory.entry - GetBounds(volumeTarget).center).normalized;
            angleToSurface = Vector3.Angle(_trajectory.Direction, radial);
        }

        float toolAngleError = 0f;
        float toolPosErrorMm = 0f;
        if (toolTransform != null && _trajectory.hasEntry && _trajectory.hasTarget)
        {
            toolAngleError = Vector3.Angle(toolTransform.forward, _trajectory.Direction);
            toolPosErrorMm = DistancePointToLine(toolTransform.position, _trajectory.entry, _trajectory.target) * 1000f;
        }

        GUI.Label(new Rect(x + S(8), y + S(32), w - S(16), S(18)), $"Length: {length:0.0} mm", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(52), w - S(16), S(18)), $"Depth: {Mathf.Max(0f, _insertedDistanceMm):0.0} / {length:0.0} mm", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(72), w - S(16), S(18)), $"Target Distance: {depthRemaining:0.0} mm", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(92), w - S(16), S(18)), $"Angle vs vertical: {angleToVertical:0.0}°", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(112), w - S(16), S(18)), $"Angle vs surface normal: {angleToSurface:0.0}°", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(132), w - S(16), S(18)), $"Angle Error: {toolAngleError:0.0}°", _sValue);
        GUI.Label(new Rect(x + S(8), y + S(152), w - S(16), S(18)), $"Position Error: {toolPosErrorMm:0.0} mm", _sValue);

        if (_measure != null && _measure.valid)
            GUI.Label(new Rect(x + S(8), y + S(172), w - S(16), S(18)), $"Measurement: {_measure.DistanceMm:0.0} mm", _sValue);
        else
            GUI.Label(new Rect(x + S(8), y + S(172), w - S(16), S(18)), "Measurement: (set A with G, set B with Shift+Click)", _sValue);

        GUI.Label(new Rect(x + S(8), y + S(192), w - S(16), S(18)), $"Accuracy: {ComputeAccuracy(toolAngleError, depthRemaining):0.0}%   Time: {_elapsedTime:0.0}s", _sValue);
    }

    void DrawWarningsPanel()
    {
        int x = S(10), y = Screen.height - S(98), w = S(460), h = S(88);
        Box(new Rect(x, y, w, h), new Color(0f, 0f, 0f, 0.45f));
        GUI.Label(new Rect(x + S(8), y + S(6), w - S(16), S(20)), "Warnings / Annotation", _sTitle);

        string msg = _collisionWarning ? _collisionMessage : "No critical collision";
        GUI.color = _collisionWarning ? new Color(1f, 0.35f, 0.35f) : Color.white;
        GUI.Label(new Rect(x + S(8), y + S(30), w - S(16), S(20)), msg, _sValue);
        GUI.color = Color.white;

        GUI.Label(new Rect(x + S(8), y + S(52), w - S(16), S(20)), $"Markers: {_markers.Count} (last: {(_markers.Count > 0 ? _markers[_markers.Count - 1].label : "none")})", _sValue);
    }

    float ComputeAccuracy(float angleErrDeg, float depthRemainingMm)
    {
        float a = Mathf.Clamp01(1f - angleErrDeg / 30f);
        float d = Mathf.Clamp01(1f - depthRemainingMm / Mathf.Max(10f, _trajectory.LengthMm));
        float t = Mathf.Clamp01(1f - _elapsedTime / 300f);
        return (a * 0.4f + d * 0.4f + t * 0.2f) * 100f;
    }

    void DrawMaxButtons()
    {
        int bw = S(34), bh = S(26), pad = S(8);
        for (int i = 0; i < 4; i++)
        {
            if (_maxQuad != -1 && _maxQuad != i) continue;
            Rect pr = PixelRectFromViewport(_quadVP[i]);
            Rect btn = new Rect(pr.xMax - bw - pad, pr.yMax - bh - pad, bw, bh);
            bool hot = btn.Contains(Event.current.mousePosition);
            Box(btn, hot ? new Color(0f, 0.8f, 0.7f, 0.22f) : new Color(0.08f, 0.1f, 0.14f, 0.9f));
            GUI.Label(btn, _maxQuad == i ? "⧉" : "□", _sLabel);
            if (GUI.Button(btn, GUIContent.none, GUIStyle.none)) ToggleMaximize(i);
        }
    }

    void DrawLine(Vector2 a, Vector2 b, Color c, float thickness)
    {
        Matrix4x4 matrix = GUI.matrix;
        Color old = GUI.color;

        Vector2 d = b - a;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        float length = d.magnitude;

        GUIUtility.RotateAroundPivot(angle, a);
        GUI.color = c;
        GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, length, thickness), _px);
        GUI.color = old;
        GUI.matrix = matrix;
    }

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _sLabel = new GUIStyle(GUI.skin.label);
        _sLabel.fontStyle = FontStyle.Bold;
        _sLabel.alignment = TextAnchor.MiddleLeft;
        _sLabel.normal.textColor = new Color(0.82f, 0.86f, 0.92f);

        _sTitle = new GUIStyle(GUI.skin.label);
        _sTitle.fontStyle = FontStyle.Bold;
        _sTitle.normal.textColor = new Color(0f, 0.84f, 0.72f);

        _sValue = new GUIStyle(GUI.skin.label);
        _sValue.normal.textColor = new Color(0.78f, 0.82f, 0.88f);

        _sBtn = new GUIStyle(GUI.skin.button);
        _sBtn.fontStyle = FontStyle.Bold;
    }

    void ResolveXRayShader()
    {
        _resolvedXRayShader = xRayShader;
        if (_resolvedXRayShader == null)
        {
            for (int i = 0; i < xRayShaderCandidates.Length; i++)
            {
                _resolvedXRayShader = Shader.Find(xRayShaderCandidates[i]);
                if (_resolvedXRayShader != null) break;
            }
        }

        _xRayAvailable = _resolvedXRayShader != null;
        if (!_xRayAvailable)
        {
            Debug.LogWarning("[FourView] X-Ray shader not found. Disabled.");
            for (int i = 0; i < quadXRay.Length; i++) quadXRay[i] = false;
        }
    }

    void ApplyXRayToQuadrants()
    {
        if (_isSRP)
        {
            if (useMaterialXRayFallback)
                ApplyMaterialXRayFallback();
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            if (_baseCams[i] == null) continue;

            if (_xRayAvailable && quadXRay[i])
            {
                //_baseCams[i].SetReplacementShader(_resolvedXRayShader, "RenderType");
                _baseCams[i].SetReplacementShader(_resolvedXRayShader, "");
            }
            else
            {
                _baseCams[i].ResetReplacementShader();
            }
        }

        if (_cam3D != null)
        {
            _cam3D.ResetReplacementShader();
        }
    }

    void ApplyMaterialXRayFallback()
    {
        if (!_xRayAvailable || _resolvedXRayShader == null || volumeTarget == null) return;
        if (_xrayFallbackMat == null) _xrayFallbackMat = new Material(_resolvedXRayShader) { name = "XRay_Fallback_Runtime" };

        bool anyXRay = quadXRay[0] || quadXRay[1] || quadXRay[2];
        Renderer[] rs = volumeTarget.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;

            if (!_originalMats.ContainsKey(r)) _originalMats[r] = r.sharedMaterials;

            if (anyXRay)
            {
                Material[] mats = new Material[r.sharedMaterials.Length];
                for (int m = 0; m < mats.Length; m++) mats[m] = _xrayFallbackMat;
                r.sharedMaterials = mats;
            }
            else if (_originalMats.TryGetValue(r, out Material[] original) && original != null)
            {
                r.sharedMaterials = original;
            }
        }
    }

    void SnapTo(float yaw, float pitch)
    {
        _sy0 = _yaw; _sp0 = _pitch;
        _sy1 = yaw; _sp1 = pitch;
        _snapT = 0f; _snapping = true;
    }

    void ToggleMaximize(int q)
    {
        if (_maxQuad == q)
        {
            _maxQuad = -1;
            for (int i = 0; i < 4; i++) SetQuadViewport(i, _defaultVP[i]);
            return;
        }

        _maxQuad = q;
        Rect full = new Rect(0f, 0f, 1f, 1f);
        Rect hide = new Rect(0f, 0f, 0f, 0f);
        for (int i = 0; i < 4; i++) SetQuadViewport(i, i == q ? full : hide);
    }

    void SetQuadViewport(int q, Rect vp)
    {
        _quadVP[q] = vp;
        if (_baseCams[q] != null) _baseCams[q].rect = vp;
        if (q < 3 && _overlayCams[q] != null) _overlayCams[q].rect = vp;
    }

    bool Is3DQuadActive() => _maxQuad == -1 || _maxQuad == 3;

    bool In3DArea(Vector2 mp)
    {
        if (_maxQuad == 3) return true;
        return mp.x > Screen.width * 0.5f && mp.y < Screen.height * 0.5f;
    }

    int QuadFromMouse(Vector2 mp)
    {
        if (_maxQuad != -1) return _maxQuad;
        bool right = mp.x > Screen.width * 0.5f;
        bool bottom = mp.y < Screen.height * 0.5f;
        if (!right && !bottom) return 0;
        if (right && !bottom) return 1;
        if (!right && bottom) return 2;
        return 3;
    }

    Rect SliderRect()
    {
        Rect pr = PixelRectFromViewport(_quadVP[3]);
        int sw = S(30);
        int pad = S(12);
        int h = Mathf.RoundToInt(pr.height * 0.6f);
        return new Rect(Mathf.RoundToInt(pr.xMax) - sw - pad, Mathf.RoundToInt(pr.y + (pr.height - h) * 0.5f), sw, h);
    }

    Rect PixelRectFromViewport(Rect vp)
    {
        return new Rect(
            Mathf.Round(vp.x * Screen.width),
            Mathf.Round((1f - vp.y - vp.height) * Screen.height),
            Mathf.Round(vp.width * Screen.width),
            Mathf.Round(vp.height * Screen.height));
    }

    bool IsMouseInsideScreen(Vector2 mp)
    {
        return mp.x >= 0f && mp.y >= 0f && mp.x <= Screen.width && mp.y <= Screen.height;
    }

    void OrbitApply()
    {
        if (_cam3D == null) return;
        Quaternion r = Quaternion.Euler(_pitch, _yaw, 0f);
        _cam3D.transform.position = _pivot - (r * Vector3.forward) * _orbitDist;
        _cam3D.transform.LookAt(_pivot);
    }

    Vector3 NormalizedToWorld(Vector3 n)
    {
        Bounds b = GetBounds(volumeTarget);
        return new Vector3(
            Mathf.Lerp(b.min.x, b.max.x, n.x),
            Mathf.Lerp(b.min.y, b.max.y, n.y),
            Mathf.Lerp(b.min.z, b.max.z, n.z));
    }

    Vector3 WorldToNormalized(Vector3 world)
    {
        Bounds b = GetBounds(volumeTarget);
        Vector3 min = b.min;
        Vector3 size = b.size;
        return new Vector3(
            Mathf.Clamp01((world.x - min.x) / Mathf.Max(0.0001f, size.x)),
            Mathf.Clamp01((world.y - min.y) / Mathf.Max(0.0001f, size.y)),
            Mathf.Clamp01((world.z - min.z) / Mathf.Max(0.0001f, size.z))
        );
    }

    void ApplyResliceView()
    {
        resliceQuadrant = Mathf.Clamp(resliceQuadrant, 0, 2);
        // Make selected quadrant the primary reslice (axial/sagittal/coronal style)
        if (_baseCams[resliceQuadrant] != null)
        {
            Bounds b = GetBounds(volumeTarget);
            Vector3 c = b.center;
            float ms = Mathf.Max(b.size.x, b.size.y, b.size.z);
            if (resliceQuadrant == 0) _baseCams[0].transform.SetPositionAndRotation(c + Vector3.up * ms, Quaternion.Euler(90, 0, 0));
            if (resliceQuadrant == 1) _baseCams[1].transform.SetPositionAndRotation(c + Vector3.left * ms, Quaternion.Euler(0, 90, 0));
            if (resliceQuadrant == 2) _baseCams[2].transform.SetPositionAndRotation(c + Vector3.forward * ms, Quaternion.Euler(0, 180, 0));
        }
    }

    float DistancePointToLine(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Vector3.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude);
        Vector3 proj = a + Mathf.Clamp01(t) * ab;
        return Vector3.Distance(p, proj);
    }

    void CaptureScreenshot()
    {
        string path = Path.Combine(Application.persistentDataPath, "fourview_report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log("[FourView] Screenshot/Report image saved: " + path);
    }

    void Box(Rect rect, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(rect, _px);
        GUI.color = Color.white;
    }

    Vector2 MousePos()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) return Mouse.current.position.ReadValue();
#endif
        return Input.mousePosition;
    }

    bool MouseDown(int button)
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            if (button == 0) return Mouse.current.leftButton.wasPressedThisFrame;
            if (button == 1) return Mouse.current.rightButton.wasPressedThisFrame;
        }
#endif
        return Input.GetMouseButtonDown(button);
    }

    bool MouseUp(int button)
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            if (button == 0) return Mouse.current.leftButton.wasReleasedThisFrame;
            if (button == 1) return Mouse.current.rightButton.wasReleasedThisFrame;
        }
#endif
        return Input.GetMouseButtonUp(button);
    }

    bool MouseHeld(int button)
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            if (button == 0) return Mouse.current.leftButton.isPressed;
            if (button == 1) return Mouse.current.rightButton.isPressed;
        }
#endif
        return Input.GetMouseButton(button);
    }

    float MouseScrollY()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) return Mouse.current.scroll.ReadValue().y * 0.1f;
#endif
        return Input.mouseScrollDelta.y;
    }

    bool KeyPressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            if (key == KeyCode.E && Keyboard.current.eKey.wasPressedThisFrame) return true;
            if (key == KeyCode.T && Keyboard.current.tKey.wasPressedThisFrame) return true;
            if (key == KeyCode.G && Keyboard.current.gKey.wasPressedThisFrame) return true;
            if (key == KeyCode.X && Keyboard.current.xKey.wasPressedThisFrame) return true;
            if (key == KeyCode.M && Keyboard.current.mKey.wasPressedThisFrame) return true;
            if (key == KeyCode.C && Keyboard.current.cKey.wasPressedThisFrame) return true;
            if (key == KeyCode.R && Keyboard.current.rKey.wasPressedThisFrame) return true;
            if (key == KeyCode.P && Keyboard.current.pKey.wasPressedThisFrame) return true;
            if (key == KeyCode.F && Keyboard.current.fKey.wasPressedThisFrame) return true;
            if (key == KeyCode.L && Keyboard.current.lKey.wasPressedThisFrame) return true;
            if (key == KeyCode.K && Keyboard.current.kKey.wasPressedThisFrame) return true;
            if (key == KeyCode.U && Keyboard.current.uKey.wasPressedThisFrame) return true;
            if (key == KeyCode.F8 && Keyboard.current.f8Key.wasPressedThisFrame) return true;
        }
#endif
        return Input.GetKeyDown(key);
    }

    bool KeyHeld(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            if (key == KeyCode.LeftShift) return Keyboard.current.leftShiftKey.isPressed;
            if (key == KeyCode.RightShift) return Keyboard.current.rightShiftKey.isPressed;
            if (key == KeyCode.UpArrow) return Keyboard.current.upArrowKey.isPressed;
            if (key == KeyCode.DownArrow) return Keyboard.current.downArrowKey.isPressed;
        }
#endif
        return Input.GetKey(key);
    }

    void DisableMainCam()
    {
        if (Camera.main != null) Camera.main.gameObject.SetActive(false);
    }

    void CleanupManagedCameras()
    {
        string[] names = { "T1_Base", "T2_Base", "PEV_Base", "3DV_Cam", "T1_Overlay", "T2_Overlay", "PEV_Overlay" };
        for (int i = 0; i < names.Length; i++)
        {
            GameObject go = GameObject.Find(names[i]);
            if (go != null)
            {
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
        }
    }

    Camera MakeOrtho(string id, Vector3 pos, Quaternion rot, Rect vp, float size, int idx, float depth)
    {
        Camera bc = new GameObject(id + "_Base").AddComponent<Camera>();
        bc.transform.SetPositionAndRotation(pos, rot);
        bc.orthographic = true;
        bc.orthographicSize = Mathf.Max(0.05f, size * 0.5f);
        bc.nearClipPlane = 0.01f;
        bc.farClipPlane = size * 4f;
        bc.clearFlags = CameraClearFlags.SolidColor;
        bc.backgroundColor = Color.black;
        bc.rect = vp;
        bc.cullingMask = LayerMask.GetMask("Default", "RodOverlay");
        bc.depth = depth;

        Camera oc = new GameObject(id + "_Overlay").AddComponent<Camera>();
        oc.transform.SetPositionAndRotation(pos, rot);
        oc.orthographic = true;
        oc.orthographicSize = Mathf.Max(0.05f, size * 0.5f);
        oc.nearClipPlane = 0.01f;
        oc.farClipPlane = size * 4f;
        oc.clearFlags = CameraClearFlags.Depth;
        oc.rect = vp;
        oc.cullingMask = LayerMask.GetMask("RodOverlay");
        oc.depth = depth + 0.1f;

        _overlayCams[idx] = oc;
        return bc;
    }

    Camera MakePersp(string id, Vector3 lookAt, Rect vp, float size, float depth)
    {
        Camera cam = new GameObject(id + "_Cam").AddComponent<Camera>();
        cam.transform.position = Vector3.zero;
        cam.transform.LookAt(lookAt);
        cam.orthographic = false;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = size * 10f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.rect = vp;
        cam.cullingMask = LayerMask.GetMask("Default", "RodOverlay");
        cam.depth = depth;
        return cam;
    }

    Bounds GetBounds(Transform root)
    {
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds(root.position, Vector3.one * 10f);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    void OnDestroy()
    {
        if (_px != null)
        {
            if (Application.isPlaying) Destroy(_px);
            else DestroyImmediate(_px);
        }

        foreach (KeyValuePair<Renderer, Material[]> kv in _originalMats)
            if (kv.Key != null && kv.Value != null) kv.Key.sharedMaterials = kv.Value;
        if (_xrayFallbackMat != null)
        {
            if (Application.isPlaying) Destroy(_xrayFallbackMat);
            else DestroyImmediate(_xrayFallbackMat);
        }

        for (int i = 0; i < _baseCams.Length; i++)
            if (_baseCams[i] != null)
                if (Application.isPlaying) Destroy(_baseCams[i].gameObject); else DestroyImmediate(_baseCams[i].gameObject);

        for (int i = 0; i < _overlayCams.Length; i++)
            if (_overlayCams[i] != null)
                if (Application.isPlaying) Destroy(_overlayCams[i].gameObject); else DestroyImmediate(_overlayCams[i].gameObject);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SPINE SCREW SIMULATION — EMBEDDED IN 3D QUADRANT
    // ═══════════════════════════════════════════════════════════════

    void InitSpineSimulation()
    {
        if (!enableSpineSim) return;

        // Find or create the SpineScrewSimulation component
        if (spineSimulation == null)
            spineSimulation = GetComponent<SpineScrewSimulation>();
        if (spineSimulation == null)
            spineSimulation = gameObject.AddComponent<SpineScrewSimulation>();

        // Configure for embedded mode
        spineSimulation.embeddedMode = true;
        spineSimulation.externalCamera = _cam3D;
        spineSimulation.simulationActive = true;

        // Use volumeTarget as bone if user hasn't assigned one
        if (spineSimulation.boneRoot == null && volumeTarget != null)
            spineSimulation.boneRoot = volumeTarget.gameObject;

        // Set initial viewport
        spineSimulation.viewportPixelRect = PixelRectFromViewport(_quadVP[3]);

        // Initialize
        spineSimulation.InitEmbedded();
        _spineSimInitialized = true;
        _spineSimActive = true;
    }

    void UpdateSpineSimViewport()
    {
        if (!_spineSimInitialized || spineSimulation == null) return;

        // Keep viewport in sync (handles window resize and maximize/restore)
        spineSimulation.viewportPixelRect = PixelRectFromViewport(_quadVP[3]);
        spineSimulation.simulationActive = _spineSimActive;
    }

    void DrawSpineSimToggle()
    {
        if (!enableSpineSim || spineSimulation == null) return;

        // Only show toggle when 3D view is visible
        if (_maxQuad != -1 && _maxQuad != 3) return;

        Rect pr = PixelRectFromViewport(_quadVP[3]);
        int bw = S(140);
        int bh = S(28);
        int pad = S(8);
        float bx = pr.x + pad;
        float by = pr.yMax - bh - pad - S(30); // above the axis buttons

        Box(new Rect(bx - 2, by - 2, bw + 4, bh + 4), new Color(0f, 0f, 0f, 0.55f));

        Color btnCol = _spineSimActive ? new Color(0.15f, 0.6f, 0.4f, 0.95f) : new Color(0.4f, 0.15f, 0.15f, 0.85f);
        Box(new Rect(bx, by, bw, bh), btnCol);

        string label = _spineSimActive ? "■ Screw Sim ON" : "□ Screw Sim OFF";
        GUIStyle tgl = new GUIStyle(_sBtn);
        tgl.normal.textColor = Color.white;
        tgl.fontSize = S(11);
        tgl.alignment = TextAnchor.MiddleCenter;

        if (GUI.Button(new Rect(bx, by, bw, bh), label, tgl))
        {
            _spineSimActive = !_spineSimActive;
        }
    }
}
