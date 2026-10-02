using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

internal sealed class SeparateMeshBodiesWindow : EditorWindow
{
    private const string MenuPath = "Tools/Separate Mesh Bodies";
    private const string DefaultOutputFolder = "Assets/Generated/SeparatedMeshes";
    private const float MinimumPositionTolerance = 0.000001f;
    private const string WindowSummary = "Separate disconnected mesh bodies into individual GameObjects.";
    private const string SelectionGuidance = "Select one GameObject in the Hierarchy.";

    private static readonly string[] PivotOrientationLabels =
    {
        "World Aligned",
        "Source Rotation",
        "Auto Orient"
    };

    private string outputFolder = DefaultOutputFolder;
    private string parentName = string.Empty;
    private string partBaseName = string.Empty;
    private string lastDefaultParentName = string.Empty;
    private string lastDefaultPartBaseName = string.Empty;
    private bool createSceneParent = true;
    private float positionTolerance = 0.0001f;
    private bool addMeshColliders;
    private PivotOrientationMode pivotOrientation = PivotOrientationMode.SourceObjectRotation;
    private string statusMessage = string.Empty;
    private MessageType statusType = MessageType.Info;

    [MenuItem(MenuPath, false, 240)]
    private static void Open()
    {
        SeparateMeshBodiesWindow window = GetWindow<SeparateMeshBodiesWindow>("Separate Mesh Bodies");
        window.minSize = new Vector2(420f, 360f);
        window.Show();
    }

    private void OnEnable()
    {
        SyncDefaultNamesWithSelection();
    }

    private void OnSelectionChange()
    {
        SyncDefaultNamesWithSelection();
        Repaint();
    }

    private void OnGUI()
    {
        SyncDefaultNamesWithSelection();
        ValidationResult validation = ValidateCurrentState();

        EditorGUILayout.LabelField("Separate Mesh Bodies", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(WindowSummary, EditorStyles.miniLabel);
        EditorGUILayout.Space();

        DrawSelectionSection(validation);
        DrawOutputSection();
        DrawSeparationSection();
        DrawTransformSection();
        DrawComponentsSection();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(!validation.CanRun))
        {
            if (GUILayout.Button("Separate Mesh Bodies", GUILayout.Height(28f)))
                SeparateSelectedMesh();
        }

        DrawValidationStatus(validation);
    }

    private void DrawValidationStatus(ValidationResult validation)
    {
        if (IsOrdinarySelectionGuidance(validation))
        {
            EditorGUILayout.LabelField(SelectionGuidance, EditorStyles.miniLabel);
            return;
        }

        if (!validation.CanRun)
        {
            if (!validation.CanEnableReadWrite)
                DrawInlineMessage(validation.Message, validation.MessageType);
            return;
        }

        if (!string.IsNullOrEmpty(statusMessage))
            DrawInlineMessage(statusMessage, statusType);
    }

    private static bool IsOrdinarySelectionGuidance(ValidationResult validation)
    {
        return !validation.CanRun
            && validation.MessageType == MessageType.Info
            && validation.SelectedObject == null;
    }
    private void DrawSelectionSection(ValidationResult validation)
    {
        EditorGUILayout.LabelField("Selection", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ObjectField("Selected Object", validation.SelectedObject, typeof(GameObject), true);
            EditorGUILayout.ObjectField("Mesh", validation.Mesh, typeof(Mesh), false);
        }

        if (validation.CanEnableReadWrite)
        {
            DrawInlineMessage(validation.Message, validation.MessageType);
            if (GUILayout.Button("Enable Read/Write"))
                EnableReadWrite(validation);
        }
    }

    private static void DrawInlineMessage(string message, MessageType messageType)
    {
        if (string.IsNullOrEmpty(message))
            return;

        GUIStyle style = EditorStyles.wordWrappedMiniLabel;
        if (messageType == MessageType.Warning)
            style = EditorStyles.miniBoldLabel;
        else if (messageType == MessageType.Error)
            style = EditorStyles.boldLabel;

        EditorGUILayout.LabelField(message, style);
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            outputFolder = EditorGUILayout.TextField(
                new GUIContent("Output Folder", "Project asset folder where separated mesh assets will be saved. Must be under Assets/."),
                outputFolder);
            if (GUILayout.Button("Browse...", GUILayout.Width(84f)))
                BrowseForOutputFolder();
        }

        createSceneParent = EditorGUILayout.Toggle(
            new GUIContent("Create Scene Parent", "Create one parent GameObject for the separated mesh objects in the source scene."),
            createSceneParent);
        if (createSceneParent)
            parentName = EditorGUILayout.TextField(
                new GUIContent("Parent Name", "Name for the generated parent object and its output folder. Invalid filename characters are sanitized."),
                parentName);

        partBaseName = EditorGUILayout.TextField(
            new GUIContent("Part Base Name", "Base name for each separated part object and mesh asset. A numbered suffix is added."),
            partBaseName);
    }

    private void DrawSeparationSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Separation Settings", EditorStyles.boldLabel);
        positionTolerance = EditorGUILayout.FloatField(
            new GUIContent("Position Tolerance", "Maximum positional difference for vertices to be treated as connected. Higher values may merge nearby but separate mesh bodies."),
            Mathf.Max(MinimumPositionTolerance, positionTolerance));
    }

    private void DrawTransformSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform", EditorStyles.boldLabel);
        int selected = Mathf.Max(0, System.Array.IndexOf(PivotOrientationLabels, GetPivotOrientationLabel(pivotOrientation)));
        selected = EditorGUILayout.Popup(
            new GUIContent("Pivot Orientation", "Rotation assigned to each generated part: world, source object, or auto-estimated with source rotation fallback."),
            selected,
            PivotOrientationLabels);
        pivotOrientation = GetPivotOrientationMode(PivotOrientationLabels[selected]);
    }

    private void DrawComponentsSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Components", EditorStyles.boldLabel);
        addMeshColliders = EditorGUILayout.Toggle(
            new GUIContent("Add Mesh Colliders", "Add a MeshCollider to each generated part using its separated mesh."),
            addMeshColliders);
    }

    private void BrowseForOutputFolder()
    {
        string initialPath = Application.dataPath;
        if (TryNormalizeAssetsPath(outputFolder, out string normalized) && AssetDatabase.IsValidFolder(normalized))
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            initialPath = Path.Combine(projectRoot, normalized).Replace("\\", "/");
        }

        string selectedPath = EditorUtility.OpenFolderPanel("Choose Output Folder", initialPath, string.Empty);
        if (string.IsNullOrEmpty(selectedPath))
            return;

        selectedPath = selectedPath.Replace("\\", "/");
        string assetsPath = Application.dataPath.Replace("\\", "/");
        if (selectedPath == assetsPath)
        {
            outputFolder = "Assets";
            statusMessage = "Output folder set to Assets.";
            statusType = MessageType.Info;
            return;
        }

        if (selectedPath.StartsWith(assetsPath + "/", System.StringComparison.Ordinal))
        {
            outputFolder = "Assets" + selectedPath.Substring(assetsPath.Length);
            statusMessage = "Output folder set to " + outputFolder + ".";
            statusType = MessageType.Info;
            return;
        }

        statusMessage = "Output folder must be inside this project's Assets folder.";
        statusType = MessageType.Warning;
    }

    private void EnableReadWrite(ValidationResult validation)
    {
        if (!validation.CanEnableReadWrite || string.IsNullOrEmpty(validation.ImporterPath))
            return;

        ModelImporter importer = AssetImporter.GetAtPath(validation.ImporterPath) as ModelImporter;
        if (importer == null)
        {
            statusMessage = "Read/Write can only be enabled for supported model importers.";
            statusType = MessageType.Warning;
            return;
        }

        Object previousSelection = Selection.activeObject;
        importer.isReadable = true;
        importer.SaveAndReimport();
        AssetDatabase.Refresh();

        if (previousSelection != null)
            Selection.activeObject = previousSelection;

        statusMessage = "Enabled Read/Write for " + Path.GetFileName(validation.ImporterPath) + ".";
        statusType = MessageType.Info;
        Repaint();
    }

    private void SeparateSelectedMesh()
    {
        ValidationResult validation = ValidateCurrentState();
        if (!validation.CanRun)
        {
            statusMessage = validation.Message;
            statusType = validation.MessageType;
            return;
        }

        MeshFilter sourceFilter = validation.MeshFilter;
        Mesh sourceMesh = validation.Mesh;
        string normalizedOutputFolder = validation.OutputFolder;

        List<BodyInfo> bodies = FindBodies(sourceMesh, true, positionTolerance);
        if (bodies.Count <= 1)
        {
            ReportNoSeparateBodies(sourceFilter);
            return;
        }

        if (!EnsureFolder(normalizedOutputFolder))
        {
            statusMessage = "Could not create output folder: " + normalizedOutputFolder;
            statusType = MessageType.Warning;
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Separate Mesh Bodies");

        List<string> createdAssetPaths = new List<string>();
        List<GameObject> createdObjects = new List<GameObject>();
        GameObject parentObject = null;
        string runFolder = string.Empty;
        bool autoOrientFallbackUsed = false;

        try
        {
            Scene sourceScene = sourceFilter.gameObject.scene;
            Transform sceneParent = null;
            string effectiveParentName = MakeSafeObjectName(parentName);
            string safeBaseName = MakeSafeFileName(partBaseName);
            string runFolderName = createSceneParent ? MakeSafeFileName(effectiveParentName) : MakeSafeFileName(safeBaseName + "_Separated");
            runFolder = CreateUniqueChildFolder(normalizedOutputFolder, runFolderName);
            if (string.IsNullOrEmpty(runFolder))
            {
                statusMessage = "Could not create output folder: " + normalizedOutputFolder;
                statusType = MessageType.Warning;
                return;
            }

            if (createSceneParent)
            {
                parentObject = new GameObject(effectiveParentName);
                Undo.RegisterCreatedObjectUndo(parentObject, "Create separated mesh parent");
                MoveToSourceScene(parentObject, sourceScene);
                parentObject.transform.position = Vector3.zero;
                parentObject.transform.rotation = Quaternion.identity;
                parentObject.transform.localScale = Vector3.one;
                sceneParent = parentObject.transform;
            }

            MeshRenderer sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();
            Material[] sourceMaterials = sourceRenderer != null ? sourceRenderer.sharedMaterials : null;
            List<Object> newSelection = new List<Object>();

            for (int bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
            {
                BodyBuildResult builtBody = BuildBodyMesh(sourceFilter.transform, sourceMesh, bodies[bodyIndex], pivotOrientation);
                if (builtBody.Mesh == null)
                    continue;

                autoOrientFallbackUsed |= builtBody.UsedSourceRotationFallback;

                string numberedName = safeBaseName + "_" + (bodyIndex + 1).ToString("D3");
                string meshPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(runFolder, numberedName + ".asset").Replace("\\", "/"));
                builtBody.Mesh.name = Path.GetFileNameWithoutExtension(meshPath);
                AssetDatabase.CreateAsset(builtBody.Mesh, meshPath);
                createdAssetPaths.Add(meshPath);

                GameObject bodyObject = new GameObject(Path.GetFileNameWithoutExtension(meshPath));
                Undo.RegisterCreatedObjectUndo(bodyObject, "Create separated mesh object");
                MoveToSourceScene(bodyObject, sourceScene);
                createdObjects.Add(bodyObject);

                Transform bodyTransform = bodyObject.transform;
                bodyTransform.SetParent(sceneParent, false);
                bodyTransform.position = builtBody.Pivot;
                bodyTransform.rotation = builtBody.Rotation;
                bodyTransform.localScale = Vector3.one;

                MeshFilter newFilter = bodyObject.AddComponent<MeshFilter>();
                MeshRenderer newRenderer = bodyObject.AddComponent<MeshRenderer>();
                newFilter.sharedMesh = builtBody.Mesh;

                if (sourceMaterials != null)
                    newRenderer.sharedMaterials = BuildMaterialsForBody(sourceMaterials, builtBody.UsedSubMeshIndices);

                if (addMeshColliders)
                {
                    MeshCollider meshCollider = bodyObject.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = builtBody.Mesh;
                }

                newSelection.Add(bodyObject);
            }

            if (createdObjects.Count == 0)
            {
                CleanupGeneratedAssets(createdAssetPaths, runFolder);
                if (parentObject != null)
                    DestroyImmediate(parentObject);

                ReportNoSeparateBodies(sourceFilter);
                return;
            }

            if (createSceneParent && parentObject != null)
                CenterSceneParentAtGeneratedPartPositionBounds(parentObject.transform, createdObjects);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (createSceneParent && parentObject != null)
                Selection.activeObject = parentObject;
            else
                Selection.objects = newSelection.ToArray();

            GeneratedAssetUndoTracker.Track(createdAssetPaths, runFolder, createSceneParent && parentObject != null ? new Object[] { parentObject } : createdObjects.ToArray());
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(sourceScene);

            statusMessage = "Created " + createdObjects.Count + " mesh bodies in " + runFolder + ".";
            if (autoOrientFallbackUsed)
                statusMessage += " Auto Orient fell back to Source Rotation for at least one body.";
            statusType = MessageType.Info;
            Debug.Log(statusMessage, sourceFilter);
        }
        catch (System.Exception exception)
        {
            CleanupCreatedObjects(createdObjects);
            if (parentObject != null)
                DestroyImmediate(parentObject);
            CleanupGeneratedAssets(createdAssetPaths, runFolder);
            Undo.CollapseUndoOperations(undoGroup);

            statusMessage = "Could not separate mesh bodies: " + exception.Message;
            statusType = MessageType.Warning;
            Debug.LogWarning(statusMessage, sourceFilter);
        }
    }

    private ValidationResult ValidateCurrentState()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return ValidationResult.Blocked("Mesh separation is disabled in Play Mode.", MessageType.Info);

        GameObject[] selection = Selection.gameObjects;
        if (selection.Length == 0)
            return ValidationResult.Blocked("Select one GameObject with a MeshFilter.", MessageType.Info);

        if (selection.Length > 1)
            return ValidationResult.Blocked("Select exactly one GameObject.", MessageType.Warning, selection[0]);

        GameObject selected = selection[0];
        if (selected == null)
            return ValidationResult.Blocked("Select one GameObject with a MeshFilter.", MessageType.Info);

        MeshFilter meshFilter = selected.GetComponent<MeshFilter>();
        if (meshFilter == null)
            return ValidationResult.Blocked("Selected object does not have a MeshFilter.", MessageType.Warning, selected);

        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null)
            return ValidationResult.Blocked("Selected MeshFilter has no mesh.", MessageType.Warning, selected, meshFilter);

        if (!mesh.isReadable)
        {
            bool canEnableReadWrite = TryGetModelImporterPath(mesh, out string importerPath);
            return ValidationResult.Blocked(
                "Mesh is not readable. Enable Read/Write in the mesh import settings.",
                MessageType.Warning,
                selected,
                meshFilter,
                mesh,
                canEnableReadWrite,
                importerPath);
        }

        if (!TryNormalizeAssetsPath(outputFolder, out string normalizedOutputFolder))
            return ValidationResult.Blocked("Output folder must be a project path under Assets/.", MessageType.Warning, selected, meshFilter, mesh);

        return ValidationResult.Ready(selected, meshFilter, mesh, normalizedOutputFolder);
    }

    private void SyncDefaultNamesWithSelection()
    {
        GameObject selected = Selection.gameObjects.Length == 1 ? Selection.gameObjects[0] : null;
        string selectedName = selected != null ? selected.name : string.Empty;
        string nextPartDefault = string.IsNullOrEmpty(selectedName) ? "SeparatedMesh" : selectedName;
        string nextParentDefault = string.IsNullOrEmpty(selectedName) ? "SeparatedMeshes" : selectedName + "_Separated";

        if (string.IsNullOrWhiteSpace(partBaseName) || partBaseName == lastDefaultPartBaseName)
            partBaseName = nextPartDefault;

        if (string.IsNullOrWhiteSpace(parentName) || parentName == lastDefaultParentName)
            parentName = nextParentDefault;

        lastDefaultPartBaseName = nextPartDefault;
        lastDefaultParentName = nextParentDefault;
    }

    private static List<BodyInfo> FindBodies(Mesh mesh, bool connectVerticesByPosition, float vertexPositionTolerance)
    {
        int vertexCount = mesh.vertexCount;
        DisjointSet disjointSet = new DisjointSet(vertexCount);
        Dictionary<PositionKey, int> positionRepresentative = new Dictionary<PositionKey, int>();

        Vector3[] vertices = mesh.vertices;
        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
        {
            int[] indices = mesh.GetTriangles(subMesh);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                disjointSet.Union(a, b);
                disjointSet.Union(a, c);
            }
        }

        if (connectVerticesByPosition)
        {
            float tolerance = Mathf.Max(MinimumPositionTolerance, vertexPositionTolerance);
            for (int i = 0; i < vertices.Length; i++)
            {
                PositionKey key = new PositionKey(vertices[i], tolerance);
                if (positionRepresentative.TryGetValue(key, out int representative))
                    disjointSet.Union(i, representative);
                else
                    positionRepresentative.Add(key, i);
            }
        }

        Dictionary<int, BodyInfo> bodyByRoot = new Dictionary<int, BodyInfo>();
        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
        {
            int[] indices = mesh.GetTriangles(subMesh);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int root = disjointSet.Find(indices[i]);
                if (!bodyByRoot.TryGetValue(root, out BodyInfo body))
                {
                    body = new BodyInfo();
                    bodyByRoot.Add(root, body);
                }

                body.AddTriangle(subMesh, indices[i], indices[i + 1], indices[i + 2]);
            }
        }

        return new List<BodyInfo>(bodyByRoot.Values);
    }

    private static BodyBuildResult BuildBodyMesh(
        Transform sourceTransform,
        Mesh sourceMesh,
        BodyInfo body,
        PivotOrientationMode orientationMode)
    {
        Vector3[] sourceVertices = sourceMesh.vertices;
        Vector3[] sourceNormals = sourceMesh.normals;
        Vector4[] sourceTangents = sourceMesh.tangents;
        Vector2[] sourceUv = sourceMesh.uv;
        Vector2[] sourceUv2 = sourceMesh.uv2;
        Color[] sourceColors = sourceMesh.colors;

        List<Vector3> worldVertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector4> tangents = new List<Vector4>();
        List<Vector2> uv = new List<Vector2>();
        List<Vector2> uv2 = new List<Vector2>();
        List<Color> colors = new List<Color>();
        Dictionary<int, int> remap = new Dictionary<int, int>();
        Matrix4x4 normalMatrix = sourceTransform.localToWorldMatrix.inverse.transpose;

        Bounds worldBounds = new Bounds();
        bool hasBounds = false;

        foreach (int sourceIndex in body.UsedVertices)
        {
            Vector3 worldVertex = sourceTransform.TransformPoint(sourceVertices[sourceIndex]);
            if (!hasBounds)
            {
                worldBounds = new Bounds(worldVertex, Vector3.zero);
                hasBounds = true;
            }
            else
            {
                worldBounds.Encapsulate(worldVertex);
            }
        }

        if (!hasBounds)
            return new BodyBuildResult(null, Vector3.zero, Quaternion.identity, null, false);

        Vector3 pivot = worldBounds.center;
        Quaternion rotation = GetBodyRotation(sourceTransform, sourceVertices, body, orientationMode, out bool usedSourceRotationFallback);
        Quaternion inverseRotation = Quaternion.Inverse(rotation);

        foreach (int sourceIndex in body.UsedVertices)
        {
            int newIndex = worldVertices.Count;
            remap.Add(sourceIndex, newIndex);

            worldVertices.Add(inverseRotation * (sourceTransform.TransformPoint(sourceVertices[sourceIndex]) - pivot));

            if (sourceNormals != null && sourceNormals.Length == sourceVertices.Length)
                normals.Add((inverseRotation * normalMatrix.MultiplyVector(sourceNormals[sourceIndex])).normalized);

            if (sourceTangents != null && sourceTangents.Length == sourceVertices.Length)
            {
                Vector4 tangent = sourceTangents[sourceIndex];
                Vector3 localTangent = inverseRotation * sourceTransform.TransformDirection(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
                tangents.Add(new Vector4(localTangent.x, localTangent.y, localTangent.z, tangent.w));
            }

            if (sourceUv != null && sourceUv.Length == sourceVertices.Length)
                uv.Add(sourceUv[sourceIndex]);

            if (sourceUv2 != null && sourceUv2.Length == sourceVertices.Length)
                uv2.Add(sourceUv2[sourceIndex]);

            if (sourceColors != null && sourceColors.Length == sourceVertices.Length)
                colors.Add(sourceColors[sourceIndex]);
        }

        Mesh mesh = new Mesh();
        if (worldVertices.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.SetVertices(worldVertices);
        if (normals.Count == worldVertices.Count)
            mesh.SetNormals(normals);
        if (tangents.Count == worldVertices.Count)
            mesh.SetTangents(tangents);
        if (uv.Count == worldVertices.Count)
            mesh.SetUVs(0, uv);
        if (uv2.Count == worldVertices.Count)
            mesh.SetUVs(1, uv2);
        if (colors.Count == worldVertices.Count)
            mesh.SetColors(colors);

        List<int> usedSubMeshIndices = new List<int>();
        mesh.subMeshCount = body.TrianglesBySubMesh.Count;

        int newSubMesh = 0;
        foreach (KeyValuePair<int, List<int>> subMeshTriangles in body.TrianglesBySubMesh)
        {
            List<int> triangles = new List<int>(subMeshTriangles.Value.Count);
            foreach (int sourceIndex in subMeshTriangles.Value)
                triangles.Add(remap[sourceIndex]);

            mesh.SetTriangles(triangles, newSubMesh);
            usedSubMeshIndices.Add(subMeshTriangles.Key);
            newSubMesh++;
        }

        if (normals.Count != worldVertices.Count)
            mesh.RecalculateNormals();

        mesh.RecalculateBounds();
        if (uv.Count == worldVertices.Count)
            mesh.RecalculateTangents();

        return new BodyBuildResult(mesh, pivot, rotation, usedSubMeshIndices, usedSourceRotationFallback);
    }

    private static Quaternion GetBodyRotation(
        Transform sourceTransform,
        Vector3[] sourceVertices,
        BodyInfo body,
        PivotOrientationMode orientationMode,
        out bool usedSourceRotationFallback)
    {
        usedSourceRotationFallback = false;

        if (orientationMode == PivotOrientationMode.WorldIdentity)
            return Quaternion.identity;

        if (orientationMode == PivotOrientationMode.SourceObjectRotation)
            return sourceTransform.rotation;

        if (TryEstimateBodyRotation(sourceTransform, sourceVertices, body, out Quaternion estimated))
            return estimated;

        usedSourceRotationFallback = true;
        return sourceTransform.rotation;
    }

    private static bool TryEstimateBodyRotation(
        Transform sourceTransform,
        Vector3[] sourceVertices,
        BodyInfo body,
        out Quaternion rotation)
    {
        Vector3 weightedNormal = Vector3.zero;
        Vector3 weightedTallAxis = Vector3.zero;
        Vector3 sourceForward = sourceTransform.forward;
        Vector3 sourceUp = sourceTransform.up;

        foreach (KeyValuePair<int, List<int>> subMeshTriangles in body.TrianglesBySubMesh)
        {
            List<int> triangles = subMeshTriangles.Value;
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                Vector3 a = sourceTransform.TransformPoint(sourceVertices[triangles[i]]);
                Vector3 b = sourceTransform.TransformPoint(sourceVertices[triangles[i + 1]]);
                Vector3 c = sourceTransform.TransformPoint(sourceVertices[triangles[i + 2]]);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float area = cross.magnitude * 0.5f;
                if (area <= 0.000001f)
                    continue;

                Vector3 normal = cross / cross.magnitude;
                if (Vector3.Dot(normal, sourceForward) < 0f)
                    normal = -normal;

                weightedNormal += normal * area;

                Vector3 longestEdge = b - a;
                float longestSqr = longestEdge.sqrMagnitude;
                Vector3 edge = c - b;
                float edgeSqr = edge.sqrMagnitude;
                if (edgeSqr > longestSqr)
                {
                    longestEdge = edge;
                    longestSqr = edgeSqr;
                }

                edge = a - c;
                edgeSqr = edge.sqrMagnitude;
                if (edgeSqr > longestSqr)
                    longestEdge = edge;

                if (Vector3.Dot(longestEdge, sourceUp) < 0f)
                    longestEdge = -longestEdge;

                weightedTallAxis += longestEdge.normalized * area;
            }
        }

        Vector3 forward = weightedNormal.sqrMagnitude > 0.000001f ? weightedNormal.normalized : sourceForward;
        Vector3 up = weightedTallAxis - Vector3.Project(weightedTallAxis, forward);
        if (up.sqrMagnitude <= 0.000001f)
            up = sourceUp - Vector3.Project(sourceUp, forward);

        if (forward.sqrMagnitude <= 0.000001f || up.sqrMagnitude <= 0.000001f)
        {
            rotation = Quaternion.identity;
            return false;
        }

        rotation = Quaternion.LookRotation(forward.normalized, up.normalized);
        return true;
    }

    private static Material[] BuildMaterialsForBody(Material[] sourceMaterials, List<int> usedSubMeshIndices)
    {
        Material[] materials = new Material[usedSubMeshIndices.Count];
        for (int i = 0; i < usedSubMeshIndices.Count; i++)
        {
            int sourceIndex = usedSubMeshIndices[i];
            if (sourceMaterials != null && sourceIndex >= 0 && sourceIndex < sourceMaterials.Length)
            {
                materials[i] = sourceMaterials[sourceIndex];
            }
            else if (sourceMaterials != null && sourceMaterials.Length > 0)
            {
                materials[i] = sourceMaterials[sourceMaterials.Length - 1];
            }
        }

        return materials;
    }

    private void ReportNoSeparateBodies(MeshFilter sourceFilter)
    {
        statusMessage = "No separate mesh bodies found.";
        statusType = MessageType.Warning;
        statusMessage += " The Position Tolerance may be too high for this mesh. Try a smaller value.";
        Debug.LogWarning(statusMessage, sourceFilter);
    }

    private static bool TryGetModelImporterPath(Mesh mesh, out string importerPath)
    {
        importerPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(importerPath))
            return false;

        return AssetImporter.GetAtPath(importerPath) is ModelImporter;
    }

    private static void MoveToSourceScene(GameObject gameObject, Scene sourceScene)
    {
        if (sourceScene.IsValid() && gameObject.scene != sourceScene)
            SceneManager.MoveGameObjectToScene(gameObject, sourceScene);
    }

    private static void CenterSceneParentAtGeneratedPartPositionBounds(Transform parent, List<GameObject> generatedParts)
    {
        if (parent == null || generatedParts.Count == 0)
            return;

        var positions = new Vector3[generatedParts.Count];
        var rotations = new Quaternion[generatedParts.Count];
        bool found = false;
        Vector3 min = Vector3.zero;
        Vector3 max = Vector3.zero;

        for (int i = 0; i < generatedParts.Count; i++)
        {
            GameObject part = generatedParts[i];
            if (part == null)
                continue;

            Transform partTransform = part.transform;
            Vector3 position = partTransform.position;
            positions[i] = position;
            rotations[i] = partTransform.rotation;

            if (!found)
            {
                min = position;
                max = position;
                found = true;
                continue;
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        if (!found)
            return;

        parent.SetPositionAndRotation((min + max) * 0.5f, Quaternion.identity);
        parent.localScale = Vector3.one;

        for (int i = 0; i < generatedParts.Count; i++)
        {
            GameObject part = generatedParts[i];
            if (part == null)
                continue;

            part.transform.SetPositionAndRotation(positions[i], rotations[i]);
        }
    }
    private static bool TryNormalizeAssetsPath(string folder, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Replace("\\", "/").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (normalized != "Assets" && !normalized.StartsWith("Assets/", System.StringComparison.Ordinal))
            return false;

        string[] parts = normalized.Split('/');
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part == "." || part == "..")
                return false;

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                if (part.IndexOf(invalid) >= 0)
                    return false;
            }
        }

        return true;
    }

    private static bool EnsureFolder(string folder)
    {
        if (!TryNormalizeAssetsPath(folder, out string normalized))
            return false;

        if (AssetDatabase.IsValidFolder(normalized))
            return true;

        string[] parts = normalized.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }

        return AssetDatabase.IsValidFolder(normalized);
    }

    private static string CreateUniqueChildFolder(string parentFolder, string requestedName)
    {
        if (!EnsureFolder(parentFolder))
            return string.Empty;

        string safeName = MakeSafeFileName(requestedName);
        string candidateName = safeName;
        string candidatePath = parentFolder + "/" + candidateName;
        int suffix = 1;

        while (AssetDatabase.IsValidFolder(candidatePath))
        {
            candidateName = safeName + " " + suffix;
            candidatePath = parentFolder + "/" + candidateName;
            suffix++;
        }

        string guid = AssetDatabase.CreateFolder(parentFolder, candidateName);
        if (string.IsNullOrEmpty(guid))
            return string.Empty;

        string createdPath = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(createdPath) ? candidatePath : createdPath;
    }

    private static string MakeSafeFileName(string value)
    {
        string safe = value.Trim();
        if (string.IsNullOrEmpty(safe))
            safe = "SeparatedMesh";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        return safe;
    }

    private static string MakeSafeObjectName(string value)
    {
        string safe = value.Trim();
        return string.IsNullOrEmpty(safe) ? "SeparatedMeshes" : safe;
    }

    private static string GetPivotOrientationLabel(PivotOrientationMode mode)
    {
        switch (mode)
        {
            case PivotOrientationMode.WorldIdentity:
                return "World Aligned";
            case PivotOrientationMode.SmartBodyAxes:
                return "Auto Orient";
            default:
                return "Source Rotation";
        }
    }

    private static PivotOrientationMode GetPivotOrientationMode(string label)
    {
        switch (label)
        {
            case "World Aligned":
                return PivotOrientationMode.WorldIdentity;
            case "Auto Orient":
                return PivotOrientationMode.SmartBodyAxes;
            default:
                return PivotOrientationMode.SourceObjectRotation;
        }
    }

    private static void CleanupCreatedObjects(List<GameObject> createdObjects)
    {
        for (int i = createdObjects.Count - 1; i >= 0; i--)
        {
            if (createdObjects[i] != null)
                DestroyImmediate(createdObjects[i]);
        }
    }

    private static void CleanupGeneratedAssets(List<string> createdAssetPaths, string runFolder)
    {
        for (int i = createdAssetPaths.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(createdAssetPaths[i]))
                AssetDatabase.DeleteAsset(createdAssetPaths[i]);
        }

        DeleteFolderIfEmpty(runFolder);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void DeleteFolderIfEmpty(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            return;

        string absoluteFolder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, folder).Replace("\\", "/");
        if (!Directory.Exists(absoluteFolder))
            return;

        string[] entries = Directory.GetFileSystemEntries(absoluteFolder);
        if (entries.Length == 0 || OnlyContainsMetaFile(entries, absoluteFolder))
            AssetDatabase.DeleteAsset(folder);
    }

    private static bool OnlyContainsMetaFile(string[] entries, string absoluteFolder)
    {
        if (entries.Length != 1)
            return false;

        string expectedMeta = absoluteFolder.TrimEnd('/') + ".meta";
        return string.Equals(entries[0].Replace("\\", "/"), expectedMeta, System.StringComparison.OrdinalIgnoreCase);
    }

    private static class GeneratedAssetUndoTracker
    {
        private static readonly List<GeneratedAssetUndoOperation> Operations = new List<GeneratedAssetUndoOperation>();
        private static bool subscribed;

        public static void Track(List<string> assetPaths, string runFolder, Object[] sceneObjects)
        {
            if (assetPaths.Count == 0 || sceneObjects.Length == 0)
                return;

            Operations.Add(new GeneratedAssetUndoOperation(assetPaths.ToArray(), runFolder, sceneObjects));
            if (subscribed)
                return;

            Undo.undoRedoPerformed += CleanupAssetsForUndoneObjects;
            subscribed = true;
        }

        private static void CleanupAssetsForUndoneObjects()
        {
            for (int i = Operations.Count - 1; i >= 0; i--)
            {
                if (!Operations[i].AllSceneObjectsDestroyed())
                    continue;

                Operations[i].DeleteGeneratedAssets();
                Operations.RemoveAt(i);
            }

            if (Operations.Count != 0 || !subscribed)
                return;

            Undo.undoRedoPerformed -= CleanupAssetsForUndoneObjects;
            subscribed = false;
        }
    }

    private readonly struct GeneratedAssetUndoOperation
    {
        private readonly string[] assetPaths;
        private readonly string runFolder;
        private readonly Object[] sceneObjects;

        public GeneratedAssetUndoOperation(string[] assetPaths, string runFolder, Object[] sceneObjects)
        {
            this.assetPaths = assetPaths;
            this.runFolder = runFolder;
            this.sceneObjects = sceneObjects;
        }

        public bool AllSceneObjectsDestroyed()
        {
            for (int i = 0; i < sceneObjects.Length; i++)
            {
                if (sceneObjects[i] != null)
                    return false;
            }

            return true;
        }

        public void DeleteGeneratedAssets()
        {
            for (int i = 0; i < assetPaths.Length; i++)
            {
                if (!string.IsNullOrEmpty(assetPaths[i]) && AssetDatabase.LoadAssetAtPath<Object>(assetPaths[i]) != null)
                    AssetDatabase.DeleteAsset(assetPaths[i]);
            }

            DeleteFolderIfEmpty(runFolder);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
    private readonly struct ValidationResult
    {
        public readonly bool CanRun;
        public readonly string Message;
        public readonly MessageType MessageType;
        public readonly GameObject SelectedObject;
        public readonly MeshFilter MeshFilter;
        public readonly Mesh Mesh;
        public readonly string OutputFolder;
        public readonly bool CanEnableReadWrite;
        public readonly string ImporterPath;

        private ValidationResult(
            bool canRun,
            string message,
            MessageType messageType,
            GameObject selectedObject,
            MeshFilter meshFilter,
            Mesh mesh,
            string outputFolder,
            bool canEnableReadWrite,
            string importerPath)
        {
            CanRun = canRun;
            Message = message;
            MessageType = messageType;
            SelectedObject = selectedObject;
            MeshFilter = meshFilter;
            Mesh = mesh;
            OutputFolder = outputFolder;
            CanEnableReadWrite = canEnableReadWrite;
            ImporterPath = importerPath;
        }

        public static ValidationResult Ready(GameObject selectedObject, MeshFilter meshFilter, Mesh mesh, string outputFolder)
        {
            return new ValidationResult(true, "Ready to separate mesh bodies.", MessageType.Info, selectedObject, meshFilter, mesh, outputFolder, false, string.Empty);
        }

        public static ValidationResult Blocked(
            string message,
            MessageType messageType,
            GameObject selectedObject = null,
            MeshFilter meshFilter = null,
            Mesh mesh = null,
            bool canEnableReadWrite = false,
            string importerPath = "")
        {
            return new ValidationResult(false, message, messageType, selectedObject, meshFilter, mesh, string.Empty, canEnableReadWrite, importerPath);
        }
    }

    private sealed class BodyInfo
    {
        public readonly SortedDictionary<int, List<int>> TrianglesBySubMesh = new SortedDictionary<int, List<int>>();
        public readonly SortedSet<int> UsedVertices = new SortedSet<int>();

        public void AddTriangle(int subMesh, int a, int b, int c)
        {
            if (!TrianglesBySubMesh.TryGetValue(subMesh, out List<int> triangles))
            {
                triangles = new List<int>();
                TrianglesBySubMesh.Add(subMesh, triangles);
            }

            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
            UsedVertices.Add(a);
            UsedVertices.Add(b);
            UsedVertices.Add(c);
        }
    }

    private readonly struct BodyBuildResult
    {
        public readonly Mesh Mesh;
        public readonly Vector3 Pivot;
        public readonly Quaternion Rotation;
        public readonly List<int> UsedSubMeshIndices;
        public readonly bool UsedSourceRotationFallback;

        public BodyBuildResult(
            Mesh mesh,
            Vector3 pivot,
            Quaternion rotation,
            List<int> usedSubMeshIndices,
            bool usedSourceRotationFallback)
        {
            Mesh = mesh;
            Pivot = pivot;
            Rotation = rotation;
            UsedSubMeshIndices = usedSubMeshIndices;
            UsedSourceRotationFallback = usedSourceRotationFallback;
        }
    }

    private enum PivotOrientationMode
    {
        WorldIdentity,
        SourceObjectRotation,
        SmartBodyAxes
    }

    private readonly struct PositionKey
    {
        private readonly int x;
        private readonly int y;
        private readonly int z;

        public PositionKey(Vector3 position, float tolerance)
        {
            x = Mathf.RoundToInt(position.x / tolerance);
            y = Mathf.RoundToInt(position.y / tolerance);
            z = Mathf.RoundToInt(position.z / tolerance);
        }

        public override bool Equals(object obj)
        {
            return obj is PositionKey other && x == other.x && y == other.y && z == other.z;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x;
                hash = hash * 31 + y;
                hash = hash * 31 + z;
                return hash;
            }
        }
    }

    private sealed class DisjointSet
    {
        private readonly int[] parent;
        private readonly int[] rank;

        public DisjointSet(int count)
        {
            parent = new int[count];
            rank = new int[count];
            for (int i = 0; i < count; i++)
                parent[i] = i;
        }

        public int Find(int value)
        {
            if (parent[value] != value)
                parent[value] = Find(parent[value]);

            return parent[value];
        }

        public void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);

            if (rootA == rootB)
                return;

            if (rank[rootA] < rank[rootB])
            {
                parent[rootA] = rootB;
            }
            else if (rank[rootA] > rank[rootB])
            {
                parent[rootB] = rootA;
            }
            else
            {
                parent[rootB] = rootA;
                rank[rootA]++;
            }
        }
    }
}



