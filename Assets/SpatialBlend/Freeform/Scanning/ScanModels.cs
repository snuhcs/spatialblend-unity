// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>Which server pipeline a request uses.</summary>
    public enum ScanKind
    {
        /// <summary>SAM3 segmentation only. Returns a mask file (v1 InferenceType.Sam3SegOnly).</summary>
        Segment = 0,
        /// <summary>SAM3D reconstruction from an image and a mask (v1 InferenceType.Sam3dObjectOnly).</summary>
        Reconstruct = 1,
        // 2 was OneShot (image and points to a model in one request), retired.
    }

    public enum ScanErrorCode
    {
        None = 0,
        InvalidConfig,
        NotInitialized,
        EmptyImage,
        EmptyMask,
        InvalidMaskSize,
        NoPoints,
        InvalidPoint,
        TransportFault,
        ConnectFailed,
        RequestRejected,
        ServerFailed,
        MaskUnreadable,
        MissingOutput,
        Timeout,
        Cancelled,
        /// <summary>The request carried a box prompt and the server did not use it (an older server): retry with points.</summary>
        BoxPromptUnsupported,
    }

    public readonly struct ScanError
    {
        public readonly ScanErrorCode Code;
        public readonly string Message;

        public ScanError(ScanErrorCode code, string message)
        {
            Code = code;
            Message = message ?? string.Empty;
        }

        public bool IsError => Code != ScanErrorCode.None;
        public static ScanError None => default;
        public override string ToString() => IsError ? $"{Code}: {Message}" : "None";
    }

    /// <summary>
    /// What a <see cref="ScanPoint"/> asks of the segmenter. It travels in SegPoint.z (the proto has no label field), with
    /// SAM's own corner codes for a box. 0 is positive because older clients send z = 0 for a click.
    /// </summary>
    public enum ScanPointLabel
    {
        Positive = 0,
        Negative = -1,
        BoxTopLeft = 2,
        BoxBottomRight = 3,
    }

    /// <summary>A pixel in the coordinate space the server expects (x right, y down from the top-left), with its label.</summary>
    public readonly struct ScanPoint
    {
        public readonly double X;
        public readonly double Y;
        public readonly ScanPointLabel Label;

        public ScanPoint(double x, double y, ScanPointLabel label = ScanPointLabel.Positive)
        {
            X = x;
            Y = y;
            Label = label;
        }

        /// <summary>The value sent as SegPoint.z.</summary>
        public double Z => (int)Label;

        public bool IsBoxCorner => Label == ScanPointLabel.BoxTopLeft || Label == ScanPointLabel.BoxBottomRight;
    }

    /// <summary>
    /// The box a user drew around the object. v1 carried it as (anchorPosition, anchorBoxSize)
    /// and later turned it into an AffordanceVolume polygon.
    /// </summary>
    public readonly struct ScanAnchor
    {
        public readonly Vector3 Center;
        public readonly Vector3 Size;
        private readonly Quaternion _rotation;
        private readonly bool _hasRotation;

        /// <param name="rotation">The box's turn about the vertical as it was drawn (the scan is installed exactly
        /// there); null is unturned, as v1's box was.</param>
        public ScanAnchor(Vector3 center, Vector3 size, Quaternion? rotation = null)
        {
            Center = center;
            Size = size;
            _rotation = rotation.HasValue ? ScanRegionHint.Yaw(rotation.Value) : Quaternion.identity;
            _hasRotation = rotation.HasValue;
        }

        /// <summary>The box's yaw as drawn (identity for an unturned box, and for a default anchor).</summary>
        public Quaternion Rotation => _hasRotation ? _rotation : Quaternion.identity;

        public bool HasSize => Size.x > 0f && Size.z > 0f;
    }

    /// <summary>
    /// What the transport receives. The builder fills only the fields the kind needs.
    /// </summary>
    public sealed class ScanTransportRequest
    {
        public ScanKind Kind;
        public string ClientId;
        public byte[] ImageBytes;
        public byte[] MaskBytes;
        public int MaskWidth;
        public int MaskHeight;
        public ScanPoint[] Points;
        /// <summary>Segment: the mask file. Reconstruct: a path prefix (the server appends suffixes).</summary>
        public string OutputPath;
    }

    /// <summary>One completion callback from the session library.</summary>
    public readonly struct ScanCompletion
    {
        public readonly long SessionHandle;
        public readonly long EventHandle;
        public readonly bool Success;
        public readonly string Message;

        public ScanCompletion(long sessionHandle, long eventHandle, bool success, string message)
        {
            SessionHandle = sessionHandle;
            EventHandle = eventHandle;
            Success = success;
            Message = message ?? string.Empty;
        }
    }

    public sealed class ScanSegmentResult
    {
        public long EventHandle;
        public bool Success;
        public byte[] Mask;
        public int Width;
        public int Height;
        public ScanError Error;
        /// <summary>The request carried a box prompt.</summary>
        public bool SentBox;
        /// <summary>The server said it used the box ("box:1" in its answer).</summary>
        public bool UsedBox;
    }

    /// <summary>
    /// The box footprint v1 used to build an AffordanceVolume. It is data only. v2 turns it into
    /// an explicit region through SbSpatialAuthoring (T5.2).
    /// </summary>
    public readonly struct ScanRegionHint
    {
        /// <summary>The v1 polygon margin around the box footprint, in metres.</summary>
        public const float V1MarginMetres = 0.15f;

        public readonly Vector3 BoxCenter;
        public readonly Vector3 BoxSize;
        public readonly float Margin;
        private readonly Quaternion _rotation;
        private readonly bool _hasRotation;

        public ScanRegionHint(Vector3 boxCenter, Vector3 boxSize, float margin = V1MarginMetres, Quaternion? rotation = null)
        {
            BoxCenter = boxCenter;
            BoxSize = boxSize;
            Margin = margin;
            _rotation = rotation.HasValue ? Yaw(rotation.Value) : Quaternion.identity;
            _hasRotation = rotation.HasValue;
        }

        /// <summary>The box's turn about the vertical (its size is along its turned axes); identity for an unturned box.</summary>
        public Quaternion Rotation => _hasRotation ? _rotation : Quaternion.identity;

        /// <summary>The turn of <paramref name="rotation"/> about the vertical only (a drawn box stands upright).</summary>
        public static Quaternion Yaw(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-8f)
            {
                Vector3 right = rotation * Vector3.right;
                right.y = 0f;
                if (right.sqrMagnitude < 1e-8f) return Quaternion.identity;
                forward = Vector3.Cross(right.normalized, Vector3.up);
            }
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        public float YBottom => BoxCenter.y - BoxSize.y * 0.5f;
        public float YTop => BoxCenter.y + BoxSize.y * 0.5f;

        /// <summary>The four world-space corners on the floor plane (y = 0), as in v1 ConfigureAffordance.</summary>
        public Vector3[] FloorCorners()
        {
            float hx = BoxSize.x * 0.5f + Margin;
            float hz = BoxSize.z * 0.5f + Margin;
            return new[]
            {
                new Vector3(BoxCenter.x - hx, 0f, BoxCenter.z - hz),
                new Vector3(BoxCenter.x + hx, 0f, BoxCenter.z - hz),
                new Vector3(BoxCenter.x + hx, 0f, BoxCenter.z + hz),
                new Vector3(BoxCenter.x - hx, 0f, BoxCenter.z + hz),
            };
        }
    }

    public sealed class ScanReconstructionResult
    {
        public long EventHandle;
        public ScanKind Kind;
        /// <summary>Base name shared by the server outputs. Also the object name.</summary>
        public string ObjectName;
        /// <summary>"{ObjectName}_objmodel.glb".</summary>
        public string ModelFileName;
        /// <summary>"{ObjectName}_objinfo.json".</summary>
        public string InfoFileName;
        public string OutputPrefix;
        /// <summary>The GLB the server wrote: the output prefix plus "_objmodel.glb".</summary>
        public string ModelPath => OutputPrefix + ScanResponseParser.ModelSuffix;
        public bool HasAnchor;
        public ScanAnchor Anchor;
        public bool HasRegionHint;
        public ScanRegionHint RegionHint;
    }

    public sealed class ScanFailure
    {
        public long EventHandle;
        public ScanKind Kind;
        public ScanError Error;
        public bool HasAnchor;
        public ScanAnchor Anchor;
    }

    /// <summary>Hands a finished reconstruction to the scene. Implemented by the app.</summary>
    public interface IScanResultSink
    {
        void OnScanReconstructed(ScanReconstructionResult result);
        void OnScanFailed(ScanFailure failure);
    }

    /// <summary>
    /// The transport cannot do what the config asks (for example TLS with an older session library).
    /// The message must be fixed text: it is surfaced as is and never carries an address.
    /// </summary>
    public sealed class ScanTransportUnsupportedException : Exception
    {
        /// <summary>Fixed text for a session library that lacks the TLS connect entry point.</summary>
        public const string TlsNeedsNewerLibrary = "TLS needs a newer session library";

        public ScanTransportUnsupportedException(string message) : base(message) { }
    }

    /// <summary>
    /// The session library seam. The native implementation calls libspatialblend_session. Tests use a fake.
    /// A completion callback may arrive on any thread.
    /// </summary>
    public interface IScanSessionTransport
    {
        /// <summary>Returns a session handle, or a value of 0 or less when creation failed.</summary>
        long CreateSession(ScanKind kind);

        bool RegisterCompletion(long session, Action<ScanCompletion> onCompletion);

        /// <summary>
        /// useTls selects the TLS connect entry point. A transport that cannot honour it must throw
        /// ScanTransportUnsupportedException. It must never connect in plaintext instead.
        /// </summary>
        bool Connect(long session, string address, bool useTls, out string error);

        /// <summary>Returns an event handle, or a value of 0 or less when the request was rejected.</summary>
        long Submit(long session, ScanTransportRequest request);

        void DestroySession(long session);
    }

    public interface IScanFileSystem
    {
        /// <summary>Throws when the file cannot be read.</summary>
        byte[] ReadAllBytes(string path);

        void Delete(string path);
    }
}
