// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend
{
    /// <summary>
    /// Which avatar a player's bodies load. Every rule is v1's: the spawner sets
    /// <c>UsePresetAvatar</c> true for every player ("Always use preset avatar" in
    /// v1 SbAvatarSpawnerFusion), and both avatars read it. A player's own Meta avatar from the
    /// CDN loads only when that flag is false and the player has an Oculus id.
    /// </summary>
    public static class AvatarLoadDecision
    {
        /// <summary>The spawner's choice for the local player (as v1 SbAvatarSpawnerFusion).</summary>
        public readonly struct SpawnChoice
        {
            public readonly bool UsePresetAvatar;
            public readonly int LocalAvatarIndex;

            public SpawnChoice(bool usePresetAvatar, int localAvatarIndex)
            {
                UsePresetAvatar = usePresetAvatar;
                LocalAvatarIndex = localAvatarIndex;
            }
        }

        /// <summary>
        /// v1: the preset flag is true, and the index is the control panel's selected preset, or a random
        /// preset when there is no panel. The random range is v1's, (0, presetCount - 1) with an exclusive
        /// upper bound, so the last preset is never drawn. The panel's "Use preset" toggle does not enter into it.
        /// </summary>
        /// <param name="randomRange">Random.Range(int, int): the lower bound inclusive, the upper exclusive.</param>
        public static SpawnChoice ChooseForSpawn(bool hasControlPanel, int selectedPreset, int presetCount,
                                                 Func<int, int, int> randomRange)
        {
            int index = hasControlPanel ? selectedPreset : randomRange(0, presetCount - 1);
            return new SpawnChoice(true, index);
        }

        /// <summary>v1 MetaAvatarEntity.LoadAvatar: the preset loads when the flag is set or there is no Oculus id.</summary>
        public static bool MetaAvatarLoadsPreset(bool usePresetAvatar, ulong oculusId) =>
            usePresetAvatar || oculusId == 0;

        /// <summary>
        /// v1 SbAvatarEntity: the preset loads when the avatar's behaviour is known and sets the flag.
        /// Otherwise the entity's own configuration decides (the CDN user, or the local avatar).
        /// </summary>
        public static bool SbAvatarLoadsPreset(bool hasBehaviour, bool usePresetAvatar) =>
            hasBehaviour && usePresetAvatar;
    }
}
