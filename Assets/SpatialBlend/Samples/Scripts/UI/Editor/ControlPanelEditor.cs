// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using SpatialBlend.Samples.FreeformCollaboration.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    [CustomEditor(typeof(ControlPanel))]
    public class ControlPanelEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editor Controls", EditorStyles.boldLabel);
            
            ControlPanel controlPanel = (ControlPanel)target;
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Connect"))
            {
                controlPanel.SendMessage("OnConnectClicked", SendMessageOptions.DontRequireReceiver);
            }
            if (GUILayout.Button("Rebuild NavMesh"))
            {
                controlPanel.SendMessage("OnRebuildNavMeshClicked", SendMessageOptions.DontRequireReceiver);
            }    
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Show Scene Anchors"))
            {
                controlPanel.SendMessage("ShowSceneAnchorInEditor", SendMessageOptions.DontRequireReceiver);
            }
            if (GUILayout.Button("Hide Scene Anchors"))
            {
                controlPanel.SendMessage("HideSceneAnchorInEditor", SendMessageOptions.DontRequireReceiver);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Show Control Panel"))
            {
                controlPanel.SendMessage("ShowUICanvas", SendMessageOptions.DontRequireReceiver);
            }
            if (GUILayout.Button("Hide Control Panel"))
            {
                controlPanel.SendMessage("HideUICanvas", SendMessageOptions.DontRequireReceiver);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Load Gltf Model"))
            {
                controlPanel.SendMessage("LoadGltfModel", SendMessageOptions.DontRequireReceiver);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
