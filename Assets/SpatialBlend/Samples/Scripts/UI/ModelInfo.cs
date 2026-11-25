// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.AddressableAssets;

[CreateAssetMenu(menuName = "Data/ModelInfo", fileName = "ModelInfo_")]
public class ModelInfo : ScriptableObject
{
    public string displayName;
    public AssetReferenceGameObject modelRef;         // FBX(import→GameObject)
    public AssetReferenceTexture2D previewTexRef;     // PNG(import→Texture2D)
}
