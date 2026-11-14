using System;
using System.IO;
using System.Text;
using PathDrawer.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.IO.Compression;

namespace PathDrawer.Editor
{
    /// <summary>Builds the live portfolio scene and exports editor-measured proof artifacts.</summary>
    [InitializeOnLoad]
    public static class PathDrawerPortfolioTools
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string GeneratedFolder = "Assets/PathDrawer/Generated";
        private const string MaterialFolder = "Assets/PathDrawer/Materials";
        private const string ScreenshotPendingKey = "PathDrawer.Screenshot.Pending";
        private const string ScreenshotFrameKey = "PathDrawer.Screenshot.StartFrame";
        private const string ObstacleLayerName = "PathDrawerObstacles";
        private const int TerrainResolution = 80;

        static PathDrawerPortfolioTools()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem("Tools/PathDrawer/1. Build & Save Ready-to-Play SampleScene")]
        public static void BuildAndSaveSampleScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            int obstacleLayer = EnsureObstacleLayer();
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/PathDrawer", "Generated");
            EnsureFolder("Assets/PathDrawer", "Materials");
            EnsureFolder("Assets", "Shaders");
            EnsureFolder("Assets/Shaders", "PathDrawer");
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset("Assets/Shaders/PathDrawer/TacticalArenaGrid.shader", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset("Assets/Shaders/PathDrawer/TrajectoryRibbonHologram.shader", ImportAssetOptions.ForceSynchronousImport);
            if (Shader.Find("PathDrawer/TacticalArenaGrid") == null || Shader.Find("PathDrawer/TrajectoryRibbonHologram") == null)
                throw new InvalidOperationException("The tactical arena or trajectory hologram shader did not compile. See the Unity Console for shader errors.");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Material terrainMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/TacticalArenaGrid.mat", "PathDrawer/TacticalArenaGrid", new Color(0.12f, 0.15f, 0.19f, 1f));
            Material reachableMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/ReachablePath.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(0.051f, 0.58f, 0.54f, 0.82f));
            Material reachableCoreMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/ReachableLineCore.mat", new Color(0.035f, 0.19f, 0.18f), 0f, 0.18f);
            Material blockedMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/BlockedPath.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(0.937f, 0.267f, 0.267f, 0.75f));
            blockedMaterial.SetColor("_ChevronColor", new Color(1f, 0.42f, 0.42f, 1f));
            blockedMaterial.SetColor("_EdgeColor", new Color(0.40f, 0.075f, 0.09f, 1f));
            EditorUtility.SetDirty(blockedMaterial);
            Material goldMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/BallisticArc.mat", new Color(0.96f, 0.58f, 0.08f), 0f, 0.28f);
            Material hazardMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/HazardEmission.mat", new Color(0.937f, 0.267f, 0.267f), 0f, 0.22f);
            Material waypointMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/WaypointEmission.mat", new Color(0.08f, 0.58f, 0.54f), 0f, 0.16f);
            Material monolithMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/MonolithGunmetal.mat", new Color(0.20f, 0.24f, 0.29f), 0.05f, 0.24f);
            Material bandMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/MonolithHazardBand.mat", new Color(0.85f, 0.47f, 0.02f), 0f, 0.2f);
            Mesh ringMesh = CreateRingMeshAsset();

            CreateTerrain(terrainMaterial);
            CreateObstacleMonolith("Obstacle 1 — Ridge Bypass", TerrainBasePosition(-0.1f, 0.1f), obstacleLayer, monolithMaterial, bandMaterial);
            CreateObstacleMonolith("Obstacle 2 — Impact Target", TerrainBasePosition(5.5f, 4.3f), obstacleLayer, monolithMaterial, bandMaterial);
            CreateObstacleMonolith("Obstacle 3 — Perimeter", TerrainBasePosition(-6.2f, 2.7f), obstacleLayer, monolithMaterial, bandMaterial);
            CreateLightingAndCamera(out Camera camera);
            CreateGlobalPostProcessing();

            LineRenderer reachableLine = CreateLine("Reachable Trajectory Edge", reachableCoreMaterial, 0.025f);
            LineRenderer blockedLine = CreateLine("Blocked Trajectory", blockedMaterial, 0.12f);
            LineRenderer ballisticLine = CreateLine("Ballistic Arc Preview", goldMaterial, 0.055f);
            MeshFilter reachableRibbon = CreateRibbon("Reachable Teal Ribbon", reachableMaterial);
            Transform[] waypointMarkers = CreateWaypointMarkers(ringMesh, waypointMaterial, 24);
            Transform impactRing = CreateRingObject("Obstacle Impact Ring", ringMesh, hazardMaterial).transform;
            impactRing.localScale = Vector3.one * 3f;
            impactRing.gameObject.SetActive(false);
            Transform[] ballisticTicks = CreateBallisticTicks(goldMaterial, 10);
            Transform landingReticle = CreateRingObject("Ballistic Landing Reticle", ringMesh, goldMaterial).transform;
            landingReticle.localScale = Vector3.one * 2.2f;
            landingReticle.gameObject.SetActive(false);

            var controllerObject = new GameObject("PathDrawer Demo Controller");
            PathDrawerDemoController controller = controllerObject.AddComponent<PathDrawerDemoController>();
            var serializedController = new SerializedObject(controller);
            SetReference(serializedController, "drawCamera", camera);
            SetInteger(serializedController, "groundMask", 1 << 0);
            SetInteger(serializedController, "obstacleMask", 1 << obstacleLayer);
            SetReference(serializedController, "reachableLine", reachableLine);
            SetReference(serializedController, "blockedLine", blockedLine);
            SetReference(serializedController, "ballisticArcLine", ballisticLine);
            SetReference(serializedController, "impactBeacon", null);
            SetReference(serializedController, "ribbonMeshFilter", reachableRibbon);
            SetReference(serializedController, "blockedRibbonMeshFilter", null);
            SetReferences(serializedController, "waypointMarkers", waypointMarkers);
            SetReference(serializedController, "impactRing", impactRing);
            SetReferences(serializedController, "ballisticTickMarkers", ballisticTicks);
            SetReference(serializedController, "ballisticLandingReticle", landingReticle);
            SetInteger(serializedController, "maxInputPoints", 1024);
            SetInteger(serializedController, "maxOutputPoints", 4096);
            SetInteger(serializedController, "lookupSamplesPerSegment", 16);
            SetFloat(serializedController, "outputSpacing", 0.2f);
            SetFloat(serializedController, "ribbonWidth", 0.42f);
            SetFloat(serializedController, "surfaceOffset", 0.045f);
            SetInteger(serializedController, "showcaseRawPointCount", 140);
            SetBoolean(serializedController, "autoShowcase", true);
            SetBoolean(serializedController, "showBallisticPreview", true);
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            Text telemetry = CreateTelemetryHud(camera);
            SetReference(serializedController, "telemetryText", telemetry);
            SetReference(serializedController, "captureHudCanvas", telemetry != null ? telemetry.GetComponentInParent<Canvas>() : null);
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            IncludeDemoSceneInBuildSettings();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException($"Unity could not save the PathDrawer demo scene at {ScenePath}.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"PathDrawer ready-to-play scene saved to {ScenePath}. Ground mask: Default; obstacle mask: {ObstacleLayerName} (layer {obstacleLayer}). Start Play Mode for the animated trajectory.");
        }

        [MenuItem("Tools/PathDrawer/2. Run Benchmark Sweep & Export Markdown")]
        public static void RunBenchmarkSweepAndExportMarkdown()
        {
            string report = PathDrawerBenchmarkRunner.RunAndExport();
            UpdateReadmeBenchmarkTable(report);
            AssetDatabase.Refresh();
            Debug.Log("PathDrawer-Benchmark-Results.md was written and the README benchmark table now contains the current editor measurements.");
        }

        [MenuItem("Tools/PathDrawer/3. Capture 1080p Portfolio Screenshot")]
        public static void CapturePortfolioScreenshot()
        {
            SessionState.SetBool(ScreenshotPendingKey, true);
            SessionState.SetInt(ScreenshotFrameKey, -1);
            if (!EditorApplication.isPlaying)
                EditorApplication.isPlaying = true;
            else
                BeginScreenshotWarmup();
            Debug.Log("PathDrawer screenshot capture is armed. The editor will wait 60 Play Mode frames before saving docs/screenshots/pathdrawer-game-view.png.");
        }

        [MenuItem("Tools/PathDrawer/4. Build WebGL for itch.io (ZIP)")]
        public static void BuildWebGlForItchIo()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                throw new InvalidOperationException("WebGL Build Support is not installed for this Unity Editor. Add the module in Unity Hub, then run this menu item again.");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string buildDirectory = Path.Combine(projectRoot, "Builds", "WebGL-ItchIo");
            string zipPath = Path.Combine(projectRoot, "Builds", "WebGL-ItchIo.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(buildDirectory));
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;

            var options = new BuildPlayerOptions
            {
                scenes = Array.ConvertAll(Array.FindAll(EditorBuildSettings.scenes, scene => scene.enabled), scene => scene.path),
                locationPathName = buildDirectory,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"WebGL build ended with {report.summary.result}; see the Unity Console for details.");

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(buildDirectory, zipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            Debug.Log($"WebGL build complete: {buildDirectory}; itch.io upload archive: {zipPath} ({new FileInfo(zipPath).Length:N0} bytes). index.html is at the ZIP root.");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(ScreenshotPendingKey, false))
                BeginScreenshotWarmup();
            else if (state == PlayModeStateChange.ExitingPlayMode)
                EditorApplication.update -= CaptureAfterWarmup;
        }

        private static void BeginScreenshotWarmup()
        {
            SessionState.SetInt(ScreenshotFrameKey, Time.frameCount);
            EditorApplication.update -= CaptureAfterWarmup;
            EditorApplication.update += CaptureAfterWarmup;
        }

        private static void CaptureAfterWarmup()
        {
            if (!SessionState.GetBool(ScreenshotPendingKey, false) || !EditorApplication.isPlaying)
            {
                EditorApplication.update -= CaptureAfterWarmup;
                return;
            }

            int startFrame = SessionState.GetInt(ScreenshotFrameKey, -1);
            if (startFrame < 0 || Time.frameCount - startFrame < 60) return;

            EditorApplication.update -= CaptureAfterWarmup;
            try
            {
                GameObject controllerObject = GameObject.Find("PathDrawer Demo Controller");
                PathDrawerDemoController controller = controllerObject != null ? controllerObject.GetComponent<PathDrawerDemoController>() : null;
                if (controller == null) throw new InvalidOperationException("PathDrawerDemoController was not found. Build the SampleScene before capturing.");
                controller.RebuildNow();
                AssertScreenshotReady(controller);
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string screenshotPath = Path.Combine(projectRoot, "docs", "screenshots", "pathdrawer-game-view.png");
                controller.Capture1080pScreenshot(screenshotPath);
                Debug.Log($"Captured live PathDrawer Game View ({new FileInfo(screenshotPath).Length:N0} bytes): {screenshotPath}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                SessionState.SetBool(ScreenshotPendingKey, false);
                EditorApplication.isPlaying = false;
            }
        }

        private static void AssertScreenshotReady(PathDrawerDemoController controller)
        {
            MeshFilter ribbonFilter = controller.ReachableRibbonMeshFilter;
            Mesh ribbon = ribbonFilter != null ? ribbonFilter.sharedMesh : null;
            LineRenderer reachable = controller.ReachableLineRenderer;
            LineRenderer blocked = controller.BlockedLineRenderer;
            LineRenderer ballistic = controller.BallisticLineRenderer;

            if (ribbon == null || ribbon.vertexCount <= 40)
                throw new InvalidOperationException($"Screenshot rejected: reachable ribbon has {ribbon?.vertexCount ?? 0} vertices; expected > 40.");
            MeshRenderer ribbonRenderer = ribbonFilter.GetComponent<MeshRenderer>();
            if (ribbonRenderer == null || !ribbonRenderer.enabled)
                throw new InvalidOperationException("Screenshot rejected: reachable ribbon MeshRenderer is missing or disabled.");
            if (reachable == null || !reachable.enabled || reachable.positionCount <= 10)
                throw new InvalidOperationException($"Screenshot rejected: reachable LineRenderer has {reachable?.positionCount ?? 0} positions or is disabled; expected > 10.");
            if (blocked == null || !blocked.enabled || blocked.positionCount <= 2)
                throw new InvalidOperationException($"Screenshot rejected: blocked LineRenderer has {blocked?.positionCount ?? 0} positions or is disabled; expected > 2.");
            if (ballistic == null || !ballistic.enabled || ballistic.positionCount <= 10)
                throw new InvalidOperationException($"Screenshot rejected: ballistic LineRenderer has {ballistic?.positionCount ?? 0} positions or is disabled; expected > 10.");
            if (string.IsNullOrEmpty(controller.TelemetryText))
                throw new InvalidOperationException("Screenshot rejected: telemetry text is missing.");

            int hudLineCount = 1;
            string telemetry = controller.TelemetryText;
            for (int i = 0; i < telemetry.Length; i++)
                if (telemetry[i] == '\n') hudLineCount++;
            if (hudLineCount != 5)
                throw new InvalidOperationException($"Screenshot rejected: live HUD has {hudLineCount} lines; expected five detail lines.");
        }

        private static int EnsureObstacleLayer()
        {
            UnityEngine.Object tagManagerAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            var tagManager = new SerializedObject(tagManagerAsset);
            SerializedProperty layers = tagManager.FindProperty("layers");
            int freeIndex = -1;
            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty layer = layers.GetArrayElementAtIndex(i);
                if (layer.stringValue == ObstacleLayerName) return i;
                if (freeIndex < 0 && string.IsNullOrWhiteSpace(layer.stringValue)) freeIndex = i;
            }
            if (freeIndex < 0) throw new InvalidOperationException($"No empty Unity layer is available for {ObstacleLayerName}.");
            layers.GetArrayElementAtIndex(freeIndex).stringValue = ObstacleLayerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            return freeIndex;
        }

        private static void IncludeDemoSceneInBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (!string.Equals(scenes[i].path, ScenePath, StringComparison.Ordinal)) continue;
                if (!scenes[i].enabled)
                {
                    scenes[i] = new EditorBuildSettingsScene(ScenePath, true);
                    EditorBuildSettings.scenes = scenes;
                }
                return;
            }
            var updatedScenes = new EditorBuildSettingsScene[scenes.Length + 1];
            Array.Copy(scenes, updatedScenes, scenes.Length);
            updatedScenes[scenes.Length] = new EditorBuildSettingsScene(ScenePath, true);
            EditorBuildSettings.scenes = updatedScenes;
        }

        private static void CreateTerrain(Material material)
        {
            const int vertexCount = TerrainResolution * TerrainResolution;
            const int triangleIndexCount = (TerrainResolution - 1) * (TerrainResolution - 1) * 6;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            int[] triangles = new int[triangleIndexCount];
            for (int z = 0; z < TerrainResolution; z++)
            for (int x = 0; x < TerrainResolution; x++)
            {
                int index = z * TerrainResolution + x;
                float worldX = -PathDrawerTerrainProfile.HalfExtent + 2f * PathDrawerTerrainProfile.HalfExtent * x / (TerrainResolution - 1f);
                float worldZ = -PathDrawerTerrainProfile.HalfExtent + 2f * PathDrawerTerrainProfile.HalfExtent * z / (TerrainResolution - 1f);
                float height = PathDrawerTerrainProfile.EvaluateHeight(worldX, worldZ);
                vertices[index] = new Vector3(worldX, height, worldZ);
                uvs[index] = new Vector2((worldX + PathDrawerTerrainProfile.HalfExtent) / 24f, (worldZ + PathDrawerTerrainProfile.HalfExtent) / 24f);
            }

            int triangle = 0;
            for (int z = 0; z < TerrainResolution - 1; z++)
            for (int x = 0; x < TerrainResolution - 1; x++)
            {
                int a = z * TerrainResolution + x;
                int b = a + TerrainResolution;
                int c = a + 1;
                int d = b + 1;
                triangles[triangle++] = a;
                triangles[triangle++] = b;
                triangles[triangle++] = c;
                triangles[triangle++] = c;
                triangles[triangle++] = b;
                triangles[triangle++] = d;
            }

            string meshPath = $"{GeneratedFolder}/SmoothSlateTerrain.asset";
            Mesh terrainMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (terrainMesh == null)
            {
                terrainMesh = new Mesh { name = "PathDrawer 80x80 Smooth Slate Terrain" };
                AssetDatabase.CreateAsset(terrainMesh, meshPath);
            }
            else terrainMesh.Clear();
            terrainMesh.indexFormat = IndexFormat.UInt32;
            terrainMesh.vertices = vertices;
            terrainMesh.uv = uvs;
            terrainMesh.triangles = triangles;
            terrainMesh.RecalculateNormals();
            terrainMesh.RecalculateBounds();
            EditorUtility.SetDirty(terrainMesh);

            var terrain = new GameObject("Smooth Slate Terrain — 24m · 80 × 80", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            terrain.GetComponent<MeshFilter>().sharedMesh = terrainMesh;
            terrain.GetComponent<MeshRenderer>().sharedMaterial = material;
            terrain.GetComponent<MeshCollider>().sharedMesh = terrainMesh;
        }

        private static void CreateObstacleMonolith(string name, Vector3 basePosition, int layer, Material bodyMaterial, Material bandMaterial)
        {
            const float height = 2.6f;
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = name;
            body.layer = layer;
            body.transform.position = basePosition + Vector3.up * (height * 0.5f);
            body.transform.localScale = new Vector3(0.92f, height * 0.5f, 0.92f);
            body.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;

            CreateHazardBand(name + " / amber lower rim", basePosition + Vector3.up * 0.48f, bandMaterial);
            CreateHazardBand(name + " / amber upper rim", basePosition + Vector3.up * 2.2f, bandMaterial);
        }

        private static Vector3 TerrainBasePosition(float x, float z) =>
            new(x, PathDrawerTerrainProfile.EvaluateHeight(x, z), z);

        private static void CreateHazardBand(string name, Vector3 center, Material material)
        {
            GameObject band = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            band.name = name;
            band.transform.position = center;
            band.transform.localScale = new Vector3(1.02f, 0.05f, 1.02f);
            band.GetComponent<MeshRenderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(band.GetComponent<Collider>());
        }

        private static void CreateLightingAndCamera(out Camera camera)
        {
            var lightObject = new GameObject("Key Light", typeof(Light));
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            Light key = lightObject.GetComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.15f;
            key.color = new Color(0.91f, 0.94f, 1f);
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.65f;
            key.shadowResolution = LightShadowResolution.VeryHigh;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.30f, 0.35f, 0.42f);
            RenderSettings.ambientEquatorColor = new Color(0.18f, 0.21f, 0.25f);
            RenderSettings.ambientGroundColor = new Color(0.075f, 0.09f, 0.11f);
            RenderSettings.ambientIntensity = 0.8f;

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 17f, -21f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.55f, 0f));
            camera = cameraObject.GetComponent<Camera>();
            camera.orthographic = false;
            camera.fieldOfView = 42f;
            camera.aspect = 16f / 9f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.allowHDR = true;
            camera.backgroundColor = new Color32(22, 28, 36, 255);
            camera.clearFlags = CameraClearFlags.SolidColor;
            UniversalAdditionalCameraData cameraData = cameraObject.GetComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        }

        private static void CreateGlobalPostProcessing()
        {
            const string profilePath = "Assets/PathDrawer/Generated/PathDrawerGlobalVolume.asset";
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "PathDrawer Global Post Processing";
                AssetDatabase.CreateAsset(profile, profilePath);
            }
            profile.components.Clear();
            Bloom bloom = profile.Add<Bloom>(true);
            bloom.threshold.value = 1.1f;
            bloom.intensity.value = 0.25f;
            bloom.scatter.value = 0.35f;
            Tonemapping tone = profile.Add<Tonemapping>(true);
            tone.mode.value = TonemappingMode.ACES;
            var volumeObject = new GameObject("Global Volume — Bloom + ACES", typeof(Volume));
            Volume volume = volumeObject.GetComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = profile;
            EditorUtility.SetDirty(profile);
        }

        private static LineRenderer CreateLine(string name, Material material, float width)
        {
            var lineObject = new GameObject(name);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 0;
            line.widthMultiplier = width;
            line.numCapVertices = 5;
            line.numCornerVertices = 5;
            line.textureMode = LineTextureMode.Tile;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private static MeshFilter CreateRibbon(string name, Material material)
        {
            var ribbonObject = new GameObject(name);
            MeshFilter filter = ribbonObject.AddComponent<MeshFilter>();
            ribbonObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            MeshRenderer renderer = ribbonObject.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return filter;
        }

        private static Transform[] CreateWaypointMarkers(Mesh ringMesh, Material material, int count)
        {
            var markers = new Transform[count];
            for (int i = 0; i < markers.Length; i++)
            {
                GameObject marker = CreateRingObject($"RDP Waypoint {i + 1:00}", ringMesh, material);
                marker.transform.localScale = Vector3.one * 1.05f;
                marker.SetActive(false);
                markers[i] = marker.transform;
            }
            return markers;
        }

        private static Transform[] CreateBallisticTicks(Material material, int count)
        {
            var markers = new Transform[count];
            for (int i = 0; i < markers.Length; i++)
            {
                GameObject tick = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tick.name = $"Ballistic Flight Tick {i + 1:00}";
                tick.transform.localScale = Vector3.one * 0.09f;
                tick.GetComponent<MeshRenderer>().sharedMaterial = material;
                UnityEngine.Object.DestroyImmediate(tick.GetComponent<Collider>());
                tick.SetActive(false);
                markers[i] = tick.transform;
            }
            return markers;
        }

        private static GameObject CreateRingObject(string name, Mesh ringMesh, Material material)
        {
            var ring = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            ring.GetComponent<MeshFilter>().sharedMesh = ringMesh;
            MeshRenderer renderer = ring.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ring;
        }

        private static Text CreateTelemetryHud(Camera camera)
        {
            var canvasObject = new GameObject("PathDrawer Telemetry HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 0.7f;
            canvas.sortingOrder = 10;
            canvas.enabled = false;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var panel = new GameObject("HUD Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvasObject.transform, false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(14f, -14f);
            panelRect.sizeDelta = new Vector2(380f, 168f);
            panel.GetComponent<Image>().color = new Color32(26, 34, 44, 244);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var titleObject = new GameObject("HUD Title", typeof(RectTransform), typeof(Text));
            titleObject.transform.SetParent(panel.transform, false);
            RectTransform titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(10f, -7f);
            titleRect.sizeDelta = new Vector2(-20f, 19f);
            Text title = titleObject.GetComponent<Text>();
            title.font = font;
            title.fontSize = 13;
            title.fontStyle = FontStyle.Bold;
            title.text = "PATHDRAWER  |  UNITY 6  •  BURST + JOBS";
            title.color = new Color32(172, 226, 232, 255);
            title.raycastTarget = false;
            title.horizontalOverflow = HorizontalWrapMode.Overflow;

            var detailObject = new GameObject("Live Telemetry", typeof(RectTransform), typeof(Text));
            detailObject.transform.SetParent(panel.transform, false);
            RectTransform detailRect = detailObject.GetComponent<RectTransform>();
            detailRect.anchorMin = new Vector2(0f, 0f);
            detailRect.anchorMax = new Vector2(1f, 1f);
            detailRect.offsetMin = new Vector2(10f, 50f);
            detailRect.offsetMax = new Vector2(-10f, -30f);
            Text detail = detailObject.GetComponent<Text>();
            detail.font = font;
            detail.fontSize = 12;
            detail.lineSpacing = 1f;
            detail.alignment = TextAnchor.UpperLeft;
            detail.supportRichText = true;
            detail.horizontalOverflow = HorizontalWrapMode.Overflow;
            detail.verticalOverflow = VerticalWrapMode.Truncate;
            detail.color = new Color32(218, 226, 233, 255);
            detail.raycastTarget = false;
            CreateHudButtonVisual(panel.transform, font, "Spline Mode", 10f, -112f);
            CreateHudButtonVisual(panel.transform, font, "Ballistic Mode", 196f, -112f);
            CreateHudButtonVisual(panel.transform, font, "Arc Preview: ON", 10f, -136f);
            CreateHudButtonVisual(panel.transform, font, "Auto-Showcase: ON", 196f, -136f);
            return detail;
        }

        private static void CreateHudButtonVisual(Transform parent, Font font, string label, float x, float y)
        {
            var button = new GameObject($"HUD Button Visual — {label}", typeof(RectTransform), typeof(Image));
            button.transform.SetParent(parent, false);
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(174f, 19f);
            button.GetComponent<Image>().color = new Color32(39, 53, 65, 255);

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(button.transform, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            Text text = textObject.GetComponent<Text>();
            text.font = font;
            text.fontSize = 11;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color32(218, 226, 233, 255);
            text.text = label;
            text.raycastTarget = false;
        }

        private static Mesh CreateRingMeshAsset()
        {
            const int ringSegments = 32;
            const int tubeSegments = 6;
            const float majorRadius = 0.15f;
            const float minorRadius = 0.018f;
            string assetPath = $"{GeneratedFolder}/HologramWaypointRing.asset";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null) return existing;

            var vertices = new Vector3[ringSegments * tubeSegments];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[ringSegments * tubeSegments * 6];
            for (int ring = 0; ring < ringSegments; ring++)
            for (int tube = 0; tube < tubeSegments; tube++)
            {
                float a = ring * (Mathf.PI * 2f / ringSegments);
                float b = tube * (Mathf.PI * 2f / tubeSegments);
                Vector3 normal = new Vector3(Mathf.Cos(a) * Mathf.Cos(b), Mathf.Sin(b), Mathf.Sin(a) * Mathf.Cos(b));
                int index = ring * tubeSegments + tube;
                normals[index] = normal;
                vertices[index] = new Vector3(Mathf.Cos(a) * majorRadius, 0f, Mathf.Sin(a) * majorRadius) + normal * minorRadius;
                uvs[index] = new Vector2(ring / (float)ringSegments, tube / (float)tubeSegments);
                int nextRing = (ring + 1) % ringSegments;
                int nextTube = (tube + 1) % tubeSegments;
                int tri = index * 6;
                triangles[tri] = index;
                triangles[tri + 1] = nextRing * tubeSegments + tube;
                triangles[tri + 2] = ring * tubeSegments + nextTube;
                triangles[tri + 3] = ring * tubeSegments + nextTube;
                triangles[tri + 4] = nextRing * tubeSegments + tube;
                triangles[tri + 5] = nextRing * tubeSegments + nextTube;
            }
            var mesh = new Mesh { name = "PathDrawer Holographic Waypoint Ring" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, assetPath);
            return mesh;
        }

        private static Material LoadOrCreateMaterial(string assetPath, string shaderName, Color color)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException($"Shader '{shaderName}' was not found. Check that the PathDrawer shaders compile.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(material, assetPath);
            }
            material.shader = shader;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_HDRColor")) material.SetColor("_HDRColor", color);
            if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", color.a);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material LoadOrCreateLitMaterial(string assetPath, Color color, float metallic, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Could not find URP/Lit or Standard for the monolith material.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(material, assetPath);
            }
            material.shader = shader;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetReference(SerializedObject target, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = target.FindProperty(propertyName);
            if (property == null) throw new MissingFieldException(typeof(PathDrawerDemoController).Name, propertyName);
            property.objectReferenceValue = value;
        }

        private static void SetReferences(SerializedObject target, string propertyName, UnityEngine.Object[] values)
        {
            SerializedProperty property = target.FindProperty(propertyName);
            if (property == null) throw new MissingFieldException(typeof(PathDrawerDemoController).Name, propertyName);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        private static void SetInteger(SerializedObject target, string propertyName, int value)
        {
            SerializedProperty property = target.FindProperty(propertyName);
            if (property == null) throw new MissingFieldException(typeof(PathDrawerDemoController).Name, propertyName);
            property.intValue = value;
        }

        private static void SetFloat(SerializedObject target, string propertyName, float value)
        {
            SerializedProperty property = target.FindProperty(propertyName);
            if (property == null) throw new MissingFieldException(typeof(PathDrawerDemoController).Name, propertyName);
            property.floatValue = value;
        }

        private static void SetBoolean(SerializedObject target, string propertyName, bool value)
        {
            SerializedProperty property = target.FindProperty(propertyName);
            if (property == null) throw new MissingFieldException(typeof(PathDrawerDemoController).Name, propertyName);
            property.boolValue = value;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, child);
        }

        private static void UpdateReadmeBenchmarkTable(string report)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string readmePath = Path.Combine(projectRoot, "README.md");
            report = report.Replace("\r\n", "\n");
            string readme = File.ReadAllText(readmePath).Replace("\r\n", "\n");
            int reportTableStart = report.IndexOf("| Input points |", StringComparison.Ordinal);
            if (reportTableStart < 0) throw new InvalidOperationException("The benchmark report did not contain the expected metrics table.");
            int reportTableEnd = report.IndexOf("\n\n", reportTableStart, StringComparison.Ordinal);
            if (reportTableEnd < 0) reportTableEnd = report.Length;
            string newTable = report.Substring(reportTableStart, reportTableEnd - reportTableStart).TrimEnd();
            int readmeTableStart = readme.IndexOf("| Input points |", StringComparison.Ordinal);
            if (readmeTableStart < 0) throw new InvalidOperationException("README.md does not contain the benchmark table anchor.");
            int readmeTableEnd = readme.IndexOf("\n\n", readmeTableStart, StringComparison.Ordinal);
            if (readmeTableEnd < 0) throw new InvalidOperationException("README.md benchmark table does not end before the next section.");
            File.WriteAllText(readmePath, readme.Substring(0, readmeTableStart) + newTable + readme.Substring(readmeTableEnd), new UTF8Encoding(false));
        }
    }
}
