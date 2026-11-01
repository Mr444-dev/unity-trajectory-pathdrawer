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
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PathDrawer.Editor
{
    /// <summary>Builds the playable portfolio scene and publishes editor-measured benchmark results.</summary>
    public static class PathDrawerPortfolioTools
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string GeneratedFolder = "Assets/PathDrawer/Generated";
        private const string ResultsFileName = "PathDrawer-Benchmark-Results.md";
        private const string ObstacleLayerName = "PathDrawerObstacles";

        [MenuItem("Tools/PathDrawer/1. Build & Save Ready-to-Play SampleScene")]
        public static void BuildAndSaveSampleScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            int obstacleLayer = EnsureObstacleLayer();
            EnsureFolder("Assets/PathDrawer", "Generated");
            EnsureFolder("Assets/PathDrawer", "Materials");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Mesh terrainMesh = CreateTerrainMeshAsset();
            Material terrainMaterial = LoadOrCreateMaterial("Assets/PathDrawer/Materials/Terrain.mat", new Color(0.28f, 0.38f, 0.31f));
            Material reachableMaterial = LoadOrCreateMaterial("Assets/PathDrawer/Materials/ReachablePath.mat", new Color(0.12f, 0.88f, 0.68f));
            Material blockedMaterial = LoadOrCreateMaterial("Assets/PathDrawer/Materials/BlockedPath.mat", new Color(1f, 0.24f, 0.18f));
            Material ribbonMaterial = LoadOrCreateMaterial("Assets/PathDrawer/Materials/TrajectoryRibbon.mat", new Color(0.16f, 0.72f, 1f, 0.92f));

            CreateTerrain(terrainMesh, terrainMaterial);
            CreateObstaclePillar("Obstacle Pillar A", new Vector3(0f, 1.05f, 0.5f), new Vector3(0.85f, 2.1f, 0.85f), obstacleLayer, blockedMaterial);
            CreateObstaclePillar("Obstacle Pillar B", new Vector3(4.2f, 0.8f, -3.5f), new Vector3(0.7f, 1.6f, 0.7f), obstacleLayer, blockedMaterial);
            CreateLightingAndCamera(out Camera camera);
            LineRenderer reachableLine = CreateLine("Reachable Path", reachableMaterial, 0.13f);
            LineRenderer blockedLine = CreateLine("Blocked Path", blockedMaterial, 0.13f);
            MeshFilter ribbonFilter = CreateRibbon(ribbonMaterial);

            var controllerObject = new GameObject("PathDrawer Demo Controller");
            var controller = controllerObject.AddComponent<PathDrawerDemoController>();
            var serializedController = new SerializedObject(controller);
            serializedController.FindProperty("drawCamera").objectReferenceValue = camera;
            serializedController.FindProperty("groundMask").intValue = 1 << 0;
            serializedController.FindProperty("obstacleMask").intValue = 1 << obstacleLayer;
            serializedController.FindProperty("reachableLine").objectReferenceValue = reachableLine;
            serializedController.FindProperty("blockedLine").objectReferenceValue = blockedLine;
            serializedController.FindProperty("ribbonMeshFilter").objectReferenceValue = ribbonFilter;
            serializedController.FindProperty("maxInputPoints").intValue = 1024;
            serializedController.FindProperty("maxOutputPoints").intValue = 4096;
            serializedController.FindProperty("lookupSamplesPerSegment").intValue = 16;
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            CreateModeUi(controller);
            IncludeDemoSceneInBuildSettings();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"PathDrawer demo scene created at {ScenePath}. Ground mask: Default; obstacle mask: {ObstacleLayerName} (layer {obstacleLayer}).");
        }

        [MenuItem("Tools/PathDrawer/2. Run Benchmark Sweep & Export Markdown")]
        public static void RunBenchmarkSweepAndExportMarkdown()
        {
            string report = PathDrawerBenchmarkRunner.RunAndExport();
            UpdateReadmeBenchmarkTable(report);
            AssetDatabase.Refresh();
            Debug.Log("PathDrawer-Benchmark-Results.md was written and the README benchmark table was updated with this editor's measurements.");
        }

        private static int EnsureObstacleLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            int freeIndex = -1;
            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty layer = layers.GetArrayElementAtIndex(i);
                if (layer.stringValue == ObstacleLayerName) return i;
                if (freeIndex < 0 && string.IsNullOrWhiteSpace(layer.stringValue)) freeIndex = i;
            }

            if (freeIndex < 0)
                throw new InvalidOperationException($"No empty Unity layer is available for {ObstacleLayerName}.");
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

        private static Mesh CreateTerrainMeshAsset()
        {
            const int resolution = 64;
            const float halfExtent = 14f;
            var vertices = new Vector3[(resolution + 1) * (resolution + 1)];
            var triangles = new int[resolution * resolution * 6];
            for (int z = 0; z <= resolution; z++)
            {
                float worldZ = -halfExtent + 2f * halfExtent * z / resolution;
                for (int x = 0; x <= resolution; x++)
                {
                    float worldX = -halfExtent + 2f * halfExtent * x / resolution;
                    float ramp = Mathf.Clamp01((worldX + 11f) / 7f) * 0.55f;
                    float bumpA = 0.8f * Mathf.Exp(-((worldX + 3.5f) * (worldX + 3.5f) + (worldZ - 2f) * (worldZ - 2f)) / 8f);
                    float bumpB = 0.55f * Mathf.Exp(-((worldX - 5f) * (worldX - 5f) + (worldZ + 4f) * (worldZ + 4f)) / 5f);
                    float ripples = 0.12f * Mathf.Sin(worldX * 0.55f) * Mathf.Cos(worldZ * 0.42f);
                    float height = ramp + bumpA + bumpB + ripples;
                    vertices[z * (resolution + 1) + x] = new Vector3(worldX, height, worldZ);
                }
            }

            int triangleCursor = 0;
            for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                int lowerLeft = z * (resolution + 1) + x;
                int lowerRight = lowerLeft + 1;
                int upperLeft = lowerLeft + resolution + 1;
                int upperRight = upperLeft + 1;
                triangles[triangleCursor++] = lowerLeft;
                triangles[triangleCursor++] = upperLeft;
                triangles[triangleCursor++] = lowerRight;
                triangles[triangleCursor++] = lowerRight;
                triangles[triangleCursor++] = upperLeft;
                triangles[triangleCursor++] = upperRight;
            }

            var mesh = new Mesh { name = "PathDrawer Uneven Ramps and Bumps" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset($"{GeneratedFolder}/UnevenTerrain.asset");
            AssetDatabase.CreateAsset(mesh, $"{GeneratedFolder}/UnevenTerrain.asset");
            return mesh;
        }

        private static void CreateTerrain(Mesh mesh, Material material)
        {
            var terrain = new GameObject("Uneven Terrain (Ramp + Bumps)") { layer = 0 };
            terrain.AddComponent<MeshFilter>().sharedMesh = mesh;
            terrain.AddComponent<MeshRenderer>().sharedMaterial = material;
            terrain.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void CreateObstaclePillar(string name, Vector3 position, Vector3 scale, int layer, Material material)
        {
            var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pillar.name = name;
            pillar.layer = layer;
            pillar.transform.position = position;
            pillar.transform.localScale = scale;
            var renderer = pillar.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        private static void CreateLightingAndCamera(out Camera camera)
        {
            var lightObject = new GameObject("Key Light", typeof(Light));
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            Light light = lightObject.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 27f, 0f);
            cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera = cameraObject.GetComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 13f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 80f;
            camera.backgroundColor = new Color(0.075f, 0.1f, 0.14f);
            camera.clearFlags = CameraClearFlags.SolidColor;
        }

        private static LineRenderer CreateLine(string name, Material material, float width)
        {
            var lineObject = new GameObject(name);
            var line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 0;
            line.widthMultiplier = width;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            line.textureMode = LineTextureMode.Tile;
            line.sharedMaterial = material;
            return line;
        }

        private static MeshFilter CreateRibbon(Material material)
        {
            var ribbonObject = new GameObject("Procedural Trajectory Ribbon");
            MeshFilter filter = ribbonObject.AddComponent<MeshFilter>();
            ribbonObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            return filter;
        }

        private static void CreateModeUi(PathDrawerDemoController controller)
        {
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            InputSystemUIInputModule inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();
            inputModule.AssignDefaultActions();

            var canvasObject = new GameObject("PathDrawer Mode UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            CreateModeButton(canvasObject.transform, font, "Spline Terrain (S)", new Vector2(24f, -24f), controller.UseSplineTerrainMode);
            CreateModeButton(canvasObject.transform, font, "Ballistic Arc (B)", new Vector2(24f, -78f), controller.UseBallisticArcMode);
            CreateModeLabel(canvasObject.transform, font, "Drag on the ground to draw a trajectory", new Vector2(24f, -132f));
        }

        private static void CreateModeButton(Transform parent, Font font, string caption, Vector2 topLeftPosition, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = topLeftPosition;
            rect.sizeDelta = new Vector2(208f, 44f);
            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.08f, 0.16f, 0.22f, 0.95f);
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            UnityEventTools.AddPersistentListener(button.onClick, action);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 2f);
            labelRect.offsetMax = new Vector2(-8f, -2f);
            Text label = labelObject.GetComponent<Text>();
            label.font = font;
            label.text = caption;
            label.fontSize = 17;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
        }

        private static void CreateModeLabel(Transform parent, Font font, string caption, Vector2 topLeftPosition)
        {
            var labelObject = new GameObject("Draw Instructions", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(parent, false);
            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = topLeftPosition;
            rect.sizeDelta = new Vector2(420f, 32f);
            Text label = labelObject.GetComponent<Text>();
            label.font = font;
            label.text = caption;
            label.fontSize = 16;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = Color.white;
            label.raycastTarget = false;
        }

        private static Material LoadOrCreateMaterial(string assetPath, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("Could not find a URP Unlit or Unlit/Color shader for the PathDrawer demo materials.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(material, assetPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            EditorUtility.SetDirty(material);
            return material;
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
            string updated = readme.Substring(0, readmeTableStart) + newTable + readme.Substring(readmeTableEnd);
            File.WriteAllText(readmePath, updated, new UTF8Encoding(false));
        }
    }
}
