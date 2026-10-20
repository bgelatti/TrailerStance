// Trailer Stance - BepInEx 5 plugin for Apocalypter
// Tunes the Small and Big trailers: wheel spacing, ride height, centre of mass, springs,
// dampers, anti-bounce, sway damping, and brakes that follow the towing car.
// Written from scratch; does not use code from other mods.
// License: MIT (see LICENSE).

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyTitle("Trailer Stance")]

namespace TrailerStance
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class TrailerStancePlugin : BaseUnityPlugin
    {
        public const string GUID = "local.apocalypter.trailerstance";
        public const string NAME = "Trailer Stance";
        public const string VERSION = "1.1.0";

        internal static ManualLogSource Log;

        // General
        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<string> NameKeywords, ExcludeKeywords;
        internal ConfigEntry<KeyCode> EditKey, DumpKey, FastKey, PrevKey, NextKey, DecKey, IncKey, ResetKey, BrakeKey, ParkKey;
        internal ConfigEntry<bool> BrakesOn;
        internal ConfigEntry<float> AimDistance;
        internal static ConfigEntry<bool> StretchAxles;

        internal TrailerSettings Big;
        internal TrailerSettings Small;

        private readonly Dictionary<int, TrailerState> _trailers = new Dictionary<int, TrailerState>();
        private readonly HashSet<string> _dumped = new HashSet<string>();
        private float _nextScan;
        private TrailerState _editing;
        private string _toast;
        private float _toastUntil;

        private void Awake()
        {
            Log = Logger;

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

            Big = new TrailerSettings(Config, "BigTrailer");
            Small = new TrailerSettings(Config, "SmallTrailer");

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
                Log.LogInfo("Scene ready: " + scene.name);
            };
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
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
        }
        private int _tickErrors;

        internal void DoUpdate()
        {
            if (Input.GetKeyDown(DumpKey.Value)) DumpAimed();

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 2f;
                Scan();
            }

            HandleEditing();

            if (!Enabled.Value) return;
            foreach (var t in _trailers.Values)
                if (t.Rb != null) t.ApplyWheels(SettingsFor(t));
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

        private bool NameMatches(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (var raw in ExcludeKeywords.Value.Split(','))
            {
                string k = raw.Trim().ToLowerInvariant();
                if (k.Length > 0 && n.Contains(k)) return false;
            }
            foreach (var raw in NameKeywords.Value.Split(','))
            {
                string k = raw.Trim().ToLowerInvariant();
                if (k.Length > 0 && n.Contains(k)) return true;
            }
            return false;
        }

        private void Scan()
        {
            // drop destroyed trailers
            var dead = new List<int>();
            foreach (var kv in _trailers) if (kv.Value.Rb == null) dead.Add(kv.Key);
            foreach (var id in dead) _trailers.Remove(id);
            if (_editing != null && _editing.Rb == null) _editing = null;

            Rigidbody[] all;
            try { all = FindObjectsOfType<Rigidbody>(); }
            catch { return; }

            // a trailer is the top object whose name matches; its body is that object's rigidbody,
            // or the heaviest non-wheel rigidbody inside it
            var best = new Dictionary<int, Rigidbody>();
            var roots = new Dictionary<int, Transform>();
            foreach (var rb in all)
            {
                if (rb == null) continue;
                Transform root = null;
                for (var t = rb.transform; t != null; t = t.parent)
                    if (NameMatches(t.name)) root = t; // keep the topmost match
                if (root == null) continue;
                int rid = root.GetInstanceID();
                Rigidbody cur;
                bool own = rb.transform == root;
                bool wheel = rb.name.ToLowerInvariant().Contains("wheel");
                if (!best.TryGetValue(rid, out cur)) { if (own || !wheel) { best[rid] = rb; roots[rid] = root; } continue; }
                if (cur.transform == root) continue;
                if (own || (!wheel && rb.mass > cur.mass)) best[rid] = rb;
            }

            foreach (var kv in best)
            {
                var rb = kv.Value; var root = roots[kv.Key];
                if (_trailers.ContainsKey(kv.Key)) continue;
                var state = new TrailerState(rb, root);
                _trailers[kv.Key] = state;
                Log.LogInfo("Found trailer '" + root.name + "' (" + (state.IsSmall ? "small" : "big") + "), body '" + rb.name + "': " +
                            state.Wheels.Count + " wheels, " + state.Axles.Count + " axle model(s), mass " + rb.mass.ToString("0"));
                if (_dumped.Add(state.IsSmall ? "small" : "big")) Log.LogInfo(Describe(rb, root));
            }

            if (!_scanLogged && SceneManager.GetActiveScene().name != "MainMenu")
            {
                _scanLogged = true;
                Log.LogInfo("Scan: " + all.Length + " bodies in the world, " + _trailers.Count + " trailer(s).");
            }

            foreach (var t in _trailers.Values) t.RefreshWheels();
        }

        private Camera GetCamera()
        {
            var cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled) return cam;
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.isActiveAndEnabled && (best == null || c.depth > best.depth)) best = c;
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

        private void UpdateHud()
        {
            string text = null;
            if (_editing != null && _editing.Rb != null)
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
                text = sb.ToString();
            }
            else if (_toast != null && Time.unscaledTime < _toastUntil) text = _toast;

            if (text == null) { if (_hudGo != null && _hudGo.activeSelf) _hudGo.SetActive(false); return; }
            if (_hudGo == null) BuildHud();
            if (!_hudGo.activeSelf) _hudGo.SetActive(true);
            if (_hudText.text != text) _hudText.text = text;
        }

        // ---- trailer brakes
        private float _nextCarSearch;
        private bool _brakeLogged;

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
                if (searchCars) t.Car = FindTowingCar(t);

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

        // the car this trailer hangs on: the nearest other wheeled body close to it
        private Rigidbody FindTowingCar(TrailerState t)
        {
            Rigidbody best = null;
            float bestD = 12f;
            var seen = new HashSet<int>();
            foreach (var wc in FindObjectsOfType<WheelController>())
            {
                if (wc == null || wc.transform.IsChildOf(t.Root)) continue;
                Rigidbody rb = null;
                for (var x = wc.transform; x != null; x = x.parent) { var r = x.GetComponent<Rigidbody>(); if (r != null) rb = r; }
                if (rb == null || !seen.Add(rb.GetInstanceID())) continue;
                foreach (var other in _trailers.Values) if (other.Rb == rb) { rb = null; break; }
                if (rb == null) continue;
                float d = Vector3.Distance(rb.worldCenterOfMass, t.Rb.worldCenterOfMass);
                if (d < bestD) { bestD = d; best = rb; }
            }
            return best;
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
        private void FixedUpdate() { if (Plugin != null) Guard("FixedUpdate", Plugin.DoFixedUpdate); }
        private void OnDestroy() { TrailerStancePlugin.Log.LogWarning("Runner destroyed."); }

        // Unity errors are not written to this game's log, so catch and write them ourselves (first few only)
        private void Guard(string where, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                if (_errors++ < 5) TrailerStancePlugin.Log.LogError(where + ": " + e);
            }
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
        public float[] GetDamp()
        {
            if (Wc != null && Wc.damper != null)
            {
                var d = Wc.damper;
                return new[] { d.bumpRate, d.reboundRate, d.slowBump, d.fastBump, d.slowRebound, d.fastRebound };
            }
            if (Col != null) return new[] { Col.suspensionSpring.damper };
            return null;
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
        public float[] TargetDamp(float mult, float antiBounce, float sprungMass)
        {
            var b = BaseDamp;
            var d = (float[])b.Clone();
            float crit = CriticalDamping(sprungMass);
            // largest coefficient the physics step can take without blowing up (half the stability limit)
            float cap = Mathf.Max(1f, sprungMass) / Mathf.Max(0.002f, Time.fixedDeltaTime);

            if (d.Length >= 6)
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
            else if (d.Length == 1)
            {
                d[0] = Mathf.Max(b[0] * mult, antiBounce * crit);
                d[0] = Mathf.Min(d[0], Mathf.Max(b[0], cap));
            }
            return d;
        }

        // damping actually felt over a bump (fast range), N per m/s
        public float EffectiveDamping()
        {
            var d = GetDamp();
            if (d == null || d.Length == 0) return 0f;
            if (d.Length >= 6) return d[0] * d[3];
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
        public float[] BaseDamp, LastDamp;
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
        public float BrakeLevel;
        public readonly bool IsSmall;
        public readonly List<WheelState> Wheels = new List<WheelState>();
        private Vector3 _baseCoM, _lastCoM;
        private bool _comInit;

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
            Wheels.RemoveAll(w => !w.Alive);
            foreach (var wc in Root.GetComponentsInChildren<WheelController>(true))
                if (!Wheels.Exists(w => w.Wc == wc)) AddWheel(new WheelState { Wc = wc });
            foreach (var col in Root.GetComponentsInChildren<WheelCollider>(true))
                if (!Wheels.Exists(w => w.Col == col)) AddWheel(new WheelState { Col = col });
            if (Wheels.Count != _axleWheelCount) FindAxles();
        }

        private void AddWheel(WheelState ws)
        {
            ws.BaseLocal = ws.LastLocal = ws.T.localPosition;
            ws.BaseRb = Rb.transform.InverseTransformPoint(ws.T.position);
            ws.BaseMaxForce = ws.LastMaxForce = ws.GetSpring();
            ws.BaseDamp = ws.LastDamp = ws.GetDamp();
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

        private static bool Same(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Mathf.Abs(a[i] - b[i]) > 0.001f + Mathf.Abs(b[i]) * 1e-4f) return false;
            return true;
        }

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
                var d = w.GetDamp();
                if (d != null && (w.LastDamp == null || !Same(d, w.LastDamp))) { w.BaseDamp = d; w.LastDamp = d; }
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

                if (w.BaseDamp != null)
                {
                    var d = w.TargetDamp(s.Damper.Value, s.AntiBounce.Value, Rb.mass / n);
                    if (!Same(d, w.LastDamp)) { w.SetDamp(d); w.LastDamp = d; }
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
