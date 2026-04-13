using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Leap;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(AudioSource))]
public class SpineScrewSimulation : MonoBehaviour
{
    [Header("── Bone ──")]
    public GameObject boneRoot;
    public LayerMask boneLayerMask = ~0;
    public bool disableMouseInput = true;

    [Header("── Drill & Screw Size ──")]
    public float holeRadius = 0.03f;
    public float drillDepth = 0.06f;
    public int maxHoles = 10;
    public float highlightRadius = 0f;

    [Header("── Drill Physics ──")]
    public float baseDrillRate = 0.025f;
    [Range(0f, 1f)] public float boneResistance = 0.55f;
    public float resistanceCurve = 1.8f;
    [Range(0f, 1f)] public float drillFriction = 0.25f;
    public float drillRetractRate = 0f;
    public float drillVibrationAmp = 0.001f;
    [Range(0f, 1f)] public float minDrillPinchStrength = 0.65f;

    [Header("── Stability ──")]
    public float minHoleSpacing = 0.015f;

    [Header("── Screw ──")]
    public GameObject screwPrefab;
    public float screwLength = 0.20f;
    public float screwInsertSpeed = 0.15f;
    public float screwSpinSpeed = 300f;
    public float snapRadius = 0.15f;

    [Header("── Screw Physics ──")]
    [Range(0f, 1f)] public float screwResistance = 0.4f;
    public float screwResistanceCurve = 1.3f;
    [Range(0f, 1f)] public float screwFriction = 0.2f;

    [Header("── Camera ──")]
    public Transform pivotOverride;
    public float orbitSpeed = 0.35f;
    public float zoomSpeed = 3f;
    public float minDist = 0.1f;
    public float maxDist = 8f;

    [Header("── Audio ──")]
    public AudioClip drillSFX;
    public AudioClip screwSFX;
    public AudioClip doneSFX;
    public AudioClip snapSFX;

    [HideInInspector] public bool externalInputActive;

    public enum SimMode { Free, Drilling, WaitForScrew, Screwing, Done }
    [HideInInspector] public SimMode currentMode = SimMode.Free;

    public class HoleData
    {
        public Vector3 pos, normal;
        public bool screwPlaced, screwDriven;
        public GameObject outerRing, midRing, innerDisc, holeCut;
        public Material ringMat;
        public GameObject screwObj;
        public float driveProgress;
    }
    public readonly List<HoleData> holes = new List<HoleData>();

    Camera _cam;
    Transform _pivot;
    float _yaw = 10f, _pitch = 20f, _dist = 0.5f;
    GameObject _bone;
    Bounds _boneBounds;
    GameObject _needleGO, _discGO, _drillBitGO;
    Material _needleMat, _discMat, _drillBitMat;
    float _drillDepthCurrent, _drillVelocity, _currentResistanceForce;
    Vector3 _drillPos, _drillNormal;
    ParticleSystem _dustPS;
    bool _isPinchHeldDrill;
    float _doneRotateAngle, _doneRotateSpeed = 30f;
    bool _doneShowcaseActive;
    AudioSource _audio;
    Material _boneMat, _holeDarkMat;
    float _pulse;
    bool _initialized;
    int _activeDriveIdx = -1;
    float _activeDriveSpinAngle;

    bool _extPinch; float _extStr;
    bool _extHasHit; RaycastHit _extHit;

    [HideInInspector] public bool embeddedMode;
    [HideInInspector] public Camera externalCamera;
    [HideInInspector] public bool simulationActive = true;

    public void SetExternalPinch(bool h, float s) { _extPinch = h; _extStr = Mathf.Clamp01(s); }
    public void SetExternalRaycast(bool h, RaycastHit hit) { _extHasHit = h; _extHit = hit; }

    public int GetModeInt() => (int)currentMode;
    public int GetHoleCount() => holes.Count;
    public Bounds GetBoneBounds() => _boneBounds;
    public GameObject GetBone() => _bone;
    public float GetDrillDepthFraction() => drillDepth > 0 ? Mathf.Clamp01(_drillDepthCurrent / drillDepth) : 0;
    public float GetResistanceFraction() => Mathf.Clamp01(_currentResistanceForce);
    public bool IsDrillActive() => _isPinchHeldDrill && currentMode == SimMode.Drilling;
    public Vector3 GetHolePosition(int i) => (i >= 0 && i < holes.Count) ? holes[i].pos : Vector3.zero;
    public Vector3 GetHoleNormal(int i) => (i >= 0 && i < holes.Count) ? holes[i].normal : Vector3.up;

    public int GetFilledCount() { int c = 0; foreach (var h in holes) if (h.screwDriven) c++; return c; }
    public int GetPlacedCount() { int c = 0; foreach (var h in holes) if (h.screwPlaced) c++; return c; }
    public int GetNextUnplacedHoleIndex() { for (int i = 0; i < holes.Count; i++) if (!holes[i].screwPlaced) return i; return -1; }
    public int GetNextUndrivenHoleIndex() { for (int i = 0; i < holes.Count; i++) if (holes[i].screwPlaced && !holes[i].screwDriven) return i; return -1; }

    public float GetDriveProgress()
    {
        if (_activeDriveIdx < 0 || _activeDriveIdx >= holes.Count) return 0f;
        return holes[_activeDriveIdx].driveProgress;
    }

    public int GetActiveDriveIndex() => _activeDriveIdx;

    public Vector3 GetScrewHeadPosition(int idx)
    {
        if (idx < 0 || idx >= holes.Count) return Vector3.zero;
        var h = holes[idx];
        if (h.screwObj == null) return h.pos + h.normal * screwLength;
        return h.screwObj.transform.position + h.screwObj.transform.up * (screwLength + holeRadius * 0.5f);
    }

    public bool StartDrillFromExternal(Vector3 pos, Vector3 normal)
    {
        if (!float.IsFinite(pos.x + pos.y + pos.z + normal.x + normal.y + normal.z)) return false;
        if (currentMode != SimMode.Free && currentMode != SimMode.WaitForScrew) return false;
        if (holes.Count >= maxHoles) return false;
        foreach (var h in holes) if (Vector3.Distance(pos, h.pos) < minHoleSpacing) return false;

        _drillPos = pos;
        _drillNormal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
        _drillDepthCurrent = 0; _drillVelocity = 0; _currentResistanceForce = 0;
        _isPinchHeldDrill = true;
        currentMode = SimMode.Drilling;
        if (_dustPS != null)
        {
            _dustPS.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-_drillNormal));
            _dustPS.Play();
        }
        return true;
    }

    public void EnterWaitForScrew() { currentMode = SimMode.WaitForScrew; }

    public void PlaceScrewAtHole(int idx, GameObject screw)
    {
        if (idx < 0 || idx >= holes.Count || screw == null) return;
        var h = holes[idx]; h.screwPlaced = true; h.screwObj = screw; h.driveProgress = 0;
        Vector3 n = h.normal;
        screw.transform.position = h.pos;
        Vector3 r = Vector3.Cross(n, Vector3.up); if (r.sqrMagnitude < 0.001f) r = Vector3.Cross(n, Vector3.forward);
        r.Normalize(); Vector3 f = Vector3.Cross(r, n);
        screw.transform.rotation = Quaternion.LookRotation(f, n);
        if (_bone != null) screw.transform.SetParent(_bone.transform, true);
        var rb = screw.GetComponent<Rigidbody>();
        if (rb)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
        if (h.outerRing) h.outerRing.SetActive(false);
        if (h.midRing) h.midRing.SetActive(false);
        if (h.innerDisc) h.innerDisc.SetActive(false);
        OneShot(snapSFX != null ? snapSFX : doneSFX);
    }

    public bool StartDrivingScrew(int idx)
    {
        if (idx < 0 || idx >= holes.Count) return false;
        var h = holes[idx]; if (!h.screwPlaced || h.screwDriven) return false;
        _activeDriveIdx = idx; _activeDriveSpinAngle = 0;
        currentMode = SimMode.Screwing;
        return true;
    }

    public void DriveActiveScrew(float twistDelta, float pinchStr)
    {
        if (_activeDriveIdx < 0 || currentMode != SimMode.Screwing) return;
        var h = holes[_activeDriveIdx];
        if (h.screwDriven || h.screwObj == null) return;

        float safeScrewLength = Mathf.Max(0.0001f, screwLength);
        float depthFrac = Mathf.Clamp01(h.driveProgress);
        float resist = screwResistance * Mathf.Pow(depthFrac, screwResistanceCurve);
        float fricMod = 1f - screwFriction * depthFrac;

        float effectiveTwist = twistDelta * (1f - resist) * fricMod;
        _activeDriveSpinAngle += effectiveTwist;

        float absTwist = Mathf.Abs(effectiveTwist);
        bool driving = false;

        if (absTwist > 0.3f)
        {
            driving = true;
            float pf = Mathf.Clamp01(pinchStr);
            float twistRate = (absTwist / 360f) * (screwInsertSpeed / safeScrewLength) * 4f;
            float pinchRate = (screwInsertSpeed / safeScrewLength) * 0.15f * pf;
            float rate = (twistRate + pinchRate) * (1f - resist) * fricMod;
            rate = Mathf.Max(rate, screwInsertSpeed * 0.01f * pf);
            h.driveProgress += rate * Time.deltaTime;
            h.driveProgress = Mathf.Clamp01(h.driveProgress);
        }
        else if (pinchStr > 0.7f)
        {
            driving = true;
            float rate = (screwInsertSpeed / safeScrewLength) * 0.08f * (1f - resist) * fricMod;
            h.driveProgress += rate * Time.deltaTime;
            h.driveProgress = Mathf.Clamp01(h.driveProgress);
            _activeDriveSpinAngle += screwSpinSpeed * 0.3f * Time.deltaTime;
        }

        Vector3 n = h.normal;
        Vector3 above = h.pos, inPos = h.pos - n * drillDepth;
        h.screwObj.transform.position = Vector3.Lerp(above, inPos, h.driveProgress);
        Vector3 r = Vector3.Cross(n, Vector3.up); if (r.sqrMagnitude < 0.001f) r = Vector3.Cross(n, Vector3.forward);
        r.Normalize(); Vector3 f = Vector3.Cross(r, n);
        h.screwObj.transform.rotation = Quaternion.LookRotation(f, n) * Quaternion.AngleAxis(_activeDriveSpinAngle, Vector3.up);

        if (driving)
        {
            LoopAudio(screwSFX);
            if (_audio.isPlaying)
            {
                _audio.pitch = Mathf.Lerp(1.1f, 0.75f, depthFrac);
                _audio.volume = Mathf.Lerp(0.5f, 1f, depthFrac);
            }
        }
        else StopAudio();

        if (h.driveProgress >= 1f) FinishDrive(h);
    }

    void FinishDrive(HoleData h)
    {
        h.screwDriven = true;
        StopAudio();
        if (_audio != null) { _audio.pitch = 1; _audio.volume = 1; }
        OneShot(doneSFX);
        if (h.screwObj != null) h.screwObj.transform.position = h.pos - h.normal * drillDepth;
        _activeDriveIdx = -1;

        bool allDone = true;
        foreach (var hole in holes) if (hole.screwPlaced && !hole.screwDriven) { allDone = false; break; }
        if (allDone) { currentMode = SimMode.Done; _doneShowcaseActive = true; _doneRotateAngle = 0; }
    }

    public void GoFreeFromExternal()
    {
        StopAudio(); if (_audio != null) { _audio.pitch = 1; _audio.volume = 1; }
        _dustPS?.Stop(); if (_drillBitGO) _drillBitGO.SetActive(false);
        _drillDepthCurrent = 0; _drillVelocity = 0; _isPinchHeldDrill = false; _activeDriveIdx = -1;
        currentMode = SimMode.Free;
    }

    public void EnterDone() { currentMode = SimMode.Done; _doneShowcaseActive = true; _doneRotateAngle = 0; }

    public GameObject BuildScrewObject()
    {
        if (screwPrefab != null) return Instantiate(screwPrefab);
        float r = holeRadius, rt = r * 1.42f, rh = r * 2.4f;
        var ms = new Material(Shdr()) { color = new Color(0.72f, 0.75f, 0.82f) };
        var mt = new Material(Shdr()) { color = new Color(0.52f, 0.55f, 0.62f) };
        var mh = new Material(Shdr()) { color = new Color(0.85f, 0.85f, 0.90f) };
        var ml = new Material(Shdr()) { color = new Color(0.28f, 0.28f, 0.32f) };
        var root = new GameObject("Screw");
        SP(PrimitiveType.Sphere, root, Vector3.zero, new Vector3(r * 2, r * 2.5f, r * 2), ms);
        SP(PrimitiveType.Cylinder, root, new Vector3(0, screwLength * 0.5f, 0), new Vector3(r * 2, screwLength * 0.5f, r * 2), ms);
        SP(PrimitiveType.Cylinder, root, new Vector3(0, screwLength + r * 1.8f, 0), new Vector3(rh * 2, r * 1.6f, rh * 2), mh);
        SP(PrimitiveType.Cube, root, new Vector3(0, screwLength + r * 3.5f, 0), new Vector3(r * 0.35f, r * 0.45f, rh * 1.9f), ml);
        SP(PrimitiveType.Cube, root, new Vector3(0, screwLength + r * 3.5f, 0), new Vector3(rh * 1.9f, r * 0.45f, r * 0.35f), ml);
        int tc = Mathf.Max(4, Mathf.RoundToInt(screwLength / (r * 1.7f)));
        for (int i = 0; i < tc; i++)
        {
            float y = r + i * (screwLength * 0.88f / tc);
            SP(PrimitiveType.Cylinder, root, new Vector3(0, y, 0), new Vector3(rt * 2, r * 0.3f, rt * 2), mt);
        }
        return root;
    }

    void SP(PrimitiveType t, GameObject root, Vector3 p, Vector3 s, Material m)
    {
        var g = GameObject.CreatePrimitive(t);
        g.transform.SetParent(root.transform, false);
        g.transform.localPosition = p;
        g.transform.localScale = s;
        g.GetComponent<Renderer>().material = m;
        Destroy(g.GetComponent<Collider>());
    }

    void Awake() { _audio = GetComponent<AudioSource>(); _audio.spatialBlend = 0; }

    void Start()
    {
        if (!externalInputActive) { var c = new Controller(); }
        if (!embeddedMode) { InitCamera(); FullInit(); }
    }

    public void InitEmbedded()
    {
        if (externalCamera == null)
        {
            Debug.LogError("[SpineScrewSimulation] externalCamera is null in embedded mode.");
            return;
        }
        _cam = externalCamera;
        FullInit();
    }

    void FullInit() { InitBone(); InitPointer(); InitDust(); if (!embeddedMode) ApplyCamera(); _initialized = true; }

    void Update()
    {
        if (!_initialized) return;
        _pulse += Time.deltaTime * 0.1f;
        if (externalInputActive) { UpdateExternal(); return; }
    }

    void UpdateExternal()
    {
        if (currentMode == SimMode.Drilling) DoDrillingExt();
        else if (currentMode == SimMode.Done) DoDone();
        DrawPointer(); PulseRings(); ApplyCamera();
    }

    void DoDrillingExt()
    {
        bool driving = _extPinch;
        float force = driving ? Mathf.Clamp01((_extStr - minDrillPinchStrength) / (1f - minDrillPinchStrength)) : 0;
        _isPinchHeldDrill = driving;

        if (driving && force > 0)
        {
            float df = Mathf.Clamp01(_drillDepthCurrent / drillDepth);
            float resist = boneResistance * Mathf.Pow(df, resistanceCurve);
            float fric = 1f - drillFriction;
            float rate = baseDrillRate * force * (1f - resist) * fric;
            rate = Mathf.Max(rate, baseDrillRate * 0.05f * force);
            _drillVelocity = rate; _currentResistanceForce = resist;
            _drillDepthCurrent += rate * Time.deltaTime;
            LoopAudio(drillSFX);
        }
        else
        {
            _drillVelocity = 0;
            if (drillRetractRate > 0 && _drillDepthCurrent > 0) { _drillDepthCurrent -= drillRetractRate * Time.deltaTime; _drillDepthCurrent = Mathf.Max(0, _drillDepthCurrent); }
            StopAudio();
        }

        if (_drillDepthCurrent >= drillDepth) { _drillDepthCurrent = drillDepth; CompleteDrill(); }
    }

    void CompleteDrill()
    {
        StopAudio(); if (_audio != null) { _audio.pitch = 1; _audio.volume = 1; }
        OneShot(doneSFX); _dustPS?.Stop(); if (_drillBitGO) _drillBitGO.SetActive(false);
        var h = new HoleData { pos = _drillPos, normal = _drillNormal };
        SpawnHoleMarkers(h); SpawnHoleCut(h); holes.Add(h);
        _drillDepthCurrent = 0; _drillVelocity = 0;
        currentMode = SimMode.WaitForScrew;
    }

    void DoDone()
    {
        if (!_doneShowcaseActive) return;
        _doneRotateAngle += _doneRotateSpeed * Time.deltaTime;
        if (!embeddedMode && _pivot != null) _yaw += _doneRotateSpeed * Time.deltaTime;
        else if (_bone != null) _bone.transform.RotateAround(_boneBounds.center, Vector3.up, _doneRotateSpeed * Time.deltaTime);
        if (_doneRotateAngle >= 360) _doneRotateAngle -= 360;
    }

    void SpawnHoleMarkers(HoleData h)
    {
        float dR = highlightRadius > 0 ? highlightRadius : holeRadius * 4;
        h.outerRing = MkCyl("HoleRing", h.pos + h.normal * 0.002f, h.normal, new Vector3(dR, 0.001f, dR), new Color(1, 0.85f, 0));
        h.ringMat = h.outerRing.GetComponent<Renderer>().material;
        h.midRing = MkCyl("HoleMid", h.pos + h.normal * 0.003f, h.normal, new Vector3(dR * 0.58f, 0.0012f, dR * 0.58f), new Color(0.55f, 0.4f, 0));
        if (_holeDarkMat == null) _holeDarkMat = new Material(Shdr()) { color = new Color(0.05f, 0.02f, 0.02f) };
        h.innerDisc = MkCyl("HoleCentre", h.pos + h.normal * 0.004f, h.normal, new Vector3(holeRadius * 2, 0.0015f, holeRadius * 2), _holeDarkMat.color);
        h.innerDisc.GetComponent<Renderer>().material = _holeDarkMat;
        if (_bone) { h.outerRing.transform.SetParent(_bone.transform, true); h.midRing.transform.SetParent(_bone.transform, true); h.innerDisc.transform.SetParent(_bone.transform, true); }
    }

    GameObject MkCyl(string nm, Vector3 pos, Vector3 up, Vector3 sc, Color c)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        g.name = nm;
        Destroy(g.GetComponent<Collider>());
        g.transform.position = pos;
        g.transform.up = up;
        g.transform.localScale = sc;
        g.GetComponent<Renderer>().material = new Material(Shdr()) { color = c };
        return g;
    }

    void SpawnHoleCut(HoleData h)
    {
        float halfD = drillDepth * 0.5f;
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        g.name = "HoleCut";
        Destroy(g.GetComponent<Collider>());
        g.transform.position = h.pos - h.normal * (halfD - 0.001f);
        g.transform.up = h.normal;
        g.transform.localScale = new Vector3(holeRadius * 1.6f, halfD, holeRadius * 1.6f);
        var m = new Material(Shdr());
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = 3000;
        m.color = new Color(0.04f, 0.02f, 0.02f, 0.55f);
        g.GetComponent<Renderer>().material = m;
        if (_bone) g.transform.SetParent(_bone.transform, true);
        h.holeCut = g;
    }

    void PulseRings()
    {
        if (currentMode == SimMode.Done) return;
        float p = 0.82f + 0.18f * Mathf.Sin(_pulse * 2.5f);
        float dR = highlightRadius > 0 ? highlightRadius : holeRadius * 4;
        foreach (var h in holes)
        {
            if (h.ringMat == null) continue;
            if (h.screwPlaced)
            {
                if (h.outerRing && h.outerRing.activeSelf) h.outerRing.SetActive(false);
                if (h.midRing && h.midRing.activeSelf) h.midRing.SetActive(false);
                if (h.innerDisc && h.innerDisc.activeSelf) h.innerDisc.SetActive(false);
                continue;
            }
            h.ringMat.color = new Color(1, 0.85f, 0) * p;
            if (h.outerRing)
            {
                float s = dR * (1 + 0.07f * Mathf.Sin(_pulse * 2.5f));
                h.outerRing.transform.localScale = new Vector3(s, 0.001f, s);
            }
        }
    }

    void InitPointer()
    {
        _needleGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder); _needleGO.name = "Needle"; Destroy(_needleGO.GetComponent<Collider>()); _needleMat = new Material(Shdr()); _needleGO.GetComponent<Renderer>().material = _needleMat; _needleGO.SetActive(false);
        _discGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder); _discGO.name = "Disc"; Destroy(_discGO.GetComponent<Collider>()); _discMat = new Material(Shdr()); _discGO.GetComponent<Renderer>().material = _discMat; _discGO.SetActive(false);
        _drillBitGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder); _drillBitGO.name = "DrillBit"; Destroy(_drillBitGO.GetComponent<Collider>()); _drillBitMat = new Material(Shdr()) { color = new Color(0.6f, 0.63f, 0.68f) }; _drillBitGO.GetComponent<Renderer>().material = _drillBitMat; _drillBitGO.SetActive(false);
    }

    void DrawPointer()
    {
        if (currentMode != SimMode.Drilling) { if (_needleGO) _needleGO.SetActive(false); if (_discGO) _discGO.SetActive(false); if (_drillBitGO) _drillBitGO.SetActive(false); return; }
        float df = Mathf.Clamp01(_drillDepthCurrent / drillDepth);
        Color col = Color.Lerp(new Color(1, 0.6f, 0.05f), new Color(1, 0.1f, 0.05f), df);
        float nL = _boneBounds.extents.magnitude * 0.4f, nR = holeRadius * 0.4f;
        _needleGO.SetActive(true); _needleGO.transform.position = _drillPos + _drillNormal * (nL * 0.5f); _needleGO.transform.up = _drillNormal; _needleGO.transform.localScale = new Vector3(nR, nL * 0.5f, nR); _needleMat.color = col;
        _discGO.SetActive(true); _discGO.transform.position = _drillPos + _drillNormal * 0.005f; _discGO.transform.up = _drillNormal; float dR = highlightRadius > 0 ? highlightRadius : holeRadius * 4; _discGO.transform.localScale = new Vector3(dR, 0.0005f, dR); _discMat.color = new Color(col.r, col.g, col.b, 0.8f);
    }

    void InitCamera() { _cam = Camera.main; if (!_cam) { var g = new GameObject("MainCamera"); g.tag = "MainCamera"; _cam = g.AddComponent<Camera>(); g.AddComponent<AudioListener>(); } }
    void ApplyCamera() { if (embeddedMode || _cam == null || _pivot == null) return; _cam.transform.position = _pivot.position + Quaternion.Euler(_pitch, _yaw, 0) * Vector3.back * _dist; _cam.transform.LookAt(_pivot.position); }

    void InitBone()
    {
        _bone = boneRoot != null ? boneRoot : BuildDummy();
        int added = 0;
        foreach (var mf in _bone.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.sharedMesh || mf.GetComponent<Collider>()) continue;
            if (!mf.sharedMesh.isReadable) { mf.gameObject.AddComponent<BoxCollider>(); added++; }
            else { var mc = mf.gameObject.AddComponent<MeshCollider>(); mc.sharedMesh = mf.sharedMesh; added++; }
        }
        if (added == 0 && !_bone.GetComponentInChildren<Collider>()) _bone.AddComponent<BoxCollider>();
        var rs = _bone.GetComponentsInChildren<Renderer>(true); _boneBounds = rs.Length > 0 ? rs[0].bounds : new Bounds(_bone.transform.position, Vector3.one); foreach (var r in rs) _boneBounds.Encapsulate(r.bounds);
        float sz = _boneBounds.extents.magnitude;
        holeRadius = Mathf.Clamp(sz * 0.012f, 0.002f, 0.02f); screwLength = Mathf.Clamp(sz * 0.18f, 0.01f, 0.5f); snapRadius = Mathf.Clamp(sz * 0.35f, 0.02f, 1.5f); drillDepth = Mathf.Clamp(sz * 0.10f, 0.005f, 0.15f); screwInsertSpeed = Mathf.Clamp(sz * 0.15f, 0.01f, 0.3f); baseDrillRate = Mathf.Clamp(drillDepth * 0.35f, 0.005f, 0.08f); minHoleSpacing = Mathf.Max(holeRadius * 3, 0.01f); _dist = Mathf.Clamp(sz * 1.5f, minDist, maxDist);
        if (pivotOverride != null) _pivot = pivotOverride; else { var pg = new GameObject("_CamPivot"); pg.transform.position = _boneBounds.center; _pivot = pg.transform; }
    }

    //void InitDust()
    //{
    //    var g = new GameObject("_Dust");
    //    g.transform.SetParent(transform);
    //    _dustPS = g.AddComponent<ParticleSystem>();
    //    var m = _dustPS.main;
    //    m.loop = false;
    //    m.playOnAwake = false;
    //    m.startLifetime = 0.5f;
    //    m.startSpeed = 0.12f;
    //    m.startSize = 0.006f;
    //    m.maxParticles = 120;
    //    m.startColor = new Color(0.85f, 0.78f, 0.6f);
    //}

    void InitDust()
    {
        var g = new GameObject("_Dust");
        g.transform.SetParent(transform);

        _dustPS = g.AddComponent<ParticleSystem>();

        var main = _dustPS.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.004f, 0.01f);
        main.maxParticles = 200;
        main.simulationSpace = ParticleSystemSimulationSpace.World; // 🔥 IMPORTANT
        main.startColor = new Color(0.85f, 0.78f, 0.6f, 0.9f);

        // Emission
        var emission = _dustPS.emission;
        emission.rateOverTime = 60;

        // Shape
        var shape = _dustPS.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 25f;
        shape.radius = 0.005f;

        // Velocity (spread)
        var velocity = _dustPS.velocityOverLifetime;
        velocity.enabled = true;
        velocity.x = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
        velocity.y = new ParticleSystem.MinMaxCurve(0.02f, 0.08f);
        velocity.z = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);

        // Size over lifetime (fade shrink)
        var sizeOverLifetime = _dustPS.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, 0.2f);

        // Color fade (alpha)
        var colorOverLifetime = _dustPS.colorOverLifetime;
        colorOverLifetime.enabled = true;

        Gradient grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[] {
            new GradientColorKey(new Color(0.85f, 0.78f, 0.6f), 0f),
            new GradientColorKey(new Color(0.7f, 0.65f, 0.5f), 1f)
            },
            new GradientAlphaKey[] {
            new GradientAlphaKey(0.9f, 0f),
            new GradientAlphaKey(0f, 1f)
            }
        );
        colorOverLifetime.color = grad;

        // Renderer
        var renderer = _dustPS.GetComponent<ParticleSystemRenderer>();
        renderer.material = new Material(Shdr())
        {
            color = new Color(0.85f, 0.78f, 0.6f)
        };
    }

    GameObject BuildDummy() { var root = new GameObject("DummyBone"); _boneMat = new Material(Shdr()) { color = new Color(0.92f, 0.87f, 0.76f) }; Pm(PrimitiveType.Cylinder, root, Vector3.zero, new Vector3(0.07f, 0.04f, 0.07f)); Pm(PrimitiveType.Cube, root, new Vector3(-0.09f, 0, 0), new Vector3(0.05f, 0.02f, 0.04f)); Pm(PrimitiveType.Cube, root, new Vector3(0.09f, 0, 0), new Vector3(0.05f, 0.02f, 0.04f)); return root; }
    void Pm(PrimitiveType t, GameObject root, Vector3 p, Vector3 s) { var g = GameObject.CreatePrimitive(t); g.transform.SetParent(root.transform, false); g.transform.localPosition = p; g.transform.localScale = s; g.GetComponent<Renderer>().material = _boneMat; }

    void LoopAudio(AudioClip c) { if (c == null) return; if (_audio.clip == c && _audio.isPlaying) return; _audio.loop = true; _audio.clip = c; _audio.Play(); }
    void StopAudio() { if (_audio.loop && _audio.isPlaying) { _audio.Stop(); _audio.loop = false; } }
    void OneShot(AudioClip c) { if (c) _audio.PlayOneShot(c); }
    Shader Shdr() => UnityEngine.Shader.Find("Universal Render Pipeline/Lit") ?? UnityEngine.Shader.Find("Standard") ?? UnityEngine.Shader.Find("Diffuse");
    void OnGUI() { if (!_initialized || externalInputActive) return; }

#if UNITY_EDITOR
    void OnDrawGizmos() { if (!Application.isPlaying) return; foreach (var h in holes) { Gizmos.color = h.screwPlaced ? (h.screwDriven ? Color.green : Color.cyan) : Color.yellow; Gizmos.DrawWireSphere(h.pos, holeRadius); } }
#endif
}
