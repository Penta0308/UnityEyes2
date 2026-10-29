using UnityEngine;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using SimpleJSON;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

[System.Serializable]
public class CameraIntrinsics{
    public float fx;
    public float fy;
    public float cx;
    public float cy;
    public int width;
    public int height;
}

[System.Serializable]
public class CameraConfig{
    public string name;
    public Vector3 position;
    public Vector3 rotation;
    public bool is_orthographic;
    public CameraIntrinsics intrinsics;
}

[System.Serializable]
public class CameraConfiguration{
    public List<CameraConfig> cameras;
}

[System.Serializable]
public class EyeParameters
{
    public Vector2 pupilSizeRange;
    public Vector2 irisSizeRange;
    public float defaultYaw;
    public float defaultPitch;
    public float yawNoise;
    public float pitchNoise;
}


public class SynthesEyesServer : MonoBehaviour{
    public GameObject lightDirectionalObj;
    public GameObject eyeballObj;
    public GameObject eyeRegionObj;
    public GameObject eyeRegionSubdivObj;
    public GameObject eyeWetnessObj;
    public GameObject eyeWetnessSubdivObj;
    public GameObject eyeLashesObj;

    // Component references
    private EyeballController eyeball;
    private EyeRegionController eyeRegion;
    private SubdivMesh eyeRegionSubdiv;
    private EyeWetnessController eyeWetness;
    private SubdivMesh eyeWetnessSubdiv;
    private DeformEyeLashes[] eyeLashes;

    private LightingController lightingController;

    // Render settings for randomization
    public float defaultCameraPitch = 0;
    public float defaultCameraYaw = 0;
    public float cameraPitchNoise = Mathf.Deg2Rad * 20;
    public float cameraYawNoise = Mathf.Deg2Rad * 40;
    public float defaultEyePitch = 0;
    public float defaultEyeYaw = 0;
    public float eyePitchNoise = 20;
    public float eyeYawNoise = 40;

    private float randomizeSceneStartTime = 0f;
    private int randomizeSceneCallCount = 0;

    // Camera management fields
    private List<Camera> cameraList = new List<Camera>();
    private int currentCameraIndex = 0;
    public string jsonConfigPath = "gradpupil/gradpupil_autoload.json";
    private string activeConfigPath;

    private string outputPath;
    public string outputFolderName = "imgs";

    // num_samples is the global dataset size. Each shard derives its own local
    // save count while retaining globally unique identity and sample indices.
    private int totalSamplesToGenerate = 10000;
    private int maxSamplesToSave = 10000;
    private GroupedRandomizationController datasetRandomizer;

    private string commandLineConfigPath;
    private string commandLineOutputPath;
    private int commandLineSampleCount = -1;
    private int commandLineSeed = -1;
    private int shardIndex = 0;
    private int shardCount = 1;
    private bool quitOnComplete = false;

    // Motion Center Feature
    private bool useMotionCenter = true;
    private Vector3 cameraArrayCenter = Vector3.zero;
    private Vector3 cameraArrayRotation = Vector3.zero;
    private Vector3 cameraArrayPositionNoise = Vector3.zero;
    private Vector3 cameraArrayRotationNoise = Vector3.zero;

    private struct CameraDistanceBand
    {
        public float minCm;
        public float maxCm;
        public float weight;
    }
    private readonly List<CameraDistanceBand> cameraDistanceBands = new List<CameraDistanceBand>();
    private float cameraDistanceTotalWeight = 0f;

    private struct CameraAngleBand
    {
        public float minDeg;
        public float maxDeg;
        public float weight;
    }
    private readonly List<CameraAngleBand> cameraAngleBands = new List<CameraAngleBand>();
    private float cameraAngleTotalWeight = 0f;
    private Vector2 cameraRollRangeDeg = new Vector2(-180f, 180f);

    private GameObject randomizedBackdrop;
    private Texture2D randomizedBackdropTexture;
    private Material randomizedBackdropMaterial;

    private GameObject cameraParent;
    private List<Vector3> cameraOriginalPositions = new List<Vector3>();
    private List<Vector3> cameraOriginalRotations = new List<Vector3>();

    private List<Vector3> cameraExtrinsicsPositionNoise = new List<Vector3>();
    private List<Vector3> cameraExtrinsicsRotationNoise = new List<Vector3>();
    // Original local positions/rotations within cameraParent (motion-center mode only),
    // used to apply per-camera noise on top of the array-level noise each frame.
    private List<Vector3> cameraOriginalLocalPositions = new List<Vector3>();
    private List<Vector3> cameraOriginalLocalRotations = new List<Vector3>();

    private List<CameraIntrinsics> cameraOriginalIntrinsics = new List<CameraIntrinsics>();

    // Point light tracking
    private List<Light> pointLightList = new List<Light>();
    private List<Vector3> pointLightOriginalPositions = new List<Vector3>();
    private List<Vector3> pointLightOriginalRotations = new List<Vector3>();
    private List<bool> pointLightArrayMounted = new List<bool>();

    private EyeParameters eyeParameters;

    public bool isSavingData = false;
    public bool autoStartOnConfig = true;
    public bool nirLightingMode = true;
    private bool isSavingFrame = false;

	private Mesh eyemesh;

    int framesSaved = 0;

    private Vector3 xmlBasePosition;
    private Vector3 xmlBaseEulerAngles;

    private EyeVisualizationManager visualizationManager;

    /**
     * Initialize the SynthesEyes scene and camera configuration.
     *
     * Loads camera and lighting setup from a JSON configuration file, sets up component references,
     * and applies eye parameters like pupil and iris size.
     */
    void Start()
    {
        if (!ParseCommandLine())
        {
            enabled = false;
            return;
        }

        eyeRegion = eyeRegionObj.GetComponent<EyeRegionController>();
        eyeball = eyeballObj.GetComponent<EyeballController>();
        eyeRegionSubdiv = eyeRegionSubdivObj.GetComponent<SubdivMesh>();
        eyeWetness = eyeWetnessObj.GetComponent<EyeWetnessController>();
        eyeWetnessSubdiv = eyeWetnessSubdivObj.GetComponent<SubdivMesh>();
        eyeLashes = eyeLashesObj.GetComponentsInChildren<DeformEyeLashes>(true);

        lightingController = GameObject.Find("lighting_controller").GetComponent<LightingController>();

        visualizationManager = FindFirstObjectByType<EyeVisualizationManager>();
        if (visualizationManager == null)
        {
            GameObject visualMgrObj = new GameObject("VisualizationManager");
            visualizationManager = visualMgrObj.AddComponent<EyeVisualizationManager>();
            visualizationManager.synthesEyesServer = this;
        }

        // Load cameras from JSON after one frame so UI and EyeballController
        // initialization have finished. Resolve relative paths from the Unity
        // project root, not the process working directory.
        string requestedConfigPath = string.IsNullOrEmpty(commandLineConfigPath)
            ? jsonConfigPath
            : commandLineConfigPath;
        string resolvedConfigPath = ResolveProjectPath(requestedConfigPath);
        if (!File.Exists(resolvedConfigPath))
        {
            string fallbackConfigPath = ResolveProjectPath("gradpupil/gradpupil_autoload.json");
            Debug.LogWarning($"Config not found: {requestedConfigPath} resolved to {resolvedConfigPath}; trying {fallbackConfigPath}");
            resolvedConfigPath = fallbackConfigPath;
        }

        if (File.Exists(resolvedConfigPath))
        {
            StartCoroutine(InitializeConfiguredScene(resolvedConfigPath));
        }
        else
        {
            Debug.LogWarning($"Config not found: {resolvedConfigPath}");
            StartCoroutine(InitializeDefaultScene());
        }
    }

    private bool ParseCommandLine()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--quit-on-complete")
            {
                quitOnComplete = true;
                continue;
            }
            if (arg == "--help-gradpupil")
            {
                Debug.Log("GradPupil options: --config PATH --output PATH --samples N " +
                          "--seed N --shard-index N --shard-count N --quit-on-complete");
                Application.Quit(0);
                return false;
            }
            if (!arg.StartsWith("--")) continue; // Leave Unity's own arguments alone.
            if (i + 1 >= args.Length)
            {
                Debug.LogError($"Missing value for command-line option {arg}");
                Application.Quit(2);
                return false;
            }

            string value = args[++i];
            int parsed;
            switch (arg)
            {
                case "--config":
                    commandLineConfigPath = value;
                    break;
                case "--output":
                    commandLineOutputPath = value;
                    break;
                case "--samples":
                    if (!int.TryParse(value, out parsed) || parsed < 1)
                        return CommandLineError($"Invalid --samples value: {value}");
                    commandLineSampleCount = parsed;
                    break;
                case "--seed":
                    if (!int.TryParse(value, out parsed) || parsed < 0)
                        return CommandLineError($"Invalid --seed value: {value}");
                    commandLineSeed = parsed;
                    break;
                case "--shard-index":
                    if (!int.TryParse(value, out parsed) || parsed < 0)
                        return CommandLineError($"Invalid --shard-index value: {value}");
                    shardIndex = parsed;
                    break;
                case "--shard-count":
                    if (!int.TryParse(value, out parsed) || parsed < 1)
                        return CommandLineError($"Invalid --shard-count value: {value}");
                    shardCount = parsed;
                    break;
                default:
                    return CommandLineError($"Unknown GradPupil command-line option: {arg}");
            }
        }

        if (shardIndex >= shardCount)
            return CommandLineError($"--shard-index {shardIndex} must be less than --shard-count {shardCount}");

        Debug.Log($"GradPupil command line: shard {shardIndex}/{shardCount}, " +
                  $"config='{commandLineConfigPath}', output='{commandLineOutputPath}', " +
                  $"samples={commandLineSampleCount}, seed={commandLineSeed}, " +
                  $"quitOnComplete={quitOnComplete}");
        return true;
    }

    private bool CommandLineError(string message)
    {
        Debug.LogError(message);
        Application.Quit(2);
        return false;
    }

    private string ResolveProjectPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, path);
    }

    private IEnumerator InitializeConfiguredScene(string configPath)
    {
        yield return null;

        activeConfigPath = configPath;
        LoadCamerasFromConfig(configPath);
        ApplyCommandLineOutputOverride();

        if (eyeParameters != null)
        {
            Debug.Log($"[Eye Parameters Loaded]");
            Debug.Log($"  Pupil Size Range: min={eyeParameters.pupilSizeRange.x}, max={eyeParameters.pupilSizeRange.y}");
            Debug.Log($"  Iris Size Range: min={eyeParameters.irisSizeRange.x}, max={eyeParameters.irisSizeRange.y}");
            Debug.Log($"  Default Yaw: {eyeParameters.defaultYaw}");
            Debug.Log($"  Default Pitch: {eyeParameters.defaultPitch}");
            Debug.Log($"  Yaw Noise: {eyeParameters.yawNoise}");
            Debug.Log($"  Pitch Noise: {eyeParameters.pitchNoise}");

            eyeball.SetPupilSizeRange(eyeParameters.pupilSizeRange);
            eyeball.SetIrisSizeRange(eyeParameters.irisSizeRange);
        }

        MenuController menuController = FindFirstObjectByType<MenuController>();
        if (menuController != null)
        {
            JSONNode rootNode = JSON.Parse(File.ReadAllText(configPath));
            menuController.PopulateUIFromConfig(rootNode);
        }

        ConfigureDatasetRandomizer(configPath);
        RandomizePose();
        if (nirLightingMode)
        {
            ApplyNirLightingMode();
        }
        Debug.Log($"Configured scene initialized from {configPath}");

        if (autoStartOnConfig)
        {
            ResetFrameCounter();
            isSavingData = true;
            Canvas guiCanvas = GameObject.Find("GUI Canvas")?.GetComponent<Canvas>();
            if (guiCanvas != null) guiCanvas.enabled = false;
            Debug.Log("Auto-starting dataset generation from configured scene.");
        }
    }

    private void ConfigureDatasetRandomizer(string configPath)
    {
        JSONNode root = JSON.Parse(File.ReadAllText(configPath));
        string albedoResourceRoot = root["albedo_resource_root"] != null
            ? root["albedo_resource_root"].Value
            : "";
        eyeRegion.LoadAlbedoTextureSet(albedoResourceRoot);
        eyeball.LoadAlbedoTextureSet(albedoResourceRoot);
        JSONNode dataset = root["dataset"];
        if (dataset == null)
        {
            datasetRandomizer = null;
            eyeRegion.randomizeAppearance = true;
            return;
        }

        int posesPerIdentity = dataset["poses_per_identity"] != null
            ? dataset["poses_per_identity"].AsInt
            : 1;
        int seed = commandLineSeed >= 0
            ? commandLineSeed
            : (dataset["seed"] != null ? dataset["seed"].AsInt : 1729);

        List<float> pupilSizes = new List<float>();
        JSONArray pupilArray = dataset["pupil_diameters_mm"].AsArray;
        if (pupilArray != null)
        {
            for (int i = 0; i < pupilArray.Count; i++)
                pupilSizes.Add(pupilArray[i].AsFloat);
        }
        if (pupilSizes.Count == 0) pupilSizes.Add(4.0f);

        datasetRandomizer = GetComponent<GroupedRandomizationController>();
        if (datasetRandomizer == null)
            datasetRandomizer = gameObject.AddComponent<GroupedRandomizationController>();

        datasetRandomizer.Configure(
            this,
            eyeRegion,
            eyeball,
            posesPerIdentity,
            pupilSizes.ToArray(),
            seed,
            shardIndex,
            shardCount);

        int samplesPerIdentity = datasetRandomizer.SamplesPerIdentity;
        maxSamplesToSave = CountSamplesForShard(
            totalSamplesToGenerate, samplesPerIdentity, shardIndex, shardCount);
        Debug.Log($"Grouped dataset: {posesPerIdentity} poses x {pupilSizes.Count} pupil sizes per identity; " +
                  $"global samples={totalSamplesToGenerate}, shard {shardIndex}/{shardCount} " +
                  $"will save {maxSamplesToSave} samples.");
    }

    private static int CountSamplesForShard(
        int totalSamples, int samplesPerIdentity, int index, int count)
    {
        if (samplesPerIdentity <= 0) return 0;
        int assigned = 0;
        int totalIdentities = (totalSamples + samplesPerIdentity - 1) / samplesPerIdentity;
        for (int identity = index; identity < totalIdentities; identity += count)
        {
            int firstSample = identity * samplesPerIdentity;
            assigned += Mathf.Min(samplesPerIdentity, totalSamples - firstSample);
        }
        return assigned;
    }

    private void ApplyCommandLineOutputOverride()
    {
        string selected = string.IsNullOrEmpty(commandLineOutputPath)
            ? outputPath
            : ResolveProjectPath(commandLineOutputPath);
        if (string.IsNullOrEmpty(selected))
            selected = Path.Combine(Application.persistentDataPath, "imgs");
        if (shardCount > 1)
            selected = Path.Combine(selected, $"shard_{shardIndex:D2}");
        outputPath = selected;
        EnsureDirectoryExists(outputPath);
        Debug.Log($"Effective output path: {outputPath}");
    }

    private void ApplyNirLightingMode()
    {
        Light directional = GameObject.Find("directional_light")?.GetComponent<Light>();
        if (directional != null)
        {
            directional.intensity = 0f;
            directional.color = Color.white;
        }

        RenderSettings.ambientIntensity = 0.02f;
        if (RenderSettings.skybox != null)
        {
            RenderSettings.skybox.SetFloat("_Exposure", 0.0f);
        }

        ReflectionProbe probe = GameObject.Find("reflection_probe")?.GetComponent<ReflectionProbe>();
        if (probe != null)
        {
            probe.intensity = 0f;
        }
    }

    /// <summary>
    /// Bootstraps a fully usable scene when no JSON config is present.
    /// Runs as a coroutine so it waits one frame after Start(), giving
    /// EyeballController time to capture iris_start_pos before we call
    /// RandomizeEyeball (avoids the zeroed-vertex iris glitch).
    ///
    /// Camera.main is intentionally NOT added to cameraList — CleanupCurrentScene
    /// destroys everything in that list, which would kill the renderer. Instead,
    /// RandomizeScene skips camera positioning when cameraList is empty and only
    /// randomizes gaze; the full camera pipeline kicks in after a config is loaded.
    ///
    /// Iris range comes from MenuController's UI values (mm).
    /// Pupil: clamped 1–8 mm.  Yaw ±20°.  Pitch ±15°.
    /// </summary>
    private IEnumerator InitializeDefaultScene()
    {
        // Wait one frame so every MonoBehaviour.Start() has finished.
        yield return null;

        // Pupil: read from MenuController so it matches the UI, then convert mm → raw.
        Vector2 pupilMmRange = new Vector2(3.0f, 7.0f);
        eyeball.SetPupilMmRange(pupilMmRange);

        // Iris: read the mm values directly from MenuController (UI is the source of truth).
        // MenuController stores iris in mm; convert here exactly as SynthesEyesServer does
        // when loading iris_diameter_mm_range from JSON.
        MenuController menuController = FindFirstObjectByType<MenuController>();
        if (menuController != null &&
            menuController.eyeParameterValues.TryGetValue("IrisSize", out var irisVals))
        {
            float irisMinMm = irisVals.ContainsKey("min") ? irisVals["min"] : 10.0f;
            float irisMaxMm = irisVals.ContainsKey("max") ? irisVals["max"] : 13.0f;
            eyeball.SetIrisSizeRange(
                EyeSizeCalibration.IrisDiameterMmRangeToIrisSizeRange(new Vector2(irisMinMm, irisMaxMm)));
            Debug.Log($"[Default scene] Iris {irisMinMm:F1}–{irisMaxMm:F1} mm (from UI).");
        }
        else
        {
            // Fallback if MenuController isn't in the scene.
            eyeball.SetIrisSizeRange(
                EyeSizeCalibration.IrisDiameterMmRangeToIrisSizeRange(new Vector2(10.0f, 13.0f)));
            Debug.LogWarning("[Default scene] MenuController not found; using hardcoded iris 10–13 mm.");
        }

        // Gaze noise — used by RandomizeScene when eyeParameters is null.
        eyeYawNoise   = 40f;
        eyePitchNoise = 20f;
        defaultEyeYaw   = 0f;
        defaultEyePitch = 0f;

        Debug.Log($"[Default scene] Pupil {pupilMmRange.x:F1}–{pupilMmRange.y:F1} mm, " +
                  $"Yaw ±{eyeYawNoise}°, Pitch ±{eyePitchNoise}°");

        // Initial randomize — iris_start_pos is now safely populated.
        eyeball.RandomizeEyeball();
        eyeRegion.RandomizeAppearance();
        RandomizePose();
    }


    /**
     * Load camera configuration from a JSON file.
     *
     * @param {string} configPath - Path to the JSON configuration file.
     * 
     * Reads camera intrinsics and extrinsics, motion center settings, and initializes camera GameObjects.
     * Applies appropriate transformations and noise for realistic variability.
     * @throws {Exception} If the JSON parsing or object setup fails.
     */
    private void LoadCamerasFromConfig(string configPath)
    {
        cameraList.Clear();
        Camera.main.clearFlags = CameraClearFlags.SolidColor;
        Camera.main.backgroundColor = new Color(0.5f, 0.5f, 0.5f);

        try
        {
            string jsonData = File.ReadAllText(configPath);
            Debug.Log($"Reading config from: {configPath}");

            JSONNode rootNode = JSON.Parse(jsonData);
            if (rootNode == null)
            {
                Debug.LogError("Failed to parse JSON config file");
                return;
            }

            if (rootNode["outputPath"] != null)
            {
                outputPath = rootNode["outputPath"].Value;
                EnsureDirectoryExists(outputPath);
                Debug.Log($"Output path loaded from config: {outputPath}");
            }

            if (rootNode["num_samples"] != null)
                totalSamplesToGenerate = rootNode["num_samples"].AsInt;
            if (commandLineSampleCount > 0)
                totalSamplesToGenerate = commandLineSampleCount;
            maxSamplesToSave = totalSamplesToGenerate;
            Debug.Log($"Global dataset target is {totalSamplesToGenerate} images");

            if (rootNode["motion_center"] != null)
            {
                useMotionCenter = rootNode["motion_center"].AsInt == 1;
                Debug.Log($"Motion center mode: {useMotionCenter}");
            }

            cameraDistanceBands.Clear();
            cameraDistanceTotalWeight = 0f;
            if (rootNode["camera_distance_bands_m"] != null)
            {
                JSONArray bands = rootNode["camera_distance_bands_m"].AsArray;
                if (bands != null)
                {
                    for (int i = 0; i < bands.Count; i++)
                    {
                        float minCm = bands[i]["min"].AsFloat * 100f;
                        float maxCm = bands[i]["max"].AsFloat * 100f;
                        float weight = bands[i]["weight"].AsFloat;
                        if (minCm <= 0f || maxCm <= minCm || weight <= 0f)
                        {
                            Debug.LogWarning($"Ignoring invalid camera distance band {i}: " +
                                             $"min={minCm:F2} cm max={maxCm:F2} cm weight={weight:F3}");
                            continue;
                        }
                        cameraDistanceBands.Add(new CameraDistanceBand
                        {
                            minCm = minCm,
                            maxCm = maxCm,
                            weight = weight,
                        });
                        cameraDistanceTotalWeight += weight;
                    }
                }
                Debug.Log($"Loaded {cameraDistanceBands.Count} camera distance bands " +
                          $"with total weight {cameraDistanceTotalWeight:F3}");
            }

            cameraAngleBands.Clear();
            cameraAngleTotalWeight = 0f;
            if (rootNode["camera_angle_bands_deg"] != null)
            {
                JSONArray bands = rootNode["camera_angle_bands_deg"].AsArray;
                if (bands != null)
                {
                    for (int i = 0; i < bands.Count; i++)
                    {
                        float minDeg = Mathf.Max(0f, bands[i]["min"].AsFloat);
                        float maxDeg = bands[i]["max"].AsFloat;
                        float weight = bands[i]["weight"].AsFloat;
                        if (maxDeg <= minDeg || maxDeg > 89f || weight <= 0f)
                        {
                            Debug.LogWarning($"Ignoring invalid camera angle band {i}: " +
                                             $"min={minDeg:F1} max={maxDeg:F1} weight={weight:F3}");
                            continue;
                        }
                        cameraAngleBands.Add(new CameraAngleBand
                        {
                            minDeg = minDeg,
                            maxDeg = maxDeg,
                            weight = weight,
                        });
                        cameraAngleTotalWeight += weight;
                    }
                }
            }
            if (rootNode["camera_roll_range_deg"] != null)
            {
                cameraRollRangeDeg = new Vector2(
                    rootNode["camera_roll_range_deg"]["min"].AsFloat,
                    rootNode["camera_roll_range_deg"]["max"].AsFloat);
            }
            Debug.Log($"Loaded {cameraAngleBands.Count} camera angle bands; " +
                      $"roll [{cameraRollRangeDeg.x:F1}, {cameraRollRangeDeg.y:F1}] deg");

            if (rootNode["camera_array_center"] != null)
            {
                JSONNode centerNode = rootNode["camera_array_center"];

                Vector3 arrayPosition = new Vector3(
                    centerNode["x"] != null ? centerNode["x"].AsFloat : 0f,
                    centerNode["y"] != null ? centerNode["y"].AsFloat : 0f,
                    centerNode["z"] != null ? centerNode["z"].AsFloat : 0f
                );

                Vector3 arrayRotation = new Vector3(
                    centerNode["rx"] != null ? centerNode["rx"].AsFloat : 0f,
                    centerNode["ry"] != null ? centerNode["ry"].AsFloat : 0f,
                    centerNode["rz"] != null ? centerNode["rz"].AsFloat : 0f
                );

                // Convert from right-handed meters to left-handed centimeters to accommodate Unity's coordinate convention
                cameraArrayCenter = new Vector3(
                    arrayPosition.x * 100f,           // Scale to cm
                    -arrayPosition.y * 100f,          // Invert Y and scale to cm
                    arrayPosition.z * 100f            // Scale to cm
                );

                cameraArrayRotation = new Vector3(
                    -arrayRotation.x,                 // Invert X rotation
                    arrayRotation.y,                  // Keep Y rotation
                    -arrayRotation.z                  // Invert Z rotation
                );
                Debug.Log("Camera array center: " + cameraArrayCenter);
                Debug.Log("Camera array rotation: " + cameraArrayRotation);
            }

            if (rootNode["camera_array_center_noise"] != null)
            {
                JSONNode noiseNode = rootNode["camera_array_center_noise"];

                // Parse position noise values from JSON (in right-handed meters)
                Vector3 positionNoise = new Vector3(
                    noiseNode["x"] != null ? noiseNode["x"].AsFloat : 0f,
                    noiseNode["y"] != null ? noiseNode["y"].AsFloat : 0f,
                    noiseNode["z"] != null ? noiseNode["z"].AsFloat : 0f
                );

                Vector3 rotationNoise = new Vector3(
                    noiseNode["rx"] != null ? noiseNode["rx"].AsFloat : 0f,
                    noiseNode["ry"] != null ? noiseNode["ry"].AsFloat : 0f,
                    noiseNode["rz"] != null ? noiseNode["rz"].AsFloat : 0f
                );

                // Convert noise values from right-handed meters to left-handed centimeters to accommodate Unity's coordinate convention
                cameraArrayPositionNoise = new Vector3(
                    positionNoise.x * 100f,           // Scale to cm
                    -positionNoise.y * 100f,          // Invert Y and scale to cm
                    positionNoise.z * 100f            // Scale to cm
                );

                cameraArrayRotationNoise = new Vector3(
                    -rotationNoise.x,                 // Invert X rotation
                    rotationNoise.y,                  // Keep Y rotation
                    -rotationNoise.z                  // Invert Z rotation
                );
                Debug.Log("Camera array center noise: " + cameraArrayPositionNoise);
                Debug.Log("Camera array rotation noise: " + cameraArrayRotationNoise);
            }

            if (rootNode["eye_parameters"] != null)
            {
                JSONNode eyeParamsNode = rootNode["eye_parameters"];
                eyeParameters = new EyeParameters();

                // Pupil: prefer the physical-mm field if present; fall
                // back to the legacy dimensionless `_PupilSize` multiplier.
                if (eyeParamsNode["pupil_diameter_mm_range"] != null)
                {
                    Vector2 mmRange = new Vector2(
                        eyeParamsNode["pupil_diameter_mm_range"]["min"].AsFloat,
                        eyeParamsNode["pupil_diameter_mm_range"]["max"].AsFloat);
                    Vector2 mmClamped = new Vector2(
                        Mathf.Clamp(mmRange.x,
                            EyeSizeCalibration.PUPIL_MM_MIN,
                            EyeSizeCalibration.PUPIL_MM_MAX),
                        Mathf.Clamp(mmRange.y,
                            EyeSizeCalibration.PUPIL_MM_MIN,
                            EyeSizeCalibration.PUPIL_MM_MAX));
                    if (mmClamped != mmRange)
                    {
                        Debug.LogWarning(
                            $"pupil_diameter_mm_range [{mmRange.x:F2}, {mmRange.y:F2}] mm " +
                            $"out of supported range " +
                            $"[{EyeSizeCalibration.PUPIL_MM_MIN:F2}, {EyeSizeCalibration.PUPIL_MM_MAX:F2}] mm; " +
                            $"clamped to [{mmClamped.x:F2}, {mmClamped.y:F2}] mm");
                    }
                    eyeParameters.pupilSizeRange =
                        EyeSizeCalibration.PupilDiameterMmRangeToPupilSizeRange(mmClamped);
                    Debug.Log($"  pupil_diameter_mm_range [{mmClamped.x:F2}, {mmClamped.y:F2}] mm " +
                              $"→ _PupilSize [{eyeParameters.pupilSizeRange.x:F4}, {eyeParameters.pupilSizeRange.y:F4}]");
                }
                else if (eyeParamsNode["pupil_size_range"] != null)
                {
                    eyeParameters.pupilSizeRange = new Vector2(
                        eyeParamsNode["pupil_size_range"]["min"].AsFloat,
                        eyeParamsNode["pupil_size_range"]["max"].AsFloat
                    );
                }

                // Iris: prefer the physical-mm field if present; fall back
                // to the legacy dimensionless `irisSize` multiplier.
                if (eyeParamsNode["iris_diameter_mm_range"] != null)
                {
                    Vector2 mmRange = new Vector2(
                        eyeParamsNode["iris_diameter_mm_range"]["min"].AsFloat,
                        eyeParamsNode["iris_diameter_mm_range"]["max"].AsFloat);
                    eyeParameters.irisSizeRange =
                        EyeSizeCalibration.IrisDiameterMmRangeToIrisSizeRange(mmRange);
                    Debug.Log($"  iris_diameter_mm_range [{mmRange.x:F2}, {mmRange.y:F2}] mm " +
                              $"→ irisSize [{eyeParameters.irisSizeRange.x:F4}, {eyeParameters.irisSizeRange.y:F4}]");
                }
                else if (eyeParamsNode["iris_size_range"] != null)
                {
                    eyeParameters.irisSizeRange = new Vector2(
                        eyeParamsNode["iris_size_range"]["min"].AsFloat,
                        eyeParamsNode["iris_size_range"]["max"].AsFloat
                    );
                }

                eyeParameters.defaultYaw = eyeParamsNode["default_yaw"] != null ? eyeParamsNode["default_yaw"].AsFloat : 0f;
                eyeParameters.defaultPitch = eyeParamsNode["default_pitch"] != null ? eyeParamsNode["default_pitch"].AsFloat : 0f;
                eyeParameters.yawNoise = eyeParamsNode["yaw_noise"] != null ? eyeParamsNode["yaw_noise"].AsFloat : 30f;
                eyeParameters.pitchNoise = eyeParamsNode["pitch_noise"] != null ? eyeParamsNode["pitch_noise"].AsFloat : 30f;

            }

            JSONArray camerasArray = rootNode["cameras"].AsArray;
            if (camerasArray == null)
            {
                Debug.LogError("No cameras array found in config");
                return;
            }

            Debug.Log($"Found {camerasArray.Count} cameras");

            for (int i = 0; i < camerasArray.Count; i++)
            {
                JSONNode camNode = camerasArray[i];

                string cameraName = camNode["name"] != null ? camNode["name"].Value : $"Camera_{i}";


                GameObject camObj = new GameObject(cameraName);
                Camera newCam = camObj.AddComponent<Camera>();

                JSONNode extrinsics = camNode["extrinsics"];
                Vector3 position = Vector3.zero;
                Vector3 rotation = Vector3.zero;

                if (extrinsics != null)
                {
                    Vector3 extrinsicPosition = new Vector3(
                        extrinsics["x"] != null ? extrinsics["x"].AsFloat : 0f,
                        extrinsics["y"] != null ? extrinsics["y"].AsFloat : 0f,
                        extrinsics["z"] != null ? extrinsics["z"].AsFloat : 0f
                    );

                    Vector3 extrinsicRotation = new Vector3(
                        extrinsics["rx"] != null ? extrinsics["rx"].AsFloat : 0f,
                        extrinsics["ry"] != null ? extrinsics["ry"].AsFloat : 0f,
                        extrinsics["rz"] != null ? extrinsics["rz"].AsFloat : 0f
                    );
                    // Convert from right-handed meters to left-handed centimeters to accommodate Unity's coordinate convention
                    position = new Vector3(
                        extrinsicPosition.x * 100f,          // Scale to cm
                        -extrinsicPosition.y * 100f,         // Invert Y and scale to cm
                        extrinsicPosition.z * 100f           // Scale to cm
                    );
                    rotation = new Vector3(
                        -extrinsicRotation.x,                // Invert X rotation
                        extrinsicRotation.y,                 // Keep Y rotation
                        -extrinsicRotation.z                 // Invert Z rotation
                    );
                }

                camObj.transform.position = position;
                camObj.transform.eulerAngles = rotation;

                JSONNode extrinsicsNoise = camNode["extrinsics_noise"];
                Vector3 extrinsicsPositionNoise = Vector3.zero;
                Vector3 extrinsicsRotationNoise = Vector3.zero;

                if (extrinsicsNoise != null)
                {
                    extrinsicsPositionNoise = new Vector3(
                        extrinsicsNoise["x"] != null ? extrinsicsNoise["x"].AsFloat * 100f : 0f,
                        extrinsicsNoise["y"] != null ? -extrinsicsNoise["y"].AsFloat * 100f : 0f,
                        extrinsicsNoise["z"] != null ? extrinsicsNoise["z"].AsFloat * 100f : 0f
                    );

                    extrinsicsRotationNoise = new Vector3(
                        extrinsicsNoise["rx"] != null ? -extrinsicsNoise["rx"].AsFloat : 0f,
                        extrinsicsNoise["ry"] != null ? extrinsicsNoise["ry"].AsFloat : 0f,
                        extrinsicsNoise["rz"] != null ? -extrinsicsNoise["rz"].AsFloat : 0f
                    );
                }

                cameraExtrinsicsPositionNoise.Add(extrinsicsPositionNoise);
                cameraExtrinsicsRotationNoise.Add(extrinsicsRotationNoise);

                JSONNode intrinsics = camNode["intrinsics"];
                bool isOrthographic = camNode["is_orthographic"] != null && camNode["is_orthographic"].AsBool;

                // Set default values
                CameraIntrinsics camIntrinsics = new CameraIntrinsics
                {
                    fx = 500f,
                    fy = 500f,
                    cx = 320f,
                    cy = 240f,
                    width = 640,
                    height = 480
                };

                if (intrinsics != null)
                {
                    // TODO: these need to be calculated for 
                    if (intrinsics["fx"] != null) camIntrinsics.fx = intrinsics["fx"].AsFloat;
                    if (intrinsics["fy"] != null) camIntrinsics.fy = intrinsics["fy"].AsFloat;
                    if (intrinsics["cx"] != null) camIntrinsics.cx = intrinsics["cx"].AsFloat;
                    if (intrinsics["cy"] != null) camIntrinsics.cy = intrinsics["cy"].AsFloat;
                    if (intrinsics["w"] != null) camIntrinsics.width = intrinsics["w"].AsInt;
                    if (intrinsics["h"] != null) camIntrinsics.height = intrinsics["h"].AsInt;
                }

                CameraConfig config = new CameraConfig
                {
                    name = cameraName,
                    position = position,
                    rotation = rotation,
                    is_orthographic = isOrthographic,
                    intrinsics = camIntrinsics
                };

                cameraOriginalIntrinsics.Add(new CameraIntrinsics
                {
                    fx = camIntrinsics.fx,
                    fy = camIntrinsics.fy,
                    cx = camIntrinsics.cx,
                    cy = camIntrinsics.cy,
                    width = camIntrinsics.width,
                    height = camIntrinsics.height,
                });

                ConfigureCameraFromIntrinsics(newCam, config);

                newCam.tag = cameraList.Count == 0 ? "MainCamera" : "Untagged";
                newCam.enabled = (cameraList.Count == 0);

                cameraList.Add(newCam);
                cameraOriginalPositions.Add(position);
                cameraOriginalRotations.Add(rotation);
            }

            LoadPointLightsFromConfig(rootNode);

            if (useMotionCenter)
            {
                cameraParent = new GameObject("CameraArrayParent");

                cameraParent.transform.position = cameraArrayCenter;
                cameraParent.transform.eulerAngles = cameraArrayRotation;


                foreach (Camera cam in cameraList)
                {
                    Vector3 currentLocalPosition = cam.transform.localPosition;
                    Quaternion currentLocalRotation = cam.transform.localRotation;

                    Vector3 desiredWorldPosition = cameraParent.transform.TransformPoint(currentLocalPosition);
                    Quaternion desiredWorldRotation = cameraParent.transform.rotation * currentLocalRotation;

                    cam.transform.position = desiredWorldPosition;
                    cam.transform.rotation = desiredWorldRotation;

                    cam.transform.parent = cameraParent.transform;
                }

                // Store each camera's resting local transform so RandomizeScene can
                // apply per-camera noise on top of the array-level parent noise.
                cameraOriginalLocalPositions.Clear();
                cameraOriginalLocalRotations.Clear();
                foreach (Camera cam in cameraList)
                {
                    cameraOriginalLocalPositions.Add(cam.transform.localPosition);
                    cameraOriginalLocalRotations.Add(cam.transform.localEulerAngles);
                }

                for (int i = 0; i < pointLightList.Count; i++)
                {
                    Light light = pointLightList[i];
                    bool isArrayMounted = pointLightArrayMounted[i];

                    if (isArrayMounted)
                    {
                        Vector3 currentLocalPosition = light.transform.localPosition;
                        Quaternion currentLocalRotation = light.transform.localRotation;

                        Vector3 desiredWorldPosition = cameraParent.transform.TransformPoint(currentLocalPosition);
                        Quaternion desiredWorldRotation = cameraParent.transform.rotation * currentLocalRotation;

                        light.transform.position = desiredWorldPosition;
                        light.transform.rotation = desiredWorldRotation;

                        light.transform.parent = cameraParent.transform;
                    }
                }

                if (cameraList.Count > 0)
                {
                    xmlBasePosition = cameraList[0].transform.position;
                    xmlBaseEulerAngles = cameraList[0].transform.eulerAngles;
                }

            }

            if (!useMotionCenter)
            {
                foreach (Camera cam in cameraList)
                {
                    Vector3 currentRotation = cam.transform.eulerAngles;
                    cam.transform.eulerAngles = new Vector3(currentRotation.x, currentRotation.y + 180f, currentRotation.z);
                }

                for (int i = 0; i < cameraOriginalRotations.Count; i++)
                {
                    Vector3 origRot = cameraOriginalRotations[i];
                    cameraOriginalRotations[i] = new Vector3(origRot.x, origRot.y + 180f, origRot.z);
                }
            }

            ApplyGradPupilHardcodedRig();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error loading camera config: {e.Message}\n{e.StackTrace}");
        }
    }

    private void ApplyGradPupilHardcodedRig()
    {
        if (cameraList == null || cameraList.Count == 0 || pointLightList == null || pointLightList.Count == 0)
            return;

        Camera cam = cameraList[0];

        // Production IR PCB pattern, rigidly mounted to the camera. KiCad uses
        // a downward-positive board Y; camera-local Unity Y is upward-positive.
        // Units are Unity centimeters. D1-D4 form the inner square and D5-D6
        // are the two asymmetric outer emitters.
        Vector3[] localOffsets = new Vector3[]
        {
            new Vector3(-0.64f,  0.64f, 0f), // D1: PCB (43.6, 43.6) mm
            new Vector3( 0.64f,  0.64f, 0f), // D2: PCB (56.4, 43.6) mm
            new Vector3( 0.64f, -0.64f, 0f), // D3: PCB (56.4, 56.4) mm
            new Vector3(-0.64f, -0.64f, 0f), // D4: PCB (43.6, 56.4) mm
            new Vector3(-1.60f, -0.60f, 0f), // D5: PCB (34.0, 56.0) mm
            new Vector3( 1.60f,  0.60f, 0f), // D6: PCB (66.0, 44.0) mm
        };
        int n = Mathf.Min(pointLightList.Count, localOffsets.Length);

        // Remove superseded multi-point emitter proxies from an earlier model.
        List<GameObject> oldProxies = new List<GameObject>();
        foreach (Transform child in cam.transform)
            if (child.name.StartsWith("GradPupil0805_") || child.name.StartsWith("GradPupil1206_"))
                oldProxies.Add(child.gameObject);
        foreach (GameObject oldProxy in oldProxies) Destroy(oldProxy);

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, n);
        for (int i = 0; i < n; i++)
        {
            Light source = pointLightList[i];
            if (source == null) continue;
            pointLightArrayMounted[i] = true;
            source.transform.SetParent(cam.transform, false);
            source.transform.localPosition = localOffsets[i];
            source.transform.localRotation = Quaternion.identity;
            source.enabled = true;
            source.type = LightType.Point;
            source.range = 25f;
            source.intensity = 0.8f;
            source.color = Color.white;
            source.shadows = LightShadows.None;
            source.renderMode = LightRenderMode.ForcePixel;
        }

        Debug.Log($"Applied exactly {n} GradPupil IR point emitters from the 6-emitter flex PCB.");
    }

    private void LoadPointLightsFromConfig(JSONNode rootNode)
    {
        pointLightList.Clear();
        pointLightOriginalPositions.Clear();
        pointLightOriginalRotations.Clear();
        pointLightArrayMounted.Clear();

        if (rootNode["lights"] == null)
        {
            Debug.Log("No lights array found in config");
            return;
        }

        JSONArray lightsArray = rootNode["lights"].AsArray;
        if (lightsArray == null)
        {
            Debug.Log("Invalid lights array in config");
            return;
        }

        Debug.Log($"Found {lightsArray.Count} point lights");

        for (int i = 0; i < lightsArray.Count; i++)
        {
            JSONNode lightNode = lightsArray[i];
            string lightName = lightNode["name"] != null ? lightNode["name"].Value : $"PointLight_{i}";
            bool isArrayMounted = lightNode["array_mounted"] != null ? lightNode["array_mounted"].AsInt == 1 : false;

            GameObject lightObj = new GameObject(lightName);
            Light pointLight = lightObj.AddComponent<Light>();

            pointLight.type = LightType.Point;

            JSONNode extrinsics = lightNode["position"];
            Vector3 position = Vector3.zero;
            Vector3 rotation = Vector3.zero;

            if (extrinsics != null)
            {
                Vector3 extrinsicPosition = new Vector3(
                    extrinsics["x"] != null ? extrinsics["x"].AsFloat : 0f,
                    extrinsics["y"] != null ? extrinsics["y"].AsFloat : 0f,
                    extrinsics["z"] != null ? extrinsics["z"].AsFloat : 0f
                );

                Vector3 extrinsicRotation = new Vector3(
                    extrinsics["rx"] != null ? extrinsics["rx"].AsFloat : 0f,
                    extrinsics["ry"] != null ? extrinsics["ry"].AsFloat : 0f,
                    extrinsics["rz"] != null ? extrinsics["rz"].AsFloat : 0f
                );

                // Convert from right-handed meters to left-handed centimeters to accommodate Unity's coordinate convention
                position = new Vector3(
                    extrinsicPosition.x * 100f,          // Scale to cm
                    -extrinsicPosition.y * 100f,         // Invert Y and scale to cm
                    extrinsicPosition.z * 100f           // Scale to cm
                );

                rotation = new Vector3(
                    -extrinsicRotation.x,                // Invert X rotation
                    extrinsicRotation.y,                 // Keep Y rotation
                    -extrinsicRotation.z                 // Invert Z rotation
                );
            }

            lightObj.transform.position = position;
            lightObj.transform.eulerAngles = rotation;

            JSONNode properties = lightNode["properties"];
            if (properties != null)
            {
                if (properties["range"] != null)
                    pointLight.range = properties["range"].AsFloat;
                else
                    pointLight.range = 100f;

                if (properties["intensity"] != null)
                    pointLight.intensity = properties["intensity"].AsFloat;
                else
                    pointLight.intensity = 0.3f;

                JSONNode colorNode = properties["color"];
                if (colorNode != null)
                {
                    float r = colorNode["r"] != null ? colorNode["r"].AsFloat : 1.0f;
                    float g = colorNode["g"] != null ? colorNode["g"].AsFloat : 1.0f;
                    float b = colorNode["b"] != null ? colorNode["b"].AsFloat : 1.0f;
                    pointLight.color = new Color(r, g, b);
                }
                else
                {
                    pointLight.color = Color.white;
                }

                if (properties["shadows"] != null)
                {
                    string shadowsValue = properties["shadows"].Value.ToLower();
                    if (shadowsValue == "hard") pointLight.shadows = LightShadows.Hard;
                    else if (shadowsValue == "soft") pointLight.shadows = LightShadows.Soft;
                    else pointLight.shadows = LightShadows.None;
                }
                else
                {
                    pointLight.shadows = LightShadows.Soft;
                }

                if (properties["shadow_bias"] != null)
                    pointLight.shadowBias = properties["shadow_bias"].AsFloat;
                else
                    pointLight.shadowBias = 0.05f;
            }
            else
            {
                // Set defaults if no properties are defined
                pointLight.range = 100f;
                pointLight.intensity = 0.3f;
                pointLight.color = Color.white;
                pointLight.shadows = LightShadows.Soft;
                pointLight.shadowBias = 0.05f;
            }

            pointLightList.Add(pointLight);
            pointLightOriginalPositions.Add(position);
            pointLightOriginalRotations.Add(rotation);
            pointLightArrayMounted.Add(isArrayMounted);
        }
    }


    /**
     * Randomizes camera and light positions and orientations.
     *
     * Applies motion noise to camera parent transform or individual cameras depending on motion center setting.
     * Also randomizes eyeball rotation within a specified noise range.
     */
    private void ConfigureCameraFromIntrinsics(Camera cam, CameraConfig config)
    {
        cam.orthographic = config.is_orthographic;
        cam.nearClipPlane = 0.005f;
        cam.farClipPlane = 1000f;

        if (config.is_orthographic)
        {
            cam.orthographicSize = config.intrinsics.height / (2 * config.intrinsics.fy);
        }
        else
        {
            cam.usePhysicalProperties = true;
            cam.lensShift = new Vector2(
                (config.intrinsics.cx - config.intrinsics.width / 2f) / config.intrinsics.width,
                (config.intrinsics.cy - config.intrinsics.height / 2f) / config.intrinsics.height
            );
            cam.sensorSize = new Vector2(config.intrinsics.width, config.intrinsics.height);

            float near = cam.nearClipPlane;
            float far = cam.farClipPlane;

            float left = -config.intrinsics.cx * near / config.intrinsics.fx;
            float right = (config.intrinsics.width - config.intrinsics.cx) * near / config.intrinsics.fx;
            float bottom = - (config.intrinsics.height - config.intrinsics.cy) * near / config.intrinsics.fy;
            float top = config.intrinsics.cy * near / config.intrinsics.fy;

            cam.projectionMatrix = PerspectiveOffCenter(left, right, bottom, top, near, far);
            
            float fovY = 2f * Mathf.Atan((top - bottom) / (2 * near)) * Mathf.Rad2Deg;
            float fovX = 2f * Mathf.Atan((right - left) / (2 * near)) * Mathf.Rad2Deg;
            Debug.Log($"Computed fovY: {fovY}, fovX: {fovX}");
            
            cam.aspect = (config.intrinsics.width * config.intrinsics.fx) / (config.intrinsics.height * config.intrinsics.fy);
            // Screen.SetResolution(config.intrinsics.width, config.intrinsics.height, false);
        }

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f);

    }

    public static Matrix4x4 PerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
    {
        Matrix4x4 m = new Matrix4x4();
        m[0, 0] = 2f * near / (right - left);
        m[0, 1] = 0f;
        m[0, 2] = (right + left) / (right - left);
        m[0, 3] = 0f;
        
        m[1, 0] = 0f;
        m[1, 1] = 2f * near / (top - bottom);
        m[1, 2] = (top + bottom) / (top - bottom);
        m[1, 3] = 0f;
        
        m[2, 0] = 0f;
        m[2, 1] = 0f;
        m[2, 2] = -(far + near) / (far - near);
        m[2, 3] = -2f * far * near / (far - near);
        
        m[3, 0] = 0f;
        m[3, 1] = 0f;
        m[3, 2] = -1f;
        m[3, 3] = 0f;
        return m;
    }

    private float GetRandomOffset(float range)
    {
        if (range == 0) return 0;

        range = Mathf.Abs(range);

        float offset = (float)SyntheseyesUtils.NextGaussianDouble() * (range / 2f);
        return Mathf.Clamp(offset, -range, range);
    }

    private float SampleCameraDistanceCm()
    {
        if (cameraDistanceBands.Count == 0 || cameraDistanceTotalWeight <= 0f)
            return cameraArrayCenter.z + GetRandomOffset(cameraArrayPositionNoise.z);

        float draw = Random.value * cameraDistanceTotalWeight;
        float cumulative = 0f;
        for (int i = 0; i < cameraDistanceBands.Count; i++)
        {
            CameraDistanceBand band = cameraDistanceBands[i];
            cumulative += band.weight;
            if (draw <= cumulative || i == cameraDistanceBands.Count - 1)
                return Random.Range(band.minCm, band.maxCm);
        }
        return cameraArrayCenter.z;
    }

    private Vector3 SampleCameraRotationOffsetDeg()
    {
        if (cameraAngleBands.Count == 0 || cameraAngleTotalWeight <= 0f)
            return new Vector3(
                GetRandomOffset(cameraArrayRotationNoise.x),
                GetRandomOffset(cameraArrayRotationNoise.y),
                Random.Range(cameraRollRangeDeg.x, cameraRollRangeDeg.y));

        float draw = Random.value * cameraAngleTotalWeight;
        float cumulative = 0f;
        CameraAngleBand selected = cameraAngleBands[cameraAngleBands.Count - 1];
        for (int i = 0; i < cameraAngleBands.Count; i++)
        {
            cumulative += cameraAngleBands[i].weight;
            if (draw <= cumulative)
            {
                selected = cameraAngleBands[i];
                break;
            }
        }
        float magnitude = Random.Range(selected.minDeg, selected.maxDeg);
        float direction = Random.Range(0f, 2f * Mathf.PI);
        return new Vector3(
            magnitude * Mathf.Sin(direction),
            magnitude * Mathf.Cos(direction),
            Random.Range(cameraRollRangeDeg.x, cameraRollRangeDeg.y));
    }

    private void EnsureRandomizedBackdrop()
    {
        if (randomizedBackdrop != null) return;
        randomizedBackdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
        randomizedBackdrop.name = "GradPupilRandomizedBackdrop";
        Collider backdropCollider = randomizedBackdrop.GetComponent<Collider>();
        if (backdropCollider != null) Destroy(backdropCollider);
        randomizedBackdropTexture = new Texture2D(32, 32, TextureFormat.RGB24, false);
        randomizedBackdropTexture.wrapMode = TextureWrapMode.Repeat;
        randomizedBackdropTexture.filterMode = FilterMode.Bilinear;
        // Load through Resources so player shader stripping cannot remove the
        // procedural backdrop shader from standalone builds.
        Shader shader = Resources.Load<Shader>("GradPupilBackdrop");
        if (shader == null)
        {
            Debug.LogError("GradPupilBackdrop shader is missing from Assets/Resources; " +
                           "disabling procedural backdrop instead of crashing.");
            randomizedBackdrop.SetActive(false);
            return;
        }
        randomizedBackdropMaterial = new Material(shader);
        randomizedBackdropMaterial.mainTexture = randomizedBackdropTexture;
        randomizedBackdrop.GetComponent<Renderer>().material = randomizedBackdropMaterial;
        randomizedBackdrop.transform.position = new Vector3(0f, 0f, -30f);
        randomizedBackdrop.transform.localScale = new Vector3(200f, 200f, 1f);
    }

    private void RandomizeBackdrop()
    {
        EnsureRandomizedBackdrop();
        if (randomizedBackdropMaterial == null || randomizedBackdropTexture == null) return;
        int width = randomizedBackdropTexture.width;
        int height = randomizedBackdropTexture.height;
        Color[] pixels = new Color[width * height];
        float baseLevel = Random.Range(0.03f, 0.75f);
        float gradientX = Random.Range(-0.45f, 0.45f);
        float gradientY = Random.Range(-0.45f, 0.45f);
        float waveAmplitude = Random.Range(0f, 0.30f);
        float waveX = Random.Range(0.5f, 4f);
        float waveY = Random.Range(0.5f, 4f);
        float phase = Random.Range(0f, 2f * Mathf.PI);
        for (int y = 0; y < height; y++)
        {
            float yn = (y + 0.5f) / height - 0.5f;
            for (int x = 0; x < width; x++)
            {
                float xn = (x + 0.5f) / width - 0.5f;
                float wave = Mathf.Sin((xn * waveX + yn * waveY) * 2f * Mathf.PI + phase);
                float noise = Random.Range(-0.10f, 0.10f);
                float value = Mathf.Clamp01(baseLevel + gradientX * xn + gradientY * yn +
                                             waveAmplitude * wave + noise);
                pixels[y * width + x] = new Color(value, value, value, 1f);
            }
        }
        randomizedBackdropTexture.SetPixels(pixels);
        randomizedBackdropTexture.Apply(false, false);
        randomizedBackdrop.transform.position = new Vector3(
            Random.Range(-20f, 20f), Random.Range(-20f, 20f), Random.Range(-50f, -20f));
        randomizedBackdrop.transform.eulerAngles = new Vector3(0f, 0f, Random.Range(-180f, 180f));
        randomizedBackdrop.transform.localScale = new Vector3(200f, 200f, 1f);
        float clearLevel = Random.Range(0f, 0.8f);
        for (int i = 0; i < cameraList.Count; i++)
        {
            cameraList[i].clearFlags = CameraClearFlags.SolidColor;
            cameraList[i].backgroundColor = new Color(clearLevel, clearLevel, clearLevel, 1f);
        }
    }

    public void RandomizePose()
    {
        if (eyeball == null)
        {
            Debug.LogWarning("RandomizeScene: eyeball not ready.");
            return;
        }

        if (randomizeSceneCallCount == 0)
        {
            randomizeSceneStartTime = Time.time;
        }
        randomizeSceneCallCount++;

        float yawNoise = (eyeParameters != null) ? eyeParameters.yawNoise : eyeYawNoise;
        float pitchNoise = (eyeParameters != null) ? eyeParameters.pitchNoise : eyePitchNoise;
        float defaultYaw = (eyeParameters != null) ? eyeParameters.defaultYaw : defaultEyeYaw;
        float defaultPitch = (eyeParameters != null) ? eyeParameters.defaultPitch : defaultEyePitch;

        float randomYaw = Random.Range(-yawNoise, yawNoise) + defaultYaw;
        float randomPitch = Random.Range(-pitchNoise, pitchNoise) + defaultPitch;

        eyeball.SetEyeRotation(randomPitch, randomYaw);

        // Camera positioning only applies when a config has been loaded with cameras.
        if (cameraList == null || cameraList.Count == 0)
            return;

        if (useMotionCenter && cameraParent != null)
        {
            float offsetX = GetRandomOffset(cameraArrayPositionNoise.x);
            float offsetY = GetRandomOffset(cameraArrayPositionNoise.y);
            float distanceZ = SampleCameraDistanceCm();
            Vector3 rotationOffset = SampleCameraRotationOffsetDeg();

            cameraParent.transform.position = new Vector3(
                cameraArrayCenter.x + offsetX,
                cameraArrayCenter.y + offsetY,
                distanceZ);
            cameraParent.transform.eulerAngles = cameraArrayRotation + rotationOffset;
            RandomizeBackdrop();

            // Apply per-camera noise on top of the array noise (local space within parent).
            for (int i = 0; i < cameraList.Count; i++)
            {
                if (i >= cameraOriginalLocalPositions.Count) break;
                Vector3 camPosNoise = i < cameraExtrinsicsPositionNoise.Count ? cameraExtrinsicsPositionNoise[i] : Vector3.zero;
                Vector3 camRotNoise = i < cameraExtrinsicsRotationNoise.Count ? cameraExtrinsicsRotationNoise[i] : Vector3.zero;
                cameraList[i].transform.localPosition = cameraOriginalLocalPositions[i] + new Vector3(
                    GetRandomOffset(camPosNoise.x),
                    GetRandomOffset(camPosNoise.y),
                    GetRandomOffset(camPosNoise.z));
                cameraList[i].transform.localEulerAngles = cameraOriginalLocalRotations[i] + new Vector3(
                    GetRandomOffset(camRotNoise.x),
                    GetRandomOffset(camRotNoise.y),
                    GetRandomOffset(camRotNoise.z));
            }
        }
        else
        {
            Camera currentCam = cameraList[currentCameraIndex];

            Vector3 originalPosition = cameraOriginalPositions[currentCameraIndex];
            Vector3 originalRotation = cameraOriginalRotations[currentCameraIndex];

            Vector3 positionNoise = cameraExtrinsicsPositionNoise[currentCameraIndex];
            Vector3 rotationNoise = cameraExtrinsicsRotationNoise[currentCameraIndex];

            float offsetX = GetRandomOffset(positionNoise.x);
            float offsetY = GetRandomOffset(positionNoise.y);
            float offsetZ = GetRandomOffset(positionNoise.z);
            float offsetPitch = GetRandomOffset(rotationNoise.x);
            float offsetYaw = GetRandomOffset(rotationNoise.y);
            float offsetRoll = GetRandomOffset(rotationNoise.z);

            currentCam.transform.position = originalPosition + new Vector3(offsetX, offsetY, offsetZ);
            currentCam.transform.eulerAngles = originalRotation + new Vector3(offsetPitch, offsetYaw, offsetRoll);

            for (int i = 0; i < pointLightList.Count; i++)
            {
                if (pointLightArrayMounted[i]) continue;

                Light light = pointLightList[i];
                Vector3 lightOriginalPosition = pointLightOriginalPositions[i];
                Vector3 lightOriginalRotation = pointLightOriginalRotations[i];

                JSONNode lightsArray = JSON.Parse(File.ReadAllText(string.IsNullOrEmpty(activeConfigPath) ? ResolveProjectPath(jsonConfigPath) : activeConfigPath))["lights"].AsArray;
                if (i >= lightsArray.Count) continue;

                JSONNode lightNode = lightsArray[i];
                JSONNode lightPositionNoiseNode = lightNode["position_noise"];

                if (lightPositionNoiseNode != null)
                {
                    Vector3 lightPositionNoise = new Vector3(
                        lightPositionNoiseNode["x"] != null ? lightPositionNoiseNode["x"].AsFloat : 0f,
                        lightPositionNoiseNode["y"] != null ? lightPositionNoiseNode["y"].AsFloat : 0f,
                        lightPositionNoiseNode["z"] != null ? lightPositionNoiseNode["z"].AsFloat : 0f
                    );

                    Vector3 lightRotationNoise = new Vector3(
                        lightPositionNoiseNode["rx"] != null ? lightPositionNoiseNode["rx"].AsFloat : 0f,
                        lightPositionNoiseNode["ry"] != null ? lightPositionNoiseNode["ry"].AsFloat : 0f,
                        lightPositionNoiseNode["rz"] != null ? lightPositionNoiseNode["rz"].AsFloat : 0f
                    );

                    float lightOffsetX = GetRandomOffset(lightPositionNoise.x * 100f); 
                    float lightOffsetY = GetRandomOffset(-lightPositionNoise.y * 100f); 
                    float lightOffsetZ = GetRandomOffset(lightPositionNoise.z * 100f);
                    float lightOffsetPitch = GetRandomOffset(-lightRotationNoise.x);
                    float lightOffsetYaw = GetRandomOffset(lightRotationNoise.y); 
                    float lightOffsetRoll = GetRandomOffset(-lightRotationNoise.z); 

                    light.transform.position = lightOriginalPosition + new Vector3(lightOffsetX, lightOffsetY, lightOffsetZ);
                    light.transform.eulerAngles = lightOriginalRotation + new Vector3(lightOffsetPitch, lightOffsetYaw, lightOffsetRoll);
                }
            }
        }
    }


    /**
     * Update the file system path where output images and metadata will be saved.
     *
     * @param {string} newPath - New directory path for output data.
     *
     * Ensures the directory exists and logs the update.
     */
    public void UpdateOutputPath(string newPath)
    {
        if (!string.IsNullOrEmpty(newPath))
        {
            // Combine the base path with the user-specified folder name.
            string combinedPath = Path.Combine(newPath, outputFolderName);
            Debug.Log($"Setting output path to: {combinedPath}");
            outputPath = combinedPath;
            EnsureDirectoryExists(combinedPath);
        }
        else
        {
            Debug.LogWarning("No output path provided; using persistentDataPath fallback.");
            string fallbackPath = Path.Combine(Application.persistentDataPath, "imgs");
            outputPath = fallbackPath;
            EnsureDirectoryExists(fallbackPath);
        }
    }


    /**
     * Save frame data from all cameras including rendered images and ground truth metadata.
     *
     * @returns {IEnumerator} Used as a coroutine to yield after rendering.
     *
     * Saves an image and JSON metadata for each camera and handles frame count limits.
     */
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            SwitchCamera(+1);
        }
        else if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            SwitchCamera(-1);
        }

        if (isSavingData && datasetRandomizer != null)
        {
            datasetRandomizer.PrepareCurrentSample();
        }
        else if (isSavingData || Input.GetKey("c"))
        {
            ToggleOutputPreview();
            RandomizePose();
            ToggleOutputPreview();
        }

        if (Input.GetKeyDown("p"))
        {
            ToggleOutputPreview();
        }

        if (datasetRandomizer == null && (isSavingData || Input.GetKey("r")))
        {
            ToggleOutputPreview();
            eyeRegion.RandomizeAppearance();
            eyeball.RandomizeEyeball();
            ToggleOutputPreview();
        }

        if (Input.GetKey("l"))
        {
            lightingController.RandomizeLighting();
            if (nirLightingMode)
            {
                ApplyNirLightingMode();
            }
        }

        eyeRegion.UpdateEyeRegion();
        eyeRegionSubdiv.Subdivide();
        eyeWetness.UpdateEyeWetness();
        eyeWetnessSubdiv.Subdivide();
        if (datasetRandomizer == null && (isSavingData || Input.GetKey("r")))
        {
            RandomizeIdentityLashes();
        }
        foreach (DeformEyeLashes eyeLash in eyeLashes)
            eyeLash.UpdateLashes();

        if ((isSavingData || Input.GetKey("s")) && !isSavingFrame)
        {
            StartCoroutine(saveFrame());
        }

        if (Input.GetKeyUp("h"))
            GameObject.Find("GUI Canvas").GetComponent<Canvas>().enabled = !GameObject.Find("GUI Canvas").GetComponent<Canvas>().enabled;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Debug.Log("Escape key pressed - quitting application");
            Application.Quit();
        }
    }

    private void SwitchCamera(int direction) {
        if (cameraList == null || cameraList.Count == 0) {
            Debug.LogWarning("No cameras available to switch.");
            return;
        }
        cameraList[currentCameraIndex].enabled = false;
            
        currentCameraIndex = (currentCameraIndex + direction) % cameraList.Count;
        if (currentCameraIndex < 0)
            currentCameraIndex = cameraList.Count - 1;
            
        cameraList[currentCameraIndex].enabled = true;
        cameraList[currentCameraIndex].tag = "MainCamera";

        // set screen resolution to current camera
        int width = cameraOriginalIntrinsics[currentCameraIndex].width;
        int height = cameraOriginalIntrinsics[currentCameraIndex].height;
        Screen.SetResolution(width, height, false);

        Debug.Log($"Switched to camera {currentCameraIndex}: {cameraList[currentCameraIndex].name}");
        Debug.Log($"Dimensions: {width} x {height}");
    }

    public void ToggleOutputPreview() {
        visualizationManager.ToggleVisualization();
    }

    public void RandomizeIdentityLashes()
    {
        if (eyeLashes == null) return;

        foreach (DeformEyeLashes eyeLash in eyeLashes)
        {
            if (eyeLash == null) continue;

            if (eyeLash.isTopLash)
            {
                eyeLash.hairLengthModifier = Random.Range(0.55f, 1.35f);
                eyeLash.margin_offset = Random.Range(0.35f, 0.70f);
                eyeLash.topLashStartAngle = Random.Range(25f, 48f);
                eyeLash.topLashDeltaAngle = Random.Range(-28f, -10f);
            }
            else
            {
                eyeLash.hairLengthModifier = Random.Range(0.35f, 1.10f);
                eyeLash.margin_offset = Random.Range(0.30f, 0.70f);
                eyeLash.bottomLashStartAngle = Random.Range(30f, 58f);
                eyeLash.bottomLashDeltaAngle = Random.Range(-28f, -8f);
            }
        }
    }


    private Color parseColor(JSONNode jN)
    {
        return new Color(jN[0].AsFloat, jN[1].AsFloat, jN[2].AsFloat, 1.0f);
    }

    private Vector3 parseVec(JSONNode jN)
    {
        return new Vector3(jN[0].AsFloat, jN[1].AsFloat, jN[2].AsFloat);
    }

    private void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            Debug.Log($"Created directory: {path}");
        }
    }

    private IEnumerator saveFrame()
    {
        isSavingFrame = true;
        if (framesSaved >= maxSamplesToSave)
        {
            Debug.Log($"Maximum number of samples ({maxSamplesToSave}) reached. Stopping data collection.");
            isSavingData = false;
            Canvas completedCanvas = GameObject.Find("GUI Canvas")?.GetComponent<Canvas>();
            if (completedCanvas != null) completedCanvas.enabled = true;
            isSavingFrame = false;
            if (quitOnComplete)
            {
                Debug.Log("Shard complete; quitting because --quit-on-complete was specified.");
                Application.Quit(0);
            }
            yield break;
        }

        framesSaved++;
        int outputFrame = datasetRandomizer != null
            ? datasetRandomizer.GlobalSampleIndex + 1
            : framesSaved;
        int originalCameraIndex = currentCameraIndex;

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = Path.Combine(Application.persistentDataPath, "imgs");
        }
        EnsureDirectoryExists(outputPath);

        for (int i = 0; i < cameraList.Count; i++)
        {
            Camera cam = cameraList[i];
            string cameraName = cam.gameObject.name;

            Debug.Log(cameraName + " local frame: " + framesSaved + " global frame: " + outputFrame);

            SwitchCamera(i - currentCameraIndex);

            int width = cameraOriginalIntrinsics[i].width;
            int height = cameraOriginalIntrinsics[i].height;
            // Keep a linear half-float master so later optics/sensor processing
            // can set exposure, clipping and RAW8 quantization without JPEG or
            // 8-bit readback artefacts.
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                width,
                height,
                24,
                RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear);

            RenderTexture originalRenderTexture = cam.targetTexture;
            cam.targetTexture = renderTexture;
            cam.Render();

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = renderTexture;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            cam.targetTexture = originalRenderTexture;
            RenderTexture.active = null;

            byte[] imgBytes = tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
            string fileName = Path.Combine(outputPath, $"{outputFrame}_{cameraName}.exr");
            File.WriteAllBytes(fileName, imgBytes);

            RenderTexture.ReleaseTemporary(renderTexture);
            Object.Destroy(tex);
        }

        saveAllCamerasDetails(outputFrame);
        if (datasetRandomizer != null)
        {
            datasetRandomizer.AdvanceAfterSave();
        }

        SwitchCamera(originalCameraIndex - currentCameraIndex);
        isSavingFrame = false;
    }

    public void ResetFrameCounter()
    {
        framesSaved = 0;
        if (datasetRandomizer != null) datasetRandomizer.ResetSequence();
        Debug.Log("Frame counter reset. Ready to collect new samples.");
    }

    public void ReloadConfiguration(string configPath = null)
    {
        string path = configPath != null ? configPath : (string.IsNullOrEmpty(activeConfigPath) ? ResolveProjectPath(jsonConfigPath) : activeConfigPath);
        Debug.Log($"Reloading configuration from: {path}");

        CleanupCurrentScene();

        ResetFrameCounter();

        if (File.Exists(path))
        {
            LoadCamerasFromConfig(path);
            ApplyCommandLineOutputOverride();

            if (eyeParameters != null)
            {
                eyeball.SetPupilSizeRange(eyeParameters.pupilSizeRange);
                eyeball.SetIrisSizeRange(eyeParameters.irisSizeRange);
            }

            // Sync the UI to reflect the loaded config.
            MenuController menuController = FindFirstObjectByType<MenuController>();
            if (menuController != null)
            {
                JSONNode rootNode = JSON.Parse(File.ReadAllText(path));
                menuController.PopulateUIFromConfig(rootNode);
            }

            ConfigureDatasetRandomizer(path);
            RandomizePose();

            ToggleOutputPreview();
            ToggleOutputPreview();

            Debug.Log("Configuration reloaded successfully");
        }
        else
        {
            Debug.LogError($"Config file not found at: {path}");
        }
        UpdateMainCameraResolution();
    }

    public void UpdateMainCameraResolution()
    {
        if (cameraList == null || cameraList.Count == 0)
        {
            Debug.LogWarning("No cameras available for updating resolution.");
            return;
        }
        int width = cameraOriginalIntrinsics[currentCameraIndex].width;
        int height = cameraOriginalIntrinsics[currentCameraIndex].height;
        Screen.SetResolution(width, height, false);
        Debug.Log($"Main camera resolution set to: {width} x {height}");
    }

    private void CleanupCurrentScene()
    {
        foreach (Camera cam in cameraList)
        {
            if (cam != null)
            {
                Destroy(cam.gameObject);
            }
        }
        cameraList.Clear();
        cameraOriginalPositions.Clear();
        cameraOriginalRotations.Clear();
        cameraExtrinsicsPositionNoise.Clear();
        cameraExtrinsicsRotationNoise.Clear();
        cameraOriginalLocalPositions.Clear();
        cameraOriginalLocalRotations.Clear();
        cameraOriginalIntrinsics.Clear();

        foreach (Light light in pointLightList)
        {
            if (light != null)
            {
                Destroy(light.gameObject);
            }
        }
        pointLightList.Clear();
        pointLightOriginalPositions.Clear();
        pointLightOriginalRotations.Clear();
        pointLightArrayMounted.Clear();

        if (cameraParent != null)
        {
            Destroy(cameraParent);
            cameraParent = null;
        }

        currentCameraIndex = 0;
    }


    /**
     * Save 2D projection and metadata from all camera views for a given frame.
     *
     * @param {int} frame - Frame index used for naming the output JSON file.
     * @param {string} outputPath - Directory path where the metadata JSON will be saved. Defaults to "imgs".
     *
     * Gathers 2D screen-space coordinates of anatomical landmarks (e.g., iris, caruncle, interior margin)
     * from all cameras and stores the ground truth gaze vector and poses. Also logs eyeball and lighting details.
     * Useful for creating labeled datasets for training or evaluation.
     */
    private void saveAllCamerasDetails(int frame)
    {
        JSONNode rootNode = new JSONClass();

        rootNode.Add("eye_details", eyeball.GetEyeballDetails());
        rootNode.Add("lighting_details", lightingController.GetLightingDetails());
        rootNode.Add("eye_region_details", eyeRegion.GetEyeRegionDetails());
        if (datasetRandomizer != null)
        {
            rootNode.Add("dataset_state", datasetRandomizer.GetMetadata());
        }

        JSONNode camerasNode = new JSONClass();
        rootNode.Add("cameras", camerasNode);

        Mesh meshEyeRegion = eyeRegion.transform.GetComponent<MeshFilter>().mesh;
        Mesh meshEyeBall = eyeball.transform.GetComponent<MeshFilter>().mesh;

        for (int i = 0; i < cameraList.Count; i++)
        {
            Camera cam = cameraList[i];
            string cameraName = cam.gameObject.name;
            JSONNode cameraNode = new JSONClass();
            camerasNode.Add(cameraName, cameraNode);

            cameraNode.Add("head_pose", cam.transform.rotation.eulerAngles.ToString("F4"));
            cameraNode.Add("camera_pose", eyeball.GetCameratoEyeCenterPose(cam));

            int imageWidth = cameraOriginalIntrinsics[i].width;
            int imageHeight = cameraOriginalIntrinsics[i].height;
            JSONNode labelImageSize = new JSONClass();
            labelImageSize.Add("width", new JSONData(imageWidth));
            labelImageSize.Add("height", new JSONData(imageHeight));
            cameraNode.Add("label_image_size", labelImageSize);

            JSONArray listInteriorMargin2D = new JSONArray();
            cameraNode.Add("interior_margin_2d", listInteriorMargin2D);
            foreach (var idx in EyeRegionTopology.interior_margin_idxs)
            {
                Vector3 v_3d = eyeRegion.transform.localToWorldMatrix * meshEyeRegion.vertices[idx];
                Vector3 p = cam.WorldToViewportPoint(v_3d);
                p.x *= imageWidth;
                p.y *= imageHeight;
                listInteriorMargin2D.Add(new JSONData(p.ToString("F4")));
            }

            JSONArray listCaruncle2D = new JSONArray();
            cameraNode.Add("caruncle_2d", listCaruncle2D);
            foreach (var idx in EyeRegionTopology.caruncle_idxs)
            {
                Vector3 v_3d = eyeRegion.transform.localToWorldMatrix * meshEyeRegion.vertices[idx];
                Vector3 p = cam.WorldToViewportPoint(v_3d);
                p.x *= imageWidth;
                p.y *= imageHeight;
                listCaruncle2D.Add(new JSONData(p.ToString("F4")));
            }

            JSONArray listIris2D = new JSONArray();
            cameraNode.Add("iris_2d", listIris2D);
            foreach (var idx in EyeRegionTopology.iris_idxs)
            {
                Vector3 v_3d = eyeball.transform.localToWorldMatrix * meshEyeBall.vertices[idx];
                Vector3 p = cam.WorldToViewportPoint(v_3d);
                p.x *= imageWidth;
                p.y *= imageHeight;
                listIris2D.Add(new JSONData(p.ToString("F4")));
            }

            cameraNode.Add("ground_truth", eyeball.GetGazeVector(cam));
        }

        string filePath = Path.Combine(outputPath, $"{frame}.json");
        File.WriteAllText(filePath, rootNode.ToJSON(0));
    }
}
