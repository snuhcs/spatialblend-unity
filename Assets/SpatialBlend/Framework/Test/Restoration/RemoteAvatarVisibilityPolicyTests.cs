// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Expected rows are the observable result of v1 SbAvatarBehaviourFusion.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RemoteAvatarVisibilityPolicyTests
    {
        private const float Fade = 1.5f;

        [Test]
        public void HideClocksMatchV1SpawnAndUserAvatarLoaded()
        {
            // As v1 Spawned: a spawned avatar is hidden for 3 s.
            Assert.That(RemoteAvatarVisibilityPolicy.SpawnedHideSeconds, Is.EqualTo(3f));
            // As v1 OnUserAvatarLoaded: a loaded user avatar is hidden for 5 s.
            Assert.That(RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds, Is.EqualTo(5f));
        }

        [Test]
        public void LocalAvatar_NeverShowsMeta_AndDoesNotStartAFade()
        {
            // As v1: input authority returns before the remote visibility rules, and the local Meta avatar is hidden.
            var modes = new[] { AvatarDisplayMode.Default, AvatarDisplayMode.ZoneBased };
            var flags = new[] { false, true };
            foreach (AvatarDisplayMode mode in modes)
            {
                foreach (bool interacting in flags)
                {
                    foreach (bool hidden in flags)
                    {
                        var decision = Evaluate(mode, interacting, isLocal: true,
                            timeSinceSpawn: hidden ? 0f : 10f,
                            spawnHideSeconds: RemoteAvatarVisibilityPolicy.SpawnedHideSeconds,
                            fadeDuration: Fade,
                            fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
                        Assert.That(decision.ApplyRemoteRules, Is.False, decision.ToString());
                        Assert.That(decision.SbVisible, Is.False, decision.ToString());
                        Assert.That(decision.MetaVisible, Is.False, decision.ToString());
                        Assert.That(decision.MetaAlpha, Is.EqualTo(0f), decision.ToString());
                        Assert.That(decision.StartFade, Is.False, decision.ToString());
                    }
                }
            }
        }

        [Test]
        public void SpawnHideWindow_HidesBothAvatars_AcrossTheMatrix()
        {
            // As v1: both avatars are forced hidden until the clock elapses, and only then do the remote visibility
            // rules apply, so no fade starts inside the window.
            var modes = new[] { AvatarDisplayMode.Default, AvatarDisplayMode.ZoneBased };
            var flags = new[] { false, true };
            float[] hideClocks =
            {
                RemoteAvatarVisibilityPolicy.SpawnedHideSeconds,
                RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds,
            };
            foreach (float hideSeconds in hideClocks)
            {
                foreach (AvatarDisplayMode mode in modes)
                {
                    foreach (bool interacting in flags)
                    {
                        foreach (float fadeDuration in new[] { 0f, Fade })
                        {
                            var inside = Evaluate(mode, interacting, isLocal: false,
                                timeSinceSpawn: 0f, spawnHideSeconds: hideSeconds,
                                fadeDuration: fadeDuration,
                                fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
                            AssertHidden(inside, "t=0 hide=" + hideSeconds);

                            var stillInside = Evaluate(mode, interacting, isLocal: false,
                                timeSinceSpawn: hideSeconds - 0.01f, spawnHideSeconds: hideSeconds,
                                fadeDuration: fadeDuration, fadeProgress: 0.5f);
                            AssertHidden(stillInside, "just inside hide=" + hideSeconds);
                        }
                    }
                }
            }
        }

        [Test]
        public void SpawnHideWindow_EndsAtTheClock_ThenDefaultApplies()
        {
            // As v1, the window is elapsed < duration, so t == 3s and t == 5s are outside it.
            // Default, not interacting, then shows Sb and hides Meta.
            var atThree = Evaluate(AvatarDisplayMode.Default, interacting: false, isLocal: false,
                timeSinceSpawn: RemoteAvatarVisibilityPolicy.SpawnedHideSeconds,
                spawnHideSeconds: RemoteAvatarVisibilityPolicy.SpawnedHideSeconds,
                fadeDuration: Fade, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            AssertInstant(atThree, sb: true, meta: false, "window boundary then Default");

            var atFive = Evaluate(AvatarDisplayMode.Default, interacting: false, isLocal: false,
                timeSinceSpawn: RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds,
                spawnHideSeconds: RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds,
                fadeDuration: 0f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            AssertInstant(atFive, sb: true, meta: false, "window boundary then Default");
        }

        [Test]
        public void Default_OutsideSpawnWindow_SwapsSbAndMeta_AndIgnoresFade()
        {
            // As v1, Default swaps Sb and Meta on interaction. Fade duration and progress are not read on this path.
            AssertInstant(Evaluate(AvatarDisplayMode.Default, false, false, 10f, 0f, 0f, NoFade()),
                sb: true, meta: false, "Default not interacting, fade 0");
            AssertInstant(Evaluate(AvatarDisplayMode.Default, true, false, 10f, 0f, 0f, NoFade()),
                sb: false, meta: true, "Default interacting, fade 0");
            AssertInstant(Evaluate(AvatarDisplayMode.Default, false, false, 10f, 0f, Fade, 0.5f),
                sb: true, meta: false, "Default not interacting, fade ignored");
            AssertInstant(Evaluate(AvatarDisplayMode.Default, true, false, 10f, 0f, Fade, 0.5f),
                sb: false, meta: true, "Default interacting, fade ignored");
        }

        [Test]
        public void ZoneBased_ZeroFade_HidesSb_AndSnapsMetaToInteraction()
        {
            // As v1, ZoneBased hides Sb and snaps Meta with no fade when the duration is 0.
            AssertInstant(Evaluate(AvatarDisplayMode.ZoneBased, false, false, 10f, 0f, 0f, 0.5f),
                sb: false, meta: false, "zero fade, not interacting");
            AssertInstant(Evaluate(AvatarDisplayMode.ZoneBased, true, false, 10f, 0f, 0f, NoFade()),
                sb: false, meta: true, "zero fade, interacting");
        }

        [Test]
        public void ZoneBased_PositiveFade_StartsFade_WithoutSnappingMeta()
        {
            // As v1, a positive duration starts FadeAvatarTransition and returns before Meta is snapped;
            // Sb is already hidden.
            var hidden = Evaluate(AvatarDisplayMode.ZoneBased, false, false, 10f, 0f, Fade, NoFade());
            AssertBeganFade(hidden, showMeta: false, "fade out");
            var shown = Evaluate(AvatarDisplayMode.ZoneBased, true, false, 10f, 0f, Fade, NoFade());
            AssertBeganFade(shown, showMeta: true, "fade in");
        }

        [Test]
        public void ZoneBased_FadeSamples_MatchV1AlphaAndFinalSnap()
        {
            // As v1: alpha = showMeta ? t : 1 - t, and the end snaps the alpha and calls MakeVisibleAvatar.
            AssertFadeSample(interacting: true, progress: 0f, alpha: 0f, metaVisible: false, finished: false);
            AssertFadeSample(interacting: true, progress: 0.5f, alpha: 0.5f, metaVisible: true, finished: false);
            AssertFadeSample(interacting: true, progress: 1f, alpha: 1f, metaVisible: true, finished: true);
            AssertFadeSample(interacting: false, progress: 0f, alpha: 1f, metaVisible: true, finished: false);
            AssertFadeSample(interacting: false, progress: 0.5f, alpha: 0.5f, metaVisible: true, finished: false);
            AssertFadeSample(interacting: false, progress: 1f, alpha: 0f, metaVisible: false, finished: true);
        }

        private static void AssertFadeSample(bool interacting, float progress, float alpha, bool metaVisible, bool finished)
        {
            var decision = Evaluate(AvatarDisplayMode.ZoneBased, interacting, false, 10f, 0f, Fade, progress);
            string label = "fade progress " + progress + " interacting=" + interacting;
            Assert.That(decision.StartFade, Is.False, label);
            Assert.That(decision.SbVisible, Is.False, label);
            Assert.That(decision.ApplySbVisibility, Is.True, label);
            Assert.That(decision.MetaAlpha, Is.EqualTo(alpha).Within(0.0001f), label);
            Assert.That(decision.ApplyMetaAlpha, Is.True, label);
            Assert.That(decision.MetaVisible, Is.EqualTo(metaVisible), label);
            Assert.That(decision.ApplyMetaVisibility, Is.EqualTo(finished), label);
            Assert.That(decision.FadeShowsMeta, Is.EqualTo(interacting), label);
        }

        private static void AssertBeganFade(RemoteAvatarVisibilityDecision decision, bool showMeta, string label)
        {
            Assert.That(decision.ApplyRemoteRules, Is.True, label);
            Assert.That(decision.SbVisible, Is.False, label);
            Assert.That(decision.ApplySbVisibility, Is.True, label);
            Assert.That(decision.StartFade, Is.True, label);
            Assert.That(decision.FadeShowsMeta, Is.EqualTo(showMeta), label);
            Assert.That(decision.ApplyMetaVisibility, Is.False, label);
            Assert.That(decision.ApplyMetaAlpha, Is.False, label);
        }

        private static void AssertInstant(RemoteAvatarVisibilityDecision decision, bool sb, bool meta, string label)
        {
            Assert.That(decision.ApplyRemoteRules, Is.True, label);
            Assert.That(decision.SbVisible, Is.EqualTo(sb), label);
            Assert.That(decision.MetaVisible, Is.EqualTo(meta), label);
            Assert.That(decision.MetaAlpha, Is.EqualTo(meta ? 1f : 0f), label);
            Assert.That(decision.ApplySbVisibility, Is.True, label);
            Assert.That(decision.ApplyMetaVisibility, Is.True, label);
            Assert.That(decision.ApplyMetaAlpha, Is.False, label);
            Assert.That(decision.StartFade, Is.False, label);
        }

        private static void AssertHidden(RemoteAvatarVisibilityDecision decision, string label)
        {
            Assert.That(decision.ApplyRemoteRules, Is.True, label);
            Assert.That(decision.SbVisible, Is.False, label);
            Assert.That(decision.MetaVisible, Is.False, label);
            Assert.That(decision.MetaAlpha, Is.EqualTo(0f), label);
            Assert.That(decision.ApplyMetaVisibility, Is.True, label);
            Assert.That(decision.StartFade, Is.False, label);
        }

        private static float NoFade()
        {
            return RemoteAvatarVisibilityPolicy.NoFadeSample;
        }

        private static RemoteAvatarVisibilityDecision Evaluate(
            AvatarDisplayMode mode,
            bool interacting,
            bool isLocal,
            float timeSinceSpawn,
            float spawnHideSeconds,
            float fadeDuration,
            float fadeProgress)
        {
            var input = new RemoteAvatarVisibilityInput(
                mode, interacting, isLocal, timeSinceSpawn, spawnHideSeconds, fadeDuration, fadeProgress);
            return RemoteAvatarVisibilityPolicy.Evaluate(input);
        }
    }
}
#endif
