// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    /// <summary>
    /// Pure remote-avatar visibility rules from v1
    /// <c>SbAvatarBehaviourFusion.ApplyRemoteAvatarVisibility</c>.
    /// No Meta, Fusion, or Unity engine types.
    /// </summary>
    public static class RemoteAvatarVisibilityPolicy
    {
        /// <summary>As v1 Spawned(): remote avatars stay hidden for 3 seconds.</summary>
        public const float SpawnedHideSeconds = 3f;

        /// <summary>As v1 OnUserAvatarLoaded(): a second hide runs for 5 seconds.</summary>
        public const float UserAvatarLoadedHideSeconds = 5f;

        /// <summary>Pass as fade progress when ApplyRemoteAvatarVisibility is deciding, not sampling a fade.</summary>
        public const float NoFadeSample = -1f;

        public static RemoteAvatarVisibilityDecision Evaluate(in RemoteAvatarVisibilityInput input)
        {
            if (input.IsLocal)
            {
                // As v1: return before changing remote visibility.
                // As v1: hide the local Meta avatar and never show it.
                return RemoteAvatarVisibilityDecision.LocalHidden();
            }

            if (input.SpawnHideSeconds > 0f && input.TimeSinceSpawnSeconds < input.SpawnHideSeconds)
            {
                // As v1 KeepAvatarsInvisibleThenShow, until it calls Apply.
                return RemoteAvatarVisibilityDecision.SpawnHidden();
            }

            if (input.DisplayMode == AvatarDisplayMode.ZoneBased)
            {
                if (input.FadeDurationSeconds > 0f)
                {
                    if (input.FadeProgress < 0f)
                    {
                        // As v1: Sb is hidden, then the fade starts and returns
                        // before Meta visibility is snapped.
                        return RemoteAvatarVisibilityDecision.BeginZoneFade(input.Interacting);
                    }

                    // As v1 FadeAvatarTransition.
                    return RemoteAvatarVisibilityDecision.SampleZoneFade(input.Interacting, input.FadeProgress);
                }

                // As v1: duration 0 snaps Meta to the interaction flag.
                return RemoteAvatarVisibilityDecision.Instant(sbVisible: false, metaVisible: input.Interacting);
            }

            // As v1: Default ignores fade duration and fade progress.
            return RemoteAvatarVisibilityDecision.Instant(
                sbVisible: !input.Interacting,
                metaVisible: input.Interacting);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }

        internal static float ZoneFadeAlpha(bool showMeta, float progress)
        {
            float t = Clamp01(progress);
            return showMeta ? t : 1f - t;
        }
    }

    public readonly struct RemoteAvatarVisibilityInput
    {
        public RemoteAvatarVisibilityInput(
            AvatarDisplayMode displayMode,
            bool interacting,
            bool isLocal,
            float timeSinceSpawnSeconds,
            float spawnHideSeconds,
            float fadeDurationSeconds,
            float fadeProgress)
        {
            DisplayMode = displayMode;
            Interacting = interacting;
            IsLocal = isLocal;
            TimeSinceSpawnSeconds = timeSinceSpawnSeconds;
            SpawnHideSeconds = spawnHideSeconds;
            FadeDurationSeconds = fadeDurationSeconds;
            FadeProgress = fadeProgress;
        }

        public AvatarDisplayMode DisplayMode { get; }
        public bool Interacting { get; }
        public bool IsLocal { get; }
        public float TimeSinceSpawnSeconds { get; }
        public float SpawnHideSeconds { get; }
        public float FadeDurationSeconds { get; }
        public float FadeProgress { get; }
    }

    public readonly struct RemoteAvatarVisibilityDecision
    {
        private RemoteAvatarVisibilityDecision(
            bool applyRemoteRules,
            bool sbVisible,
            bool applySbVisibility,
            bool metaVisible,
            float metaAlpha,
            bool applyMetaVisibility,
            bool applyMetaAlpha,
            bool startFade,
            bool fadeShowsMeta)
        {
            ApplyRemoteRules = applyRemoteRules;
            SbVisible = sbVisible;
            ApplySbVisibility = applySbVisibility;
            MetaVisible = metaVisible;
            MetaAlpha = metaAlpha;
            ApplyMetaVisibility = applyMetaVisibility;
            ApplyMetaAlpha = applyMetaAlpha;
            StartFade = startFade;
            FadeShowsMeta = fadeShowsMeta;
        }

        public bool ApplyRemoteRules { get; }
        public bool SbVisible { get; }
        public bool ApplySbVisibility { get; }
        public bool MetaVisible { get; }
        public float MetaAlpha { get; }
        public bool ApplyMetaVisibility { get; }
        public bool ApplyMetaAlpha { get; }
        public bool StartFade { get; }
        public bool FadeShowsMeta { get; }

        public static RemoteAvatarVisibilityDecision LocalHidden()
        {
            return new RemoteAvatarVisibilityDecision(
                applyRemoteRules: false,
                sbVisible: false,
                applySbVisibility: false,
                metaVisible: false,
                metaAlpha: 0f,
                applyMetaVisibility: true,
                applyMetaAlpha: false,
                startFade: false,
                fadeShowsMeta: false);
        }

        public static RemoteAvatarVisibilityDecision SpawnHidden()
        {
            return new RemoteAvatarVisibilityDecision(
                applyRemoteRules: true,
                sbVisible: false,
                applySbVisibility: true,
                metaVisible: false,
                metaAlpha: 0f,
                applyMetaVisibility: true,
                applyMetaAlpha: false,
                startFade: false,
                fadeShowsMeta: false);
        }

        public static RemoteAvatarVisibilityDecision Instant(bool sbVisible, bool metaVisible)
        {
            return new RemoteAvatarVisibilityDecision(
                applyRemoteRules: true,
                sbVisible: sbVisible,
                applySbVisibility: true,
                metaVisible: metaVisible,
                metaAlpha: metaVisible ? 1f : 0f,
                applyMetaVisibility: true,
                applyMetaAlpha: false,
                startFade: false,
                fadeShowsMeta: false);
        }

        public static RemoteAvatarVisibilityDecision BeginZoneFade(bool showMeta)
        {
            return new RemoteAvatarVisibilityDecision(
                applyRemoteRules: true,
                sbVisible: false,
                applySbVisibility: true,
                metaVisible: false,
                metaAlpha: 0f,
                applyMetaVisibility: false,
                applyMetaAlpha: false,
                startFade: true,
                fadeShowsMeta: showMeta);
        }

        public static RemoteAvatarVisibilityDecision SampleZoneFade(bool showMeta, float progress)
        {
            float alpha = RemoteAvatarVisibilityPolicy.ZoneFadeAlpha(showMeta, progress);
            bool finished = progress >= 1f;
            bool metaVisible = finished ? showMeta : alpha > 0f;
            return new RemoteAvatarVisibilityDecision(
                applyRemoteRules: true,
                sbVisible: false,
                applySbVisibility: true,
                metaVisible: metaVisible,
                metaAlpha: finished ? (showMeta ? 1f : 0f) : alpha,
                applyMetaVisibility: finished,
                applyMetaAlpha: true,
                startFade: false,
                fadeShowsMeta: showMeta);
        }

        public override string ToString()
        {
            return "sb=" + SbVisible
                + " meta=" + MetaVisible
                + " alpha=" + MetaAlpha
                + " startFade=" + StartFade
                + " applyRemote=" + ApplyRemoteRules
                + " applyMeta=" + ApplyMetaVisibility
                + " applyAlpha=" + ApplyMetaAlpha;
        }
    }
}
