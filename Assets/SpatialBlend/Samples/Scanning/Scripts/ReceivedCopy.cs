// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// Marks a copy of a peer's object that arrived in this room and gives it the one menu a receiver may use: who it is from, and
    /// "Hide for me". Delete stays with the owner: a copy that is not yours is not removed from the room, only taken out of your view.
    /// A hidden copy keeps receiving the owner's updates, so it stays where the owner puts it, only unseen and unreachable. The mark is local
    /// and lasts until the room is left; nothing is sent.
    /// </summary>
    public sealed class ReceivedCopy : MonoBehaviour
    {
        public const string HiddenTitle = "Received object";
        public const string HideForMeLabel = "Hide for me";
        /// <summary>What the owner line says while the transport does not tell who sent the object.</summary>
        public const string UnknownOwnerLine = "Shared by another user";

        private SbNativeEntity _entity;

        /// <summary>The owner's name once the room knows it (the ownership work of the edit-lock design), or null.</summary>
        public string OwnerName { get; private set; }

        public bool HiddenForMe { get; private set; }

        /// <summary>"Owner: Jiwon", or the unknown-owner wording.</summary>
        public string OwnerLine => string.IsNullOrWhiteSpace(OwnerName) ? UnknownOwnerLine : "Owner: " + OwnerName.Trim();

        /// <summary>
        /// Gives a received object its menu: marks it, and adds the A-button panel the user's own scans have. Returns the mark, or null for
        /// no object. A second call only updates the owner.
        /// </summary>
        public static ReceivedCopy Attach(GameObject root, string ownerName = null)
        {
            if (root == null) return null;
            if (!root.TryGetComponent(out ReceivedCopy copy)) copy = root.AddComponent<ReceivedCopy>();
            copy.SetOwner(ownerName);
            copy.Watch();   // Awake does it too; a copy attached after Awake (and EditMode, which has none) is watched at once
            ManipulationPanelMotif.Setup(root);
            // The share dropped this scan on the floor. The hover Edit prompt offers to place it until the user does.
            if (root.GetComponent<SbNativeEntity>() is { IsRemoteReplica: true })
            {
                if (root.GetComponent<UnplacedSharedScan>() == null) root.AddComponent<UnplacedSharedScan>();
                if (root.GetComponent<PlaceCalloutView>() == null) root.AddComponent<PlaceCalloutView>();
            }
            return copy;
        }

        /// <summary>Records who the object is from, once that is known. A blank name leaves the owner as it was.</summary>
        public void SetOwner(string ownerName)
        {
            if (!string.IsNullOrWhiteSpace(ownerName)) OwnerName = ownerName;
        }

        /// <summary>True for an object that arrived from a peer and has been given the receiver's menu.</summary>
        public static bool Is(GameObject sceneObject) => sceneObject != null && sceneObject.GetComponent<ReceivedCopy>() != null;

        private void Awake() => Watch();

        private void OnDestroy()
        {
            if (_entity != null) _entity.ContentReady -= OnContentReady;
        }

        private void Watch()
        {
            if (_entity != null) return;
            _entity = GetComponent<SbNativeEntity>();
            if (_entity != null) _entity.ContentReady += OnContentReady;
        }

        private void OnContentReady(SbNativeEntity entity)
        {
            // The copy is made before its model loads: its interactable may only exist now.
            var panel = GetComponent<ManipulationPanelMotif>();
            if (panel != null) panel.RebindInteractable();
            if (HiddenForMe) Apply();   // a reload of the owner's content brings new renderers
        }

        /// <summary>Takes the copy out of this user's view: not drawn, not hit by the ray. Local only; the owner's copy is untouched.</summary>
        public void HideForMe()
        {
            Watch();
            HiddenForMe = true;
            Apply();
        }

        private void Apply()
        {
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (Collider collider in GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            // The grounding shadow lies at the scene root (it is not a child): it is hidden with the copy.
            var shadow = GetComponent<SpatialBlend.Samples.Objects.GroundingShadowMotif>();
            if (shadow != null) shadow.SetHidden(true);
        }
    }
}
