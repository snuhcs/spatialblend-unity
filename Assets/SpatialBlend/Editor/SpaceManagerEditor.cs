// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEditor;
using SpatialBlend.Internal;
using System.Linq;
using SpatialBlend;

namespace SpatialBlend
{
    [CustomEditor(typeof(SpatialBlendManager))]
    public class SpaceManagerEditor : UnityEditor.Editor
    {
        private bool[] entityFoldouts = new bool[0];
        private static bool visualizeInScene = false;
        private ProximityType selectedProximityType = ProximityType.Angular1D;
        private readonly string[] proximityTypeNames = System.Enum.GetNames(typeof(ProximityType));
        private Color interactionColor = new Color(0, 1, 1, 0.5f); // Cyan, semi-transparent

        // Debug visualization controls
        private int selectedSourceEntityIndex = -1;
        private float proximityThreshold = 1.0f; // Only show interactions within this proximity threshold
        private bool showOnlyInteractable = true; // Only show interactable connections

        private void OnEnable()
        {
            SceneView.duringSceneGui += DrawSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawSceneGUI;
        }

        private void DrawSceneGUI(SceneView sceneView)
        {
            if (!visualizeInScene || !Application.isPlaying) return;

            var manager = (SpatialBlendManager)target;
            if (manager == null) return;

            // Visualize Geometry
            var spaceInfo = manager.GetSpaceInfo();
            if (spaceInfo.HasWalkableGeometry)
            {
                var walkablePolygon = manager.GetWalkablePolygon();
                Handles.color = Color.green;
                DrawPolygonRings(walkablePolygon);
                // Draw walkable bounds
                var walkableBounds = manager.GetGeometryBounds((int)GeometryType.Walkable);
                Handles.color = Color.yellow;
                DrawBounds(walkableBounds);
            }
            if (spaceInfo.HasWallGeometry)
            {
                var wallPolygon = manager.GetWallPolygon();
                Handles.color = Color.blue;
                DrawPolygonRings(wallPolygon);
            }

            // Visualize Entity Affordance Polygons
            if (manager.Entities != null)
            {
                Handles.color = Color.magenta;
                foreach (var entity in manager.Entities)
                {
                    if (entity == null) continue;
                    var affordancePolygon = manager.GetEntityAffordancePolygon(entity.ID);
                    DrawPolygonRings(affordancePolygon);
                }
            }

            // Visualize Entities
            if (manager.Entities != null)
            {
                foreach (var entity in manager.Entities)
                {
                    if (entity == null) continue;
                    Vector3 pos = entity.EntityTransform.position;
                    Quaternion rot = entity.EntityTransform.rotation;
                    Handles.color = Color.yellow;
                    Handles.SphereHandleCap(0, pos, Quaternion.identity, 0.1f, EventType.Repaint);
                    Handles.color = Color.red;
                    Handles.DrawLine(pos, pos + (rot * Vector3.forward) * 0.5f);
                    Handles.Label(pos + Vector3.up * 0.5f, $"ID: {entity.ID}");
                }
            }

            // Visualize Native Interactions for selected source entity only
            if (selectedSourceEntityIndex >= 0 && selectedSourceEntityIndex < manager.Entities.Count)
            {
                var sourceEntity = manager.Entities[selectedSourceEntityIndex];
                if (sourceEntity != null)
                {
                    Handles.color = interactionColor;
                    Vector3 sourcePos = sourceEntity.EntityTransform.position;

                    // Highlight the selected source entity
                    Handles.color = Color.yellow;
                    Handles.SphereHandleCap(0, sourcePos, Quaternion.identity, 0.15f, EventType.Repaint);
                    Handles.Label(sourcePos + Vector3.up * 0.3f, $"SOURCE: {sourceEntity.ID}");

                    // Get timeline data for the source entity
                    var timeline = manager.GetEntityInteractionTimeline(sourceEntity.ID, (int)selectedProximityType);

                    // Draw lines only to entities in the timeline that meet our criteria
                    for (ulong i = 0; i < timeline.NumInteractions; i++)
                    {
                        var interaction = timeline.Interactions[i];
                        long targetId = interaction.TargetId;

                        // Find the target entity
                        var targetEntity = manager.Entities.FirstOrDefault(e => e != null && e.ID == targetId);
                        if (targetEntity == null) continue;

                        Vector3 targetPos = targetEntity.EntityTransform.position;

                        // Get proximity value
                        double proximityValue = manager.GetProximity((int)selectedProximityType, sourceEntity.ID, targetId);

                        // Apply proximity threshold filter
                        if (proximityValue > proximityThreshold) continue;

                        // Check if interaction is possible
                        bool isInteractable = manager.IsInteractable((int)selectedProximityType, sourceEntity.ID, targetId);

                        // Apply interactable filter
                        if (showOnlyInteractable && !isInteractable) continue;

                        // Draw line from source to target
                        Handles.color = isInteractable ? Color.green : Color.red;
                        Handles.DrawLine(sourcePos, targetPos);

                        // Draw label at midpoint
                        Vector3 mid = Vector3.Lerp(sourcePos, targetPos, 0.5f) + Vector3.up * 0.2f;

                        // Draw a solid disc at the midpoint
                        Handles.color = new Color(0, 1, 1, 0.25f);
                        Handles.DrawSolidDisc(mid, Vector3.up, 0.08f);

                        // Draw label with interaction data
                        Handles.color = interactionColor;
                        double steps = interaction.Steps;
                        string interactableText = isInteractable ? "✓" : "✗";
                        Handles.Label(mid, $"({proximityValue:F3}, {steps:F1} steps, {interactableText})");
                    }
                }
            }
        }

        private void DrawBounds(GeometryBounds bounds)
        {
            var p1 = new Vector3((float)bounds.MinX, 0, (float)bounds.MinY);
            var p2 = new Vector3((float)bounds.MaxX, 0, (float)bounds.MinY);
            var p3 = new Vector3((float)bounds.MaxX, 0, (float)bounds.MaxY);
            var p4 = new Vector3((float)bounds.MinX, 0, (float)bounds.MaxY);
            Handles.DrawPolyLine(p1, p2, p3, p4, p1);
        }

        private void DrawPolygonRings(Polygon polygon)
        {
            if (polygon.Rings == null) return;
            foreach (var ring in polygon.Rings)
            {
                if (ring.Points == null || ring.NumPoints < 2) continue;
                Vector3[] verts = new Vector3[ring.NumPoints + 1];
                for (int i = 0; i < (int)ring.NumPoints; i++)
                {
                    verts[i] = new Vector3((float)ring.Points[i * 2], 0, (float)ring.Points[i * 2 + 1]);
                }
                verts[ring.NumPoints] = verts[0]; // close the loop
                Handles.DrawPolyLine(verts);
            }
        }

        public override void OnInspectorGUI()
        {
            // Draw the default inspector
            DrawDefaultInspector();

            var manager = (SpatialBlendManager)target;

            // Show connection-related fields when _connectOnAwake is true,
            // even though the fields themselves are marked with [HideInInspector]
            // (we draw them manually here).
            serializedObject.Update();
            var connectOnAwakeProp = serializedObject.FindProperty("_connectOnAwake");
            if (connectOnAwakeProp != null && connectOnAwakeProp.boolValue)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Connection Settings", EditorStyles.boldLabel);

                var modeProp = serializedObject.FindProperty("mode");
                var portProp = serializedObject.FindProperty("port");
                var hostIpProp = serializedObject.FindProperty("hostIP");

                if (modeProp != null)
                {
                    EditorGUILayout.PropertyField(modeProp, new GUIContent("Mode"));
                }
                if (portProp != null)
                {
                    EditorGUILayout.PropertyField(portProp, new GUIContent("Port"));
                }
                if (hostIpProp != null)
                {
                    EditorGUILayout.PropertyField(hostIpProp, new GUIContent("Host IP"));
                }

                serializedObject.ApplyModifiedProperties();
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Live information is available only in Play Mode.", MessageType.Info);
                return;
            }

            // Interaction type selector
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Interaction Visualization", EditorStyles.boldLabel);
            int selectedIdx = (int)selectedProximityType;
            selectedIdx = EditorGUILayout.Popup("Proximity Type", selectedIdx, proximityTypeNames);
            selectedProximityType = (ProximityType)selectedIdx;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live Information", EditorStyles.boldLabel);

            // --- Space Info ---
            EditorGUILayout.LabelField("Space Info", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("  Space ID", manager.ID.ToString());
            var spaceInfo = manager.GetSpaceInfo();
            EditorGUILayout.LabelField("  Is Host", spaceInfo.IsHost.ToString());
            EditorGUILayout.LabelField("  Is Connected", spaceInfo.IsConnected.ToString());
            EditorGUILayout.LabelField("  Num Connected Spaces", spaceInfo.NumConnectedSpaces.ToString());

            // --- Entity Counts ---
            EditorGUILayout.LabelField("Entity Counts", EditorStyles.boldLabel);
            var entityCounts = manager.GetEntityCounts();
            EditorGUILayout.LabelField("  Total Entities", entityCounts.TotalEntities.ToString());
            EditorGUILayout.LabelField("  Local Entities", entityCounts.LocalEntities.ToString());
            EditorGUILayout.LabelField("  Remote Entities", entityCounts.RemoteEntities.ToString());
            EditorGUILayout.LabelField("  Dirty Entities", entityCounts.DirtyEntities.ToString());

            // --- Geometry Info ---
            EditorGUILayout.LabelField("Geometry", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("  Has Walkable", spaceInfo.HasWalkableGeometry.ToString());
            EditorGUILayout.LabelField("  Has Wall", spaceInfo.HasWallGeometry.ToString());

            if (spaceInfo.HasWalkableGeometry)
            {
                var walkableBounds = manager.GetGeometryBounds((int)GeometryType.Walkable);
                EditorGUILayout.LabelField("  Walkable Bounds", $"Min:({walkableBounds.MinX:F2}, {walkableBounds.MinY:F2}), Max:({walkableBounds.MaxX:F2}, {walkableBounds.MaxY:F2})");
            }
            if (spaceInfo.HasWallGeometry)
            {
                var wallBounds = manager.GetGeometryBounds((int)GeometryType.Wall);
                EditorGUILayout.LabelField("  Wall Bounds", $"Min:({wallBounds.MinX:F2}, {wallBounds.MinY:F2}), Max:({wallBounds.MaxX:F2}, {wallBounds.MaxY:F2})");
            }

            // --- Entities List (Simplified) ---
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entities", EditorStyles.boldLabel);
            if (manager.Entities != null)
            {
                if (entityFoldouts.Length != manager.Entities.Count)
                {
                    entityFoldouts = new bool[manager.Entities.Count];
                }

                int i = 0;
                foreach (var entity in manager.Entities)
                {
                    if (entity == null) continue;

                    EditorGUILayout.BeginHorizontal();
                    entityFoldouts[i] = EditorGUILayout.Foldout(entityFoldouts[i], $"Entity ID: {entity.ID}", true);
                    EditorGUILayout.ObjectField(entity.gameObject, typeof(GameObject), true);
                    EditorGUILayout.EndHorizontal();

                    if (entityFoldouts[i])
                    {
                        EditorGUI.indentLevel++;
                        var entityInfo = manager.GetEntityInfo(entity.ID);
                        EditorGUILayout.LabelField("Is Local", entityInfo.IsLocal.ToString());
                        EditorGUILayout.LabelField("Is Valid", entityInfo.IsValid.ToString());
                        EditorGUILayout.LabelField("Position", new Vector3((float)entityInfo.Position3D[0], (float)entityInfo.Position3D[1], (float)entityInfo.Position3D[2]).ToString("F2"));
                        EditorGUILayout.LabelField("Yaw", entityInfo.RotationYaw.ToString("F2"));
                        EditorGUILayout.LabelField("Pitch", entityInfo.RotationPitch.ToString("F2"));
                        EditorGUI.indentLevel--;
                    }
                    i++;
                }
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Log Full Space State"))
            {
                SpatialBlendDebug.ForceLog($"Space State:\n{manager.GetSpaceStateString()}");
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Logging Configuration", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            bool sbLogEnabled = SpatialBlendLog.IsEnabled;
            bool newSbLogEnabled = EditorGUILayout.Toggle("SpatialBlendLog (Source Linking)", sbLogEnabled);
            if (newSbLogEnabled != sbLogEnabled)
            {
                SpatialBlendLog.SetEnabled(newSbLogEnabled);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            bool debugEnabled = SpatialBlendDebug.IsLoggingEnabled;
            bool newDebugEnabled = EditorGUILayout.Toggle("SpatialBlendDebug (Filtered)", debugEnabled);
            if (newDebugEnabled != debugEnabled)
            {
                SpatialBlendDebug.SetLoggingEnabled(newDebugEnabled);
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Show Logging Status"))
            {
                SpatialBlendLoggingUtils.LogCurrentConfiguration();
                SpatialBlendLog.LogEditor($"SpatialBlendLog enabled: {SpatialBlendLog.IsEnabled}");
            }

            EditorGUILayout.LabelField("Quick Presets:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Dev Mode"))
            {
                SpatialBlendLoggingUtils.ConfigureForDevelopment();
                SpatialBlendLog.SetEnabled(true);
            }
            if (GUILayout.Button("Production"))
            {
                SpatialBlendLoggingUtils.ConfigureForProduction();
                SpatialBlendLog.SetEnabled(false);
            }
            if (GUILayout.Button("Minimal"))
            {
                SpatialBlendLoggingUtils.ConfigureMinimal();
                SpatialBlendLog.SetEnabled(false);
            }
            if (GUILayout.Button("Disable All"))
            {
                SpatialBlendLoggingUtils.DisableAllLogging();
                SpatialBlendLog.SetEnabled(false);
            }
            EditorGUILayout.EndHorizontal();

            visualizeInScene = GUILayout.Toggle(visualizeInScene, "Visualize in Scene View", "Button");
            if (visualizeInScene)
            {
                SceneView.RepaintAll();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Debug Visualization Settings", EditorStyles.boldLabel);

                // Proximity type selection
                int currentProximityIndex = System.Array.IndexOf(proximityTypeNames, selectedProximityType.ToString());
                int newProximityIndex = EditorGUILayout.Popup("Proximity Type", currentProximityIndex, proximityTypeNames);
                if (newProximityIndex != currentProximityIndex && newProximityIndex >= 0)
                {
                    selectedProximityType = (ProximityType)newProximityIndex;
                }

                // Source entity selection
                if (manager.Entities != null && manager.Entities.Count > 0)
                {
                    string[] entityNames = new string[manager.Entities.Count];
                    for (int i = 0; i < manager.Entities.Count; i++)
                    {
                        var entity = manager.Entities[i];
                        entityNames[i] = entity != null ? $"Entity {entity.ID}" : "Null Entity";
                    }

                    selectedSourceEntityIndex = EditorGUILayout.Popup("Source Entity", selectedSourceEntityIndex, entityNames);
                    if (selectedSourceEntityIndex >= manager.Entities.Count)
                    {
                        selectedSourceEntityIndex = -1;
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("No entities available for visualization");
                    selectedSourceEntityIndex = -1;
                }

                // Proximity threshold
                proximityThreshold = EditorGUILayout.Slider("Proximity Threshold", proximityThreshold, 0.1f, 10.0f);

                // Show only interactable filter
                showOnlyInteractable = EditorGUILayout.Toggle("Show Only Interactable", showOnlyInteractable);

                EditorGUILayout.HelpBox("Green lines = Interactable, Red lines = Not Interactable", MessageType.Info);
            }

            // Repaint the inspector to show updates
            Repaint();
        }
    }
}