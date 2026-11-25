// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples
{
    public class SlowRotate : MonoBehaviour
    {
        [SerializeField] private float degreesPerSecond = 30f;
        [SerializeField] private Vector3 axis = Vector3.up;
        [SerializeField, Tooltip("The point turned about, in this object's own frame (its origin by default).")]
        private Vector3 localPivot = Vector3.zero;

        public float DegreesPerSecond
        {
            get => degreesPerSecond;
            set => degreesPerSecond = value;
        }

        /// <summary>
        /// The point the object turns about, in its own frame. A scan preview turns about its mesh centre: its origin is not there (the
        /// normalisation moved the mesh, not the origin), and turning about the origin swung the model around beside its box.
        /// </summary>
        public Vector3 LocalPivot
        {
            get => localPivot;
            set => localPivot = value;
        }

        private void Update() => Turn(degreesPerSecond * Time.deltaTime);

        /// <summary>Turns by <paramref name="degrees"/> about the world axis through the pivot; the pivot stays where it is.</summary>
        internal void Turn(float degrees) => transform.RotateAround(transform.TransformPoint(localPivot), axis, degrees);
    }
}
