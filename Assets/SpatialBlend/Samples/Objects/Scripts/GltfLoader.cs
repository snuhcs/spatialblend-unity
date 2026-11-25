// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.IO;
using UnityEngine;
using UnityEngine.Android;
using GLTFast;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using SpatialBlend;
using SpatialBlend.Interactions;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Objects;

namespace SpatialBlend.Samples.Objects
{
public class GltfLoader : MonoBehaviour
{
    private static GltfLoader _instance;
    private static SpatialBlendSettings _settings;
    private string _androidDownloadPath = null;
    private string _presetAddressKey = null;
    private string _agentAddressKey = null;
    private GameObject _parentObject;
    private GameObject _objPresetPrefab;
    private bool _isAgentLoaded = false;
    private bool _debugLog = false;
    private int _targetLayer = -1;
    private AsyncOperationHandle<GameObject>? _presetHandle;
    private AsyncOperationHandle<GameObject>? _agentHandle;

    private void Awake()
    {
        _instance = this;
        _parentObject = GameObject.Find("Objects");
        _targetLayer = LayerMask.NameToLayer("VirtualObject");
        LoadSpatialBlendSettings(); // call in start()?
    }

    public static GltfLoader EnsureInstance()
    {
        if (_instance != null)
        {
            return _instance;
        }

        var go = new GameObject("Object Loader");
        _instance = go.AddComponent<GltfLoader>();
        _instance.EnsureAgent(go);
        _instance.EnsurePresetLoadedSync();
        return _instance;
    }

    public async void LoadModelFromName(string fileName, string objName = null)
    {
        string fullPath = GetPlatformPath(fileName);

        if (!CheckPermission())
        {
            if (_debugLog) Debug.LogWarning("[GltfLoader] Requesting permission... Please approve and try again.");
            return;
        }
        if (!File.Exists(fullPath))
        {
            if (_debugLog) Debug.LogError($"[GltfLoader] File not found: {fullPath}");
            return;
        }
        await LoadAndInstantiate(fullPath, objName);
    }

    /// <summary>
    /// Loads a GLB from an absolute path. Scan results use this: the session library writes next to
    /// the output prefix it was given, which is not the Download folder LoadModelFromName searches.
    /// </summary>
    /// <param name="configure">
    /// Runs on the loaded object after it is instantiated and fitted, and before it is given its content
    /// and registered for sharing. The scan sink binds the annotated box's spatial authoring here, so the
    /// source shares the declaration with the object.
    /// </param>
    /// <param name="box">
    /// The box the user drew around the object, in world space. It fixes the object's metric scale and its
    /// place (v1 anchorPosition and anchorBoxSize). Without it the object takes
    /// the server's pose from the object and camera info files that sit next to the model.
    /// </param>
    public async void LoadModelFromPath(string fullPath, string objName = null, System.Action<GameObject> configure = null,
        ScanRegionHint? box = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            Debug.LogWarning($"[GltfLoader] Model file not found: {fullPath}");
            return;
        }
        await LoadAndInstantiate(fullPath, objName, configure, box);
    }

    private string GetPlatformPath(string fileName)
    {
#if UNITY_EDITOR
        var path = Path.Combine(Application.dataPath, "SpatialBlend/Samples/Objects/RuntimeModels", fileName);
        return path; 
#else
        string nameOnly = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string fileNameSearch = nameOnly + "*" + extension;
        string filePath = GetLatestFile(_androidDownloadPath, fileNameSearch);
        return filePath;
#endif
    }

    private async Task GltfInstantiate(GltfImport gltfImport, GameObject target, bool isSource,
        string modelPath = null, ScanRegionHint? box = null)
    {
        var instSettings = new InstantiationSettings
        {
            Layer = _targetLayer,
            SceneObjectCreation = SceneObjectCreation.Never
        };
        var instantiator = new GameObjectInstantiator(gltfImport, target.transform, null, instSettings);
        await gltfImport.InstantiateMainSceneAsync(instantiator);
        SetupLoadedObject(target, isSource, modelPath, box, ServerPoseAvailable);
    }

    private async Task LoadAndInstantiate(string filePath, string objName, System.Action<GameObject> configure = null,
        ScanRegionHint? box = null)
    {
        // TODO(dongho): Block the gizmo mode during this process
        string fileUri = $"file://{filePath}";
        string localPath = filePath.Replace("file://", "");
        if (!File.Exists(localPath))
        {
            if (_debugLog) Debug.LogError($"[GltfLoader] File not found: {localPath}");
            return;
        }    
        GameObject modelRoot = CreateObjectTemplate(objName ?? Path.GetFileNameWithoutExtension(filePath));
        if (modelRoot == null) return;

        var settings = new ImportSettings {
            GenerateMipMaps = true,
            AnisotropicFilterLevel = 3
        };
        GltfImport gltfImport = new GltfImport(materialGenerator: SpatialBlend.Objects.GltfOcclusionMaterial.CreateGenerator());
        bool success = await gltfImport.Load(fileUri, settings);

        if (success)
        {
            await GltfInstantiate(gltfImport, modelRoot, true, filePath, box);
            configure?.Invoke(modelRoot);

            var sbNativeEntity = modelRoot.GetComponent<SbNativeEntity>();
            if (sbNativeEntity != null)
            {
                if (File.Exists(filePath))
                {
                    byte[] bytes = File.ReadAllBytes(filePath);
                    sbNativeEntity.SetContentData(bytes);
                }

                // Before the app has started, the app's own pre-existing content
                // registration shares this entity once, when its content is ready.
                var sbApp = FindFirstObjectByType<SbApp>();
                if (sbApp != null && sbApp.IsAppStarted)
                {
                    // Don't propagate the VO-RO synced objects
                    sbApp.RegisterGameObject(modelRoot, false);
                }
            }
        }
        else
        {
            if (_debugLog) Debug.LogError("[GltfLoader] Failed to load");
            Destroy(modelRoot);
        }
    }

    public async Task<bool> LoadModelFromBytes(GameObject target, byte[] data)
    {
        try
        {
            GltfImport gltfImport = new GltfImport(materialGenerator: SpatialBlend.Objects.GltfOcclusionMaterial.CreateGenerator());
            bool success = await gltfImport.LoadGltfBinary(data);

            if (success)
            {
                await GltfInstantiate(gltfImport, target, false);
                
                return true;
            }
            else
            {
                if (_debugLog) Debug.LogError("[GltfLoader] Failed to Parse GLTF Binary");
                return false;
            }
        }
        catch (System.Exception e)
        {
            if (_debugLog) Debug.LogError($"[GltfLoader] Exception during byte loading: {e.Message}");
            return false;
        }
    }

    public GameObject CreateObjectTemplate(string objName = null)
    {
        // TODO(dongho): If other object is in gizmo mode, delay this request
        EnsurePresetLoadedSync();
        if (_objPresetPrefab == null)
        {
            Debug.LogError("[GltfLoader] cannot create object template.");
            return null;
        }
        GameObject modelRoot = Instantiate(_objPresetPrefab);
        modelRoot.name = objName ?? "ObjectTemplate";
        if (_parentObject != null)
        {
            modelRoot.transform.SetParent(_parentObject.transform);
        }
        
        modelRoot.layer = _targetLayer;
        return modelRoot;
    }

    /// <summary>
    /// On the device the object takes the server's pose from the info files that sit next to the model. The
    /// Editor has none, as in v1.
    /// </summary>
    private static bool ServerPoseAvailable
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    public void SetupLoadedObject(GameObject modelRoot, bool isSource = false, string modelPath = null,
        ScanRegionHint? box = null)
    {
        SetupLoadedObject(modelRoot, isSource, modelPath, box, ServerPoseAvailable);
    }

    internal void SetupLoadedObject(GameObject modelRoot, bool isSource, string modelPath, ScanRegionHint? box,
        bool useServerPose)
    {
        // The user's own scan stands for the real object it was scanned from: installed where that object is (its scan box)
        // and never moved. A peer's copy is a virtual object the receiving user places (ReceivedCopy).
        if (isSource && modelRoot.GetComponent<SpatialBlend.PhysicalCounterpart>() == null)
            modelRoot.AddComponent<SpatialBlend.PhysicalCounterpart>();
        ScanObjectFit.LiftWorldNode(modelRoot);
        ScanObjectFit.Normalization normalization = ScanObjectFit.Normalize(modelRoot);
        ScanObjectFit.FitCollider(modelRoot);
        // Apply when mesh data is loaded
        ApplyTransform(modelRoot, isSource, normalization, modelPath, box, useServerPose);
    }

    private void EnsurePresetLoadedSync()
    {
        if (_objPresetPrefab != null) return;
        if (string.IsNullOrEmpty(_presetAddressKey)) LoadSpatialBlendSettings();
        // No key: Addressables would throw on it (a null key); the caller reports that no template could be made.
        if (string.IsNullOrEmpty(_presetAddressKey)) return;

        var handle = Addressables.LoadAssetAsync<GameObject>(_presetAddressKey);
        _presetHandle = handle;
        _objPresetPrefab = handle.WaitForCompletion();
    }

    private void OnDestroy()
    {
        if (_presetHandle.HasValue && _presetHandle.Value.IsValid())
        {
            Addressables.Release(_presetHandle.Value);
        }
        if (_agentHandle.HasValue && _agentHandle.Value.IsValid())
        {
            Addressables.Release(_agentHandle.Value);
        }
    }

    private void EnsureAgent(GameObject host)
    {
        if (_isAgentLoaded) return;

        if (!string.IsNullOrEmpty(_agentAddressKey))
        {
            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(_agentAddressKey);
                _agentHandle = handle;
                var agentPrefab = handle.WaitForCompletion();
                if (agentPrefab != null)
                {
                    var agent = Instantiate(agentPrefab, host.transform);
                    _isAgentLoaded = true;
                    return;
                }
            }
            catch (System.Exception e)
            {
                if (_debugLog) Debug.LogWarning($"[GltfLoader] agent load failed: {e.Message}");
            }
        }
    }

    // Every instance reads the keys into its own fields. The loader is made on demand in the scene and is destroyed with it (a
    // Leave reloads the scene), so a later instance must not skip this because an earlier one already found the settings: it
    // then had no preset key, and making a template threw inside Addressables (the rejoined peer lost the partner's scan).
    private void LoadSpatialBlendSettings()
    {
        SpatialBlendSettings settings = SpatialBlendManager.CurrentSettings != null ? SpatialBlendManager.CurrentSettings : _settings;
        if (settings == null) return;
        _settings = settings;
        if (!string.IsNullOrEmpty(settings.objectPresetAddressKey))
        {
            _presetAddressKey = settings.objectPresetAddressKey;
        }
        if (!string.IsNullOrEmpty(settings.agentAddressKey))
        {
            _agentAddressKey = settings.agentAddressKey;
        }
        if (!string.IsNullOrEmpty(settings.androidDownloadPath))
        {
            _androidDownloadPath = settings.androidDownloadPath;
        }
    }

    /// <summary>
    /// As v1 ApplyTransform. A scan with a box takes the box's metric scale and
    /// place. A scan without one takes the server pose from the info files. The source tells its peers the
    /// scale; a peer applies the scale it was told.
    /// </summary>
    private void ApplyTransform(GameObject modelRoot, bool isSource, ScanObjectFit.Normalization normalization,
        string modelPath, ScanRegionHint? box, bool useServerPose)
    {
        var nativeEntity = modelRoot.GetComponent<SbNativeEntity>();
        if (!isSource)
        {
            // The peer's object is the size the source's is (as v1): the shared scale. Its place is the
            // staging pose assigned when the entity was created.
            if (nativeEntity != null && nativeEntity.TryGetSharedScale(out Vector3 sharedScale))
            {
                modelRoot.transform.localScale = sharedScale;
            }
            return;
        }

        // The user's own scan is installed where the real object is. The box the user drew around it is that place; the
        // server's pose, when there is one, only adds the rotation. Without a server pose (the Editor, or info files missing
        // on the device) the box alone places it: never a recenter in front of the user.
        Matrix4x4 serverPose = Matrix4x4.identity;
        bool hasServerPose = useServerPose && TryReadServerPose(modelPath, out serverPose);
        if (box.HasValue)
        {
            if (useServerPose && !hasServerPose)
                Debug.LogWarning($"[GltfLoader] Camera or object info missing next to {modelPath}; installing the scan at its box.");
            ApplyBox(modelRoot, nativeEntity, box.Value, hasServerPose ? serverPose.rotation : Quaternion.identity);
            return;
        }
        if (hasServerPose)
        {
            ScanObjectFit.PlaceAtServerPose(modelRoot, serverPose, normalization);
            Quaternion upright = ScanObjectFit.LevelRoot(modelRoot);
            if (nativeEntity != null) nativeEntity.SetUprightRotation(upright);
            // The collider was fitted to the unturned geometry: it follows the model as it now stands.
            ScanObjectFit.FitCollider(modelRoot);
            return;
        }
        if (modelRoot.GetComponent<SpatialBlend.PhysicalCounterpart>() != null)
        {
            // No box and no server pose: nothing says where the real object is. It stays where it loaded and is not moved.
            Debug.LogError($"[GltfLoader] '{modelRoot.name}' has neither a scan box nor a server pose; it cannot be installed at the real object.");
            return;
        }
        PlacementMotif.RequestGlobalRecenter();
    }

    private static void ApplyBox(GameObject modelRoot, SbNativeEntity nativeEntity, ScanRegionHint box,
        Quaternion rotation)
    {
        float fullScale = ScanObjectFit.PlaceInBox(modelRoot, box, rotation);
        if (nativeEntity != null) nativeEntity.SetSharedScale(Vector3.one * fullScale);
        // The root stays level (the object's frame: its region, its pose for the core and the peers); the server's upright
        // correction goes onto the geometry and to the peers, so their copy stands as this one does.
        Quaternion upright = ScanObjectFit.LevelRoot(modelRoot);
        if (nativeEntity != null) nativeEntity.SetUprightRotation(upright);
        // The source sees its scan as a small, lifted, slowly turning preview (as in v1),
        // centred over its box. The peers' copy is at the full scale just set.
        ScanObjectFit.ApplyPreview(modelRoot, box);
        StartPreviewMotion(modelRoot);
        BuildFootprintRing(modelRoot.transform, box);
    }

    /// <summary>
    /// The preview turns at 30 degrees per second about its own mesh centre, so it stays over the box. No outline glow (too
    /// strong); the box's floor ring marks where the real object is (<see cref="BuildFootprintRing"/>).
    /// </summary>
    private static void StartPreviewMotion(GameObject modelRoot)
    {
        GameObject rotateTarget = ResolveRotationTarget(modelRoot);
        var rotator = rotateTarget.GetComponent<SlowRotate>();
        if (rotator == null) rotator = rotateTarget.AddComponent<SlowRotate>();
        rotator.DegreesPerSecond = PreviewDegreesPerSecond;
        rotator.LocalPivot = ScanObjectFit.GeometryPivot(rotateTarget.transform);
    }

    private const float PreviewDegreesPerSecond = 30f;

    /// <summary>The child of a scan that draws the ring under its box.</summary>
    internal const string FootprintRingName = "ScanFootprintRing";
    internal const int FootprintRingSegments = 48;
    internal const float FootprintRingWidth = 0.008f;
    internal const float FootprintRingAlpha = 0.6f;
    // Just above the box bottom, so the ring does not flicker against the floor.
    internal const float FootprintRingLift = 0.003f;

    /// <summary>
    /// A thin flat circle on the box's bottom face, around its footprint, marking the bottom of the box with
    /// a circle: one loop line in the accent, with the one shared outline material (<see cref="SpatialBlend.Samples.FreeformCollaboration.UI.SceneAnchorHighlight.LineMaterial"/>).
    /// A child of the scan, so it goes where the scan is carried; flat and at 1:1 whatever the root's rotation and box-fit scale. Built
    /// once (a scan that has one keeps it); nothing runs per frame.
    /// </summary>
    internal static GameObject BuildFootprintRing(Transform root, ScanRegionHint box)
    {
        if (root == null) return null;
        Transform existing = root.Find(FootprintRingName);
        if (existing != null) return existing.gameObject;
        var ring = new GameObject(FootprintRingName);
        ring.layer = root.gameObject.layer;
        ring.transform.SetParent(root, false);
        // Its local XY plane is the floor plane (local Z points down), the plane a TransformZ line lies in.
        ring.transform.SetPositionAndRotation(new Vector3(box.BoxCenter.x, box.YBottom + FootprintRingLift, box.BoxCenter.z),
            Quaternion.Euler(90f, 0f, 0f));
        float rootScale = root.lossyScale.y;
        ring.transform.localScale = Vector3.one / (Mathf.Abs(rootScale) > 1e-6f ? Mathf.Abs(rootScale) : 1f);

        float radius = 0.5f * Mathf.Sqrt(box.BoxSize.x * box.BoxSize.x + box.BoxSize.z * box.BoxSize.z);
        var points = new Vector3[FootprintRingSegments];
        for (int i = 0; i < FootprintRingSegments; i++)
        {
            float a = i * Mathf.PI * 2f / FootprintRingSegments;
            points[i] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
        }
        var line = ring.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.alignment = LineAlignment.TransformZ;
        line.widthMultiplier = FootprintRingWidth;
        line.numCornerVertices = 0;
        line.numCapVertices = 0;
        Color accent = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.Accent;
        line.startColor = line.endColor = new Color(accent.r, accent.g, accent.b, FootprintRingAlpha);
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        Material material = SpatialBlend.Samples.FreeformCollaboration.UI.SceneAnchorHighlight.LineMaterial;
        if (material != null) line.sharedMaterial = material;
        line.positionCount = points.Length;
        line.SetPositions(points);
        return ring;
    }

    /// <summary>v1 ResolveRotationTarget: turn the geometry, so the declared region stays on the root.</summary>
    private static GameObject ResolveRotationTarget(GameObject modelRoot)
    {
        if (modelRoot == null) return null;
        Transform geometry = modelRoot.transform.Find(ScanObjectFit.GeometryNodeName);
        if (geometry != null) return geometry.gameObject;
        var renderer = modelRoot.GetComponentInChildren<Renderer>();
        if (renderer != null) return renderer.gameObject;
        return modelRoot;
    }

    private static bool TryReadServerPose(string modelPath, out Matrix4x4 objectToWorld)
    {
        return ScanObjectFit.TryReadObjectToWorld(ScanObjectFit.CameraInfoPath(modelPath),
            ScanObjectFit.ObjectInfoPath(modelPath), out objectToWorld);
    }

    private string GetLatestFile(string directory, string pattern)
    {
        var dirInfo = new DirectoryInfo(directory);
        return dirInfo.GetFiles(pattern)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault()?.FullName;
    }

    private bool CheckPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageRead))
        {
            Permission.RequestUserPermission(Permission.ExternalStorageRead);
            return false;
        }
#endif
        return true;
    }
}   
}