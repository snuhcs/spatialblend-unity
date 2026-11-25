// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// The tutorial's bundled files, all under a Resources folder: the narration clips, the sample photo and the sample model.
    /// The model and the photo are stored as .bytes text assets so they stay readable at run time (the photo is decoded with
    /// LoadImage) and the model is not imported as a prefab. Sources, licences and hashes: <c>Assets/SpatialBlend/Samples/Tutorial/ASSETS.md</c>.
    /// </summary>
    public static class TutorialAssets
    {
        public const string AudioFolder = "TutorialAssets/Audio/";
        public const string SamplePhoto = "TutorialAssets/sample_photo";
        public const string SampleModel = "TutorialAssets/sample_object";
        public const string SampleMask = "TutorialAssets/sample_mask";
        /// <summary>The lamp that stands for an object that is not in the user's room (created as a virtual object); not the vase.</summary>
        public const string SharedModel = "TutorialAssets/sample_shared";
        /// <summary>The sample photo's size; the sample mask has one byte per pixel of it (row 0 at the top, as the server writes it).</summary>
        public const int SampleImageWidth = 640;
        public const int SampleImageHeight = 480;

        public static AudioClip LoadClip(string clipName) => string.IsNullOrEmpty(clipName) ? null : Resources.Load<AudioClip>(AudioFolder + clipName);

        /// <summary>
        /// True when narration clips are bundled under Resources/TutorialAssets/Audio. The public repository bundles none (the free-plan
        /// ElevenLabs voice could not be redistributed): every line then shows as a subtitle for its reading time.
        /// </summary>
        public static bool HasNarration => Resources.LoadAll<AudioClip>(AudioFolder.TrimEnd('/')).Length > 0;

        /// <summary>The sample photo as a readable texture (the caller owns and destroys it), or null when the file is missing.</summary>
        public static Texture2D LoadSamplePhoto()
        {
            var asset = Resources.Load<TextAsset>(SamplePhoto);
            if (asset == null) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "TutorialSamplePhoto" };
            if (!texture.LoadImage(asset.bytes, false))
            {
                if (Application.isPlaying) Object.Destroy(texture);
                else Object.DestroyImmediate(texture);
                return null;
            }
            return texture;
        }

        /// <summary>The sample mask (SAM3's answer for the vase in the sample photo, see ASSETS.md): <see cref="SampleImageWidth"/> x <see cref="SampleImageHeight"/> bytes, non-zero inside the vase. Null when the file is missing.</summary>
        public static byte[] LoadSampleMask()
        {
            var asset = Resources.Load<TextAsset>(SampleMask);
            return asset != null ? asset.bytes : null;
        }

        /// <summary>The lamp as GLB bytes, or null when the file is missing.</summary>
        public static byte[] LoadSharedModel()
        {
            var asset = Resources.Load<TextAsset>(SharedModel);
            return asset != null ? asset.bytes : null;
        }

        /// <summary>The sample model as GLB bytes, or null when the file is missing.</summary>
        public static byte[] LoadSampleModel()
        {
            var asset = Resources.Load<TextAsset>(SampleModel);
            return asset != null ? asset.bytes : null;
        }
    }
}
