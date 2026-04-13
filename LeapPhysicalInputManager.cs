using UnityEngine;
using Leap;
using Leap.PhysicalHands;

[RequireComponent(typeof(SpineScrewSimulation))]
public class LeapPhysicalInputManager : MonoBehaviour
{
    [Header("── Leap Provider ──")]
    public LeapProvider leapProvider;

    [Header("── Tools ──")]
    public GameObject drillTool;
    public Transform drillTipPoint;
    public Transform screwSpawnPoint;
    public GameObject screwDriverTool;

    [Header("── Drill Raycast ──")]
    public LayerMask boneMask = ~0;
    public float drillRayLength = 0.08f;
    public Vector3 drillLocalDir = Vector3.down;

    [Header("── Thresholds ──")]
    [Range(0.3f, 1f)] public float pinchThreshold = 0.6f;
    [Range(0f, 0.2f)] public float pinchHysteresis = 0.08f;
    [Range(0.3f, 1f)] public float grabThreshold = 0.75f;

    [Header("── Screw Placement ──")]
    public float screwSnapDistance = 0.06f;

    [Header("── Screwdriver ──")]
    public float driverAttachDistance = 0.06f;
    [Tooltip("Offset along screw normal from screw head. Increase to move driver higher above screw.")]
    public float screwDriverHeadOffset = 0.02f;
    [Tooltip("Additional Y offset in world space for screwdriver positioning.")]
    public float screwDriverExtraOffset = 0.01f;
    [Tooltip("Screwdriver follow smoothing time while attached (lower = tighter lock).")]
    [Range(0.01f, 0.2f)] public float driverFollowSmoothTime = 0.05f;
    [Tooltip("Screwdriver rotation follow speed while attached.")]
    [Range(4f, 30f)] public float driverRotationLerpSpeed = 14f;
    [Tooltip("Distance from screwdriver used to select which hand drives wrist twist.")]
    [Range(0.08f, 0.35f)] public float twistHandSelectDistance = 0.2f;

    [Header("── Twist (Smooth) ──")]
    public float twistSensitivity = 1.0f;
    public float maxTwistPerFrame = 15f;
    [Tooltip("Deadzone in degrees. Twist below this is ignored.")]
    public float twistDeadzone = 2f;
    [Tooltip("Smoothing frames for twist averaging.")]
    [Range(3, 20)] public int twistSmoothFrames = 8;
    [Tooltip("Exponential smoothing alpha for twist (lower=smoother).")]
    [Range(0.05f, 0.5f)] public float twistSmoothAlpha = 0.15f;

    [Header("── Visual Feedback ──")]
    public float hitMarkerScale = 0.025f;
    public float aimRingScale = 0.06f;
    public bool showContactLine = true;

    // ── Phase ──
    public enum Phase { Drilling, Placing, Driving, Done }
    [HideInInspector] public Phase currentPhase = Phase.Drilling;
    [HideInInspector] public bool doneDrillingRequested;

    // ── Runtime ──
    SpineScrewSimulation _sim;
    Hand _rHand, _lHand;
    bool _hasR, _hasL;
    bool _rPinch, _lPinch, _prevRP, _prevLP;
    float _rStr, _lStr;
    bool _drillTouching;
    RaycastHit _drillHit;
    Vector3 _lastDrilledPos;

    // Screw placement
    GameObject _currentScrew;
    int _placingIdx = -1;

    // Screwdriver
    bool _driverAttached;
    int _drivingIdx = -1;

    // Twist smoothing
    bool _twistActive;
    float _prevTwistAngle;
    float[] _twistBuffer;
    int _twistBufIdx;
    float _smoothedTwist;
    Vector3 _driverPosVel;

    // Visuals
    GameObject _hitMarker, _aimRing, _tipGlow;
    Material _hitMat, _ringMat, _tipMat;
    GameObject _lineObj; LineRenderer _line;
    float _pulse;

    void Awake()
    {
        _sim = GetComponent<SpineScrewSimulation>();
        _sim.externalInputActive = true;
        _sim.disableMouseInput = true;
    }

    void Start()
    {
        if (leapProvider == null) leapProvider = FindObjectOfType<LeapProvider>();
        if (leapProvider == null)
        {
            Debug.LogError("[LeapPhysicalInputManager] Missing LeapProvider. Disabling input manager.");
            enabled = false;
            return;
        }

        twistSmoothFrames = Mathf.Max(3, twistSmoothFrames);
        _twistBuffer = new float[twistSmoothFrames];

        CreateVisuals();
        if (screwDriverTool) screwDriverTool.SetActive(false);
    }

    void Update()
    {
        _pulse += Time.deltaTime;
        ReadHands();
        UpdatePinch();

        bool anyP = _rPinch || _lPinch;
        float bestS = Mathf.Max(_rStr, _lStr);

        switch (currentPhase)
        {
            case Phase.Drilling: UpdateDrilling(anyP, bestS); break;
            case Phase.Placing: UpdatePlacing(); break;
            case Phase.Driving: UpdateDriving(anyP, bestS); break;
        }

        _prevRP = _rPinch;
        _prevLP = _lPinch;
    }

    void UpdateDrilling(bool anyP, float bestS)
    {
        _sim.SetExternalPinch(anyP, bestS);

        _drillTouching = false;
        if (drillTool && drillTipPoint && drillTool.activeInHierarchy)
        {
            Vector3 tip = drillTipPoint.position;
            Vector3 dir = drillTipPoint.TransformDirection(drillLocalDir).normalized;
            _drillTouching = Physics.Raycast(tip, dir, out _drillHit, drillRayLength, boneMask);
        }

        if (_drillTouching)
        {
            _sim.SetExternalRaycast(true, _drillHit);
            ShowContact(_drillHit.point, _drillHit.normal, anyP);

            int mode = _sim.GetModeInt();
            if (mode == 0 || mode == 2)
            {
                bool justP = (_rPinch && !_prevRP) || (_lPinch && !_prevLP);

                bool continuous = false;
                if (anyP && mode == 2 && _sim.GetHoleCount() < _sim.maxHoles)
                {
                    bool valid = true;
                    for (int i = 0; i < _sim.GetHoleCount(); i++)
                    {
                        if (Vector3.Distance(_drillHit.point, _sim.GetHolePosition(i)) < _sim.minHoleSpacing * 1.5f)
                        { valid = false; break; }
                    }

                    if (valid && _lastDrilledPos != Vector3.zero &&
                        Vector3.Distance(_drillHit.point, _lastDrilledPos) < _sim.minHoleSpacing * 2f)
                        valid = false;

                    continuous = valid;
                }

                if ((justP || continuous) && _sim.StartDrillFromExternal(_drillHit.point, _drillHit.normal))
                    _lastDrilledPos = _drillHit.point;
            }
        }
        else
        {
            _sim.SetExternalRaycast(false, default);
            ShowNoContact();
        }

        bool transition = false;
        if (_sim.GetHoleCount() > 0 && _sim.GetModeInt() != 1)
        {
            bool rG = _hasR && _rHand.GrabStrength >= grabThreshold;
            bool lG = _hasL && _lHand.GrabStrength >= grabThreshold;
            if (rG && lG) transition = true;
            if (_sim.GetHoleCount() >= _sim.maxHoles) transition = true;
        }

        if (doneDrillingRequested && _sim.GetHoleCount() > 0 && _sim.GetModeInt() != 1)
        {
            transition = true;
            doneDrillingRequested = false;
        }

        if (transition)
        {
            currentPhase = Phase.Placing;
            _sim.EnterWaitForScrew();
            HideVisuals();
            SpawnNextScrew();
        }
    }

    void SpawnNextScrew()
    {
        int idx = _sim.GetNextUnplacedHoleIndex();
        if (idx < 0)
        {
            currentPhase = Phase.Driving;
            StartDriving();
            return;
        }

        _placingIdx = idx;
        if (_currentScrew) Destroy(_currentScrew);

        _currentScrew = _sim.BuildScrewObject();
        _currentScrew.name = $"PhysScrew_{idx}";

        Vector3 sp = screwSpawnPoint ? screwSpawnPoint.position : transform.position + Vector3.up * 0.3f;
        _currentScrew.transform.position = sp;

        var rb = _currentScrew.GetComponent<Rigidbody>();
        if (!rb) rb = _currentScrew.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = false;
        rb.mass = 0.1f;
        rb.drag = 5;
        rb.angularDrag = 5;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (!_currentScrew.GetComponent<Collider>())
        {
            var b = _currentScrew.AddComponent<BoxCollider>();
            b.size = new Vector3(_sim.holeRadius * 4, _sim.screwLength * 1.2f, _sim.holeRadius * 4);
            b.center = new Vector3(0, _sim.screwLength * 0.5f, 0);
        }
    }

    void UpdatePlacing()
    {
        if (!_currentScrew)
        {
            SpawnNextScrew();
            return;
        }

        Vector3 pos = _currentScrew.transform.position;
        for (int i = 0; i < _sim.GetHoleCount(); i++)
        {
            var h = _sim.holes[i];
            if (h.screwPlaced) continue;

            if (Vector3.Distance(pos, h.pos) < screwSnapDistance)
            {
                _sim.PlaceScrewAtHole(i, _currentScrew);

                // Hard-lock snapped screw: no more physics tugging/pulling back.
                FreezeSnappedScrewPhysics(_currentScrew);
                EnsureDriverAttachPoint(_currentScrew, _sim.screwLength + _sim.holeRadius * 0.5f + screwDriverHeadOffset);

                _currentScrew = null;
                SpawnNextScrew();
                return;
            }
        }
    }

    void FreezeSnappedScrewPhysics(GameObject screw)
    {
        if (!screw) return;

        var rbs = screw.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rbs.Length; i++)
        {
            var rb = rbs[i];
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.detectCollisions = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        var cols = screw.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
            cols[i].enabled = false;
    }

    Transform EnsureDriverAttachPoint(GameObject screwObj, float localUpOffset)
    {
        if (!screwObj) return null;
        Transform existing = screwObj.transform.Find("DriverAttachPoint");
        if (existing != null)
        {
            existing.localPosition = Vector3.up * localUpOffset;
            existing.localRotation = Quaternion.identity;
            return existing;
        }

        var go = new GameObject("DriverAttachPoint");
        go.transform.SetParent(screwObj.transform, false);
        go.transform.localPosition = Vector3.up * localUpOffset;
        go.transform.localRotation = Quaternion.identity;
        return go.transform;
    }

    void StartDriving()
    {
        _driverAttached = false;
        _drivingIdx = -1;
        _twistActive = false;
        _smoothedTwist = 0;
        _twistBufIdx = 0;
        for (int i = 0; i < _twistBuffer.Length; i++) _twistBuffer[i] = 0;

        if (screwDriverTool)
        {
            screwDriverTool.SetActive(true);
            var rb = screwDriverTool.GetComponent<Rigidbody>();
            if (!rb) rb = screwDriverTool.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.mass = 0.15f;
            rb.drag = 5;
            rb.angularDrag = 5;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            if (screwSpawnPoint) screwDriverTool.transform.position = screwSpawnPoint.position + Vector3.right * 0.1f;
        }

        _drivingIdx = _sim.GetNextUndrivenHoleIndex();
        if (_drivingIdx >= 0) _sim.StartDrivingScrew(_drivingIdx);
        else
        {
            currentPhase = Phase.Done;
            _sim.EnterDone();
        }
    }

    void UpdateDriving(bool anyP, float bestS)
    {
        if (!screwDriverTool) return;

        if (!_driverAttached && _drivingIdx >= 0)
        {
            Vector3 dPos = screwDriverTool.transform.position;
            var hole = _sim.holes[_drivingIdx];
            if (hole.screwObj == null) return;
            Transform attach = EnsureDriverAttachPoint(hole.screwObj, _sim.screwLength + _sim.holeRadius * 0.5f + screwDriverHeadOffset);
            Vector3 head = attach.position + hole.normal * screwDriverExtraOffset;
            float dist = Vector3.Distance(dPos, head);
            if (dist < driverAttachDistance)
            {
                _driverAttached = true;
                var rb = screwDriverTool.GetComponent<Rigidbody>();
                if (rb)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = true;
                }
                _twistActive = false;
                _smoothedTwist = 0;
                _driverPosVel = Vector3.zero;
            }
        }

        if (_driverAttached && _drivingIdx >= 0)
        {
            var h = _sim.holes[_drivingIdx];
            if (h.screwObj)
            {
                Transform attach = EnsureDriverAttachPoint(h.screwObj, _sim.screwLength + _sim.holeRadius * 0.5f + screwDriverHeadOffset);

                Vector3 axis = h.screwObj.transform.up;
                Vector3 headPos = attach.position + h.normal * screwDriverExtraOffset;

                screwDriverTool.transform.position = Vector3.SmoothDamp(
                    screwDriverTool.transform.position, headPos, ref _driverPosVel, driverFollowSmoothTime, Mathf.Infinity, Time.deltaTime);

                Vector3 perp = Vector3.Cross(axis, Vector3.up);
                if (perp.sqrMagnitude < 0.001f) perp = Vector3.Cross(axis, Vector3.forward);
                perp.Normalize();

                Quaternion targetRot = Quaternion.LookRotation(-axis, perp);
                float rotT = 1f - Mathf.Exp(-driverRotationLerpSpeed * Time.deltaTime);
                screwDriverTool.transform.rotation = Quaternion.Slerp(screwDriverTool.transform.rotation, targetRot, rotT);

                if (anyP)
                    screwDriverTool.transform.Rotate(axis, _smoothedTwist * 0.5f, Space.World);
            }

            Hand twistHand = GetTwistHand();

            if (twistHand != null && anyP)
            {
                Vector3 palmN = twistHand.PalmNormal;
                float angle = Mathf.Atan2(palmN.x, palmN.z) * Mathf.Rad2Deg;

                if (!_twistActive)
                {
                    _twistActive = true;
                    _prevTwistAngle = angle;
                    _twistBufIdx = 0;
                    for (int i = 0; i < _twistBuffer.Length; i++) _twistBuffer[i] = 0;
                    _smoothedTwist = 0;
                }
                else
                {
                    float raw = Mathf.DeltaAngle(_prevTwistAngle, angle);
                    _prevTwistAngle = angle;

                    if (Mathf.Abs(raw) < twistDeadzone) raw = 0;
                    raw = Mathf.Clamp(raw * twistSensitivity, -maxTwistPerFrame, maxTwistPerFrame);

                    _twistBuffer[_twistBufIdx % _twistBuffer.Length] = raw;
                    _twistBufIdx++;

                    float avg = 0;
                    for (int i = 0; i < _twistBuffer.Length; i++) avg += _twistBuffer[i];
                    avg /= _twistBuffer.Length;

                    _smoothedTwist = Mathf.Lerp(_smoothedTwist, avg, twistSmoothAlpha);
                    _sim.DriveActiveScrew(_smoothedTwist, bestS);
                }
            }
            else if (anyP)
            {
                // Require both pinch + valid twist hand movement to advance screw.
                // Pinch alone should not drive penetration.
                _twistActive = false;
                _smoothedTwist = Mathf.Lerp(_smoothedTwist, 0, 0.35f);
            }
            else
            {
                _twistActive = false;
                _smoothedTwist = Mathf.Lerp(_smoothedTwist, 0, 0.5f);
            }

            if (_drivingIdx >= 0 && _sim.holes[_drivingIdx].screwDriven)
            {
                _driverAttached = false;
                _twistActive = false;

                var rb = screwDriverTool.GetComponent<Rigidbody>();
                if (rb)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = false;
                }

                _drivingIdx = _sim.GetNextUndrivenHoleIndex();
                if (_drivingIdx >= 0)
                    _sim.StartDrivingScrew(_drivingIdx);
                else
                {
                    currentPhase = Phase.Done;
                    _sim.EnterDone();
                    if (screwDriverTool) screwDriverTool.SetActive(false);
                    HideVisuals();
                }
            }
        }
    }

    Hand GetTwistHand()
    {
        if (_hasR && screwDriverTool)
        {
            Vector3 p = LeapPalm(_rHand);
            if (Vector3.Distance(p, screwDriverTool.transform.position) < twistHandSelectDistance) return _rHand;
        }
        if (_hasL && screwDriverTool)
        {
            Vector3 p = LeapPalm(_lHand);
            if (Vector3.Distance(p, screwDriverTool.transform.position) < twistHandSelectDistance) return _lHand;
        }
        if (_rPinch && _hasR) return _rHand;
        if (_lPinch && _hasL) return _lHand;
        return null;
    }

    void ReadHands()
    {
        _hasR = _hasL = false;
        _rHand = _lHand = null;
        _rStr = _lStr = 0;

        if (!leapProvider) return;
        var f = leapProvider.CurrentFrame;
        if (f == null) return;

        foreach (var h in f.Hands)
        {
            if (h.IsRight)
            {
                _rHand = h;
                _hasR = true;
                _rStr = h.PinchStrength;
            }
            else
            {
                _lHand = h;
                _hasL = true;
                _lStr = h.PinchStrength;
            }
        }
    }

    void UpdatePinch()
    {
        if (_rPinch) { if (_rStr < pinchThreshold - pinchHysteresis) _rPinch = false; }
        else { if (_rStr >= pinchThreshold) _rPinch = true; }

        if (_lPinch) { if (_lStr < pinchThreshold - pinchHysteresis) _lPinch = false; }
        else { if (_lStr >= pinchThreshold) _lPinch = true; }
    }

    Vector3 LeapPalm(Hand h)
    {
        if (leapProvider) return leapProvider.transform.TransformPoint(h.PalmPosition * 0.001f);
        return h.PalmPosition * 0.001f;
    }

    public bool HasRightHand => _hasR;
    public bool HasLeftHand => _hasL;
    public float BestPinchStr => Mathf.Max(_rStr, _lStr);
    public bool AnyPinch => _rPinch || _lPinch;
    public bool DrillTouching => _drillTouching;
    public bool DriverAttached => _driverAttached;
    public int DrivingIndex => _drivingIdx;
    public float SmoothedTwist => _smoothedTwist;

    Shader Shdr() => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");

    Material TranspMat(Color c)
    {
        var m = new Material(Shdr());
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = 3100;
        m.color = c;
        return m;
    }

    void CreateVisuals()
    {
        _hitMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _hitMarker.name = "_Hit";
        _hitMarker.transform.localScale = Vector3.one * hitMarkerScale;
        Destroy(_hitMarker.GetComponent<Collider>());
        _hitMat = new Material(Shdr()) { color = Color.yellow };
        _hitMarker.GetComponent<Renderer>().material = _hitMat;
        _hitMarker.SetActive(false);

        _aimRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _aimRing.name = "_Ring";
        Destroy(_aimRing.GetComponent<Collider>());
        _ringMat = TranspMat(new Color(1, 0.85f, 0, 0.4f));
        _aimRing.GetComponent<Renderer>().material = _ringMat;
        _aimRing.SetActive(false);

        _tipGlow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _tipGlow.name = "_Tip";
        _tipGlow.transform.localScale = Vector3.one * 0.012f;
        Destroy(_tipGlow.GetComponent<Collider>());
        _tipMat = TranspMat(new Color(1, 0.4f, 0.1f, 0.6f));
        _tipGlow.GetComponent<Renderer>().material = _tipMat;
        _tipGlow.SetActive(false);

        if (showContactLine)
        {
            _lineObj = new GameObject("_Line");
            _line = _lineObj.AddComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.startWidth = 0.003f;
            _line.endWidth = 0.001f;
            _line.material = new Material(Shader.Find("Sprites/Default") ?? Shdr());
            _line.useWorldSpace = true;
            _lineObj.SetActive(false);
        }
    }

    void ShowContact(Vector3 p, Vector3 n, bool pinch)
    {
        float pl = 1 + 0.15f * Mathf.Sin(_pulse * 6);
        bool drilling = _sim.GetModeInt() == 1;
        Color c = (drilling || pinch) ? new Color(0, 1, 0.3f) : new Color(1, 0.85f, 0);

        if (_hitMarker)
        {
            _hitMarker.SetActive(true);
            _hitMarker.transform.position = p + n * 0.003f;
            _hitMarker.transform.localScale = Vector3.one * hitMarkerScale * ((drilling || pinch) ? 1.5f : 1) * pl;
            _hitMat.color = c;
        }

        if (_aimRing)
        {
            _aimRing.SetActive(true);
            _aimRing.transform.position = p + n * 0.002f;
            _aimRing.transform.up = n;
            float s = aimRingScale * pl;
            _aimRing.transform.localScale = new Vector3(s, 0.001f, s);
            Color rc = c;
            rc.a = (drilling || pinch) ? 0.6f : 0.35f;
            _ringMat.color = rc;
        }

        if (_tipGlow && drillTipPoint)
        {
            _tipGlow.SetActive(true);
            _tipGlow.transform.position = drillTipPoint.position;
            _tipGlow.transform.localScale = Vector3.one * ((drilling || pinch) ? 0.018f : 0.012f) * pl;
            Color gc = c;
            gc.a = 0.7f;
            _tipMat.color = gc;
        }

        if (_line && drillTipPoint)
        {
            _lineObj.SetActive(true);
            _line.SetPosition(0, drillTipPoint.position);
            _line.SetPosition(1, p);
            Color lc = c;
            lc.a = 0.8f;
            _line.startColor = lc;
            lc.a = 0.3f;
            _line.endColor = lc;
        }
    }

    void ShowNoContact()
    {
        if (_hitMarker) _hitMarker.SetActive(false);
        if (_aimRing) _aimRing.SetActive(false);
        if (_lineObj) _lineObj.SetActive(false);

        if (_tipGlow && drillTipPoint && drillTool && drillTool.activeInHierarchy)
        {
            _tipGlow.SetActive(true);
            _tipGlow.transform.position = drillTipPoint.position;
            _tipGlow.transform.localScale = Vector3.one * 0.01f;
            _tipMat.color = new Color(1, 0.3f, 0.2f, 0.5f);
        }
        else if (_tipGlow)
        {
            _tipGlow.SetActive(false);
        }
    }

    void HideVisuals()
    {
        if (_hitMarker) _hitMarker.SetActive(false);
        if (_aimRing) _aimRing.SetActive(false);
        if (_lineObj) _lineObj.SetActive(false);
        if (_tipGlow) _tipGlow.SetActive(false);
    }

    void OnGUI()
    {
        if (GetComponent<SimulationUI>()) return;
    }

    void OnDestroy()
    {
        if (_hitMarker) Destroy(_hitMarker);
        if (_aimRing) Destroy(_aimRing);
        if (_lineObj) Destroy(_lineObj);
        if (_tipGlow) Destroy(_tipGlow);
        if (_currentScrew) Destroy(_currentScrew);
        if (_hitMat) Destroy(_hitMat);
        if (_ringMat) Destroy(_ringMat);
        if (_tipMat) Destroy(_tipMat);
        if (_line != null && _line.material != null) Destroy(_line.material);
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (drillTipPoint && currentPhase == Phase.Drilling)
        {
            Vector3 t = drillTipPoint.position;
            Vector3 d = drillTipPoint.TransformDirection(drillLocalDir).normalized;
            Gizmos.color = _drillTouching ? Color.green : Color.yellow;
            Gizmos.DrawRay(t, d * drillRayLength);
            if (_drillTouching)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(_drillHit.point, 0.005f);
            }
        }

        if (currentPhase == Phase.Placing)
        {
            Gizmos.color = new Color(1, 1, 0, 0.3f);
            for (int i = 0; i < _sim.GetHoleCount(); i++)
            {
                if (_sim.holes[i].screwPlaced) continue;
                Gizmos.DrawWireSphere(_sim.holes[i].pos, screwSnapDistance);
            }
        }

        if (currentPhase == Phase.Driving && _drivingIdx >= 0)
        {
            Gizmos.color = new Color(0, 1, 1, 0.3f);
            Gizmos.DrawWireSphere(_sim.GetScrewHeadPosition(_drivingIdx), driverAttachDistance);
        }
    }
#endif
}
