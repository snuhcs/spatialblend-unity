// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEditor;
using SpatialBlend;

namespace SpatialBlend
{
    [CustomEditor(typeof(SbApp), true)]
    public class SbAppEditor : UnityEditor.Editor
    {
        private SerializedProperty _sbEntityScriptAssetProp;
        private SerializedProperty _sbEntityTypeNameProp;

        private void OnEnable()
        {
            _sbEntityScriptAssetProp = serializedObject.FindProperty("_sbEntityScriptAsset");
            _sbEntityTypeNameProp = serializedObject.FindProperty("_sbEntityTypeName");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw the default inspector
            DrawDefaultInspector();

            // Custom handling for the script asset field
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Sb Entity Script Asset");
            var newValue = EditorGUILayout.ObjectField(_sbEntityScriptAssetProp.objectReferenceValue, typeof(MonoScript), false);
            if (newValue != _sbEntityScriptAssetProp.objectReferenceValue)
            {
                _sbEntityScriptAssetProp.objectReferenceValue = newValue;
                serializedObject.ApplyModifiedProperties();
            }
            EditorGUILayout.EndHorizontal();

            // Auto-update the runtime type name when the script changes
            if (_sbEntityScriptAssetProp.objectReferenceValue != null)
            {
                var monoScript = _sbEntityScriptAssetProp.objectReferenceValue as MonoScript;
                if (monoScript != null)
                {
                    var scriptType = monoScript.GetClass();
                    if (scriptType != null && typeof(SbEntity).IsAssignableFrom(scriptType))
                    {
                        var runtimeTypeName = scriptType.AssemblyQualifiedName;
                        if (_sbEntityTypeNameProp.stringValue != runtimeTypeName)
                        {
                            _sbEntityTypeNameProp.stringValue = runtimeTypeName;
                            serializedObject.ApplyModifiedProperties();
                        }
                    }
                    else if (scriptType != null)
                    {
                        EditorGUILayout.HelpBox($"Warning: {scriptType.Name} does not inherit from SbEntity", MessageType.Warning);
                    }
                }
                else
                {
                    // Clear invalid references and show warning
                    EditorGUILayout.HelpBox("Warning: Only MonoScript assets are allowed for this field", MessageType.Warning);
                    if (GUILayout.Button("Clear Invalid Reference"))
                    {
                        _sbEntityScriptAssetProp.objectReferenceValue = null;
                        serializedObject.ApplyModifiedProperties();
                    }
                }
            }
            else
            {
                // Clear the type name when no script is assigned
                if (!string.IsNullOrEmpty(_sbEntityTypeNameProp.stringValue))
                {
                    _sbEntityTypeNameProp.stringValue = string.Empty;
                    serializedObject.ApplyModifiedProperties();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
