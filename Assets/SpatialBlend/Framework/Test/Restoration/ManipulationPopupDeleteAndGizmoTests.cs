// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The unimplemented gizmo toggle is not offered, and delete
    /// goes through the unregister path before the object is destroyed.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ManipulationPopupDeleteAndGizmoTests
    {
        [Test]
        public void TheGizmoToggleIsHiddenWhenThePopupEnables()
        {
            var popupObject = new GameObject("popup");
            try
            {
                var popup = popupObject.AddComponent<ManipulationPopUp>();
                Toggle gizmo = MakeToggle(popupObject, "Gizmo");
                Toggle region = MakeToggle(popupObject, "Region");
                typeof(ManipulationPopUp).GetField("_gizmoToggle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(popup, gizmo);
                typeof(ManipulationPopUp).GetField("_affordanceVolumeToggle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(popup, region);
                Assert.That(gizmo.gameObject.activeSelf, Is.True);

                typeof(ManipulationPopUp).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(popup, null);

                Assert.That(gizmo.gameObject.activeSelf, Is.False, "the unimplemented mode is not offered");
                Assert.That(region.gameObject.activeSelf, Is.True, "the other modes are");
            }
            finally { UnityEngine.Object.DestroyImmediate(popupObject); }
        }

        [Test]
        public void DeleteUnregistersTheSharedEntityBeforeTheDeferredDestroy()
        {
            var managerObject = new GameObject("manager");
            var targetObject = new GameObject("shared-object");
            var contextObject = new GameObject("context");
            try
            {
                var manager = managerObject.AddComponent<SpatialBlendManager>();
                targetObject.AddComponent<BoxCollider>(); // Attach sets up ISDK interaction and wants a collider
                var entity = targetObject.AddComponent<SbLocalEntity>();
                typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(entity, new object[] { manager, 101L, true });
                Assert.That(manager.GetEntity(101L), Is.SameAs(entity));
                Assert.That(entity.IsAttached(), Is.True);

                var context = contextObject.AddComponent<ManipulationContext>();
                typeof(ManipulationContext).GetField("_targetObject", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, targetObject);
                typeof(ManipulationContext).GetField("_targetSbEntity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, entity);

                // The deferred Destroy() is refused in edit mode; the log is expected and the test is about what happens first.
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Destroy may not be called from edit mode"));
                typeof(ManipulationContext).GetMethod("ExecuteDeleteMode", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(context, null);

                // Destroy lands at the end of the frame; the registration is withdrawn now.
                Assert.That(entity.IsAttached(), Is.False, "the entity is unregistered by the delete itself");
                Assert.That(manager.GetEntity(101L), Is.Null, "the manager no longer lists it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(contextObject);
                UnityEngine.Object.DestroyImmediate(targetObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        private static Toggle MakeToggle(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go.AddComponent<Toggle>();
        }
    }
}
#endif
