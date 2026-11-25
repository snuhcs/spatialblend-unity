// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;


namespace SpatialBlend
{
    [CustomEditor(typeof(SbEntity), true)]
    public class SbEntityEditor : UnityEditor.Editor
    {
        private bool showEntityInfo = true;
        private bool showStateVectors = true;
        private bool showInteractions = true;
        private bool showProximityData = true;
        private bool showTimelineData = true;
        private bool showRawData = false;
        private bool autoRefresh = true;
        private float lastRefreshTime = 0f;
        private const float REFRESH_INTERVAL = 0.5f;

        // Custom AffordanceVolume editing
        private bool showAffordanceVolumeEditor = true;
        private bool isEditingAffordancePoints = false;
        private int selectedAffordancePointIndex = -1;
        private const float affordancePointHandleSize = 0.15f;
        private SerializedObject affordanceSerializedObject;
        private SerializedProperty affordanceTypeProperty;
        private AffordanceVolume cachedAffordanceVolume;

        private void OnEnable()
        {
            RefreshAffordanceVolumeProperties();
            SceneView.duringSceneGui += OnSceneGUIAlways;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUIAlways;
        }

        private void OnSceneGUIAlways(SceneView sceneView)
        {
            DrawAffordanceVolumeHandles();
        }

        private void RefreshAffordanceVolumeProperties()
        {
            var entity = (SbEntity)target;
            if (entity == null) return;

            cachedAffordanceVolume = entity.GetComponent<AffordanceVolume>();
            if (cachedAffordanceVolume != null)
            {
                affordanceSerializedObject = new SerializedObject(cachedAffordanceVolume);
                affordanceTypeProperty = affordanceSerializedObject.FindProperty("type");
            }
            else
            {
                affordanceSerializedObject = null;
                affordanceTypeProperty = null;
            }
        }

        public override void OnInspectorGUI()
        {
            // Draw the default inspector first
            DrawDefaultInspector();

            var entity = (SbEntity)target;
            if (entity == null) return;

            // Draw Custom AffordanceVolume Editor (only in edit mode)
            if (!Application.isPlaying)
            {
                DrawCustomAffordanceVolumeEditor(entity);
            }

            // Only show entity info in play mode
            if (!Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Entity information is only available in Play Mode.", MessageType.Info);
                return;
            }

            // Check if entity is attached to a space manager
            if (!entity.IsAttached())
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Entity is not attached to a Space Manager.", MessageType.Warning);
                return;
            }

            // Auto-refresh logic
            if (autoRefresh && Event.current.type == EventType.Repaint)
            {
                if (Time.realtimeSinceStartup - lastRefreshTime > REFRESH_INTERVAL)
                {
                    lastRefreshTime = Time.realtimeSinceStartup;
                    Repaint();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("SpatialBlend Entity Information", EditorStyles.boldLabel);

            // Display options
            EditorGUILayout.BeginHorizontal();
            showEntityInfo = EditorGUILayout.Toggle("Basic Info", showEntityInfo);
            showStateVectors = EditorGUILayout.Toggle("State Vectors", showStateVectors);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            showInteractions = EditorGUILayout.Toggle("Interactions", showInteractions);
            showProximityData = EditorGUILayout.Toggle("Proximity", showProximityData);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            showTimelineData = EditorGUILayout.Toggle("Timeline", showTimelineData);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            showRawData = EditorGUILayout.Toggle("Raw Data", showRawData);
            autoRefresh = EditorGUILayout.Toggle("Auto Refresh", autoRefresh);
            EditorGUILayout.EndHorizontal();

            DrawEntityInformation(entity);
        }

        private void DrawCustomAffordanceVolumeEditor(SbEntity entity)
        {
            var affordanceVolume = entity.GetComponent<AffordanceVolume>();
            if (affordanceVolume == null)
            {
                return;
            }

            // Refresh if needed
            if (cachedAffordanceVolume != affordanceVolume)
            {
                RefreshAffordanceVolumeProperties();
            }

            if (affordanceSerializedObject == null || affordanceTypeProperty == null) return;

            affordanceSerializedObject.Update();

            EditorGUILayout.Space(10);
            showAffordanceVolumeEditor = EditorGUILayout.Foldout(showAffordanceVolumeEditor, "Custom Affordance Volume Editor", true, EditorStyles.foldoutHeader);

            if (!showAffordanceVolumeEditor) return;

            if (affordanceVolume.type != AffordanceVolume.AffordanceVolumeType.PRESET)
            {
                EditorGUILayout.HelpBox("Set AffordanceVolume type to PRESET to edit custom points.", MessageType.Info);
                return;
            }

            EditorGUI.indentLevel++;

            // Center Offset
            EditorGUI.BeginChangeCheck();
            Vector2 newCenterOffset = EditorGUILayout.Vector2Field("Center Offset", affordanceVolume.CustomCenterOffset);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(affordanceVolume, "Change Center Offset");
                affordanceVolume.CustomCenterOffset = newCenterOffset;
                EditorUtility.SetDirty(affordanceVolume);
            }

            EditorGUILayout.Space(5);

            Vector2[] points = affordanceVolume.CustomPoints ?? new Vector2[0];

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Points: {points.Length}", GUILayout.Width(80));

            GUI.backgroundColor = isEditingAffordancePoints ? Color.green : Color.white;
            if (GUILayout.Button(isEditingAffordancePoints ? "Stop Editing" : "Edit Points in Scene"))
            {
                isEditingAffordancePoints = !isEditingAffordancePoints;
                selectedAffordancePointIndex = -1;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (isEditingAffordancePoints)
            {
                EditorGUILayout.HelpBox(
                    "Scene View Controls:\n" +
                    "• Shift + Click: Add new point\n" +
                    "• Click point: Select point\n" +
                    "• Drag point: Move point\n" +
                    "• Delete/Backspace: Remove selected point\n" +
                    "• Click center handle: Move center offset",
                    MessageType.Info);
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All Points"))
            {
                Undo.RecordObject(affordanceVolume, "Clear Custom Points");
                affordanceVolume.CustomPoints = new Vector2[0];
                selectedAffordancePointIndex = -1;
                EditorUtility.SetDirty(affordanceVolume);
            }

            if (GUILayout.Button("Create Default Rectangle"))
            {
                CreateDefaultAffordanceRectangle(affordanceVolume);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Display points list
            EditorGUILayout.LabelField("Point List", EditorStyles.boldLabel);
            for (int i = 0; i < points.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();

                GUI.backgroundColor = (i == selectedAffordancePointIndex) ? Color.yellow : Color.white;
                if (GUILayout.Button($"P{i}", GUILayout.Width(30)))
                {
                    selectedAffordancePointIndex = i;
                    SceneView.RepaintAll();
                }
                GUI.backgroundColor = Color.white;

                EditorGUI.BeginChangeCheck();
                Vector2 newPoint = EditorGUILayout.Vector2Field(GUIContent.none, points[i]);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(affordanceVolume, "Edit Point");
                    var newPoints = (Vector2[])points.Clone();
                    newPoints[i] = newPoint;
                    affordanceVolume.CustomPoints = newPoints;
                    EditorUtility.SetDirty(affordanceVolume);
                }

                if (GUILayout.Button("X", GUILayout.Width(20)))
                {
                    Undo.RecordObject(affordanceVolume, "Delete Point");
                    var newPoints = new System.Collections.Generic.List<Vector2>(points);
                    newPoints.RemoveAt(i);
                    affordanceVolume.CustomPoints = newPoints.ToArray();
                    if (selectedAffordancePointIndex >= newPoints.Count)
                    {
                        selectedAffordancePointIndex = newPoints.Count - 1;
                    }
                    EditorUtility.SetDirty(affordanceVolume);
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
        }

        private void CreateDefaultAffordanceRectangle(AffordanceVolume affordanceVolume)
        {
            Undo.RecordObject(affordanceVolume, "Create Default Rectangle");

            Vector2[] defaultPoints = new Vector2[]
            {
                new Vector2(-1f, -1f),
                new Vector2(1f, -1f),
                new Vector2(1f, 1f),
                new Vector2(-1f, 1f)
            };

            affordanceVolume.CustomPoints = defaultPoints;
            EditorUtility.SetDirty(affordanceVolume);
        }

        private void OnSceneGUI()
        {
            DrawAffordanceVolumeHandles();
        }

        private void DrawAffordanceVolumeHandles()
        {
            // Only draw in edit mode
            if (Application.isPlaying) return;

            var entity = (SbEntity)target;
            if (entity == null)
            {
                return;
            }

            var affordanceVolume = entity.GetComponent<AffordanceVolume>();
            if (affordanceVolume == null)
            {
                return;
            }

            if (affordanceVolume.type != AffordanceVolume.AffordanceVolumeType.PRESET)
            {
                return;
            }

            // Disable default tools when editing points to prevent conflicts
            if (isEditingAffordancePoints)
            {
                Tools.current = Tool.None;
            }

            Transform transform = affordanceVolume.transform;
            Vector2 centerOffset = affordanceVolume.CustomCenterOffset;
            Vector2[] points = affordanceVolume.CustomPoints ?? new Vector2[0];

            // Draw center offset handle
            Vector3 centerWorld = transform.TransformPoint(new Vector3(centerOffset.x, 0, centerOffset.y));
            Handles.color = Color.cyan;
            float handleSize = HandleUtility.GetHandleSize(centerWorld) * 0.2f;

            if (isEditingAffordancePoints)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newCenterWorld = Handles.FreeMoveHandle(
                    centerWorld,
                    handleSize,
                    Vector3.zero,
                    Handles.SphereHandleCap);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(affordanceVolume, "Move Center Offset");
                    Vector3 localPos = transform.InverseTransformPoint(newCenterWorld);
                    affordanceVolume.CustomCenterOffset = new Vector2(localPos.x, localPos.z);
                    EditorUtility.SetDirty(affordanceVolume);
                }

                // Draw center label
                Handles.Label(centerWorld + Vector3.up * 0.3f, "Center", EditorStyles.boldLabel);
            }

            // Draw and edit points
            int pointCount = points.Length;
            if (pointCount > 0)
            {
                Vector3[] worldPoints = new Vector3[pointCount];

                for (int i = 0; i < pointCount; i++)
                {
                    Vector2 point = points[i];
                    Vector3 localPos = new Vector3(point.x + centerOffset.x, 0, point.y + centerOffset.y);
                    worldPoints[i] = transform.TransformPoint(localPos);
                }

                // Draw polygon outline
                Handles.color = new Color(1f, 0.5f, 0f, 1f);
                for (int i = 0; i < pointCount; i++)
                {
                    int nextIndex = (i + 1) % pointCount;
                    Handles.DrawLine(worldPoints[i], worldPoints[nextIndex], 2f);
                }

                // Draw and handle point manipulation
                if (isEditingAffordancePoints)
                {
                    Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

                    for (int i = 0; i < pointCount; i++)
                    {
                        Vector2 point = points[i];
                        Vector3 localPos = new Vector3(point.x + centerOffset.x, 0, point.y + centerOffset.y);
                        Vector3 worldPos = transform.TransformPoint(localPos);

                        // Much larger handle size for easier selection
                        float size = HandleUtility.GetHandleSize(worldPos) * 0.15f;

                        // Highlight selected point
                        Handles.color = (i == selectedAffordancePointIndex) ? Color.yellow : Color.green;

                        // Draw a solid disc first for visibility
                        Handles.DrawSolidDisc(worldPos, Vector3.up, size * 0.5f);

                        // Use PositionHandle for reliable interaction
                        EditorGUI.BeginChangeCheck();
                        Vector3 newWorldPos = Handles.PositionHandle(worldPos, Quaternion.identity);

                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(affordanceVolume, "Move Point");
                            Vector3 newLocalPos = transform.InverseTransformPoint(newWorldPos);
                            Vector2 newPoint = new Vector2(newLocalPos.x - centerOffset.x, newLocalPos.z - centerOffset.y);
                            var newPoints = (Vector2[])points.Clone();
                            newPoints[i] = newPoint;
                            affordanceVolume.CustomPoints = newPoints;
                            selectedAffordancePointIndex = i;
                            EditorUtility.SetDirty(affordanceVolume);
                        }

                        // Draw point index label
                        Handles.Label(worldPos + Vector3.up * 0.3f, $"P{i}", EditorStyles.whiteBoldLabel);
                    }

                    // Handle keyboard input for deleting points
                    Event e = Event.current;
                    if (e.type == EventType.KeyDown &&
                        (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) &&
                        selectedAffordancePointIndex >= 0 && selectedAffordancePointIndex < pointCount)
                    {
                        Undo.RecordObject(affordanceVolume, "Delete Point");
                        var newPoints = new System.Collections.Generic.List<Vector2>(points);
                        newPoints.RemoveAt(selectedAffordancePointIndex);
                        affordanceVolume.CustomPoints = newPoints.ToArray();
                        selectedAffordancePointIndex = Mathf.Min(selectedAffordancePointIndex, newPoints.Count - 1);
                        EditorUtility.SetDirty(affordanceVolume);
                        e.Use();
                    }

                    // Handle Shift+Click to add new point
                    if (e.type == EventType.MouseDown && e.button == 0 && e.shift)
                    {
                        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                        Plane groundPlane = new Plane(transform.up, transform.position);

                        if (groundPlane.Raycast(ray, out float distance))
                        {
                            Vector3 hitPoint = ray.GetPoint(distance);
                            Vector3 localHit = transform.InverseTransformPoint(hitPoint);
                            Vector2 newPoint = new Vector2(localHit.x - centerOffset.x, localHit.z - centerOffset.y);

                            Undo.RecordObject(affordanceVolume, "Add Point");

                            // Find best insertion index (closest edge)
                            int insertIndex = FindBestAffordanceInsertionIndex(newPoint, points);

                            var newPoints = new System.Collections.Generic.List<Vector2>(points);
                            newPoints.Insert(insertIndex, newPoint);
                            affordanceVolume.CustomPoints = newPoints.ToArray();
                            selectedAffordancePointIndex = insertIndex;
                            EditorUtility.SetDirty(affordanceVolume);

                            e.Use();
                        }
                    }
                }
            }
            else if (isEditingAffordancePoints)
            {
                // No points yet, allow adding first point with Shift+Click
                Event e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 0 && e.shift)
                {
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    Plane groundPlane = new Plane(transform.up, transform.position);

                    if (groundPlane.Raycast(ray, out float distance))
                    {
                        Vector3 hitPoint = ray.GetPoint(distance);
                        Vector3 localHit = transform.InverseTransformPoint(hitPoint);
                        Vector2 newPoint = new Vector2(localHit.x - centerOffset.x, localHit.z - centerOffset.y);

                        Undo.RecordObject(affordanceVolume, "Add First Point");
                        affordanceVolume.CustomPoints = new Vector2[] { newPoint };
                        selectedAffordancePointIndex = 0;
                        EditorUtility.SetDirty(affordanceVolume);

                        e.Use();
                    }
                }
            }
        }

        private int FindBestAffordanceInsertionIndex(Vector2 newPoint, Vector2[] points)
        {
            int pointCount = points.Length;
            if (pointCount < 2)
            {
                return pointCount;
            }

            float minDistance = float.MaxValue;
            int bestIndex = pointCount;

            for (int i = 0; i < pointCount; i++)
            {
                int nextIndex = (i + 1) % pointCount;
                Vector2 p1 = points[i];
                Vector2 p2 = points[nextIndex];

                float dist = DistanceToLineSegment(newPoint, p1, p2);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    bestIndex = nextIndex;
                }
            }

            return bestIndex;
        }

        private float DistanceToLineSegment(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
        {
            Vector2 line = lineEnd - lineStart;
            float lineLength = line.magnitude;
            if (lineLength < 0.0001f)
            {
                return Vector2.Distance(point, lineStart);
            }

            Vector2 lineDir = line / lineLength;
            Vector2 toPoint = point - lineStart;
            float projection = Vector2.Dot(toPoint, lineDir);

            if (projection < 0)
            {
                return Vector2.Distance(point, lineStart);
            }
            if (projection > lineLength)
            {
                return Vector2.Distance(point, lineEnd);
            }

            Vector2 closestPoint = lineStart + lineDir * projection;
            return Vector2.Distance(point, closestPoint);
        }

        private void DrawEntityInformation(SbEntity entity)
        {
            var manager = entity.spaceManager;
            if (manager == null) return;

            var entityInfo = manager.GetEntityInfo(entity.ID);
            var entityState = manager.GetEntityState(entity.ID);

            // Basic Entity Information
            if (showEntityInfo)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Basic Information", EditorStyles.boldLabel);

                EditorGUILayout.LabelField("Entity ID", entity.ID.ToString());
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Is Local", entityInfo.IsLocal ? "✓" : "✗", entityInfo.IsLocal ? EditorStyles.boldLabel : EditorStyles.label);
                EditorGUILayout.LabelField("Is Valid", entityInfo.IsValid ? "✓" : "✗", entityInfo.IsValid ? EditorStyles.boldLabel : EditorStyles.label);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField("Position 2D", $"({entityInfo.Position2D[0]:F3}, {entityInfo.Position2D[1]:F3})");
                EditorGUILayout.LabelField("Position 3D", $"({entityInfo.Position3D[0]:F3}, {entityInfo.Position3D[1]:F3}, {entityInfo.Position3D[2]:F3})");
                EditorGUILayout.LabelField("Rotation", $"Yaw: {entityInfo.RotationYaw:F2}°, Pitch: {entityInfo.RotationPitch:F2}°");
                EditorGUILayout.LabelField("User Data Size", $"{entityInfo.UserDataSize} bytes");
            }

            // State Vectors
            if (showStateVectors && entityState.NumStateVectors > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("State Vectors", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Count: {entityState.NumStateVectors}, User Data: {entityState.UserDataSize} bytes");

                for (ulong i = 0; i < entityState.NumStateVectors; i++)
                {
                    var stateVector = entityState.StateVectors[i];
                    EditorGUILayout.LabelField($"State {i + 1}", $"Type: {stateVector.StateType}, Values: {stateVector.NumValues}");

                    if (stateVector.NumValues > 0)
                    {
                        string valuesStr = "";
                        for (ulong j = 0; j < stateVector.NumValues && j < (ulong)stateVector.Values.Length; j++)
                        {
                            valuesStr += $"{stateVector.Values[j]:F3}";
                            if (j < stateVector.NumValues - 1) valuesStr += ", ";
                        }
                        EditorGUILayout.LabelField("  Values", valuesStr);
                    }
                }
            }

            // Interactions
            if (showInteractions && entity.EntityType == EntityType.NetworkAvatar)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Interactions", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    if ((int)proximityType != (int)ProximityType.Euclidean2D || (int)proximityType != (int)ProximityType.Angular1D) continue;
                    var interactionInfo = manager.GetInteractionInfo((int)proximityType, entity.ID);
                    if (interactionInfo.NumTargets > 0)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Targets: {interactionInfo.NumTargets}");
                        if (interactionInfo.NumTargets > 0)
                        {
                            string targetIds = "";
                            for (ulong j = 0; j < interactionInfo.NumTargets && j < (ulong)interactionInfo.TargetEntityIds.Length; j++)
                            {
                                targetIds += interactionInfo.TargetEntityIds[j].ToString();
                                if (j < interactionInfo.NumTargets - 1) targetIds += ", ";
                            }
                            EditorGUILayout.LabelField("  Target IDs", targetIds);
                        }
                    }
                }
            }

            // Proximity Data
            if (showProximityData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Proximity Data", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    var proximityInfo = manager.GetProximityInfo((int)proximityType);
                    if (proximityInfo.IsActive)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Value: {proximityInfo.CurrentValue:F3}, Threshold: {proximityInfo.Threshold:F3}");
                    }
                }
            }

            // Timeline Data
            if (showTimelineData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Interaction Timeline", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    var timeline = manager.GetEntityInteractionTimeline(entity.ID, (int)proximityType);
                    if (timeline.NumInteractions > 0)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Interactions: {timeline.NumInteractions}");

                        // Show first few interactions
                        int maxToShow = Mathf.Min(5, (int)timeline.NumInteractions);
                        for (int i = 0; i < maxToShow; i++)
                        {
                            var interaction = timeline.Interactions[i];
                            double steps = interaction.Steps;
                            bool isInteractable = manager.IsInteractable(interaction.ProximityType, entity.ID, interaction.TargetId);
                            EditorGUILayout.LabelField($"  → {interaction.TargetId}", $"{steps:F1} steps, Interactable: {(isInteractable ? "✓" : "✗")}");
                        }

                        if (timeline.NumInteractions > (ulong)maxToShow)
                        {
                            EditorGUILayout.LabelField($"  ... and {timeline.NumInteractions - (ulong)maxToShow} more");
                        }
                    }
                }
            }

            // Raw Data (if enabled)
            if (showRawData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Raw Data", EditorStyles.boldLabel);

                if (entityState.UserDataSize > 0)
                {
                    EditorGUILayout.LabelField("User Data (Hex):");
                    string hexData = "";
                    for (ulong i = 0; i < entityState.UserDataSize && i < (ulong)entityState.UserData.Length; i++)
                    {
                        hexData += $"{entityState.UserData[i]:X2} ";
                        if ((i + 1) % 16 == 0) hexData += "\n";
                    }
                    EditorGUILayout.TextArea(hexData, GUILayout.Height(60));
                }
            }
        }
    }

    // Keep the original EditorWindow for the dedicated window
    public class SbEntityInspectorWindow : EditorWindow
    {
        private Vector2 scrollPosition;
        private SpatialBlendManager selectedManager;
        private int selectedEntityIndex = -1;
        private bool showAllEntities = true;
        private bool showDetailedInfo = true;
        private bool showStateVectors = true;
        private bool showInteractions = true;
        private bool showProximityData = true;
        private bool showTimelineData = true;
        private bool autoRefresh = true;
        private float lastRefreshTime = 0f;
        private const float REFRESH_INTERVAL = 0.5f;

        // Filtering options
        private bool showLocalOnly = false;
        private bool showRemoteOnly = false;
        private string searchFilter = "";

        // Display settings
        private bool compactMode = false;
        private bool showRawData = false;

        [MenuItem("Window/SpatialBlend/Entity Inspector")]
        public static void ShowWindow()
        {
            var window = GetWindow<SbEntityInspectorWindow>("SpatialBlend Entity Inspector");
            window.minSize = new Vector2(400, 300);
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Entity Inspector is only available in Play Mode.", MessageType.Info);
                return;
            }

            // Find SpatialBlendManager if not selected
            if (selectedManager == null)
            {
                var managers = FindObjectsByType<SpatialBlendManager>(FindObjectsSortMode.None);
                if (managers.Length > 0)
                {
                    selectedManager = managers[0];
                }
                else
                {
                    EditorGUILayout.HelpBox("No SpatialBlendManager found in the scene.", MessageType.Warning);
                    return;
                }
            }

            DrawToolbar();
            DrawEntityList();
            DrawEntityDetails();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Manager selection
            var managers = FindObjectsByType<SpatialBlendManager>(FindObjectsSortMode.None);
            string[] managerNames = new string[managers.Length];
            for (int i = 0; i < managers.Length; i++)
            {
                managerNames[i] = $"Manager {i + 1} (ID: {managers[i].ID})";
            }

            int currentIndex = System.Array.IndexOf(managers, selectedManager);
            int newIndex = EditorGUILayout.Popup(currentIndex, managerNames, EditorStyles.toolbarPopup, GUILayout.Width(200));
            if (newIndex != currentIndex && newIndex >= 0)
            {
                selectedManager = managers[newIndex];
            }

            GUILayout.FlexibleSpace();

            // Display options
            showDetailedInfo = GUILayout.Toggle(showDetailedInfo, "Details", EditorStyles.toolbarButton);
            showStateVectors = GUILayout.Toggle(showStateVectors, "States", EditorStyles.toolbarButton);
            showInteractions = GUILayout.Toggle(showInteractions, "Interactions", EditorStyles.toolbarButton);
            showProximityData = GUILayout.Toggle(showProximityData, "Proximity", EditorStyles.toolbarButton);
            showTimelineData = GUILayout.Toggle(showTimelineData, "Timeline", EditorStyles.toolbarButton);
            autoRefresh = GUILayout.Toggle(autoRefresh, "Auto Refresh", EditorStyles.toolbarButton);

            EditorGUILayout.EndHorizontal();

            // Filter options
            EditorGUILayout.BeginHorizontal();
            searchFilter = EditorGUILayout.TextField("Search", searchFilter, GUILayout.Width(200));
            showLocalOnly = GUILayout.Toggle(showLocalOnly, "Local Only", EditorStyles.toolbarButton);
            showRemoteOnly = GUILayout.Toggle(showRemoteOnly, "Remote Only", EditorStyles.toolbarButton);
            compactMode = GUILayout.Toggle(compactMode, "Compact", EditorStyles.toolbarButton);
            showRawData = GUILayout.Toggle(showRawData, "Raw Data", EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntityList()
        {
            if (selectedManager == null || selectedManager.Entities == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Entities ({selectedManager.Entities.Count})", EditorStyles.boldLabel);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));

            int entityIndex = 0;
            foreach (var entity in selectedManager.Entities)
            {
                if (entity == null) continue;

                // Apply filters
                var entityInfo = selectedManager.GetEntityInfo(entity.ID);
                if (showLocalOnly && !entityInfo.IsLocal) continue;
                if (showRemoteOnly && entityInfo.IsLocal) continue;
                if (!string.IsNullOrEmpty(searchFilter) && !entity.ID.ToString().Contains(searchFilter)) continue;

                EditorGUILayout.BeginHorizontal();

                // Entity selection
                bool isSelected = selectedEntityIndex == entityIndex;
                bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
                if (newSelected != isSelected)
                {
                    selectedEntityIndex = entityIndex;
                }

                // Entity info
                EditorGUILayout.LabelField($"ID: {entity.ID}", GUILayout.Width(80));
                EditorGUILayout.LabelField(entityInfo.IsLocal ? "Local" : "Remote", entityInfo.IsLocal ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(60));
                EditorGUILayout.LabelField($"Pos: ({entityInfo.Position3D[0]:F1}, {entityInfo.Position3D[1]:F1}, {entityInfo.Position3D[2]:F1})");

                if (!compactMode)
                {
                    EditorGUILayout.LabelField($"Valid: {(entityInfo.IsValid ? "✓" : "✗")}", entityInfo.IsValid ? EditorStyles.boldLabel : EditorStyles.label);
                }

                EditorGUILayout.EndHorizontal();
                entityIndex++;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawEntityDetails()
        {
            if (selectedEntityIndex < 0 || selectedEntityIndex >= selectedManager.Entities.Count) return;

            var entity = selectedManager.Entities[selectedEntityIndex];
            if (entity == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Entity {entity.ID} Details", EditorStyles.boldLabel);

            var entityInfo = selectedManager.GetEntityInfo(entity.ID);
            var entityState = selectedManager.GetEntityState(entity.ID);

            // Basic Information
            EditorGUILayout.LabelField("Basic Information", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Entity ID", entity.ID.ToString());
            EditorGUILayout.LabelField("Is Local", entityInfo.IsLocal ? "✓" : "✗", entityInfo.IsLocal ? EditorStyles.boldLabel : EditorStyles.label);
            EditorGUILayout.LabelField("Is Valid", entityInfo.IsValid ? "✓" : "✗", entityInfo.IsValid ? EditorStyles.boldLabel : EditorStyles.label);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Position 2D", $"({entityInfo.Position2D[0]:F3}, {entityInfo.Position2D[1]:F3})");
            EditorGUILayout.LabelField("Position 3D", $"({entityInfo.Position3D[0]:F3}, {entityInfo.Position3D[1]:F3}, {entityInfo.Position3D[2]:F3})");
            EditorGUILayout.LabelField("Rotation", $"Yaw: {entityInfo.RotationYaw:F2}°, Pitch: {entityInfo.RotationPitch:F2}°");
            EditorGUILayout.LabelField("User Data Size", $"{entityInfo.UserDataSize} bytes");

            // State Vectors
            if (showStateVectors && entityState.NumStateVectors > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("State Vectors", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Count: {entityState.NumStateVectors}, User Data: {entityState.UserDataSize} bytes");

                for (ulong i = 0; i < entityState.NumStateVectors; i++)
                {
                    var stateVector = entityState.StateVectors[i];
                    EditorGUILayout.LabelField($"State {i + 1}", $"Type: {stateVector.StateType}, Values: {stateVector.NumValues}");

                    if (stateVector.NumValues > 0)
                    {
                        string valuesStr = "";
                        for (ulong j = 0; j < stateVector.NumValues && j < (ulong)stateVector.Values.Length; j++)
                        {
                            valuesStr += $"{stateVector.Values[j]:F3}";
                            if (j < stateVector.NumValues - 1) valuesStr += ", ";
                        }
                        EditorGUILayout.LabelField("  Values", valuesStr);
                    }
                }
            }

            // Interactions
            if (showInteractions)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Interactions", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    var interactionInfo = selectedManager.GetInteractionInfo((int)proximityType, entity.ID);
                    if (interactionInfo.NumTargets > 0)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Targets: {interactionInfo.NumTargets}");
                        if (interactionInfo.NumTargets > 0)
                        {
                            string targetIds = "";
                            for (ulong j = 0; j < interactionInfo.NumTargets && j < (ulong)interactionInfo.TargetEntityIds.Length; j++)
                            {
                                targetIds += interactionInfo.TargetEntityIds[j].ToString();
                                if (j < interactionInfo.NumTargets - 1) targetIds += ", ";
                            }
                            EditorGUILayout.LabelField("  Target IDs", targetIds);
                        }
                    }
                }
            }

            // Proximity Data
            if (showProximityData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Proximity Data", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    var proximityInfo = selectedManager.GetProximityInfo((int)proximityType);
                    if (proximityInfo.IsActive)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Value: {proximityInfo.CurrentValue:F3}, Threshold: {proximityInfo.Threshold:F3}");
                    }
                }
            }

            // Timeline Data
            if (showTimelineData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Interaction Timeline", EditorStyles.boldLabel);

                foreach (ProximityType proximityType in System.Enum.GetValues(typeof(ProximityType)))
                {
                    var timeline = selectedManager.GetEntityInteractionTimeline(entity.ID, (int)proximityType);
                    if (timeline.NumInteractions > 0)
                    {
                        EditorGUILayout.LabelField($"{proximityType}", $"Interactions: {timeline.NumInteractions}");

                        // Show first few interactions
                        int maxToShow = Mathf.Min(5, (int)timeline.NumInteractions);
                        for (int i = 0; i < maxToShow; i++)
                        {
                            var interaction = timeline.Interactions[i];
                            double steps = interaction.Steps;
                            bool isInteractable = selectedManager.IsInteractable(interaction.ProximityType, entity.ID, interaction.TargetId);
                            EditorGUILayout.LabelField($"  → {interaction.TargetId}", $"{steps:F1} steps, Interactable: {(isInteractable ? "✓" : "✗")}");
                        }

                        if (timeline.NumInteractions > (ulong)maxToShow)
                        {
                            EditorGUILayout.LabelField($"  ... and {timeline.NumInteractions - (ulong)maxToShow} more");
                        }
                    }
                }
            }

            // Raw Data (if enabled)
            if (showRawData)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Raw Data", EditorStyles.boldLabel);

                if (entityState.UserDataSize > 0)
                {
                    EditorGUILayout.LabelField("User Data (Hex):");
                    string hexData = "";
                    for (ulong i = 0; i < entityState.UserDataSize && i < (ulong)entityState.UserData.Length; i++)
                    {
                        hexData += $"{entityState.UserData[i]:X2} ";
                        if ((i + 1) % 16 == 0) hexData += "\n";
                    }
                    EditorGUILayout.TextArea(hexData, GUILayout.Height(60));
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void Update()
        {
            if (autoRefresh && Application.isPlaying)
            {
                if (Time.realtimeSinceStartup - lastRefreshTime > REFRESH_INTERVAL)
                {
                    lastRefreshTime = Time.realtimeSinceStartup;
                    Repaint();
                }
            }
        }
    }
}
