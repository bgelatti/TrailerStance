// Trailer Stance - BepInEx 5 plugin for Apocalypter
// Tunes the Small and Big trailers: wheel spacing, ride height, centre of mass, springs,
// dampers, anti-bounce, sway damping, and brakes that follow the towing car.
// Written from scratch; does not use code from other mods.
// All rights reserved (see LICENSE).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyTitle("Trailer Stance")]

namespace TrailerStance
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class TrailerStancePlugin : BaseUnityPlugin
    {
        public const string GUID = "com.gelatti.apocalypter.trailerstance";
        public const string NAME = "Trailer Stance";
        public const string VERSION = "1.2.0";
        // Before 1.1.1 the GUID (and so the config file name) was this
        private const string OldGUID = "local.apocalypter.trailerstance";

        internal static ManualLogSource Log;

        // General
        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<string> NameKeywords, ExcludeKeywords;
        internal ConfigEntry<KeyCode> EditKey, DumpKey, FastKey, PrevKey, NextKey, DecKey, IncKey, ResetKey, BrakeKey, ParkKey;
        internal ConfigEntry<bool> BrakesOn;
        internal ConfigEntry<float> AimDistance;
        internal static ConfigEntry<bool> StretchAxles;
        internal ConfigEntry<bool> PerfLog;

        internal TrailerSettings Big;
        internal TrailerSettings Small;

        private readonly Dictionary<int, TrailerState> _trailers = new Dictionary<int, TrailerState>();
        private readonly HashSet<string> _dumped = new HashSet<string>();
        private float _nextScan;
        private const float ScanInterval = 5f;
        // wheels are only re-checked for changes made by the game this often; setting changes apply at once
        private float _nextCheck;
        private const float CheckInterval = 0.25f;
        // bumped by every config change, so targets are only recalculated when something changed
        private int _settingsVersion;
        private TrailerState _editing;
        private string _toast;
        private float _toastUntil;

        private void Awake()
        {
            Log = Logger;
            MigrateOldConfig();

            Enabled = Config.Bind("General", "Enabled", true, "Apply trailer settings.");
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu.");
            NameKeywords = Config.Bind("General", "NameKeywords", "trailer",
                "Comma separated words; a vehicle whose name contains one of them is treated as a trailer.");
            ExcludeKeywords = Config.Bind("General", "ExcludeKeywords", "hitch",
                "Comma separated words; names containing one of them are never treated as a trailer.");
            EditKey = Config.Bind("Keys", "EditKey", KeyCode.Insert, "Look at a trailer and press to start/stop adjusting it.");
            PrevKey = Config.Bind("Keys", "PreviousSettingKey", KeyCode.UpArrow, "While adjusting: select the setting above.");
            NextKey = Config.Bind("Keys", "NextSettingKey", KeyCode.DownArrow, "While adjusting: select the setting below.");
            DecKey = Config.Bind("Keys", "DecreaseKey", KeyCode.LeftArrow, "While adjusting: lower the selected setting.");
            IncKey = Config.Bind("Keys", "IncreaseKey", KeyCode.RightArrow, "While adjusting: raise the selected setting.");
            ResetKey = Config.Bind("Keys", "ResetKey", KeyCode.Delete, "While adjusting: reset everything for this trailer type.");
            FastKey = Config.Bind("Keys", "FastKey", KeyCode.LeftAlt, "Hold for 5x bigger steps.");
            DumpKey = Config.Bind("Keys", "DumpKey", KeyCode.F9, "Writes the structure of the trailer you look at to LogOutput.log.");
            BrakeKey = Config.Bind("Brakes", "BrakeKey", KeyCode.S, "The car's brake key. While the car rolls forward and this is held, the trailer brakes too.");
            ParkKey = Config.Bind("Brakes", "HandbrakeKey", KeyCode.Space, "The car's handbrake key. Held, the trailer brakes as well (also when stopped).");
            BrakesOn = Config.Bind("Brakes", "Enabled", true, "Trailer brakes follow the car's brakes.");
            AimDistance = Config.Bind("General", "AimDistance", 8f,
                new ConfigDescription("How far you can be from a trailer to select it (m).", new AcceptableValueRange<float>(2f, 20f)));

            StretchAxles = Config.Bind("General", "StretchAxles", true,
                "Stretch the trailer's axle model sideways together with the wheels when Width is changed.");
            PerfLog = Config.Bind("Debug", "PerfLog", false,
                "Every 10 s, write how long this mod takes per frame (average and peak, in ms) to LogOutput.log.");

            Big = new TrailerSettings(Config, "BigTrailer");
            Small = new TrailerSettings(Config, "SmallTrailer");
            Config.SettingChanged += (sender, e) => _settingsVersion++;
            Config.ConfigReloaded += (sender, e) => _settingsVersion++;

            CreateRunner();
            Application.onBeforeRender += () => Tick("render");
            try
            {
                var h = new Harmony(GUID);
                var step = AccessTools.Method(typeof(WheelController), "Step");
                h.Patch(step, prefix: new HarmonyMethod(typeof(BrakePatch), "Prefix"));
                Log.LogInfo("Brake hook installed on WheelController.Step.");
            }
            catch (Exception e) { Log.LogError("Brake hook failed: " + e.Message); }
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                _trailers.Clear(); _editing = null; _scanLogged = false;
                _nextScan = 0f; // scan on the next frame
                Log.LogInfo("Scene ready: " + scene.name);
            };
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        // Keeps the settings of players coming from the old GUID's config file.
        private void MigrateOldConfig()
        {
            try
            {
                string oldPath = Utility.CombinePaths(Paths.ConfigPath, OldGUID + ".cfg");
                if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(oldPath)) return;
                System.IO.File.Move(oldPath, Config.ConfigFilePath);
                Config.Reload();
                Log.LogInfo("Moved settings from " + OldGUID + ".cfg to " + GUID + ".cfg.");
            }
            catch (Exception e) { Log.LogWarning("Could not move the old config file: " + e.Message); }
        }

        // The game removes the plugin's own object, so the work runs on a separate object that survives.
        private TrailerStanceRunner _runner;
        private bool _scanLogged;

        private void CreateRunner()
        {
            var go = new GameObject("TrailerStance_Runner");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _runner = go.AddComponent<TrailerStanceRunner>();
            _runner.Plugin = this;
            Log.LogInfo("Runner created.");
        }

        // ---------------------------------------------------------------- update loop

        private int _lastTickFrame = -1;
        private bool _tickLogged;

        internal void Tick(string source)
        {
            if (Time.frameCount == _lastTickFrame) return;
            _lastTickFrame = Time.frameCount;
            if (!_tickLogged) { _tickLogged = true; Log.LogInfo("Ticking (" + source + ")."); }
            long start = Perf.Start(PerfLog.Value);
            try
            {
                DoUpdate();
                DoFixedUpdate(); // centre of mass is only written when it changes, so once a frame is enough
                UpdateBrakes();
                UpdateHud();
                UpdateComMarker();
            }
            catch (Exception e)
            {
                if (_tickErrors++ < 5) Log.LogError("Tick: " + e);
            }
            Perf.Tick.Stop(start);
            if (start != 0) WritePerfLog();
        }
        private int _tickErrors;

        internal void DoUpdate()
        {
            if (Input.GetKeyDown(DumpKey.Value)) DumpAimed();

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                Scan();
            }

            HandleEditing();

            if (!Enabled.Value) return;
            bool check = Time.unscaledTime >= _nextCheck;
            if (check) _nextCheck = Time.unscaledTime + CheckInterval;
            foreach (var t in _trailers.Values)
            {
                if (t.Rb == null) continue;
                if (!check && !t.Dirty && t.AppliedVersion == _settingsVersion) continue;
                t.ApplyWheels(SettingsFor(t));
                t.AppliedVersion = _settingsVersion;
                t.Dirty = false;
            }
        }

        // ---- PerfLog
        private float _nextPerfLog;
        private int _perfGc;

        private void WritePerfLog()
        {
            if (Time.unscaledTime < _nextPerfLog) return;
            bool first = _nextPerfLog == 0f;
            _nextPerfLog = Time.unscaledTime + 10f;
            int gc = GC.CollectionCount(0);
            if (!first)
            {
                int wheels = 0;
                foreach (var t in _trailers.Values) wheels += t.Wheels.Count;
                Log.LogInfo("Perf (10 s): tick " + Perf.Tick.Text() + ", fixed " + Perf.Fixed.Text() + ", scan " + Perf.Scan.Text() +
                            ", car search " + Perf.CarSearch.Text() + "; " + _trailers.Count + " trailer(s), " + wheels + " wheel(s), " +
                            _rootCount + " root objects; game GC runs " + (gc - _perfGc) + ".");
            }
            _perfGc = gc;
            Perf.ResetAll();
        }

        internal void DoFixedUpdate()
        {
            if (!Enabled.Value) return;
            foreach (var t in _trailers.Values)
                if (t.Rb != null) t.ApplyCenterOfMass(SettingsFor(t));
        }

        internal TrailerSettings SettingsFor(TrailerState t)
        {
            return t.IsSmall ? Small : Big;
        }

        // ---------------------------------------------------------------- discovery

        // keyword lists, split again only when the setting text changes
        private string _includeSrc, _excludeSrc;
        private string[] _include = new string[0], _exclude = new string[0];

        private static string[] SplitKeywords(string raw)
        {
            var list = new List<string>();
            foreach (var part in (raw ?? "").Split(','))
            {
                string k = part.Trim().ToLowerInvariant();
                if (k.Length > 0) list.Add(k);
            }
            return list.ToArray();
        }

        private bool NameMatches(string name)
        {
            if (name == null) return false;
            if (!string.Equals(_excludeSrc, ExcludeKeywords.Value)) { _excludeSrc = ExcludeKeywords.Value; _exclude = SplitKeywords(_excludeSrc); }
            if (!string.Equals(_includeSrc, NameKeywords.Value)) { _includeSrc = NameKeywords.Value; _include = SplitKeywords(_includeSrc); }
            for (int i = 0; i < _exclude.Length; i++)
                if (name.IndexOf(_exclude[i], StringComparison.OrdinalIgnoreCase) >= 0) return false;
            for (int i = 0; i < _include.Length; i++)
                if (name.IndexOf(_include[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private readonly List<int> _dead = new List<int>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private int _rootCount;

        private void Scan()
        {
            long start = Perf.Start(PerfLog.Value);

            // drop destroyed trailers
            _dead.Clear();
            foreach (var kv in _trailers) if (kv.Value.Rb == null) _dead.Add(kv.Key);
            for (int i = 0; i < _dead.Count; i++) _trailers.Remove(_dead[i]);
            if (_editing != null && _editing.Rb == null) _editing = null;

            // a trailer is a scene root object whose name matches (Trailer_Big(Clone)NN, Trailer_Small(Clone)NN)
            _rootCount = 0;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                scene.GetRootGameObjects(_roots);
                _rootCount += _roots.Count;
                for (int i = 0; i < _roots.Count; i++)
                {
                    var go = _roots[i];
                    if (go == null || !go.activeInHierarchy) continue;
                    var root = go.transform;
                    int rid = root.GetInstanceID();
                    if (_trailers.ContainsKey(rid) || !NameMatches(go.name)) continue;
                    var rb = TrailerBody(root);
                    if (rb == null) continue;

                    var state = new TrailerState(rb, root);
                    _trailers[rid] = state;
                    Log.LogInfo("Found trailer '" + root.name + "' (" + (state.IsSmall ? "small" : "big") + "), body '" + rb.name + "': " +
                                state.Wheels.Count + " wheels, " + state.Axles.Count + " axle model(s), mass " + rb.mass.ToString("0"));
                    if (_dumped.Add(state.IsSmall ? "small" : "big")) Log.LogInfo(Describe(rb, root));
                }
            }
            _roots.Clear();

            if (!_scanLogged && SceneManager.GetActiveScene().name != "MainMenu")
            {
                _scanLogged = true;
                Log.LogInfo("Scan: " + _rootCount + " root objects in " + SceneManager.sceneCount + " scene(s), " + _trailers.Count + " trailer(s).");
            }

            foreach (var t in _trailers.Values) t.RefreshWheels();
            Perf.Scan.Stop(start);
        }

        // the trailer's body: the root's own rigidbody, or the heaviest non-wheel rigidbody inside it
        private Rigidbody TrailerBody(Transform root)
        {
            var own = root.GetComponent<Rigidbody>();
            if (own != null) return own;
            root.GetComponentsInChildren(false, _bodies);
            Rigidbody best = null;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var rb = _bodies[i];
                if (rb == null || rb.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (best == null || rb.mass > best.mass) best = rb;
            }
            _bodies.Clear();
            return best;
        }

        private Camera[] _cameras = new Camera[8];

        private Camera GetCamera()
        {
            var cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled) return cam;
            if (_cameras.Length < Camera.allCamerasCount) _cameras = new Camera[Camera.allCamerasCount + 4];
            int n = Camera.GetAllCameras(_cameras);
            Camera best = null;
            for (int i = 0; i < n; i++)
            {
                var c = _cameras[i];
                if (c != null && c.isActiveAndEnabled && (best == null || c.depth > best.depth)) best = c;
                _cameras[i] = null;
            }
            return best;
        }

        private Rigidbody AimedRigidbody(float maxDist)
        {
            var cam = GetCamera();
            if (cam == null) return null;
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, maxDist, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var rb = h.collider.attachedRigidbody;
                if (rb == null) continue;
                for (var t = rb.transform; t != null; t = t.parent)
                {
                    TrailerState ts;
                    if (_trailers.TryGetValue(t.GetInstanceID(), out ts)) return ts.Rb;
                }
                return rb;
            }
            return null;
        }

        private TrailerState AimedTrailer()
        {
            var rb = AimedRigidbody(AimDistance.Value);
            if (rb != null)
                foreach (var t in _trailers.Values) if (t.Rb == rb) return t;
            // fall back to the nearest trailer in range
            var cam = GetCamera();
            if (cam == null) return null;
            TrailerState best = null;
            float bestD = AimDistance.Value;
            foreach (var t in _trailers.Values)
            {
                if (t.Rb == null) continue;
                float d = Vector3.Distance(cam.transform.position, t.Rb.worldCenterOfMass);
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        // ---------------------------------------------------------------- editing

        private void HandleEditing()
        {
            if (Input.GetKeyDown(EditKey.Value))
            {
                if (_editing != null) { _editing = null; Toast("Trailer Stance: done"); }
                else
                {
                    Scan();
                    _editing = AimedTrailer();
                    var aimed = AimedRigidbody(AimDistance.Value);
                    Log.LogInfo("Edit key: looking at '" + (aimed != null ? aimed.name : "nothing") + "', trailers known " +
                                _trailers.Count + ", selected " + (_editing != null ? _editing.Root.name : "none"));
                    if (_editing == null) Toast("Trailer Stance: no trailer in sight (press " + DumpKey.Value + " to log what is there)");
                }
            }
            if (_editing == null) return;

            var cam = GetCamera();
            if (_editing.Rb == null || cam == null ||
                Vector3.Distance(cam.transform.position, _editing.Rb.worldCenterOfMass) > AimDistance.Value * 2.5f)
            {
                _editing = null;
                return;
            }

            var s = SettingsFor(_editing);
            float m = Input.GetKey(FastKey.Value) ? 5f : 1f;
            if (Input.GetKeyDown(PrevKey.Value)) _selected = (_selected + RowCount - 1) % RowCount;
            if (Input.GetKeyDown(NextKey.Value)) _selected = (_selected + 1) % RowCount;
            int dir = Input.GetKeyDown(IncKey.Value) ? 1 : (Input.GetKeyDown(DecKey.Value) ? -1 : 0);
            if (dir != 0)
            {
                switch (_selected)
                {
                    case 0: s.Width.Value += 0.05f * m * dir; break;
                    case 1: s.Height.Value += 0.01f * m * dir; break;
                    case 2: s.CenterOfMass.Value += 0.05f * m * dir; break;
                    case 3: s.Spring.Value += 0.1f * m * dir; break;
                    case 4: s.Damper.Value += 0.1f * m * dir; break;
                    case 5: s.AntiBounce.Value += 0.05f * m * dir; break;
                    case 6: s.SwayDamping.Value += 0.1f * m * dir; break;
                    case 7: s.BrakeStrength.Value += 0.05f * m * dir; break;
                }
            }
            if (Input.GetKeyDown(ResetKey.Value)) s.Reset();
        }

        private int _selected;
        private const int RowCount = 8;

        private string SuspensionInfo(TrailerState t)
        {
            foreach (var w in t.Wheels)
            {
                if (!w.Alive) continue;
                int n = Mathf.Max(1, t.Wheels.Count);
                float crit = w.CriticalDamping(t.Rb.mass / n);
                float damp = w.EffectiveDamping();
                return "Per wheel: spring " + w.SpringRate().ToString("0") + " N/m, damping over bumps " + damp.ToString("0") +
                       " (" + (crit > 0 ? (damp / crit).ToString("0.00") : "?") + " of critical), mass " + t.Rb.mass.ToString("0") + " kg";
            }
            return "";
        }

        private void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + 4f;
        }

        // ---- on-screen panel (a UI canvas; the game's IMGUI font has no letters)
        private GameObject _hudGo;
        private Text _hudText;
        private Font _font;

        // the panel text is rebuilt on a change, and a few times a second for the live values
        private string _panelText;
        private TrailerState _panelFor;
        private int _panelSelected = -1, _panelVersion = -1;
        private float _panelNext;

        private void UpdateHud()
        {
            string text = null;
            if (_editing != null && _editing.Rb != null)
            {
                if (_panelText == null || _panelFor != _editing || _panelSelected != _selected ||
                    _panelVersion != _settingsVersion || Time.unscaledTime >= _panelNext)
                {
                    _panelFor = _editing; _panelSelected = _selected; _panelVersion = _settingsVersion;
                    _panelNext = Time.unscaledTime + 0.2f;
                    _panelText = PanelText();
                }
                text = _panelText;
            }
            else if (_toast != null && Time.unscaledTime < _toastUntil) text = _toast;

            if (text == null) { if (_hudGo != null && _hudGo.activeSelf) _hudGo.SetActive(false); return; }
            if (_hudGo == null) BuildHud();
            if (!_hudGo.activeSelf) _hudGo.SetActive(true);
            if (!ReferenceEquals(_hudText.text, text) && _hudText.text != text) _hudText.text = text;
        }

        private string PanelText()
        {
            var s = SettingsFor(_editing);
            string[] rows = {
                "Width          " + s.Width.Value.ToString("0.00") + "x",
                "Ride height    " + Signed(s.Height.Value) + " m",
                "Centre of mass " + Signed(s.CenterOfMass.Value) + " m   (" + ComHeightText() + ")",
                "Spring         " + s.Spring.Value.ToString("0.0") + "x",
                "Damper         " + s.Damper.Value.ToString("0.0") + "x",
                "Anti-bounce    " + (s.AntiBounce.Value <= 0f ? "off" : s.AntiBounce.Value.ToString("0.00")),
                "Sway damping   +" + s.SwayDamping.Value.ToString("0.0"),
                "Brakes         " + (s.BrakeStrength.Value <= 0f ? "off" : s.BrakeStrength.Value.ToString("0.00")) +
                    (_editing.Car != null ? "   (car: " + _editing.Car.name + ")" : "   (no car found)") };
            var sb = new StringBuilder();
            sb.AppendLine("TRAILER STANCE - " + _editing.Root.name + (_editing.IsSmall ? " (small)" : " (big)"));
            for (int i = 0; i < rows.Length; i++) sb.AppendLine((i == _selected ? "> " : "  ") + rows[i]);
            sb.AppendLine(PrevKey.Value + "/" + NextKey.Value + " select   " + DecKey.Value + "/" + IncKey.Value + " change   hold " + FastKey.Value + " = 5x");
            sb.AppendLine(ResetKey.Value + " reset   " + EditKey.Value + " done   (" + _editing.Wheels.Count + " wheels, " + _editing.Axles.Count + " axle models)");
            sb.AppendLine("Pink ball = centre of mass, line = down to the ground. Lower ball = harder to roll over.");
            sb.AppendLine(SuspensionInfo(_editing));
            return sb.ToString();
        }

        // ---- trailer brakes
        private float _nextCarSearch;
        private bool _brakeLogged;
        private const float TowRange = 12f;
        private readonly Collider[] _overlap = new Collider[512];
        private readonly List<Joint> _joints = new List<Joint>();
        private readonly HashSet<int> _seenBodies = new HashSet<int>();

        private void UpdateBrakes()
        {
            bool searchCars = Time.unscaledTime >= _nextCarSearch;
            if (searchCars) _nextCarSearch = Time.unscaledTime + 2f;

            bool brakeHeld = Input.GetKey(BrakeKey.Value);
            bool parkHeld = Input.GetKey(ParkKey.Value);
            float dt = Mathf.Max(0.001f, Time.deltaTime);

            foreach (var t in _trailers.Values)
            {
                if (t.Rb == null) continue;
                if (searchCars && !StillTowing(t))
                {
                    long start = Perf.Start(PerfLog.Value);
                    t.Car = FindTowingCar(t);
                    Perf.CarSearch.Stop(start);
                }

                float target = 0f;
                if (BrakesOn.Value && t.Car != null)
                {
                    float carForward = Vector3.Dot(t.Car.velocity, t.Car.transform.forward);
                    if (brakeHeld && carForward > 0.5f) target = 1f;      // braking, not reversing
                    if (parkHeld) target = 1f;
                }
                // smooth on/off so the hitch does not jerk
                float rate = target > t.BrakeLevel ? dt / 0.25f : dt / 0.1f;
                t.BrakeLevel = Mathf.MoveTowards(t.BrakeLevel, target, rate);

                float strength = t.BrakeLevel * SettingsFor(t).BrakeStrength.Value;
                foreach (var w in t.Wheels)
                    if (w.Wc != null) BrakePatch.Set(w.Wc, strength);

                if (strength > 0f && !_brakeLogged)
                {
                    _brakeLogged = true;
                    Log.LogInfo("Trailer brakes working on '" + t.Root.name + "' (towed by '" + t.Car.name + "').");
                }
            }
        }

        // the car found last time still holds the trailer: same hitch joint, or (found by distance) still close
        private bool StillTowing(TrailerState t)
        {
            if (t.Car == null) return false;
            if (t.CarByJoint) return t.CarJoint != null && t.CarJoint.connectedBody == t.CarJointBody;
            return Vector3.Distance(t.Car.worldCenterOfMass, t.Rb.worldCenterOfMass) < TowRange;
        }

        // the car this trailer hangs on: the body at the other end of the hitch joint, or else
        // the nearest other wheeled body close to it
        private Rigidbody FindTowingCar(TrailerState t)
        {
            t.CarByJoint = false; t.CarJoint = null; t.CarJointBody = null;

            // a joint on the trailer that holds another body
            t.Root.GetComponentsInChildren(false, _joints);
            for (int i = 0; i < _joints.Count; i++)
            {
                var j = _joints[i];
                var other = j != null ? j.connectedBody : null;
                if (other == null || other.transform.IsChildOf(t.Root)) continue;
                var car = CarOf(other, t);
                if (car == null) continue;
                _joints.Clear();
                return LinkCar(t, j, other, car);
            }
            _joints.Clear();

            // a joint on a nearby body that holds the trailer, else the nearest wheeled body
            Vector3 c = t.Rb.worldCenterOfMass;
            int n = Physics.OverlapSphereNonAlloc(c, TowRange, _overlap, ~0, QueryTriggerInteraction.Ignore);
            Rigidbody best = null, linked = null;
            Joint linkJoint = null;
            float bestD = TowRange;
            _seenBodies.Clear();
            for (int i = 0; i < n; i++)
            {
                var col = _overlap[i];
                _overlap[i] = null;
                if (linked != null) continue;
                var body = col != null ? col.attachedRigidbody : null;
                if (body == null || !_seenBodies.Add(body.GetInstanceID()) || body.transform.IsChildOf(t.Root)) continue;

                body.GetComponents(_joints);
                for (int k = 0; k < _joints.Count; k++)
                {
                    var j = _joints[k];
                    var held = j != null ? j.connectedBody : null;
                    if (held == null || !held.transform.IsChildOf(t.Root)) continue;
                    linked = CarOf(body, t);
                    if (linked != null) { linkJoint = j; break; }
                }
                _joints.Clear();
                if (linked != null) continue;

                var top = TopBody(body);
                if (top == best) continue;
                float d = Vector3.Distance(top.worldCenterOfMass, c);
                if (d < bestD && IsCar(top, t)) { bestD = d; best = top; }
            }
            if (linked != null) return LinkCar(t, linkJoint, linkJoint.connectedBody, linked);
            return best;
        }

        private static Rigidbody LinkCar(TrailerState t, Joint joint, Rigidbody connected, Rigidbody car)
        {
            t.CarByJoint = true; t.CarJoint = joint; t.CarJointBody = connected;
            return car;
        }

        // topmost rigidbody above (or at) this one: the vehicle's main body
        private static Rigidbody TopBody(Rigidbody rb)
        {
            var top = rb;
            for (var x = rb.transform.parent; x != null; x = x.parent)
            {
                var r = x.GetComponent<Rigidbody>();
                if (r != null) top = r;
            }
            return top;
        }

        private Rigidbody CarOf(Rigidbody rb, TrailerState t)
        {
            var top = TopBody(rb);
            return IsCar(top, t) ? top : null;
        }

        // a wheeled body that is not this trailer or another trailer
        private bool IsCar(Rigidbody rb, TrailerState t)
        {
            if (rb == null || rb.transform.IsChildOf(t.Root)) return false;
            foreach (var other in _trailers.Values) if (other.Rb == rb) return false;
            return rb.GetComponentInChildren<WheelController>() != null;
        }

        // ---- centre of mass marker
        private GameObject _comBall;
        private LineRenderer _comLine;

        private float GroundY(TrailerState t)
        {
            float sum = 0f; int n = 0;
            foreach (var w in t.Wheels)
                if (w.Alive) { sum += w.WheelWorldPos().y - w.Radius; n++; }
            if (n > 0) return sum / n;
            RaycastHit hit;
            var c = t.Rb.worldCenterOfMass;
            if (Physics.Raycast(c, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;
            return c.y - 1f;
        }

        private string ComHeightText()
        {
            if (_editing == null || _editing.Rb == null) return "";
            float h = _editing.Rb.worldCenterOfMass.y - GroundY(_editing);
            return h.ToString("0.00") + " m above the ground";
        }

        private static Material MarkerMaterial(Color c)
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var m = new Material(sh);
            m.color = c;
            if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 8);   // always on top, seen through the trailer
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
            m.renderQueue = 5000;
            return m;
        }

        private void UpdateComMarker()
        {
            bool show = _editing != null && _editing.Rb != null;
            if (!show)
            {
                if (_comBall != null && _comBall.activeSelf) _comBall.SetActive(false);
                return;
            }
            if (_comBall == null)
            {
                var color = new Color(1f, 0.2f, 0.85f);
                _comBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _comBall.name = "TrailerStance_CoM";
                var col = _comBall.GetComponent<Collider>();
                if (col != null) Destroy(col);
                _comBall.transform.localScale = Vector3.one * 0.2f;
                var r = _comBall.GetComponent<MeshRenderer>();
                r.sharedMaterial = MarkerMaterial(color);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                DontDestroyOnLoad(_comBall);

                var lgo = new GameObject("Line");
                lgo.transform.SetParent(_comBall.transform, false);
                _comLine = lgo.AddComponent<LineRenderer>();
                _comLine.sharedMaterial = MarkerMaterial(color);
                _comLine.useWorldSpace = true;
                _comLine.positionCount = 2;
                _comLine.startWidth = _comLine.endWidth = 0.03f;
                _comLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (!_comBall.activeSelf) _comBall.SetActive(true);
            var c = _editing.Rb.worldCenterOfMass;
            _comBall.transform.position = c;
            _comLine.SetPosition(0, c);
            _comLine.SetPosition(1, new Vector3(c.x, GroundY(_editing), c.z));
        }

        private void BuildHud()
        {
            _hudGo = new GameObject("TrailerStance_HUD");
            DontDestroyOnLoad(_hudGo);
            var canvas = _hudGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = _hudGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;

            var tgo = new GameObject("Text");
            tgo.transform.SetParent(_hudGo.transform, false);
            _hudText = tgo.AddComponent<Text>();
            if (_font == null)
            {
                try { _font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Arial" }, 22); } catch { }
            }
            _hudText.font = _font;
            _hudText.fontSize = 22;
            _hudText.color = new Color(0.6f, 1f, 0.45f);
            _hudText.alignment = TextAnchor.UpperLeft;
            _hudText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _hudText.verticalOverflow = VerticalWrapMode.Overflow;
            _hudText.raycastTarget = false;
            var outline = tgo.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var rt = _hudText.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(40f, -120f);
            rt.sizeDelta = new Vector2(1200f, 400f);
        }

        private static string Signed(float v)
        {
            return (v >= 0 ? "+" : "") + v.ToString("0.00");
        }

        // ---------------------------------------------------------------- diagnostics

        private void DumpAimed()
        {
            var rb = AimedRigidbody(AimDistance.Value * 2f);
            var cam = GetCamera();
            if (rb == null && cam != null)
            {
                float bestD = 30f;
                foreach (var t in _trailers.Values)
                {
                    if (t.Rb == null) continue;
                    float d = Vector3.Distance(cam.transform.position, t.Rb.worldCenterOfMass);
                    if (d < bestD) { bestD = d; rb = t.Rb; }
                }
                foreach (var wc in FindObjectsOfType<WheelController>())
                {
                    var r = wc.GetComponentInParent<Rigidbody>();
                    if (r == null) continue;
                    float d = Vector3.Distance(cam.transform.position, r.worldCenterOfMass);
                    if (d < bestD) { bestD = d; rb = r; }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("===== Trailer Stance dump =====");

            // every vehicle nearby, so the trailer names are known
            if (cam != null)
            {
                var seen = new HashSet<int>();
                sb.AppendLine("Trailers found so far: " + _trailers.Count);
                foreach (var r in FindObjectsOfType<Rigidbody>())
                    if (NameMatches(r.name))
                        sb.AppendLine("  body '" + PathOf(r.transform) + "' mass=" + r.mass);
                sb.AppendLine("Vehicles with wheels within 60 m:");
                foreach (var wc in FindObjectsOfType<WheelController>())
                {
                    var r = wc.GetComponentInParent<Rigidbody>();
                    if (r == null || !seen.Add(r.GetInstanceID())) continue;
                    float d = Vector3.Distance(cam.transform.position, r.worldCenterOfMass);
                    if (d > 60f) continue;
                    sb.AppendLine("  '" + r.name + "' dist=" + d.ToString("0.0") + " mass=" + r.mass.ToString("0") +
                                  " wheels=" + r.GetComponentsInChildren<WheelController>(true).Length +
                                  " trailerMatch=" + NameMatches(r.name));
                }
            }

            if (rb == null)
            {
                sb.AppendLine("No vehicle in sight.");
                Log.LogInfo(sb.ToString());
                Toast("Trailer Stance: nothing found, see LogOutput.log");
                return;
            }


            Transform droot = rb.transform;
            foreach (var t in _trailers.Values) if (t.Rb == rb) droot = t.Root;
            sb.Append(Describe(rb, droot));
            sb.AppendLine("===== end dump =====");
            Log.LogInfo(sb.ToString());
            Toast("Trailer Stance: '" + rb.name + "' written to LogOutput.log");
        }

        private string Describe(Rigidbody rb, Transform root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Selected: '" + rb.name + "' path=" + PathOf(rb.transform) + " root='" + root.name + "' trailerMatch=" + NameMatches(root.name));
            sb.AppendLine("  mass=" + rb.mass + " drag=" + rb.drag + " angDrag=" + rb.angularDrag +
                          " CoM=" + rb.centerOfMass.ToString("F3") + " inertia=" + rb.inertiaTensor.ToString("F2") +
                          " interp=" + rb.interpolation + " layer=" + rb.gameObject.layer);
            sb.AppendLine("  root components: " + ComponentList(rb.gameObject));

            var joints = rb.GetComponents<Joint>();
            foreach (var j in joints)
                sb.AppendLine("  joint " + j.GetType().Name + " connected=" + (j.connectedBody != null ? j.connectedBody.name : "none") +
                              " anchor=" + j.anchor.ToString("F3"));
            foreach (var j in FindObjectsOfType<Joint>())
                if (j.connectedBody == rb)
                    sb.AppendLine("  joint on '" + j.name + "' (" + j.GetType().Name + ") connects to this");

            foreach (var wc in root.GetComponentsInChildren<WheelController>(true))
            {
                var lp = rb.transform.InverseTransformPoint(wc.transform.position);
                sb.AppendLine("  WheelController " + PathOf(wc.transform) + " active=" + wc.isActiveAndEnabled);
                sb.AppendLine("    rbLocalPos=" + lp.ToString("F3") + " localPos=" + wc.transform.localPosition.ToString("F3") +
                              " parent='" + (wc.transform.parent != null ? wc.transform.parent.name : "-") + "'");
                if (wc.wheel != null)
                    sb.AppendLine("    wheel radius=" + wc.wheel.radius + " width=" + wc.wheel.width + " mass=" + wc.wheel.mass +
                                  " visual='" + (wc.wheel.visual != null ? PathOf(wc.wheel.visual.transform) : "-") + "'");
                if (wc.spring != null)
                    sb.AppendLine("    spring maxForce=" + wc.spring.maxForce + " maxLength=" + wc.spring.maxLength +
                                  " length=" + wc.spring.length.ToString("F3"));
                if (wc.damper != null)
                    sb.AppendLine("    damper bump=" + wc.damper.bumpRate + " rebound=" + wc.damper.reboundRate +
                                  " slowBump=" + wc.damper.slowBump + " fastBump=" + wc.damper.fastBump +
                                  " slowRebound=" + wc.damper.slowRebound + " fastRebound=" + wc.damper.fastRebound);
            }

            foreach (var col in root.GetComponentsInChildren<WheelCollider>(true))
                sb.AppendLine("  WheelCollider " + PathOf(col.transform) + " radius=" + col.radius + " susp=" + col.suspensionDistance +
                              " spring=" + col.suspensionSpring.spring + " damper=" + col.suspensionSpring.damper +
                              " rbLocalPos=" + rb.transform.InverseTransformPoint(col.transform.position).ToString("F3"));
            foreach (var other in root.GetComponentsInChildren<Rigidbody>(true))
                if (other != rb) sb.AppendLine("  child body '" + PathOf(other.transform) + "' mass=" + other.mass + " kinematic=" + other.isKinematic);

            sb.AppendLine("  hierarchy:");
            int lines = 0;
            DumpTree(root, 1, sb, ref lines);
            return sb.ToString();
        }

        private static void DumpTree(Transform t, int depth, StringBuilder sb, ref int lines)
        {
            if (lines > 400 || depth > 6) return;
            lines++;
            string extra = "";
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) extra = " size=" + mr.bounds.size.ToString("F2") + " ls=" + t.localScale.ToString("F2");
            sb.AppendLine(new string(' ', depth * 2 + 2) + t.name + (t.gameObject.activeSelf ? "" : " [off]") +
                          " lp=" + t.localPosition.ToString("F2") + extra + " {" + ComponentList(t.gameObject) + "}");
            foreach (Transform c in t) DumpTree(c, depth + 1, sb, ref lines);
        }

        private static string ComponentList(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { names.Add("null"); continue; }
                if (c is Transform) continue;
                names.Add(c.GetType().Name);
            }
            return string.Join(", ", names.ToArray());
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            while (t != null) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts.ToArray());
        }
    }

    // Runs before each wheel's physics step: the game resets brake torque every step, so ours is added here.
    internal static class BrakePatch
    {
        private static readonly Dictionary<WheelController, float> _strength = new Dictionary<WheelController, float>();

        public static void Set(WheelController wc, float strength)
        {
            if (strength <= 0f) _strength.Remove(wc);
            else _strength[wc] = strength;
        }

        public static void Prefix(WheelController __instance)
        {
            float k;
            if (__instance == null || !_strength.TryGetValue(__instance, out k)) return;
            // torque that uses 'k' of the grip this wheel has right now (load x radius)
            float torque = k * Mathf.Max(0f, __instance.Load) * Mathf.Max(0.1f, __instance.Radius);
            if (torque > __instance.BrakeTorque) __instance.BrakeTorque = torque;
        }
    }

    internal class TrailerStanceRunner : MonoBehaviour
    {
        public TrailerStancePlugin Plugin;
        private bool _ticked;
        private int _errors;

        private void Update()
        {
            if (Plugin == null) return;
            if (!_ticked) { _ticked = true; TrailerStancePlugin.Log.LogInfo("Runner ticking."); }
            Plugin.Tick("runner");
        }
        private void FixedUpdate()
        {
            if (Plugin == null) return;
            long start = Perf.Start(Plugin.PerfLog.Value);
            // Unity errors are not written to this game's log, so catch and write them ourselves (first few only)
            try { Plugin.DoFixedUpdate(); }
            catch (Exception e)
            {
                if (_errors++ < 5) TrailerStancePlugin.Log.LogError("FixedUpdate: " + e);
            }
            Perf.Fixed.Stop(start);
        }
        private void OnDestroy() { TrailerStancePlugin.Log.LogWarning("Runner destroyed."); }
    }

    // Timings for the PerfLog option. Start returns 0 when it is off, and Stop then does nothing.
    internal class Perf
    {
        public static readonly Perf Tick = new Perf(), Fixed = new Perf(), Scan = new Perf(), CarSearch = new Perf();
        private long _sum, _max;
        private int _count;

        public static long Start(bool on) { return on ? Math.Max(1L, Stopwatch.GetTimestamp()) : 0L; }

        public void Stop(long start)
        {
            if (start == 0) return;
            long d = Stopwatch.GetTimestamp() - start;
            _sum += d; _count++;
            if (d > _max) _max = d;
        }

        private static string Ms(double ticks) { return (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.000"); }

        public string Text()
        {
            if (_count == 0) return "-";
            return "avg " + Ms((double)_sum / _count) + " ms / peak " + Ms(_max) + " ms (" + _count + "x)";
        }

        public static void ResetAll()
        {
            foreach (var p in new[] { Tick, Fixed, Scan, CarSearch }) { p._sum = 0; p._max = 0; p._count = 0; }
        }
    }

    // -------------------------------------------------------------------- settings per trailer type

    internal class TrailerSettings
    {
        public readonly ConfigEntry<float> Width;
        public readonly ConfigEntry<float> Height;
        public readonly ConfigEntry<float> CenterOfMass;
        public readonly ConfigEntry<float> Spring;
        public readonly ConfigEntry<float> Damper;
        public readonly ConfigEntry<float> AntiBounce;
        public readonly ConfigEntry<float> SwayDamping;
        public readonly ConfigEntry<float> BrakeStrength;

        public TrailerSettings(ConfigFile cfg, string section)
        {
            Width = cfg.Bind(section, "Width", 1f, new ConfigDescription(
                "Wheel spacing multiplier. Wider = harder to roll over.", new AcceptableValueRange<float>(1f, 1.5f)));
            Height = cfg.Bind(section, "RideHeight", 0f, new ConfigDescription(
                "Raise (+) or lower (-) the trailer body, in metres.", new AcceptableValueRange<float>(-0.15f, 0.15f)));
            CenterOfMass = cfg.Bind(section, "CenterOfMassHeight", 0f, new ConfigDescription(
                "Moves the physics centre of mass up (+) or down (-), in metres. Lower = much harder to roll over.",
                new AcceptableValueRange<float>(-0.6f, 0.3f)));
            Spring = cfg.Bind(section, "SpringStrength", 1f, new ConfigDescription(
                "Spring force multiplier. Stiffer = less body roll.", new AcceptableValueRange<float>(0.5f, 3f)));
            Damper = cfg.Bind(section, "DamperStrength", 1f, new ConfigDescription(
                "Damper multiplier. Higher = less bouncing and swaying.", new AcceptableValueRange<float>(0.5f, 6f)));
            AntiBounce = cfg.Bind(section, "AntiBounce", 0f, new ConfigDescription(
                "Minimum damping as a share of critical damping (0 = off, 0.3 = firm, 0.7 = very firm, 1 = no bounce at all). " +
                "Works whatever the stock damper is.", new AcceptableValueRange<float>(0f, 1.2f)));
            SwayDamping = cfg.Bind(section, "SwayDamping", 0f, new ConfigDescription(
                "Extra resistance to rocking and pitching (rigidbody angular drag added to the stock value).",
                new AcceptableValueRange<float>(0f, 5f)));
            BrakeStrength = cfg.Bind(section, "BrakeStrength", 0.6f, new ConfigDescription(
                "How hard the trailer brakes when you brake the car (share of the grip each wheel has; 0 = no trailer brakes).",
                new AcceptableValueRange<float>(0f, 1.2f)));
        }

        public void Reset()
        {
            Width.Value = 1f;
            Height.Value = 0f;
            CenterOfMass.Value = 0f;
            Spring.Value = 1f;
            Damper.Value = 1f;
            AntiBounce.Value = 0f;
            SwayDamping.Value = 0f;
            BrakeStrength.Value = 0.6f;
        }
    }

    // -------------------------------------------------------------------- runtime state

    internal class WheelState
    {
        public WheelController Wc;
        public WheelCollider Col;
        public bool Alive { get { return Wc != null || Col != null; } }
        public Transform T { get { return Wc != null ? Wc.transform : (Col != null ? Col.transform : null); } }
        public float Radius { get { return Wc != null ? (Wc.wheel != null ? Wc.wheel.radius : 0f) : (Col != null ? Col.radius : 0f); } }

        public float GetSpring()
        {
            if (Wc != null) return Wc.spring != null ? Wc.spring.maxForce : 0f;
            return Col != null ? Col.suspensionSpring.spring : 0f;
        }
        public void SetSpring(float v)
        {
            if (Wc != null) { if (Wc.spring != null) Wc.spring.maxForce = v; return; }
            if (Col != null) { var js = Col.suspensionSpring; js.spring = v; Col.suspensionSpring = js; }
        }
        // damper values: 6 for an NWH wheel (two rates, four slow/fast multipliers), 1 for a WheelCollider
        public const int MaxDamp = 6;
        public readonly float[] BaseDamp = new float[MaxDamp], LastDamp = new float[MaxDamp];
        public readonly float[] CurDamp = new float[MaxDamp], TargetDamp = new float[MaxDamp];
        public int DampLen; // 0 = no damper seen yet

        // reads the damper values into buf, returns how many (0 = no damper)
        public int ReadDamp(float[] buf)
        {
            if (Wc != null && Wc.damper != null)
            {
                var d = Wc.damper;
                buf[0] = d.bumpRate; buf[1] = d.reboundRate; buf[2] = d.slowBump;
                buf[3] = d.fastBump; buf[4] = d.slowRebound; buf[5] = d.fastRebound;
                return 6;
            }
            if (Col != null) { buf[0] = Col.suspensionSpring.damper; return 1; }
            return 0;
        }
        public void SetDamp(float[] v)
        {
            if (Wc != null && Wc.damper != null)
            {
                var d = Wc.damper;
                d.bumpRate = v[0]; d.reboundRate = v[1]; d.slowBump = v[2];
                d.fastBump = v[3]; d.slowRebound = v[4]; d.fastRebound = v[5];
                return;
            }
            if (Col != null) { var js = Col.suspensionSpring; js.damper = v[0]; Col.suspensionSpring = js; }
        }
        // spring rate (N/m) as the physics sees it now
        public float SpringRate()
        {
            if (Wc != null && Wc.spring != null)
                return Wc.spring.maxLength > 0.01f ? Wc.spring.maxForce / Wc.spring.maxLength : Wc.spring.maxForce;
            return Col != null ? Col.suspensionSpring.spring : 0f;
        }

        // damping (N per m/s) that stops a bounce without any overshoot, for the mass this wheel carries
        public float CriticalDamping(float sprungMass)
        {
            return 2f * Mathf.Sqrt(Mathf.Max(0f, SpringRate()) * Mathf.Max(1f, sprungMass));
        }

        // NWH damper: force = rate x (slow or fast multiplier) x speed. Only the two rates are scaled;
        // the four multipliers are shapes, not strengths, and stay as the game set them.
        // writes the wanted damper values into TargetDamp
        public void CalcTargetDamp(float mult, float antiBounce, float sprungMass)
        {
            var b = BaseDamp;
            var d = TargetDamp;
            Array.Copy(b, d, DampLen);
            float crit = CriticalDamping(sprungMass);
            // largest coefficient the physics step can take without blowing up (half the stability limit)
            float cap = Mathf.Max(1f, sprungMass) / Mathf.Max(0.002f, Time.fixedDeltaTime);

            if (DampLen >= 6)
            {
                float slowB = Mathf.Max(0.05f, b[2]), fastB = Mathf.Max(0.05f, b[3]);
                float slowR = Mathf.Max(0.05f, b[4]), fastR = Mathf.Max(0.05f, b[5]);
                d[0] = b[0] * mult;
                d[1] = b[1] * mult;
                if (antiBounce > 0f)
                {
                    d[0] = Mathf.Max(d[0], antiBounce * crit / fastB);
                    d[1] = Mathf.Max(d[1], antiBounce * crit / fastR);
                }
                d[0] = Mathf.Min(d[0], Mathf.Max(b[0], cap / Mathf.Max(slowB, fastB)));
                d[1] = Mathf.Min(d[1], Mathf.Max(b[1], cap / Mathf.Max(slowR, fastR)));
            }
            else if (DampLen == 1)
            {
                d[0] = Mathf.Max(b[0] * mult, antiBounce * crit);
                d[0] = Mathf.Min(d[0], Mathf.Max(b[0], cap));
            }
        }

        // damping actually felt over a bump (fast range), N per m/s
        public float EffectiveDamping()
        {
            var d = CurDamp;
            int n = ReadDamp(d);
            if (n == 0) return 0f;
            if (n >= 6) return d[0] * d[3];
            return d[0];
        }

        // where the wheel itself sits (for finding the axle model)
        public Vector3 WheelWorldPos()
        {
            if (Wc != null && Wc.wheel != null && Wc.wheel.visual != null) return Wc.wheel.visual.transform.position;
            if (Col != null)
            {
                Vector3 p; Quaternion q;
                Col.GetWorldPose(out p, out q);
                return p;
            }
            return T.position;
        }
        public Vector3 BaseRb;         // stock position in trailer rigidbody space
        public Vector3 BaseLocal;      // stock transform.localPosition
        public Vector3 LastLocal;      // localPosition we last wrote (or saw)
        public float BaseMaxForce, LastMaxForce;
    }

    internal class AxleState
    {
        public Transform T;
        public int Axis;               // local axis that points sideways on the trailer
        public float PivotRbX, CenterRbX;
        public Vector3 BaseScale, BaseLocal;
        public Vector3 LastScale, LastLocal;
    }

    internal class TrailerState
    {
        public readonly List<AxleState> Axles = new List<AxleState>();
        private int _axleWheelCount = -1;
        public readonly Rigidbody Rb;
        public readonly Transform Root;
        public Rigidbody Car;
        // how the car was found: through this joint (still valid while it holds CarJointBody), or by distance
        public bool CarByJoint;
        public Joint CarJoint;
        public Rigidbody CarJointBody;
        public float BrakeLevel;
        public readonly bool IsSmall;
        public readonly List<WheelState> Wheels = new List<WheelState>();
        // wheels need applying on the next frame (new or lost wheels), and the settings version last applied
        public bool Dirty = true;
        public int AppliedVersion = -1;
        private Vector3 _baseCoM, _lastCoM;
        private bool _comInit;

        // shared buffers for GetComponentsInChildren (main thread only)
        private static readonly List<WheelController> _wcBuf = new List<WheelController>();
        private static readonly List<WheelCollider> _colBuf = new List<WheelCollider>();

        public TrailerState(Rigidbody rb, Transform root)
        {
            Rb = rb;
            Root = root;
            IsSmall = root.name.ToLowerInvariant().Contains("small");
            RefreshWheels();
        }

        public void RefreshWheels()
        {
            if (Rb == null || Root == null) return;
            int before = Wheels.Count;
            for (int i = Wheels.Count - 1; i >= 0; i--) if (!Wheels[i].Alive) Wheels.RemoveAt(i);
            bool changed = Wheels.Count != before;

            Root.GetComponentsInChildren(true, _wcBuf);
            for (int i = 0; i < _wcBuf.Count; i++)
                if (!HasWheel(_wcBuf[i], null)) { AddWheel(new WheelState { Wc = _wcBuf[i] }); changed = true; }
            _wcBuf.Clear();
            Root.GetComponentsInChildren(true, _colBuf);
            for (int i = 0; i < _colBuf.Count; i++)
                if (!HasWheel(null, _colBuf[i])) { AddWheel(new WheelState { Col = _colBuf[i] }); changed = true; }
            _colBuf.Clear();

            if (changed) Dirty = true;
            if (Wheels.Count != _axleWheelCount) FindAxles();
        }

        private bool HasWheel(WheelController wc, WheelCollider col)
        {
            for (int i = 0; i < Wheels.Count; i++)
            {
                var w = Wheels[i];
                if (wc != null ? ReferenceEquals(w.Wc, wc) : ReferenceEquals(w.Col, col)) return true;
            }
            return false;
        }

        private void AddWheel(WheelState ws)
        {
            ws.BaseLocal = ws.LastLocal = ws.T.localPosition;
            ws.BaseRb = Rb.transform.InverseTransformPoint(ws.T.position);
            ws.BaseMaxForce = ws.LastMaxForce = ws.GetSpring();
            ws.DampLen = ws.ReadDamp(ws.BaseDamp);
            Array.Copy(ws.BaseDamp, ws.LastDamp, ws.DampLen);
            Wheels.Add(ws);
        }

        // Finds the axle model(s): long, thin meshes that run across the trailer at wheel height.
        private void FindAxles()
        {
            _axleWheelCount = Wheels.Count;
            foreach (var old in Axles)
                if (old.T != null) { old.T.localScale = old.BaseScale; old.T.localPosition = old.BaseLocal; }
            Axles.Clear();
            if (Wheels.Count < 2) return;

            var rbT = Rb.transform;
            var exclude = new HashSet<Transform>();
            float minX = float.MaxValue, maxX = float.MinValue, wheelY = 0f, radius = 0f;
            var wheelZ = new List<float>();
            foreach (var w in Wheels)
            {
                if (!w.Alive) continue;
                // wheel visuals and everything above a wheel must not be scaled
                for (var t = w.T; t != null && t != rbT; t = t.parent) exclude.Add(t);
                if (w.Wc != null && w.Wc.wheel != null && w.Wc.wheel.visual != null)
                    foreach (var t in w.Wc.wheel.visual.GetComponentsInChildren<Transform>(true)) exclude.Add(t);
                foreach (var t in w.T.GetComponentsInChildren<Transform>(true)) exclude.Add(t);

                Vector3 c = rbT.InverseTransformPoint(w.WheelWorldPos());
                c.x = w.BaseRb.x; // stock lateral position, even if already widened
                minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
                wheelY += c.y; wheelZ.Add(c.z);
                radius = Mathf.Max(radius, w.Radius);
            }
            // wheel parts bolted to the trailer (their own small bodies) are not axles either
            foreach (var body in Root.GetComponentsInChildren<Rigidbody>(true))
                if (body != Rb && body.name.ToLowerInvariant().Contains("wheel"))
                    foreach (var t in body.GetComponentsInChildren<Transform>(true)) exclude.Add(t);
            wheelY /= Mathf.Max(1, wheelZ.Count);
            if (radius <= 0f) radius = 0.35f;
            float track = maxX - minX;
            float cx = (minX + maxX) * 0.5f;
            if (track < 0.3f) return;

            foreach (var r in Root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var t = r.transform;
                if (t == rbT || exclude.Contains(t)) continue;

                Vector3 bmin, bmax;
                RbBounds(r.bounds, rbT, out bmin, out bmax);
                Vector3 size = bmax - bmin, center = (bmin + bmax) * 0.5f;

                bool acrossTrailer = size.x >= track * 0.5f && size.x <= track * 1.4f;
                bool thin = size.y <= radius * 1.6f && size.z <= radius * 1.6f;
                bool atWheelHeight = Mathf.Abs(center.y - wheelY) <= radius * 1.2f;
                bool nearAWheel = wheelZ.Exists(z => Mathf.Abs(center.z - z) <= radius * 1.2f);
                bool centred = Mathf.Abs(center.x - cx) <= track * 0.25f;
                if (!(acrossTrailer && thin && atWheelHeight && nearAWheel && centred)) continue;

                // which local axis points sideways
                Vector3 right = rbT.right;
                float[] dots = {
                    Mathf.Abs(Vector3.Dot(t.right, right)),
                    Mathf.Abs(Vector3.Dot(t.up, right)),
                    Mathf.Abs(Vector3.Dot(t.forward, right)) };
                int axis = 0;
                if (dots[1] > dots[axis]) axis = 1;
                if (dots[2] > dots[axis]) axis = 2;
                if (dots[axis] < 0.9f) continue; // not aligned, scaling would shear it

                var a = new AxleState
                {
                    T = t, Axis = axis,
                    PivotRbX = rbT.InverseTransformPoint(t.position).x,
                    CenterRbX = cx,
                    BaseScale = t.localScale, BaseLocal = t.localPosition,
                    LastScale = t.localScale, LastLocal = t.localPosition
                };
                Axles.Add(a);
                TrailerStancePlugin.Log.LogInfo("  axle model: " + t.name + " (size " + size.ToString("F2") + ")");
            }
            if (Axles.Count == 0)
                TrailerStancePlugin.Log.LogInfo("  no separate axle model found on " + Rb.name + " (it may be part of the body mesh).");
        }

        private static void RbBounds(Bounds b, Transform rbT, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                p = rbT.InverseTransformPoint(p);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        }

        private void ApplyAxles(float width)
        {
            if (!TrailerStancePlugin.StretchAxles.Value) width = 1f;
            var rbT = Rb.transform;
            foreach (var a in Axles)
            {
                if (a.T == null) continue;
                // the game replaced or moved it: take it as the new stock pose
                if ((a.T.localScale - a.LastScale).sqrMagnitude > 1e-10f || (a.T.localPosition - a.LastLocal).sqrMagnitude > 1e-10f)
                {
                    a.BaseScale = a.LastScale = a.T.localScale;
                    a.BaseLocal = a.LastLocal = a.T.localPosition;
                    a.PivotRbX = rbT.InverseTransformPoint(a.T.position).x;
                }

                var scale = a.BaseScale;
                scale[a.Axis] = a.BaseScale[a.Axis] * width;

                // scale about the trailer centre instead of the model's pivot
                var deltaRb = new Vector3((a.CenterRbX - a.PivotRbX) * (1f - width), 0f, 0f);
                var world = rbT.TransformVector(deltaRb);
                var parent = a.T.parent;
                var pos = a.BaseLocal + (parent != null ? parent.InverseTransformVector(world) : world);

                if ((scale - a.LastScale).sqrMagnitude > 1e-10f || (pos - a.LastLocal).sqrMagnitude > 1e-10f)
                {
                    a.T.localScale = scale;
                    a.T.localPosition = pos;
                    a.LastScale = a.T.localScale;
                    a.LastLocal = a.T.localPosition;
                }
            }
        }

        private static bool Same(float[] a, float[] b, int len)
        {
            for (int i = 0; i < len; i++) if (Mathf.Abs(a[i] - b[i]) > 0.001f + Mathf.Abs(b[i]) * 1e-4f) return false;
            return true;
        }

        // Runs on a setting change, for new wheels, and every CheckInterval otherwise; allocates nothing.
        public void ApplyWheels(TrailerSettings s)
        {
            if (Wheels.Count == 0) return;

            // detect changes made by the game (part swaps etc.) and take them as the new stock values
            foreach (var w in Wheels)
            {
                if (!w.Alive) continue;
                var cur = w.T.localPosition;
                if ((cur - w.LastLocal).sqrMagnitude > 1e-10f)
                {
                    w.BaseLocal = cur; w.LastLocal = cur;
                    w.BaseRb = Rb.transform.InverseTransformPoint(w.T.position);
                }
                float f = w.GetSpring();
                if (Mathf.Abs(f - w.LastMaxForce) > 0.01f) w.BaseMaxForce = w.LastMaxForce = f;
                int len = w.ReadDamp(w.CurDamp);
                if (len > 0 && (len != w.DampLen || !Same(w.CurDamp, w.LastDamp, len)))
                {
                    w.DampLen = len;
                    Array.Copy(w.CurDamp, w.BaseDamp, len);
                    Array.Copy(w.CurDamp, w.LastDamp, len);
                }
            }

            // lateral centre of the trailer's wheels
            float cx = 0f; int n = 0;
            foreach (var w in Wheels) if (w.Alive) { cx += w.BaseRb.x; n++; }
            if (n == 0) return;
            cx /= n;

            ApplyAxles(s.Width.Value);

            foreach (var w in Wheels)
            {
                if (!w.Alive) continue;

                // offset in trailer space: spread sideways, spring top lower = body higher
                var delta = new Vector3((w.BaseRb.x - cx) * (s.Width.Value - 1f), -s.Height.Value, 0f);
                var world = Rb.transform.TransformVector(delta);
                var parent = w.T.parent;
                var deltaLocal = parent != null ? parent.InverseTransformVector(world) : world;
                var target = w.BaseLocal + deltaLocal;
                if ((target - w.LastLocal).sqrMagnitude > 1e-10f)
                {
                    w.T.localPosition = target;
                    w.LastLocal = w.T.localPosition;
                }

                float f = w.BaseMaxForce * s.Spring.Value;
                if (Mathf.Abs(f - w.LastMaxForce) > 0.01f) { w.SetSpring(f); w.LastMaxForce = f; }

                if (w.DampLen > 0)
                {
                    w.CalcTargetDamp(s.Damper.Value, s.AntiBounce.Value, Rb.mass / n);
                    if (!Same(w.TargetDamp, w.LastDamp, w.DampLen))
                    {
                        w.SetDamp(w.TargetDamp);
                        Array.Copy(w.TargetDamp, w.LastDamp, w.DampLen);
                    }
                }
            }
        }

        private float _baseAngDrag, _lastAngDrag;
        private bool _dragInit;

        public void ApplyCenterOfMass(TrailerSettings s)
        {
            float ad = Rb.angularDrag;
            if (!_dragInit) { _baseAngDrag = _lastAngDrag = ad; _dragInit = true; }
            else if (Mathf.Abs(ad - _lastAngDrag) > 1e-4f) _baseAngDrag = ad; // the game changed it
            float targetDrag = _baseAngDrag + s.SwayDamping.Value;
            if (Mathf.Abs(targetDrag - ad) > 1e-4f) Rb.angularDrag = targetDrag;
            _lastAngDrag = Rb.angularDrag;

            var cur = Rb.centerOfMass;
            if (!_comInit) { _baseCoM = cur; _lastCoM = cur; _comInit = true; }
            else if ((cur - _lastCoM).sqrMagnitude > 1e-8f) _baseCoM = cur; // the game moved it

            var target = _baseCoM + new Vector3(0f, s.CenterOfMass.Value, 0f);
            if ((target - cur).sqrMagnitude > 1e-8f)
            {
                Rb.centerOfMass = target;
                _lastCoM = Rb.centerOfMass;
            }
            else _lastCoM = cur;
        }
    }
}
