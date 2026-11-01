using System;
using System.IO;
using System.Text;
using PathDrawer.Runtime;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
        private const float TerrainTop = 0f;
        private const float PlateauTop = 1.4f;

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
                $"{MaterialFolder}/TacticalArenaGrid.mat", "PathDrawer/TacticalArenaGrid", new Color(0.035f, 0.055f, 0.09f, 1f));
            Material reachableMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/ReachablePath.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(0.02f, 2.3f, 2.45f, 0.92f));
            Material blockedMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/BlockedPath.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(2.8f, 0.055f, 0.08f, 0.95f));
            Material goldMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/BallisticArc.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(2.4f, 1.05f, 0.025f, 0.96f));
            Material hazardMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/HazardEmission.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(2.8f, 0.09f, 0.16f, 0.98f));
            Material waypointMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/WaypointEmission.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(0.02f, 2.2f, 2.3f, 0.95f));
            Material monolithMaterial = LoadOrCreateLitMaterial(
                $"{MaterialFolder}/MonolithGunmetal.mat", new Color(0.055f, 0.075f, 0.12f), 0.78f, 0.82f);
            Material bandMaterial = LoadOrCreateMaterial(
                $"{MaterialFolder}/MonolithHazardBand.mat", "PathDrawer/TrajectoryRibbonHologram", new Color(2.1f, 0.62f, 0.04f, 1f));
            Mesh ringMesh = CreateRingMeshAsset();

            CreateTerrain(terrainMaterial);
            CreateObstacleMonolith("Monolith A — Plateau", new Vector3(0.75f, PlateauTop, 0.15f), obstacleLayer, monolithMaterial, bandMaterial);
            CreateObstacleMonolith("Monolith B — Impact Target", new Vector3(4.2f, TerrainTop, 3.2f), obstacleLayer, monolithMaterial, hazardMaterial);
            CreateObstacleMonolith("Monolith C — Perimeter", new Vector3(-6.4f, TerrainTop, 2.5f), obstacleLayer, monolithMaterial, bandMaterial);
            CreateLightingAndCamera(out Camera camera);
            CreateGlobalPostProcessing();

            LineRenderer reachableLine = CreateLine("Reachable Trajectory", reachableMaterial, 0.11f);
            LineRenderer blockedLine = CreateLine("Blocked Trajectory", blockedMaterial, 0.13f);
            LineRenderer ballisticLine = CreateLine("Ballistic Arc Preview", goldMaterial, 0.075f);
            LineRenderer impactBeacon = CreateLine("Obstacle Impact Laser", hazardMaterial, 0.06f);
            MeshFilter reachableRibbon = CreateRibbon("Reachable Hologram Ribbon", reachableMaterial);
            MeshFilter blockedRibbon = CreateRibbon("Blocked Hologram Ribbon", blockedMaterial);
            Transform[] waypointMarkers = CreateWaypointMarkers(ringMesh, waypointMaterial, 24);
            Transform impactRing = CreateRingObject("Obstacle Impact Ring", ringMesh, hazardMaterial).transform;
            impactRing.gameObject.SetActive(false);

            var controllerObject = new GameObject("PathDrawer Demo Controller");
            PathDrawerDemoController controller = controllerObject.AddComponent<PathDrawerDemoController>();
            var serializedController = new SerializedObject(controller);
            SetReference(serializedController, "drawCamera", camera);
            SetInteger(serializedController, "groundMask", 1 << 0);
            SetInteger(serializedController, "obstacleMask", 1 << obstacleLayer);
            SetReference(serializedController, "reachableLine", reachableLine);
            SetReference(serializedController, "blockedLine", blockedLine);
            SetReference(serializedController, "ballisticArcLine", ballisticLine);
            SetReference(serializedController, "impactBeacon", impactBeacon);
            SetReference(serializedController, "ribbonMeshFilter", reachableRibbon);
            SetReference(serializedController, "blockedRibbonMeshFilter", blockedRibbon);
            SetReferences(serializedController, "waypointMarkers", waypointMarkers);
            SetReference(serializedController, "impactRing", impactRing);
            SetInteger(serializedController, "maxInputPoints", 1024);
            SetInteger(serializedController, "maxOutputPoints", 4096);
            SetInteger(serializedController, "lookupSamplesPerSegment", 16);
            SetFloat(serializedController, "outputSpacing", 0.2f);
            SetFloat(serializedController, "ribbonWidth", 0.45f);
            SetInteger(serializedController, "showcaseRawPointCount", 120);
            SetBoolean(serializedController, "autoShowcase", true);
            SetBoolean(serializedController, "showBallisticPreview", true);
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            Text telemetry = CreateTelemetryHud(camera);
            SetReference(serializedController, "telemetryText", telemetry);
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
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Tactical Arena — 24m Ground Plane";
            floor.transform.position = new Vector3(0f, -0.2f, 0f);
            floor.transform.localScale = new Vector3(24f, 0.4f, 24f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = material;

            GameObject plateau = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plateau.name = "Elevated Tactical Plateau — 1.4m";
            plateau.transform.position = new Vector3(-1.2f, PlateauTop - 0.16f, -0.1f);
            plateau.transform.localScale = new Vector3(7f, 0.32f, 5.8f);
            plateau.GetComponent<MeshRenderer>().sharedMaterial = material;

            CreateRamp("Entry Ramp — climb to plateau", new Vector3(-5.8f, 0.7f, -4.7f), new Vector3(3.4f, 1.4f, 4.2f), 3.8f, 5.55f, material);
            CreateRamp("Exit Ramp — descend to impact lane", new Vector3(3.1f, 0.7f, 2.5f), new Vector3(2.2f, -1.4f, 1.4f), 3.5f, 2.9f, material);
        }

        private static void CreateRamp(string name, Vector3 center, Vector3 direction, float width, float length, Material material)
        {
            GameObject ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp.name = name;
            ramp.transform.position = center;
            ramp.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            ramp.transform.localScale = new Vector3(width, 0.32f, length);
            ramp.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void CreateObstacleMonolith(string name, Vector3 basePosition, int layer, Material bodyMaterial, Material bandMaterial)
        {
            const float height = 2.6f;
            const float width = 0.92f;
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = name;
            body.layer = layer;
            body.transform.position = basePosition + Vector3.up * (height * 0.5f);
            body.transform.localScale = new Vector3(width, height, width);
            body.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;

            CreateHazardBand(name + " / lower hazard rim", basePosition + Vector3.up * 0.43f, width + 0.075f, bandMaterial);
            CreateHazardBand(name + " / upper hazard rim", basePosition + Vector3.up * 2.2f, width + 0.075f, bandMaterial);
        }

        private static void CreateHazardBand(string name, Vector3 center, float width, Material material)
        {
            GameObject band = GameObject.CreatePrimitive(PrimitiveType.Cube);
            band.name = name;
            band.transform.position = center;
            band.transform.localScale = new Vector3(width, 0.095f, width);
            band.GetComponent<MeshRenderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(band.GetComponent<Collider>());
        }

        private static void CreateLightingAndCamera(out Camera camera)
        {
            var lightObject = new GameObject("Key Light", typeof(Light));
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            Light key = lightObject.GetComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.25f;
            key.color = new Color(0.73f, 0.84f, 1f);

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 11.5f, -14.5f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.75f, 0f));
            camera = cameraObject.GetComponent<Camera>();
            camera.orthographic = false;
            camera.fieldOfView = 40f;
            camera.aspect = 16f / 9f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.allowHDR = true;
            camera.backgroundColor = new Color(0.027f, 0.039f, 0.071f, 1f);
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
            bloom.threshold.value = 0.9f;
            bloom.intensity.value = 0.95f;
            bloom.scatter.value = 0.65f;
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
                marker.transform.localScale = Vector3.one * 0.86f;
                marker.SetActive(false);
                markers[i] = marker.transform;
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
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            InputSystemUIInputModule inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();
            inputModule.AssignDefaultActions();

            var canvasObject = new GameObject("PathDrawer Telemetry HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 0.7f;
            canvas.sortingOrder = 10;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var border = new GameObject("Telemetry Border", typeof(RectTransform), typeof(Image));
            border.transform.SetParent(canvasObject.transform, false);
            RectTransform borderRect = border.GetComponent<RectTransform>();
            borderRect.anchorMin = new Vector2(0f, 1f);
            borderRect.anchorMax = new Vector2(0f, 1f);
            borderRect.pivot = new Vector2(0f, 1f);
            borderRect.anchoredPosition = new Vector2(14f, -14f);
            borderRect.sizeDelta = new Vector2(330f, 126f);
            border.GetComponent<Image>().color = new Color(0.169f, 0.357f, 0.518f, 0.96f);

            var panel = new GameObject("Telemetry Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(border.transform, false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.one;
            panelRect.offsetMax = Vector2.one * -1f;
            panel.GetComponent<Image>().color = new Color(0.031f, 0.047f, 0.078f, 0.91f);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var titleObject = new GameObject("HUD Title", typeof(RectTransform), typeof(Text));
            titleObject.transform.SetParent(panel.transform, false);
            RectTransform titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(10f, -7f);
            titleRect.sizeDelta = new Vector2(-20f, 20f);
            Text title = titleObject.GetComponent<Text>();
            title.font = font;
            title.fontSize = 13;
            title.fontStyle = FontStyle.Bold;
            title.text = "PATHDRAWER  |  UNITY 6  •  BURST + JOBS";
            title.color = new Color(0.56f, 0.88f, 1f, 1f);
            title.raycastTarget = false;
            title.horizontalOverflow = HorizontalWrapMode.Overflow;

            var detailObject = new GameObject("Live Telemetry", typeof(RectTransform), typeof(Text));
            detailObject.transform.SetParent(panel.transform, false);
            RectTransform detailRect = detailObject.GetComponent<RectTransform>();
            detailRect.anchorMin = new Vector2(0f, 0f);
            detailRect.anchorMax = new Vector2(1f, 1f);
            detailRect.offsetMin = new Vector2(10f, 7f);
            detailRect.offsetMax = new Vector2(-10f, -28f);
            Text detail = detailObject.GetComponent<Text>();
            detail.font = font;
            detail.fontSize = 12;
            detail.lineSpacing = 1.05f;
            detail.alignment = TextAnchor.UpperLeft;
            detail.horizontalOverflow = HorizontalWrapMode.Overflow;
            detail.verticalOverflow = VerticalWrapMode.Truncate;
            detail.color = new Color(0.78f, 0.87f, 0.96f, 1f);
            detail.raycastTarget = false;
            return detail;
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
