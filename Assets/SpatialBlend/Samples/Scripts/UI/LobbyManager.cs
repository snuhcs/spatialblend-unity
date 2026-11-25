// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

using Meta.XR.BuildingBlocks;

namespace SpatialBlend.Samples
{
    [Serializable]
    public class ToggleLevelPair
    {
        public Toggle toggle;
        public string levelName;
#if UNITY_EDITOR
        public SceneAsset sceneAsset;
#endif
    }

    [Serializable]
    public class ToggleNamePair
    {
        public Toggle toggle;
        public string toggleName;
    }

    public class LobbyManager : MonoBehaviour
    {

        [SerializeField]
        private List<ToggleNamePair> toggleNamePairs = new List<ToggleNamePair>();

        [SerializeField]
        private List<ToggleLevelPair> levelToggles = new List<ToggleLevelPair>();

        [SerializeField]
        private RoomMeshEvent roomMeshEvent;



        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            // Set up image toggles
            foreach (var pair in toggleNamePairs)
            {
                // Capture the pair for the lambda
                var togglePair = pair;
                pair.toggle.onValueChanged.AddListener((isOn) => OnToggleValueChanged(isOn, togglePair));
            }

            // Set up level toggles
            foreach (var pair in levelToggles)
            {
                string targetLevel = pair.levelName;
                pair.toggle.onValueChanged.AddListener((isOn) => OnToggleChanged(isOn, targetLevel));
            }

            if (roomMeshEvent != null)
            {
                roomMeshEvent.OnRoomMeshLoadCompleted.AddListener((meshFilter) => OnRoomMeshLoaded(meshFilter));
            }
        }
#if UNITY_EDITOR
        void OnValidate()
        {
            // Auto-populate levelName from selected SceneAsset
            foreach (var pair in levelToggles)
            {
                if (pair != null && pair.sceneAsset != null)
                {
                    var path = AssetDatabase.GetAssetPath(pair.sceneAsset);
                    if (!string.IsNullOrEmpty(path))
                    {
                        pair.levelName = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                }
            }
        }
#endif

        // Update is called once per frame
        void Update()
        {

        }

        void OnDestroy()
        {
            // Clean up image toggle listeners
            foreach (var pair in toggleNamePairs)
            {
                if (pair.toggle != null)
                {
                    var togglePair = pair;
                    pair.toggle.onValueChanged.RemoveListener((isOn) => OnToggleValueChanged(isOn, togglePair));
                }
            }

            // Clean up level toggle listeners
            foreach (var pair in levelToggles)
            {
                if (pair.toggle != null)
                {
                    string targetLevel = pair.levelName;
                    pair.toggle.onValueChanged.RemoveListener((isOn) => OnToggleChanged(isOn, targetLevel));
                }
            }
        }

        private void OnToggleValueChanged(bool isOn, ToggleNamePair pair)
        {
            if (pair.toggle != null)
                pair.toggle.gameObject.SetActive(isOn);
        }

        private void OnToggleChanged(bool isOn, string levelName)
        {
            if (isOn)
            {
                // if has a level name, load the level
                SceneManager.LoadScene(levelName);
            }
        }

        private void OnRoomMeshLoaded(MeshFilter meshFilter)
        {
            // TODO: 
        }
    }
}