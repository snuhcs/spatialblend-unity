// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The pose of one bone in the CORE's world (Z-up, right-handed; X right, Y forward, Z up of
    /// <see cref="CoordinateConverter"/>), with the rotation as a quaternion (x, y, z, w) of the bone frame the
    /// embodiment registered. Doubles, as <c>sb_bone_pose_t</c>. The driver converts a Unity transform to this at one
    /// place, <see cref="EmbodimentFrames"/>.
    /// </summary>
    internal readonly struct EmbodiedBonePose
    {
        internal readonly double X, Y, Z;
        internal readonly double Qx, Qy, Qz, Qw;

        internal EmbodiedBonePose(double x, double y, double z, double qx, double qy, double qz, double qw)
        {
            X = x; Y = y; Z = z;
            Qx = qx; Qy = qy; Qz = qz; Qw = qw;
        }
    }

    /// <summary>A 3x3 matrix of doubles, row-major. Just what the frame conversion needs.</summary>
    internal readonly struct Mat3
    {
        internal readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        internal Mat3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22;
        }

        internal static Mat3 Identity => new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        /// <summary>Rows given row-major, 9 numbers.</summary>
        internal static Mat3 FromRows(double[] r) => new Mat3(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]);

        /// <summary>The matrix whose columns are the images of the three unit vectors.</summary>
        internal static Mat3 FromColumns((double x, double y, double z) cx, (double x, double y, double z) cy,
                                         (double x, double y, double z) cz) =>
            new Mat3(cx.x, cy.x, cz.x, cx.y, cy.y, cz.y, cx.z, cy.z, cz.z);

        internal Mat3 Transpose => new Mat3(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        internal double Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public static Mat3 operator *(Mat3 a, Mat3 b) => new Mat3(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        internal (double x, double y, double z) Apply(double x, double y, double z) =>
            (M00 * x + M01 * y + M02 * z, M10 * x + M11 * y + M12 * z, M20 * x + M21 * y + M22 * z);

        /// <summary>The rotation matrix of a quaternion (x, y, z, w), normalised first.</summary>
        internal static Mat3 FromQuaternion(double x, double y, double z, double w)
        {
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n; y /= n; z /= n; w /= n;
            return new Mat3(
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
        }

        /// <summary>The unit quaternion (x, y, z, w) of a proper rotation matrix, w >= 0 (Shepperd's method).</summary>
        internal void ToQuaternion(out double x, out double y, out double z, out double w)
        {
            double t = M00 + M11 + M22;
            if (t > 0)
            {
                double s = Math.Sqrt(t + 1) * 2;
                w = s / 4; x = (M21 - M12) / s; y = (M02 - M20) / s; z = (M10 - M01) / s;
            }
            else if (M00 > M11 && M00 > M22)
            {
                double s = Math.Sqrt(1 + M00 - M11 - M22) * 2;
                w = (M21 - M12) / s; x = s / 4; y = (M01 + M10) / s; z = (M02 + M20) / s;
            }
            else if (M11 > M22)
            {
                double s = Math.Sqrt(1 + M11 - M00 - M22) * 2;
                w = (M02 - M20) / s; x = (M01 + M10) / s; y = s / 4; z = (M12 + M21) / s;
            }
            else
            {
                double s = Math.Sqrt(1 + M22 - M00 - M11) * 2;
                w = (M10 - M01) / s; x = (M02 + M20) / s; y = (M12 + M21) / s; z = s / 4;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n; y /= n; z /= n; w /= n;
            if (w < 0) { x = -x; y = -y; z = -z; w = -w; }
        }
    }

    /// <summary>
    /// The one place a Unity bone transform becomes a core bone pose and back (the core's embodiment registration,
    /// C ABI 4.7: its bone frames and the native-to-canonical basis). It contains no number: every matrix is built from a
    /// declared basis.
    ///
    /// Three frames meet.
    /// - Unity world and the core world differ by <see cref="CoordinateConverter"/>'s position map P (x, z, y): a handedness
    ///   change. P is read off <see cref="CoordinateConverter.UnityToSpatialBlend3D"/>, not retyped.
    /// - The registered bone frame is the asset's native bone frame carried into the core's canonical axes: canonical local
    ///   coordinates c = B n, B the native-to-canonical basis (glTF/OVR: rows (0,0,1; 1,0,0; 0,1,0), read from the
    ///   measurement artefact).
    /// - A Unity bone's local axes are the native ones relabelled by Z, the SDK's Unity-client-space conversion (a
    ///   reflection of z, the SDK's ConvertSpace): u = Z n. ASSUMPTION A1, from the SDK sources and not verified at run time.
    ///
    /// With R_u the Unity world rotation of the bone, the registered bone rotation is
    ///   R_core = P R_u P^T K,   K = P Z B^T          (K is a proper rotation: a pure change of the local frame)
    /// because the core composes an anchor as p_bone + R_core * offset_c, offset_c = B offset_n is the registered offset, and the
    /// displacement of the measured point in the core world is P (R_u (Z offset_n)) = (P R_u P^T) (P Z B^T) (B offset_n) =
    /// R_core offset_c (derivation: the artefact's registration.procedure and tools/embodiment/ovr_registration_artefacts.py).
    /// Positions are P p_u.
    /// </summary>
    internal sealed class EmbodimentFrames
    {
        private readonly Mat3 _p;     // Unity world -> core world (positions)
        private readonly Mat3 _k;     // local-frame change: registered bone frame <- P-relabelled Unity bone frame

        /// <summary>
        /// <paramref name="nativeToCanonical"/> is B; <paramref name="unityLocalFromNativeLocal"/> is Z (how the SDK's Unity
        /// bone transforms relate to the asset's native bone frames).
        /// </summary>
        internal EmbodimentFrames(Mat3 nativeToCanonical, Mat3 unityLocalFromNativeLocal)
        {
            _p = UnityWorldToCoreWorld();
            _k = _p * unityLocalFromNativeLocal * nativeToCanonical.Transpose;
        }

        internal Mat3 K => _k;
        internal Mat3 P => _p;

        /// <summary>P, read off the position conversion every other pose already goes through.</summary>
        internal static Mat3 UnityWorldToCoreWorld()
        {
            CoordinateConverter.UnityToSpatialBlend3D(new Vector3(1f, 0f, 0f), out double ax, out double ay, out double az);
            CoordinateConverter.UnityToSpatialBlend3D(new Vector3(0f, 1f, 0f), out double bx, out double by, out double bz);
            CoordinateConverter.UnityToSpatialBlend3D(new Vector3(0f, 0f, 1f), out double cx, out double cy, out double cz);
            return Mat3.FromColumns((ax, ay, az), (bx, by, bz), (cx, cy, cz));
        }

        /// <summary>A Unity bone transform (world) as the core bone pose it registered.</summary>
        internal EmbodiedBonePose ToCore(Vector3 unityPosition, Quaternion unityRotation)
        {
            (double x, double y, double z) = _p.Apply(unityPosition.x, unityPosition.y, unityPosition.z);
            Mat3 ru = Mat3.FromQuaternion(unityRotation.x, unityRotation.y, unityRotation.z, unityRotation.w);
            Mat3 rc = _p * ru * _p.Transpose * _k;
            rc.ToQuaternion(out double qx, out double qy, out double qz, out double qw);
            return new EmbodiedBonePose(x, y, z, qx, qy, qz, qw);
        }

        /// <summary>The inverse of <see cref="ToCore"/>: a core bone pose as a Unity world transform.</summary>
        internal void FromCore(in EmbodiedBonePose core, out Vector3 unityPosition, out Quaternion unityRotation)
        {
            Mat3 pt = _p.Transpose;
            (double x, double y, double z) = pt.Apply(core.X, core.Y, core.Z);
            unityPosition = new Vector3((float)x, (float)y, (float)z);
            Mat3 rc = Mat3.FromQuaternion(core.Qx, core.Qy, core.Qz, core.Qw);
            Mat3 ru = pt * rc * _k.Transpose * _p;
            ru.ToQuaternion(out double qx, out double qy, out double qz, out double qw);
            unityRotation = new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
        }

        // ---------------------------------------------------------------- anchors (core world, x forward at yaw 0)

        /// <summary>Yaw, pitch, roll (ZYX, radians; +pitch is nose down) of a core rotation: the core's own EulerFromRotation.</summary>
        internal static void EulerZyx(double qx, double qy, double qz, double qw, out double yaw, out double pitch, out double roll)
        {
            Mat3 r = Mat3.FromQuaternion(qx, qy, qz, qw);
            double cp = Math.Sqrt(r.M00 * r.M00 + r.M10 * r.M10);
            pitch = Math.Atan2(-r.M20, cp);
            if (cp > 0.0)
            {
                yaw = Math.Atan2(r.M10, r.M00);
                roll = Math.Atan2(r.M21, r.M22);
            }
            else
            {
                // The forward axis is vertical: yaw and roll are one angle; the core carries it in yaw.
                yaw = Math.Atan2(-r.M01, r.M11);
                roll = 0.0;
            }
        }

        /// <summary>The quaternion (x, y, z, w) of R = Rz(yaw) Ry(pitch) Rx(roll).</summary>
        internal static void QuaternionFromEulerZyx(double yaw, double pitch, double roll,
                                                    out double qx, out double qy, out double qz, out double qw)
        {
            double cy = Math.Cos(yaw * 0.5), sy = Math.Sin(yaw * 0.5);
            double cp = Math.Cos(pitch * 0.5), sp = Math.Sin(pitch * 0.5);
            double cr = Math.Cos(roll * 0.5), sr = Math.Sin(roll * 0.5);
            qw = cr * cp * cy + sr * sp * sy;
            qx = sr * cp * cy - cr * sp * sy;
            qy = cr * sp * cy + sr * cp * sy;
            qz = cr * cp * sy - sr * sp * cy;
        }

        /// <summary>
        /// A core ANCHOR pose (the composed pose the core samples) as the driver's Unity-space pose, plus its roll (which a
        /// proposal never states, so a plan keeps it).
        /// </summary>
        internal static EmbodiedPose AnchorToUnityPose(in EmbodiedBonePose anchor, out double rollRadians)
        {
            EulerZyx(anchor.Qx, anchor.Qy, anchor.Qz, anchor.Qw, out double yaw, out double pitch, out double roll);
            rollRadians = roll;
            Vector3 position = CoordinateConverter.SpatialBlendToUnity3D(anchor.X, anchor.Y, anchor.Z);
            return new EmbodiedPose(position, SpatialBlendAPI.RadiansToUnityYawDegrees(yaw),
                HeadOrientation.PitchDegreesFromRadians(pitch));
        }

        /// <summary>A Unity-space anchor target (position, yaw, pitch) and a roll as a core anchor pose.</summary>
        internal static EmbodiedBonePose UnityPoseToAnchor(Vector3 unityPosition, float yawDegrees, float pitchDegrees,
                                                           double rollRadians)
        {
            CoordinateConverter.UnityToSpatialBlend3D(unityPosition, out double x, out double y, out double z);
            double yaw = CoordinateConverter.UnityToSpatialBlendAngle(yawDegrees) * Math.PI / 180.0;
            double pitch = HeadOrientation.PitchRadiansFromDegrees(pitchDegrees);
            QuaternionFromEulerZyx(yaw, pitch, rollRadians, out double qx, out double qy, out double qz, out double qw);
            return new EmbodiedBonePose(x, y, z, qx, qy, qz, qw);
        }
    }
}
