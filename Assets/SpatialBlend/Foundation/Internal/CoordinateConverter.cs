// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Converts between Unity's Y-up coordinate system and SpatialBlend's Z-up coordinate system.
    ///
    /// Unity (Y-up):
    ///   X = right
    ///   Y = up
    ///   Z = forward
    ///
    /// SpatialBlend (Z-up):
    ///   X = right
    ///   Y = forward
    ///   Z = up
    ///
    /// Conversion formulas:
    ///   Unity (x, y, z) → SpatialBlend (x, z, y)
    ///   SpatialBlend (x, y, z) → Unity (x, z, y)
    /// </summary>
    internal static class CoordinateConverter
    {
        private const float YawReferenceDegrees = 90f;

        /// <summary>
        /// Convert Unity Vector3 (Y-up) to SpatialBlend 3D coordinates (Z-up).
        /// Unity (x, y, z) = (right, up, forward) → SpatialBlend (x, y, z) = (right, forward, up)
        /// </summary>
        /// <param name="unityPosition">Position in Unity Y-up coordinates</param>
        /// <param name="sbX">SpatialBlend X (right)</param>
        /// <param name="sbY">SpatialBlend Y (forward)</param>
        /// <param name="sbZ">SpatialBlend Z (up)</param>
        public static void UnityToSpatialBlend3D(Vector3 unityPosition, out double sbX, out double sbY, out double sbZ)
        {
            sbX = unityPosition.x;  // right stays right
            sbY = unityPosition.z;  // Unity forward (Z) → SpatialBlend forward (Y)
            sbZ = unityPosition.y;  // Unity up (Y) → SpatialBlend up (Z)
        }

        /// <summary>
        /// Convert Unity Vector3 (Y-up) to SpatialBlend 2D coordinates (horizontal plane).
        /// Unity (x, z) = (right, forward) → SpatialBlend (x, y) = (right, forward)
        /// </summary>
        /// <param name="unityPosition">Position in Unity Y-up coordinates</param>
        /// <param name="sbX">SpatialBlend X (right)</param>
        /// <param name="sbY">SpatialBlend Y (forward)</param>
        public static void UnityToSpatialBlend2D(Vector3 unityPosition, out double sbX, out double sbY)
        {
            sbX = unityPosition.x;  // right stays right
            sbY = unityPosition.z;  // Unity forward (Z) → SpatialBlend forward (Y)
        }

        /// <summary>
        /// Convert SpatialBlend 3D coordinates (Z-up) to Unity Vector3 (Y-up).
        /// SpatialBlend (x, y, z) = (right, forward, up) → Unity (x, y, z) = (right, up, forward)
        /// </summary>
        /// <param name="sbX">SpatialBlend X (right)</param>
        /// <param name="sbY">SpatialBlend Y (forward)</param>
        /// <param name="sbZ">SpatialBlend Z (up)</param>
        /// <returns>Position in Unity Y-up coordinates</returns>
        public static Vector3 SpatialBlendToUnity3D(double sbX, double sbY, double sbZ)
        {
            return new Vector3(
                (float)sbX,  // right stays right
                (float)sbZ,  // SpatialBlend up (Z) → Unity up (Y)
                (float)sbY   // SpatialBlend forward (Y) → Unity forward (Z)
            );
        }

        /// <summary>
        /// Convert SpatialBlend 2D coordinates to Unity Vector3 (horizontal plane, Y=0).
        /// SpatialBlend (x, y) = (right, forward) → Unity (x, 0, z) = (right, 0, forward)
        /// </summary>
        /// <param name="sbX">SpatialBlend X (right)</param>
        /// <param name="sbY">SpatialBlend Y (forward)</param>
        /// <returns>Position in Unity Y-up coordinates with Y=0</returns>
        public static Vector3 SpatialBlendToUnity2D(double sbX, double sbY)
        {
            return new Vector3(
                (float)sbX,  // right stays right
                0f,            // ground level
                (float)sbY   // SpatialBlend forward (Y) → Unity forward (Z)
            );
        }

        /// <summary>
        /// Convert SpatialBlend 2D coordinates to Unity Vector3 with custom Y value.
        /// </summary>
        /// <param name="sbX">SpatialBlend X (right)</param>
        /// <param name="sbY">SpatialBlend Y (forward)</param>
        /// <param name="unityY">Unity Y coordinate to use</param>
        /// <returns>Position in Unity Y-up coordinates</returns>
        public static Vector3 SpatialBlendToUnity2D(double sbX, double sbY, float unityY)
        {
            return new Vector3(
                (float)sbX,  // right stays right
                unityY,        // specified height
                (float)sbY   // SpatialBlend forward (Y) → Unity forward (Z)
            );
        }

        /// <summary>
        /// Convert a single yaw angle from Unity (left-handed) to SpatialBlend (right-handed).
        /// Unity yaw 0° = +Z (forward), SpatialBlend yaw 0° = +X (right)
        /// Applies 90° offset for reference axis difference + negate for handedness.
        /// </summary>
        public static float UnityToSpatialBlendAngle(float angle)
        {
            return -angle + YawReferenceDegrees;
        }

        /// <summary>The same Unity heading basis/sign change in double precision, without wrapping continuous turns.</summary>
        public static double UnityToSpatialBlendAngle(double angle)
        {
            return -angle + YawReferenceDegrees;
        }

        /// <summary>
        /// Convert a single yaw angle from SpatialBlend (right-handed) to Unity (left-handed).
        /// Unity yaw 0° = +Z (forward), SpatialBlend yaw 0° = +X (right)
        /// Applies 90° offset for reference axis difference + negate for handedness.
        /// </summary>
        public static float SpatialBlendToUnityAngle(float angle)
        {
            return -angle + YawReferenceDegrees;
        }

        /// <summary>
        /// Convert Unity transformation matrix (Y-up) to SpatialBlend transformation matrix (Z-up).
        /// This is used for affordance volumes and other geometry transformations.
        /// </summary>
        /// <param name="unityMatrix">Unity Y-up transformation matrix</param>
        /// <returns>SpatialBlend Z-up transformation matrix</returns>
        public static Matrix4x4 UnityToSpatialBlendMatrix(Matrix4x4 unityMatrix)
        {
            // Geometry coordinates use the same basis change on both sides:
            // P * M * P^-1, where P swaps Y/Z and P^-1 = P. This preserves
            // identity, hierarchy composition, signed scale and affine shear.
            // A heading's +Z versus +X reference is NOT a geometry transform.
            var result = new Matrix4x4();
            for (int row = 0; row < 4; ++row)
            {
                int unityRow = row == 1 ? 2 : row == 2 ? 1 : row;
                for (int column = 0; column < 4; ++column)
                {
                    int unityColumn = column == 1 ? 2 : column == 2 ? 1 : column;
                    result[row, column] = unityMatrix[unityRow, unityColumn];
                }
            }
            return result;
        }

        /// <summary>
        /// Converts an authored anchor pose. Native anchor +X is forward and +Y
        /// is left; Unity anchor +Z is forward and +X is right. Only the anchor's
        /// local basis receives this extra mapping, never its parent geometry.
        /// </summary>
        public static Matrix4x4 UnityToSpatialBlendAnchorMatrix(Matrix4x4 unityMatrix)
        {
            Matrix4x4 geometry = UnityToSpatialBlendMatrix(unityMatrix);
            var result = geometry;
            result.SetColumn(0, geometry.GetColumn(1));
            result.SetColumn(1, -geometry.GetColumn(0));
            return result;
        }
    }
}
