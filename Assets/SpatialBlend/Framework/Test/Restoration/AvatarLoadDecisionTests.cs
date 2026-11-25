// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Which avatar a player's bodies load, against v1. v1's spawner sets UsePresetAvatar
    /// true for every player, MetaAvatarEntity.LoadAvatar loads the
    /// preset when that flag is set or the player has no Oculus id, and SbAvatarEntity loads the
    /// preset when its behaviour sets the flag. abi4 deleted the flag, so an entitled player got their own
    /// Meta avatar from the CDN.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class AvatarLoadDecisionTests
    {
        [Test]
        public void EverySpawnedPlayerUsesThePresetAvatar()
        {
            Assert.That(AvatarLoadDecision.ChooseForSpawn(true, 3, 6, (lo, hi) => lo).UsePresetAvatar, Is.True);
            Assert.That(AvatarLoadDecision.ChooseForSpawn(false, 0, 6, (lo, hi) => lo).UsePresetAvatar, Is.True);
        }

        [Test]
        public void ThePanelsSelectedPresetIsTheIndexAndTheToggleDoesNotEnterIntoIt()
        {
            int randomCalls = 0;
            AvatarLoadDecision.SpawnChoice choice = AvatarLoadDecision.ChooseForSpawn(
                true, 4, 6, (lo, hi) => { ++randomCalls; return 0; });
            Assert.That(choice.LocalAvatarIndex, Is.EqualTo(4));
            Assert.That(randomCalls, Is.EqualTo(0), "A panel's choice is not replaced by a random one.");
        }

        [Test]
        public void WithoutAPanelThePresetIsDrawnFromZeroToCountMinusOneExclusive()
        {
            int lowest = -1, highest = -1;
            AvatarLoadDecision.SpawnChoice choice = AvatarLoadDecision.ChooseForSpawn(
                false, 99, 6, (lo, hi) => { lowest = lo; highest = hi; return 2; });
            Assert.That(choice.LocalAvatarIndex, Is.EqualTo(2));
            Assert.That(lowest, Is.EqualTo(0));
            Assert.That(highest, Is.EqualTo(5), "v1's range: Random.Range(0, 6 - 1), so the last preset is never drawn.");
        }

        [Test]
        public void TheMetaAvatarLoadsThePresetWhenTheFlagIsSetWhoeverTheUserIs()
        {
            // The case that regressed: an entitled player, an Oculus id, and the flag set.
            Assert.That(AvatarLoadDecision.MetaAvatarLoadsPreset(true, 1234567890UL), Is.True);
            Assert.That(AvatarLoadDecision.MetaAvatarLoadsPreset(true, 0UL), Is.True);
        }

        [Test]
        public void TheMetaAvatarLoadsTheUsersOwnOnlyWhenTheFlagIsClearAndThereIsAnId()
        {
            Assert.That(AvatarLoadDecision.MetaAvatarLoadsPreset(false, 1234567890UL), Is.False);
            Assert.That(AvatarLoadDecision.MetaAvatarLoadsPreset(false, 0UL), Is.True, "No id, so there is no user avatar to load.");
        }

        [Test]
        public void TheSbAvatarLoadsThePresetOnlyWhenItsBehaviourIsKnownAndSetsTheFlag()
        {
            Assert.That(AvatarLoadDecision.SbAvatarLoadsPreset(true, true), Is.True);
            Assert.That(AvatarLoadDecision.SbAvatarLoadsPreset(true, false), Is.False);
            Assert.That(AvatarLoadDecision.SbAvatarLoadsPreset(false, true), Is.False);
        }

        [Test]
        public void ThePresetFlagIsNetworkedOnTheBehaviourSoEveryPeerLoadsTheSameAvatar()
        {
            // v1 networks it next to LocalAvatarIndex on SbAvatarBehaviourFusion, and a change reloads the avatar.
            System.Reflection.PropertyInfo flag = typeof(SbAvatarBehaviourFusion).GetProperty("UsePresetAvatar");
            Assert.That(flag, Is.Not.Null);
            Assert.That(flag.PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(flag.GetCustomAttributes(typeof(Fusion.NetworkedAttribute), false), Is.Not.Empty);
        }
    }
}
#endif
