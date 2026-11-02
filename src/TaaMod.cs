using System;
using System.Collections.Generic;
using System.IO;
using Game.Settings;
using Railroader.Modding;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace LondoSpark.RailroaderTaa
{
    // Settings read from (and saved back to) config.json next to the DLL.
    [Serializable]
    public class AaConfig
    {
        public bool enabled = true;
        // "off", "fxaa", "smaa" or "taa". ("stp" is mapped to "taa": the build lacks STP's shaders.)
        public string mode = "taa";
        // Supersampling factor: >1 renders above the screen and scales down. 0.5..2.
        public float renderScale = 1f;
        // TAA stability preset: "sharp", "balanced", "stable", "verystable", or
        // "custom" to use the taa* values below. Steadier = more history, less jitter,
        // softer image and more ghosting in motion.
        public string taaPreset = "stable";
        public float taaJitterScale = 1f;
        // MSAA samples (1, 2, 4) for the non-temporal modes; temporal modes force 1.
        public int msaa = 2;
        // URP refuses TAA/STP on a camera that has overlay cameras stacked on it.
        // true re-plumbs the overlays as separate cameras so TAA can run (experimental).
        public bool detachCameraStack = false;
        // TAA tuning: quality 0 VeryLow .. 4 VeryHigh; lower baseBlendFactor = smoother but more ghosting.
        public int taaQuality = 3;
        public float taaBaseBlendFactor = 0.1f;
        public float taaVarianceClampScale = 0.9f;
        public float taaSharpening = 0.5f;
        // SMAA quality 0 Low, 1 Medium, 2 High.
        public int smaaQuality = 2;
        public bool showOverlay = true;
    }

    public class TaaMod : IModEntryPoint, IWorldLifecycle
    {
        internal static IModLogger Log;
        internal static AaConfig Config = new AaConfig();
        internal static string ConfigPath;
        private GameObject _host;

        public void OnModLoad(IModContext context)
        {
            Log = context.Log;
            ConfigPath = Path.Combine(context.ModDirectory, "config.json");
            try
            {
                if (File.Exists(ConfigPath))
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(ConfigPath), Config);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not read " + ConfigPath + "; using defaults");
            }
            Log.Info($"AA mod loaded: mode={Config.mode} renderScale={Config.renderScale} msaa={Config.msaa} detachCameraStack={Config.detachCameraStack}");
        }

        internal static void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonUtility.ToJson(Config, true));
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not save " + ConfigPath);
            }
        }

        public void OnModUnload()
        {
            OnWorldUnload();
        }

        public void OnWorldLoad()
        {
            if (_host != null || !Config.enabled)
            {
                return;
            }
            _host = new GameObject("RailroaderAa");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<AaApplier>();
        }

        public void OnWorldUnload()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }
        }
    }

    // Applies the chosen AA every frame (the game rebuilds its URP asset clone whenever
    // graphics options change) and handles the F10/F11 hotkeys.
    public class AaApplier : MonoBehaviour
    {
        // STP is not offered: the game build strips its shader resources and URP throws every frame.
        private static readonly string[] Modes = { "off", "fxaa", "smaa", "taa" };
        private static readonly float[] Scales = { 1f, 1.25f, 1.5f, 1.75f, 2f };
        private static readonly int[] MsaaLevels = { 1, 2, 4 };
        private static readonly string[] Presets = { "sharp", "balanced", "stable", "verystable" };

        // (quality, baseBlendFactor, varianceClampScale, jitterScale, sharpening) per preset.
        // Lower blend keeps more history; higher clamp rejects less of it; lower jitter
        // moves samples less, which is what makes sub-pixel rails crawl.
        private static (int, float, float, float, float) PresetValues(AaConfig cfg)
        {
            switch ((cfg.taaPreset ?? "").ToLowerInvariant())
            {
                case "sharp": return (3, 0.10f, 0.90f, 1.00f, 0.50f);
                case "balanced": return (4, 0.06f, 1.00f, 0.85f, 0.40f);
                case "stable": return (4, 0.035f, 1.10f, 0.70f, 0.35f);
                case "verystable": return (4, 0.02f, 1.20f, 0.50f, 0.30f);
                default: return (cfg.taaQuality, cfg.taaBaseBlendFactor, cfg.taaVarianceClampScale, cfg.taaJitterScale, cfg.taaSharpening);
            }
        }

        private readonly List<Camera> _detached = new List<Camera>();
        private Camera _detachedFrom;
        private string _status = "";
        private string _lastLogged = "";
        private float _overlayUntil;

        private void Start()
        {
            _overlayUntil = Time.unscaledTime + 8f;
            Invoke(nameof(DumpRenderSetup), 10f);
        }

        // One-off inventory of every camera and the active renderer, for diagnosing
        // which camera actually produces the image on screen.
        private void DumpRenderSetup()
        {
            var lines = new List<string> { "Render setup dump:" };
            UniversalRenderPipelineAsset asset = GraphicsQualityApplicator.ActiveAsset;
            if (asset != null)
            {
                lines.Add($"  asset={asset.name} renderScale={asset.renderScale} msaa={asset.msaaSampleCount} filter={asset.upscalingFilter} hdr={asset.supportsHDR}");
            }
            ScriptableRendererData rendererData = GraphicsQualityApplicator.DefaultRenderer;
            if (rendererData is UniversalRendererData urd)
            {
                lines.Add($"  renderer={urd.name} mode={urd.renderingMode} postProcessData={(urd.postProcessData != null ? urd.postProcessData.name : "NULL")}");
                foreach (ScriptableRendererFeature f in urd.rendererFeatures)
                {
                    if (f != null)
                    {
                        lines.Add($"    feature {f.GetType().FullName} '{f.name}' active={f.isActive}");
                    }
                }
            }
            Camera[] all = Resources.FindObjectsOfTypeAll<Camera>();
            foreach (Camera c in all)
            {
                if (c.hideFlags != HideFlags.None && c.gameObject.scene.name == null)
                {
                    continue;
                }
                UniversalAdditionalCameraData d = c.GetComponent<UniversalAdditionalCameraData>();
                string rt = c.targetTexture != null ? $"{c.targetTexture.width}x{c.targetTexture.height} '{c.targetTexture.name}'" : "screen";
                lines.Add($"  camera '{c.name}' path={Path(c.transform)} enabled={c.isActiveAndEnabled} depth={c.depth} target={rt} type={c.cameraType}"
                    + (d != null ? $" urpType={d.renderType} post={d.renderPostProcessing} aa={d.antialiasing} stack={d.cameraStack?.Count ?? 0} renderer={d.scriptableRenderer?.GetType().Name}" : " (no URP data)"));
            }
            foreach (string l in lines)
            {
                TaaMod.Log.Info(l);
            }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                p = t.name + "/" + p;
            }
            return p;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }
            AaConfig cfg = TaaMod.Config;
            bool changed = false;
            // Only F10/F11 are used: the game binds F1-F8 (F5-F8 fly camera), F9 is the
            // user's Voxtype key and F12 is unavailable. Shift selects the second function.
            bool shift = kb.shiftKey.isPressed;
            if (kb.f10Key.wasPressedThisFrame && !shift)
            {
                cfg.mode = Next(Modes, Array.IndexOf(Modes, cfg.mode.ToLowerInvariant()));
                changed = true;
            }
            if (kb.f10Key.wasPressedThisFrame && shift)
            {
                cfg.msaa = Next(MsaaLevels, Array.IndexOf(MsaaLevels, cfg.msaa));
                changed = true;
            }
            if (kb.f11Key.wasPressedThisFrame && !shift)
            {
                cfg.taaPreset = Next(Presets, Array.IndexOf(Presets, (cfg.taaPreset ?? "").ToLowerInvariant()));
                changed = true;
            }
            if (kb.f11Key.wasPressedThisFrame && shift)
            {
                cfg.renderScale = Next(Scales, Array.FindIndex(Scales, s => Mathf.Abs(s - cfg.renderScale) < 0.01f));
                changed = true;
            }
            if (changed)
            {
                TaaMod.SaveConfig();
                _overlayUntil = Time.unscaledTime + 4f;
            }
        }

        private static T Next<T>(T[] values, int index)
        {
            return values[(index + 1) % values.Length];
        }

        private void LateUpdate()
        {
            AaConfig cfg = TaaMod.Config;
            string mode = (cfg.mode ?? "taa").ToLowerInvariant();
            if (mode == "stp")
            {
                cfg.mode = mode = "taa";
                TaaMod.Log.Warning("STP is unavailable in this game build (missing shader resources); using TAA instead");
            }
            bool temporal = mode == "taa" || mode == "stp";

            UniversalRenderPipelineAsset asset = GraphicsQualityApplicator.ActiveAsset;
            if (asset != null)
            {
                int msaa = temporal ? 1 : Mathf.Clamp(cfg.msaa, 1, 8);
                if (asset.msaaSampleCount != msaa)
                {
                    asset.msaaSampleCount = msaa;
                }
                float scale = Mathf.Clamp(cfg.renderScale, 0.5f, 2f);
                if (Mathf.Abs(asset.renderScale - scale) > 0.001f)
                {
                    asset.renderScale = scale;
                }
                // The game selects FSR1 when its own render scale is below 1; keep ours.
                UpscalingFilterSelection filter = mode == "stp" ? UpscalingFilterSelection.STP : UpscalingFilterSelection.Auto;
                if (asset.upscalingFilter != filter)
                {
                    asset.upscalingFilter = filter;
                }
            }

            Camera main = null;
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam.cameraType != CameraType.Game || cam.targetTexture != null || _detached.Contains(cam))
                {
                    continue;
                }
                UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
                if (data == null || data.renderType != CameraRenderType.Base)
                {
                    continue;
                }
                if (main == null || cam == Camera.main)
                {
                    main = cam;
                }
                ApplyToCamera(cam, data, mode, cfg);
            }

            UpdateStackDetach(main, temporal && cfg.detachCameraStack);
            UpdateStatus(main, asset, mode, temporal, cfg);
        }

        private static void ApplyToCamera(Camera cam, UniversalAdditionalCameraData data, string mode, AaConfig cfg)
        {
            AntialiasingMode wanted;
            switch (mode)
            {
                case "fxaa": wanted = AntialiasingMode.FastApproximateAntialiasing; break;
                case "smaa": wanted = AntialiasingMode.SubpixelMorphologicalAntiAliasing; break;
                case "taa":
                case "stp": wanted = AntialiasingMode.TemporalAntiAliasing; break;
                default: wanted = AntialiasingMode.None; break;
            }
            if (wanted != AntialiasingMode.None && !data.renderPostProcessing)
            {
                data.renderPostProcessing = true;
            }
            if (data.antialiasing != wanted)
            {
                data.antialiasing = wanted;
            }
            AntialiasingQuality smaaQuality = (AntialiasingQuality)Mathf.Clamp(cfg.smaaQuality, 0, 2);
            if (data.antialiasingQuality != smaaQuality)
            {
                data.antialiasingQuality = smaaQuality;
            }
            if (wanted == AntialiasingMode.TemporalAntiAliasing)
            {
                if (cam.allowDynamicResolution)
                {
                    cam.allowDynamicResolution = false;
                }
                ref TemporalAA.Settings taa = ref data.taaSettings;
                (int quality, float blend, float clamp, float jitter, float sharpen) = PresetValues(cfg);
                taa.quality = (TemporalAAQuality)Mathf.Clamp(quality, 0, 4);
                taa.baseBlendFactor = Mathf.Clamp01(blend);
                taa.varianceClampScale = Mathf.Clamp(clamp, 0.6f, 1.2f);
                taa.jitterScale = Mathf.Clamp01(jitter);
                taa.contrastAdaptiveSharpening = Mathf.Clamp01(sharpen);
            }
        }

        // URP disables TAA on a base camera with a non-empty stack. Detaching turns each
        // overlay into its own base camera drawn afterwards without clearing colour.
        private void UpdateStackDetach(Camera main, bool detach)
        {
            if (detach && main != null && _detached.Count == 0)
            {
                UniversalAdditionalCameraData data = main.GetUniversalAdditionalCameraData();
                List<Camera> stack = data.cameraStack;
                if (stack == null || stack.Count == 0)
                {
                    return;
                }
                for (int i = 0; i < stack.Count; i++)
                {
                    Camera overlay = stack[i];
                    if (overlay == null)
                    {
                        continue;
                    }
                    overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Base;
                    overlay.clearFlags = CameraClearFlags.Depth;
                    overlay.depth = main.depth + 1 + i;
                    _detached.Add(overlay);
                }
                stack.Clear();
                _detachedFrom = main;
                TaaMod.Log.Info("Detached " + _detached.Count + " overlay camera(s) from " + main.name + " so TAA can run");
            }
            else if (!detach && _detached.Count > 0)
            {
                if (_detachedFrom != null)
                {
                    List<Camera> stack = _detachedFrom.GetUniversalAdditionalCameraData().cameraStack;
                    foreach (Camera overlay in _detached)
                    {
                        if (overlay == null)
                        {
                            continue;
                        }
                        overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
                        stack.Add(overlay);
                    }
                }
                TaaMod.Log.Info("Restored " + _detached.Count + " overlay camera(s)");
                _detached.Clear();
                _detachedFrom = null;
            }
        }

        private void UpdateStatus(Camera main, UniversalRenderPipelineAsset asset, string mode, bool temporal, AaConfig cfg)
        {
            string blocked = "";
            string stackInfo = "";
            if (main != null)
            {
                UniversalAdditionalCameraData data = main.GetUniversalAdditionalCameraData();
                int stackCount = data.cameraStack?.Count ?? 0;
                if (stackCount > 0)
                {
                    var names = new List<string>();
                    foreach (Camera c in data.cameraStack)
                    {
                        names.Add(c != null ? c.name : "null");
                    }
                    stackInfo = " stack=[" + string.Join(", ", names) + "]";
                }
                if (temporal)
                {
                    if (stackCount > 0) blocked += " camera stack(" + stackCount + ")";
                    if (main.allowDynamicResolution) blocked += " dynamic-res";
                    if (!data.renderPostProcessing) blocked += " post-processing off";
                    if (asset != null && asset.msaaSampleCount > 1) blocked += " MSAA";
                }
            }
            _status = $"AA: {mode.ToUpperInvariant()} (F10)   MSAA: {(temporal ? 1 : cfg.msaa)}x (Shift+F10)   Stability: {cfg.taaPreset} (F11)   Scale: {cfg.renderScale:0.00}x (Shift+F11)"
                + (blocked.Length > 0 ? "   TAA BLOCKED by:" + blocked : "");
            string logLine = _status + "   camera=" + (main != null ? main.name : "none") + stackInfo + " detached=" + _detached.Count;
            if (logLine != _lastLogged)
            {
                _lastLogged = logLine;
                TaaMod.Log.Info(logLine);
            }
        }

        private void OnGUI()
        {
            if (!TaaMod.Config.showOverlay || Time.unscaledTime > _overlayUntil)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.box) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            GUI.Box(new Rect(20, Screen.height - 70, Screen.width - 40, 46), _status, style);
        }
    }
}
