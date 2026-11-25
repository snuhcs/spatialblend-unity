// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class DeclaredRegionZoneTests
    {
        [Test]
        public void SwitchFollowsTheCoresValueInstantlyWithNoHysteresis()
        {
            var zone = new DeclaredRegionZone.Switch();
            Assert.That(zone.Set(false), Is.False);
            Assert.That(zone.Inside, Is.False);
            Assert.That(zone.Set(true), Is.True);
            Assert.That(zone.Inside, Is.True);
            Assert.That(zone.Set(true), Is.False, "No change, no event.");
            Assert.That(zone.Set(false), Is.True);
            Assert.That(zone.Inside, Is.False);
        }

        [Test]
        public void SwitchFollowsEveryChangeWithoutDebounce()
        {
            var zone = new DeclaredRegionZone.Switch();
            int flips = 0;
            for (int step = 0; step < 6; ++step)
            {
                if (zone.Set(step % 2 == 0)) flips++;
            }
            Assert.That(flips, Is.EqualTo(6));
        }

        [Test]
        public void SwitchResetClearsTheFlag()
        {
            var zone = new DeclaredRegionZone.Switch();
            zone.Set(true);
            zone.Reset();
            Assert.That(zone.Inside, Is.False);
        }
    }
}
#endif
