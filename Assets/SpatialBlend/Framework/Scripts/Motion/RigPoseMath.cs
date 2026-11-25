// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Motion
{
    internal static class RigPoseMath
    {
        // PRECONDITION: both original operands passed the strict finite/proper
        // rigid gate. Normalize only derived arithmetic, never input matrices
        // or actual Transform readback. Matches Foundation body FK semantics.
        internal static Matrix4x4 ComposeValidatedRigid(Matrix4x4 parent, Matrix4x4 child)
        {
            Quaternion a = Quaternion.Normalize(parent.rotation), b = Quaternion.Normalize(child.rotation);
            return DerivedRigid((Vector3)parent.GetColumn(3) + a * (Vector3)child.GetColumn(3), a * b);
        }

        internal static Matrix4x4 InverseValidatedRigid(Matrix4x4 value)
        {
            Quaternion inverse = Quaternion.Inverse(Quaternion.Normalize(value.rotation));
            return DerivedRigid(inverse * -(Vector3)value.GetColumn(3), inverse);
        }

        internal static Matrix4x4 DerivedRigid(Vector3 position, Quaternion rotation)
        {
            // A float-normalized quaternion can still make Unity TRS exceed
            // the strict Gram-error bound at particular rotations. Evaluate
            // the normalized rotation in double and round each final matrix
            // element once. This is derived arithmetic, not input repair.
            double x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            double s = 2 / (x * x + y * y + z * z + w * w);
            Matrix4x4 result = Matrix4x4.identity;
            result.m00 = (float)(1 - s * (y * y + z * z));
            result.m01 = (float)(s * (x * y - z * w));
            result.m02 = (float)(s * (x * z + y * w));
            result.m10 = (float)(s * (x * y + z * w));
            result.m11 = (float)(1 - s * (x * x + z * z));
            result.m12 = (float)(s * (y * z - x * w));
            result.m20 = (float)(s * (x * z - y * w));
            result.m21 = (float)(s * (y * z + x * w));
            result.m22 = (float)(1 - s * (x * x + y * y));
            result.m03 = position.x; result.m13 = position.y; result.m23 = position.z;
            return result;
        }

        internal static bool RotationNear(Matrix4x4 first, Matrix4x4 second, float degrees)
        {
            // Quaternion.Angle's float dot can report nonzero error for q==q.
            // Double normalized dot retains the same angular tolerance.
            Quaternion a = first.rotation, b = second.rotation;
            double aa = (double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w;
            double bb = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
            double ab = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double cosine = Math.Cos(degrees * Math.PI / 360);
            return aa > 0 && bb > 0 && ab * ab >= aa * bb * cosine * cosine;
        }
    }
}
