// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Oculus.Avatar2;
using Oculus.Avatar2.Experimental;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// On device, a walking remote avatar's legs did not animate. The received avatar shown while it
    /// walks (SbAvatarPrefab) runs the SDK's OvrAvatarAnimationBehavior, which loads its legs rig with
    /// Resources.Load("RTR00003"); the scripts-only sample import had no such resource, so every load threw
    /// "The Object you want to instantiate is null" (OvrAvatarAnimationBehavior.cs:856) and the Mecanim legs controller was
    /// never attached. These pin the wiring without the SDK's native library: the rig is in the project with its controller,
    /// the prefab runs the behaviour on the default rig, and a missing rig is one error and a disabled behaviour.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SdkLegsRigTests
    {
        private const string SbAvatarPrefabPath = "Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab";
        private GameObject _host, _broken;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            if (_broken != null) Object.DestroyImmediate(_broken);
        }

        private OvrAvatarAnimationBehavior NewBehaviour(bool customAnimationOnly = false)
        {
            _host = new GameObject("legs-host");
            var behaviour = _host.AddComponent<OvrAvatarAnimationBehavior>();
            typeof(OvrAvatarAnimationBehavior).GetField("_customAnimationOnly", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(behaviour, customAnimationOnly);
            return behaviour;
        }

        [Test]
        public void TheResourceNameIsTheOneTheSdkBehaviourLoads()
        {
            var behaviour = NewBehaviour();
            var field = typeof(OvrAvatarAnimationBehavior).GetField("_defaultRtRigPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "the SDK sample renamed its default rig field: re-check SdkLegsRig.ResourceName");
            Assert.That(field.GetValue(behaviour), Is.EqualTo(SdkLegsRig.ResourceName));
            Assert.That(typeof(OvrAvatarAnimationBehavior).GetField("_customAnimationOnly", BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null, "SdkLegsRig.UsesDefaultRig reads this field");
        }

        [Test]
        public void TheSdkLegsRigIsInTheProjectWithItsMecanimLegsController()
        {
            GameObject rig = SdkLegsRig.Load();
            Assert.That(SdkLegsRig.Verify(rig), Is.Null);
            Assert.That(AssetDatabase.GetAssetPath(rig), Is.EqualTo("Assets/SpatialBlend/Avatar/LegsRig/Resources/RTR00003.prefab"),
                "the committed copy, not a machine-local sample import (two RTR00003 resources would be ambiguous)");
            var controller = rig.GetComponent<Animator>().runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
            Assert.That(controller, Is.Not.Null);
            var names = controller.parameters.Select(p => p.name).ToArray();
            // The parameters MecanimLegsAnimationController.SendEvents writes from the head's motion.
            Assert.That(names, Is.SupersetOf(new[] { "IsMoving", "MoveForward", "MoveBackward", "MoveLeft", "MoveRight", "MoveSpeed",
                "TurnLeft", "TurnRight", "TurnStrength" }));
            Assert.That(controller.layers.Select(l => l.name), Has.Member("TurnInPlaceLayer"), "turn in place has its own layer");
            Assert.That(controller.animationClips.Any(c => c != null && c.name.Contains("walk")), Is.True, "the walk clips imported");
        }

        [Test]
        public void TheWalkingRemoteAvatarRunsTheSdkLegsBehaviourOnTheDefaultRig()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SbAvatarPrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var behaviour = prefab.GetComponent<OvrAvatarAnimationBehavior>();
            Assert.That(behaviour, Is.Not.Null);
            Assert.That(behaviour.enabled, Is.True);
            Assert.That(SdkLegsRig.UsesDefaultRig(behaviour), Is.True, "_customAnimationOnly off: the SDK loads Resources/RTR00003");
            var serialized = new SerializedObject(behaviour);
            Assert.That(serialized.FindProperty("_enableLocalAnimationPlayback").boolValue, Is.True,
                "the Sb avatar is a local-input entity: locomotion must play on it");

            var entity = prefab.GetComponent<SbAvatarEntity>();
            var joints = (CAPI.ovrAvatar2JointType[])typeof(OvrAvatarEntity)
                .GetField("_criticalJointTypes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entity);
            Assert.That(joints, Is.SupersetOf(new[] { CAPI.ovrAvatar2JointType.Hips, CAPI.ovrAvatar2JointType.LeftFootAnkle,
                CAPI.ovrAvatar2JointType.RightFootAnkle, CAPI.ovrAvatar2JointType.LeftFootBall, CAPI.ovrAvatar2JointType.RightFootBall }),
                "the walk is observable: the legs' joints have Transforms (AvatarPoseLogger sb_joints)");
        }

        [Test]
        public void AMissingRigDisablesTheBehaviourWithOneErrorInsteadOfTheSdkException()
        {
            var behaviour = NewBehaviour();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[SdkLegsRig\].*Resources/RTR00003 is not in the project.*vendor-meta-avatar-legs-rig"));
            Assert.That(SdkLegsRig.GuardBehaviour(behaviour, () => null), Is.False);
            Assert.That(behaviour.enabled, Is.False, "its Start never subscribes to the load events, so OnUserAvatarLoaded never throws");
        }

        [Test]
        public void ARigWithoutTheMecanimLegsControllerIsRefusedToo()
        {
            var behaviour = NewBehaviour();
            _broken = new GameObject("RTR00003");
            _broken.AddComponent<Animator>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[SdkLegsRig\].*no MecanimLegsAnimationController"));
            Assert.That(SdkLegsRig.GuardBehaviour(behaviour, () => _broken), Is.False);
            Assert.That(behaviour.enabled, Is.False);
        }

        [Test]
        public void TheInstalledRigLeavesTheBehaviourOn()
        {
            var behaviour = NewBehaviour();
            Assert.That(SdkLegsRig.GuardBehaviour(behaviour), Is.True);
            Assert.That(behaviour.enabled, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ACustomAnimationOnlyBehaviourOrADisabledOneIsNotTouched()
        {
            var custom = NewBehaviour(customAnimationOnly: true);
            Assert.That(SdkLegsRig.GuardBehaviour(custom, () => null), Is.True, "it never loads the default rig");
            Assert.That(custom.enabled, Is.True);
            custom.enabled = false;
            Assert.That(SdkLegsRig.GuardBehaviour(custom, () => null), Is.False);
            Assert.That(custom.enabled, Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ThePoseLogWritesNullLegJointsForAMissingAvatar()
        {
            var text = new StringBuilder();
            AvatarPoseLogger.AppendLegJoints(text, "sb_joints", null);
            Assert.That(text.ToString(), Is.EqualTo(",\"sb_joints\":null"));
        }
    }
}
#endif
