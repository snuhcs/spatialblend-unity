// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// A real object the other user shared that this room has no free anchor of its label for: it arrives as a
    /// box with the sharer's label and measured size, in front of the user, outlined in the real-anchor colour, and the user moves it
    /// (the ordinary placement motif, <see cref="SbMRUKAnchorEntity.MakePlaceable"/>) onto the real object it stands for and confirms
    /// with <see cref="Place"/>. Only then is it this room's counterpart: until placed it is a shared referent at an arbitrary
    /// pose, and a seat is not yet this user's seat for sit transfer.
    /// </summary>
    public sealed class SharedRoomObjectBox : MonoBehaviour
    {
        /// <summary>Distance in front of the user the box's centre arrives at, metres. A product choice, not derived: within arm's reach of a standing user, so the box can be grabbed and dragged along the ray at once.</summary>
        public const float ArrivalDistance = 1.0f;

        private SbMRUKAnchorEntity _entity;
        private GameObject _outline;
        private bool _placed;
        private Pose _placedPose;

        public SbMRUKAnchorEntity Entity => _entity;
        public string Label => _entity != null ? _entity.SceneLabel : string.Empty;
        public bool IsPlaced => _placed;
        public Vector3 Size { get; private set; }
        public bool IsSeat => _entity != null && _entity.IsSeat;
        /// <summary>The seat key the box was created with, kept so it is still known when the box is destroyed with its entity.</summary>
        public string SeatKey { get; private set; } = string.Empty;

        /// <summary>Raised once the user placed the box (and again if they place it somewhere else).</summary>
        public event Action<SharedRoomObjectBox> Placed;

        /// <summary>Raised when the box is destroyed, whoever destroyed it (the core removing the pair, leaving, or the object menu's Delete).</summary>
        public event Action<SharedRoomObjectBox> Destroyed;

        private void OnDestroy() => Destroyed?.Invoke(this);

        /// <summary>Where the box arrives: where the user is, looking along their flat forward, on the floor. The pose frame is the anchor's (local +Z up).</summary>
        public static Pose ArrivalPose(Vector3 eye, Vector3 forward, float floorY, Vector3 boxCenter, Vector3 boxSize)
        {
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            // The anchor's frame stands local +Z up (Rx(-90)); the box rests on the floor, whatever its volume's offset from the origin.
            float bottomLocalZ = boxCenter.z - boxSize.z * 0.5f;
            // The box faces the user: the anchor's local +X is turned to the user's right.
            Quaternion rotation = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(-90f, 0f, 0f);
            // The box's centre (not the anchor's origin) is the point ArrivalDistance ahead.
            Vector3 centreOffset = rotation * new Vector3(boxCenter.x, boxCenter.y, 0f);
            Vector3 position = eye + flat * ArrivalDistance - new Vector3(centreOffset.x, 0f, centreOffset.z);
            position.y = floorY - bottomLocalZ;
            return new Pose(position, rotation);
        }

        public static SharedRoomObjectBox Create(SbMRUKAnchorEntity entity, Pose pose)
        {
            if (entity == null || !entity.TryGetMeasuredBox(out _, out Vector3 size)) return null;
            entity.transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (!entity.ApplyReplicaBox()) return null;
            entity.MakePlaceable();
            var box = entity.gameObject.GetComponent<SharedRoomObjectBox>() ?? entity.gameObject.AddComponent<SharedRoomObjectBox>();
            box._entity = entity;
            box.Size = size;
            box.SeatKey = entity.IsSeat ? entity.SeatKey ?? string.Empty : string.Empty;
            box.BuildPendingOutline();
            return box;
        }

        /// <summary>The user confirms the box stands where the real object is. The pending outline goes and the box is the counterpart.</summary>
        public void Place()
        {
            if (_entity == null) return;
            _placed = true;
            _placedPose = new Pose(_entity.transform.position, _entity.transform.rotation);
            if (_outline != null) DestroyObject(_outline);
            _outline = null;
            Placed?.Invoke(this);
        }

        private void Update() => Tick();

        /// <summary>A placed box the user moves again is no longer placed: it shows its outline and can be placed anew.</summary>
        internal void Tick()
        {
            if (!_placed || _entity == null) return;
            Transform t = _entity.transform;
            if ((t.position - _placedPose.position).sqrMagnitude < 1e-6f && Quaternion.Angle(t.rotation, _placedPose.rotation) < 0.1f) return;
            _placed = false;
            BuildPendingOutline();
        }

        /// <summary>Turns the box a quarter turn about the vertical, the way a user turns a thing they carry (the placement motif moves, it does not turn).</summary>
        public void RotateQuarterTurn()
        {
            if (_entity == null) return;
            _entity.transform.rotation = Quaternion.AngleAxis(90f, Vector3.up) * _entity.transform.rotation;
            Tick();
        }

        private void BuildPendingOutline()
        {
            if (_outline != null) return;
            _entity.TryGetMeasuredBox(out Vector3 center, out Vector3 size);
            _outline = SceneAnchorHighlight.Build(transform, center, size);
            _outline.name = "SharedBoxOutline";
            // A receiver's unmatched box draws no outline: the policy is applied to the outline as it is built, not every frame.
            if (_entity.ReceiverBoxPresentationHidden) _entity.RefreshReceiverBoxPresentation();
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        /// <summary>The label text of a box: the sharer's label and measured size in centimetres.</summary>
        public static string Describe(string label, Vector3 size)
        {
            Vector3 cm = size * 100f;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}  {1:0} x {2:0} x {3:0} cm", label, cm.x, cm.y, cm.z);
        }
    }
}
