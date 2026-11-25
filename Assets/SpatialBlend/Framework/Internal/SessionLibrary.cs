// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Internal
{
    internal enum InferenceType : int
    {
        Generic = 0,
        Sam3d = 1,           // one-shot image + points; the app no longer uses it (retired), kept as the native value
        Sam3SegOnly = 2,     // SB_INFERENCE_TYPE_SAM3_SEG
        Sam3dObjectOnly = 3, // SB_INFERENCE_TYPE_SAM3D_OBJ
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SbPoint3d
    {
        public double x;
        public double y;
        public double z;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnInferenceCompleteDelegate(
        long sessionHandle,
        long eventHandle,
        [MarshalAs(UnmanagedType.U1)] bool success,
        [MarshalAs(UnmanagedType.LPStr)] string message);

    /// <summary>
    /// P/Invoke surface of the SAM3 and SAM3D session library (spatialblend_session).
    /// This is not the spatialblend core. libspatialblend is a separate library and the two share no
    /// symbols, so this file stays apart from SpatialBlendAPI on purpose.
    ///
    /// The managed code never calls these functions directly. NativeScanSessionTransport wraps them
    /// behind IScanSessionTransport so every test runs without the library.
    ///
    /// Library status: the library is built in the spatialblend-reconstruction repository (C API target //session/c:session_c)
    /// for Android arm64, macOS arm64 and Windows x64 and published with a SHA256SUMS file as release files.
    /// tools/fetch_plugins.py installs them into the gitignored Assets/Plugins folders next to libspatialblend. Until they are
    /// fetched the Editor reports TransportFault instead of scanning.
    /// </summary>
    internal static class SessionLibrary
    {
        // The shipped file is libspatialblend_session.<ext> on every platform. Android, macOS and Linux add the "lib" prefix to a DllImport
        // name; Windows does not (it looks for <name>.dll), so the Windows name carries the prefix itself.
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        internal const string LIB_NAME = "libspatialblend_session";
#else
        internal const string LIB_NAME = "spatialblend_session";
#endif

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_session_create(InferenceType type);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_session_destroy(long sessionHandle);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_session_connect(
            long sessionHandle,
            [MarshalAs(UnmanagedType.LPStr)] string addr);

        // Same contract as spatialblend_inference_session_connect, over TLS: the session library opens
        // grpc::SslCredentials to dns:///host:port (the address stays host:port, with no URL scheme).
        // The RPCs are /spatialblend.scan.v1.*. Added in a newer library: an older build throws
        // EntryPointNotFoundException (handled in NativeScanSessionTransport) and there is no plaintext retry.
        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_session_connect_tls(
            long sessionHandle,
            [MarshalAs(UnmanagedType.LPStr)] string addr);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool spatialblend_register_on_inference_complete(
            long sessionHandle,
            OnInferenceCompleteDelegate callback);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool spatialblend_try_wait_for_event(long eventHandle, long timeoutMs);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        internal static extern string spatialblend_get_error_message(long eventHandle);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_sam3d(
            long sessionHandle,
            [MarshalAs(UnmanagedType.LPStr)] string clientId,
            IntPtr imageData,
            ulong imageDataSize,
            [In] SbPoint3d[] points,
            ulong numPoints,
            [MarshalAs(UnmanagedType.LPStr)] string outputPath);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_segment(
            long sessionHandle,
            [MarshalAs(UnmanagedType.LPStr)] string clientId,
            IntPtr imageData,
            ulong imageDataSize,
            [In] SbPoint3d[] points,
            ulong numPoints,
            [MarshalAs(UnmanagedType.LPStr)] string outputMaskPath);

        [DllImport(LIB_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long spatialblend_inference_reconstruct(
            long sessionHandle,
            [MarshalAs(UnmanagedType.LPStr)] string clientId,
            IntPtr imageData,
            ulong imageDataSize,
            IntPtr maskData,
            ulong maskDataSize,
            int maskWidth,
            int maskHeight,
            [MarshalAs(UnmanagedType.LPStr)] string outputPath);

        internal static SbPoint3d[] ToNativePoints(ScanPoint[] points)
        {
            if (points == null || points.Length == 0) return null;
            var native = new SbPoint3d[points.Length];
            for (int i = 0; i < points.Length; i++)
                native[i] = new SbPoint3d { x = points[i].X, y = points[i].Y, z = points[i].Z };   // z: the prompt label
            return native;
        }

        internal static long InferenceSegment(long session, string clientId, byte[] image, ScanPoint[] points, string outputMaskPath)
        {
            if (image == null || image.Length == 0) return 0;
            GCHandle imageHandle = GCHandle.Alloc(image, GCHandleType.Pinned);
            try
            {
                SbPoint3d[] native = ToNativePoints(points);
                return spatialblend_inference_segment(session, clientId, imageHandle.AddrOfPinnedObject(),
                    (ulong)image.Length, native, (ulong)(native?.Length ?? 0), outputMaskPath);
            }
            finally
            {
                imageHandle.Free();
            }
        }

        internal static long InferenceReconstruct(long session, string clientId, byte[] image, byte[] mask,
                                                  int maskWidth, int maskHeight, string outputPrefix)
        {
            if (image == null || image.Length == 0 || mask == null || mask.Length == 0) return 0;
            GCHandle imageHandle = GCHandle.Alloc(image, GCHandleType.Pinned);
            GCHandle maskHandle = GCHandle.Alloc(mask, GCHandleType.Pinned);
            try
            {
                return spatialblend_inference_reconstruct(session, clientId, imageHandle.AddrOfPinnedObject(),
                    (ulong)image.Length, maskHandle.AddrOfPinnedObject(), (ulong)mask.Length,
                    maskWidth, maskHeight, outputPrefix);
            }
            finally
            {
                imageHandle.Free();
                maskHandle.Free();
            }
        }
    }
}
