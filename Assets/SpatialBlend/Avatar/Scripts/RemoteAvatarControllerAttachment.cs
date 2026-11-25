// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Parents the v1 left and right controller prefabs onto wrist transforms.
    /// Offsets are v1's local poses. Meta wrists attach
    /// immediately. Sb wrists attach only when the skeleton-loaded callback
    /// subscribed here runs. Active state follows
    /// <see cref="RemoteAvatarVisibilityPolicy"/>.
    /// </summary>
    public sealed class RemoteAvatarControllerAttachment
    {
        public static readonly Vector3 SbLeftPosition = new Vector3(0.1245f, 0.0543f, 0.0354f);
        public static readonly Quaternion SbLeftRotation = new Quaternion(-0.5603049f, -0.5541525f, -0.5031417f, -0.3547139f);
        public static readonly Vector3 SbRightPosition = new Vector3(-0.1339f, -0.0546f, -0.0414f);
        public static readonly Quaternion SbRightRotation = new Quaternion(-0.44476226f, -0.5820806f, 0.53392607f, 0.42224616f);

        public static readonly Vector3 MetaLeftPosition = new Vector3(0.1022f, 0.0427f, 0.0385f);
        public static readonly Quaternion MetaLeftRotation = new Quaternion(-0.62604237f, -0.54519635f, -0.47822735f, -0.28658426f);
        public static readonly Vector3 MetaRightPosition = new Vector3(-0.0929f, -0.0426f, -0.0406f);
        public static readonly Quaternion MetaRightRotation = new Quaternion(0.42217964f, 0.43483788f, -0.5492023f, -0.57537574f);

        public GameObject SbLeft { get; private set; }
        public GameObject SbRight { get; private set; }
        public GameObject MetaLeft { get; private set; }
        public GameObject MetaRight { get; private set; }
        public bool SbAttached { get; private set; }

        /// <summary>
        /// Attaches Meta controllers now. Sb controllers stay unparented until
        /// the callback passed to <paramref name="subscribeSbSkeletonLoaded"/> runs.
        /// </summary>
        public void Bind(
            GameObject leftPrefab,
            GameObject rightPrefab,
            Transform metaLeftWrist,
            Transform metaRightWrist,
            Action<Action> subscribeSbSkeletonLoaded,
            Func<Transform> sbLeftWrist,
            Func<Transform> sbRightWrist,
            Action onSbAttached)
        {
            AttachPair(
                leftPrefab,
                rightPrefab,
                metaLeftWrist,
                metaRightWrist,
                MetaLeftPosition,
                MetaLeftRotation,
                MetaRightPosition,
                MetaRightRotation,
                out GameObject metaLeft,
                out GameObject metaRight);
            MetaLeft = metaLeft;
            MetaRight = metaRight;

            if (subscribeSbSkeletonLoaded == null)
            {
                return;
            }

            subscribeSbSkeletonLoaded(() =>
            {
                if (SbAttached)
                {
                    return;
                }

                SbAttached = true;
                Transform leftWrist = sbLeftWrist != null ? sbLeftWrist() : null;
                Transform rightWrist = sbRightWrist != null ? sbRightWrist() : null;
                AttachPair(
                    leftPrefab,
                    rightPrefab,
                    leftWrist,
                    rightWrist,
                    SbLeftPosition,
                    SbLeftRotation,
                    SbRightPosition,
                    SbRightRotation,
                    out GameObject sbLeft,
                    out GameObject sbRight);
                SbLeft = sbLeft;
                SbRight = sbRight;
                onSbAttached?.Invoke();
            });
        }

        public void Apply(RemoteAvatarVisibilityDecision decision)
        {
            if (!decision.ApplyRemoteRules)
            {
                return;
            }

            if (decision.ApplySbVisibility)
            {
                SetActive(SbLeft, decision.SbVisible);
                SetActive(SbRight, decision.SbVisible);
            }

            if (decision.ApplyMetaVisibility || decision.ApplyMetaAlpha)
            {
                SetActive(MetaLeft, decision.MetaVisible);
                SetActive(MetaRight, decision.MetaVisible);
            }
        }

        private static void AttachPair(
            GameObject leftPrefab,
            GameObject rightPrefab,
            Transform leftWrist,
            Transform rightWrist,
            Vector3 leftPosition,
            Quaternion leftRotation,
            Vector3 rightPosition,
            Quaternion rightRotation,
            out GameObject left,
            out GameObject right)
        {
            if (leftPrefab == null || rightPrefab == null)
            {
                left = null;
                right = null;
                return;
            }

            left = Attach(leftPrefab, leftWrist, leftPosition, leftRotation);
            right = Attach(rightPrefab, rightWrist, rightPosition, rightRotation);
        }

        private static GameObject Attach(GameObject prefab, Transform wrist, Vector3 localPosition, Quaternion localRotation)
        {
            if (wrist == null)
            {
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab, wrist);
            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = localRotation;
            return instance;
        }

        private static void SetActive(GameObject instance, bool active)
        {
            if (instance != null)
            {
                instance.SetActive(active);
            }
        }
    }
}
