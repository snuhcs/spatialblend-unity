// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// The SDK legs rig (Resources/RTR00003, installed under the git-ignored Assets/SpatialBlend/Avatar/LegsRig by tools/setup_sdks.py) is what makes a
    /// received avatar walk: without it OvrAvatarAnimationBehavior throws on load and the legs stay frozen while the body
    /// slides. This guard fails the player build instead of producing that app.
    /// </summary>
    public sealed class SdkLegsRigBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -999;

        public void OnPreprocessBuild(BuildReport report)
        {
            var problem = SdkLegsRig.Verify(SdkLegsRig.Load());
            if (problem != null)
            {
                throw new BuildFailedException("[SdkLegsRigBuildGuard] " + problem);
            }
        }
    }
}
