// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    public enum FreeformContentKind
    {
        Whiteboard,
        Chess
    }

    public readonly struct WhiteboardContentConfiguration
    {
        public WhiteboardContentConfiguration(int width, int height, Color32 background)
        {
            Width = width;
            Height = height;
            Background = background;
        }

        public int Width { get; }
        public int Height { get; }
        public Color32 Background { get; }
    }

    public readonly struct ChessPiecePose
    {
        public ChessPiecePose(string pieceId, Vector3 boardLocalPosition,
                              Quaternion boardLocalRotation)
        {
            PieceId = pieceId;
            BoardLocalPosition = boardLocalPosition;
            BoardLocalRotation = boardLocalRotation;
        }

        public string PieceId { get; }
        public Vector3 BoardLocalPosition { get; }
        public Quaternion BoardLocalRotation { get; }
    }

    public abstract class FreeformContentModel
    {
        internal FreeformContentModel(string contentId, SpatialBlendObject owner,
                                      FreeformContentKind kind)
        {
            ContentId = contentId;
            Owner = owner;
            Kind = kind;
        }

        public string ContentId { get; }
        public SpatialBlendObject Owner { get; }
        public FreeformContentKind Kind { get; }
        public bool IsLive { get; private set; } = true;
        public event Action<FreeformContentModel> Changed;
        public event Action<FreeformContentModel> Retired;

        protected void RaiseChanged()
        {
            if (!IsLive)
                return;
            Action<FreeformContentModel> handlers = Changed;
            if (handlers == null)
                return;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                // A callback can remove this content. Its views receive Retired,
                // and no later Changed observer may see a retired model.
                if (!IsLive)
                    break;
                TryNotify((Action<FreeformContentModel>)callback);
            }
        }

        internal void Retire()
        {
            if (!IsLive)
                return;
            IsLive = false;
            try
            {
                Action<FreeformContentModel> handlers = Retired;
                if (handlers != null)
                    foreach (Delegate callback in handlers.GetInvocationList())
                        TryNotify((Action<FreeformContentModel>)callback);
            }
            finally
            {
                Changed = null;
                Retired = null;
            }
        }

        private void TryNotify(Action<FreeformContentModel> handler)
        {
            try
            {
                handler(this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }

    /// <summary>Typed, local canvas state. It has no transport or ownership semantics.</summary>
    public sealed class WhiteboardContentModel : FreeformContentModel
    {
        internal const int MaximumDimension = 4096;
        private const int MaximumBrushRadius = 128;
        private const long MaximumRasterWork = 4000000;
        private Color32[] _pixels;

        internal WhiteboardContentModel(string contentId, SpatialBlendObject owner,
                                        WhiteboardContentConfiguration configuration)
            : base(contentId, owner, FreeformContentKind.Whiteboard)
        {
            Width = configuration.Width;
            Height = configuration.Height;
            Background = configuration.Background;
            _pixels = new Color32[Width * Height];
            for (int index = 0; index < _pixels.Length; ++index)
                _pixels[index] = Background;
        }

        public int Width { get; }
        public int Height { get; }
        public Color32 Background { get; }

        public Color32[] CopyPixels()
        {
            var copy = new Color32[_pixels.Length];
            Array.Copy(_pixels, copy, copy.Length);
            return copy;
        }

        public bool DrawLine(Vector2 startUv, Vector2 endUv, Color32 color, int brushRadius,
                             out string error)
        {
            if (!IsLive)
            {
                error = "This whiteboard content has been removed.";
                return false;
            }
            if (!FreeformValidation.ValidUv(startUv) || !FreeformValidation.ValidUv(endUv) ||
                brushRadius < 0 || brushRadius > MaximumBrushRadius)
            {
                error = "UV coordinates must be within the board and brush radius must be in range.";
                return false;
            }

            // This retains the legacy board's UV convention: the horizontal dimension
            // determines stamp density and a UV of exactly one falls outside the raster.
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(startUv, endUv) * Width));
            long maximumStampPixels = (long)(brushRadius * 2 + 1) * (brushRadius * 2 + 1);
            if ((long)(steps + 1) * maximumStampPixels > MaximumRasterWork)
            {
                error = "Stroke exceeds the supported local raster work budget.";
                return false;
            }
            bool changed = false;
            for (int step = 0; step <= steps; ++step)
            {
                Vector2 uv = Vector2.Lerp(startUv, endUv, step / (float)steps);
                changed |= DrawBrushStamp(uv, color, brushRadius);
            }
            if (changed)
                RaiseChanged();
            error = null;
            return true;
        }

        public bool TryClear(out string error)
        {
            if (!IsLive)
            {
                error = "This whiteboard content has been removed.";
                return false;
            }
            for (int index = 0; index < _pixels.Length; ++index)
                _pixels[index] = Background;
            RaiseChanged();
            error = null;
            return true;
        }

        public void Clear()
        {
            TryClear(out _);
        }

        public bool TryCreateSnapshot(out byte[] snapshot, out string error) =>
            WhiteboardSnapshotCodec.TryEncode(Width, Height, _pixels, out snapshot, out error);

        public bool TryApplySnapshot(byte[] snapshot, out string error)
        {
            if (!IsLive)
            {
                error = "This whiteboard content has been removed.";
                return false;
            }
            if (!WhiteboardSnapshotCodec.TryDecode(snapshot, out int width, out int height,
                                                    out Color32[] pixels, out error))
                return false;
            if (width != Width || height != Height)
            {
                error = "Snapshot dimensions do not match this whiteboard.";
                return false;
            }
            _pixels = pixels;
            RaiseChanged();
            return true;
        }

        private bool DrawBrushStamp(Vector2 uv, Color32 color, int radius)
        {
            int pixelX = (int)(uv.x * Width);
            int pixelY = (int)(uv.y * Height);
            bool changed = false;
            int radiusSquared = radius * radius;
            int minimumY = Mathf.Max(0, pixelY - radius);
            int maximumY = Mathf.Min(Height - 1, pixelY + radius);
            int minimumX = Mathf.Max(0, pixelX - radius);
            int maximumX = Mathf.Min(Width - 1, pixelX + radius);
            for (int y = minimumY; y <= maximumY; ++y)
            {
                for (int x = minimumX; x <= maximumX; ++x)
                {
                    int dx = x - pixelX;
                    int dy = y - pixelY;
                    if (dx * dx + dy * dy > radiusSquared)
                        continue;
                    int index = y * Width + x;
                    if (!_pixels[index].Equals(color))
                    {
                        _pixels[index] = color;
                        changed = true;
                    }
                }
            }
            return changed;
        }
    }

    /// <summary>Typed board-local positions for arbitrary chess-piece movement.</summary>
    public sealed class ChessContentModel : FreeformContentModel
    {
        private readonly Dictionary<string, ChessPiecePose> _pieces;

        internal ChessContentModel(string contentId, SpatialBlendObject owner,
                                   IReadOnlyList<ChessPiecePose> initialPieces)
            : base(contentId, owner, FreeformContentKind.Chess)
        {
            _pieces = new Dictionary<string, ChessPiecePose>(StringComparer.Ordinal);
            foreach (ChessPiecePose piece in initialPieces)
                _pieces.Add(piece.PieceId, piece);
        }

        public int PieceCount => _pieces.Count;

        public bool TryGetPiecePose(string pieceId, out ChessPiecePose pose)
        {
            pose = default;
            return FreeformValidation.ValidPiece(pieceId) && _pieces.TryGetValue(pieceId, out pose);
        }

        public ChessPiecePose[] CopyPieces()
        {
            var copy = new ChessPiecePose[_pieces.Count];
            int index = 0;
            foreach (ChessPiecePose pose in _pieces.Values)
                copy[index++] = pose;
            Array.Sort(copy, (left, right) => string.CompareOrdinal(left.PieceId, right.PieceId));
            return copy;
        }

        public bool TrySetPiecePose(string pieceId, Vector3 boardLocalPosition,
                                    Quaternion boardLocalRotation, out string error)
        {
            if (!IsLive)
            {
                error = "This chess content has been removed.";
                return false;
            }
            if (!FreeformValidation.ValidPiece(pieceId) ||
                !FreeformValidation.ValidPose(boardLocalPosition, boardLocalRotation) ||
                !_pieces.ContainsKey(pieceId))
            {
                error = "A configured piece ID and finite nonzero board-local pose are required.";
                return false;
            }
            var pose = new ChessPiecePose(pieceId, boardLocalPosition, boardLocalRotation);
            _pieces[pieceId] = pose;
            RaiseChanged();
            error = null;
            return true;
        }

        public bool TryCreateSnapshot(out byte[] snapshot, out string error) =>
            ChessSnapshotCodec.TryEncode(CopyPieces(), out snapshot, out error);

        public bool TryApplySnapshot(byte[] snapshot, out string error)
        {
            if (!IsLive)
            {
                error = "This chess content has been removed.";
                return false;
            }
            if (!ChessSnapshotCodec.TryDecode(snapshot, out ChessPiecePose[] pieces, out error))
                return false;
            if (pieces.Length != _pieces.Count)
            {
                error = "Chess snapshot does not match the configured piece IDs.";
                return false;
            }
            var candidate = new Dictionary<string, ChessPiecePose>(StringComparer.Ordinal);
            foreach (ChessPiecePose piece in pieces)
            {
                if (!_pieces.ContainsKey(piece.PieceId))
                {
                    error = "Chess snapshot does not match the configured piece IDs.";
                    return false;
                }
                candidate.Add(piece.PieceId, piece);
            }
            _pieces.Clear();
            foreach (var pair in candidate)
                _pieces.Add(pair.Key, pair.Value);
            RaiseChanged();
            return true;
        }
    }

    public static class WhiteboardSnapshotCodec
    {
        private const int HeaderLength = 16;
        private const int Magic = 0x31425746; // FWB1, little-endian bytes

        public static bool TryEncode(int width, int height, Color32[] pixels, out byte[] snapshot,
                                     out string error)
        {
            snapshot = null;
            if (!ValidDimensions(width, height) || pixels == null || pixels.Length != width * height)
            {
                error = "Whiteboard dimensions or pixel payload are invalid.";
                return false;
            }
            int byteCount = checked(HeaderLength + pixels.Length * 4);
            snapshot = new byte[byteCount];
            WriteInt(snapshot, 0, Magic);
            WriteInt(snapshot, 4, width);
            WriteInt(snapshot, 8, height);
            WriteInt(snapshot, 12, pixels.Length * 4);
            for (int index = 0, offset = HeaderLength; index < pixels.Length; ++index)
            {
                Color32 pixel = pixels[index];
                snapshot[offset++] = pixel.r;
                snapshot[offset++] = pixel.g;
                snapshot[offset++] = pixel.b;
                snapshot[offset++] = pixel.a;
            }
            error = null;
            return true;
        }

        public static bool TryDecode(byte[] snapshot, out int width, out int height,
                                     out Color32[] pixels, out string error)
        {
            width = height = 0;
            pixels = null;
            if (snapshot == null || snapshot.Length < HeaderLength || ReadInt(snapshot, 0) != Magic)
            {
                error = "Whiteboard snapshot header is invalid.";
                return false;
            }
            width = ReadInt(snapshot, 4);
            height = ReadInt(snapshot, 8);
            int payloadLength = ReadInt(snapshot, 12);
            if (!ValidDimensions(width, height) || payloadLength != width * height * 4 ||
                snapshot.Length != HeaderLength + payloadLength)
            {
                error = "Whiteboard snapshot dimensions or payload length are invalid.";
                return false;
            }
            var candidate = new Color32[width * height];
            for (int index = 0, offset = HeaderLength; index < candidate.Length; ++index)
                candidate[index] = new Color32(snapshot[offset++], snapshot[offset++],
                                               snapshot[offset++], snapshot[offset++]);
            pixels = candidate;
            error = null;
            return true;
        }

        private static bool ValidDimensions(int width, int height) =>
            width > 0 && height > 0 && width <= WhiteboardContentModel.MaximumDimension &&
            height <= WhiteboardContentModel.MaximumDimension && (long)width * height <= 16777216;

        internal static void WriteInt(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        internal static int ReadInt(byte[] bytes, int offset) =>
            bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 |
            bytes[offset + 3] << 24;
    }

    public static class ChessSnapshotCodec
    {
        private const int HeaderLength = 8;
        private const int PoseLength = 28;
        private const int Magic = 0x31484346; // FCH1, little-endian bytes
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryEncode(IReadOnlyList<ChessPiecePose> pieces, out byte[] snapshot,
                                     out string error)
        {
            snapshot = null;
            if (!TryValidatePieces(pieces, out byte[][] ids, out int totalLength, out error))
                return false;
            snapshot = new byte[totalLength];
            WhiteboardSnapshotCodec.WriteInt(snapshot, 0, Magic);
            WhiteboardSnapshotCodec.WriteInt(snapshot, 4, pieces.Count);
            int offset = HeaderLength;
            for (int index = 0; index < pieces.Count; ++index)
            {
                byte[] id = ids[index];
                snapshot[offset++] = (byte)id.Length;
                snapshot[offset++] = (byte)(id.Length >> 8);
                Array.Copy(id, 0, snapshot, offset, id.Length);
                offset += id.Length;
                ChessPiecePose piece = pieces[index];
                WriteFloat(snapshot, ref offset, piece.BoardLocalPosition.x);
                WriteFloat(snapshot, ref offset, piece.BoardLocalPosition.y);
                WriteFloat(snapshot, ref offset, piece.BoardLocalPosition.z);
                WriteFloat(snapshot, ref offset, piece.BoardLocalRotation.x);
                WriteFloat(snapshot, ref offset, piece.BoardLocalRotation.y);
                WriteFloat(snapshot, ref offset, piece.BoardLocalRotation.z);
                WriteFloat(snapshot, ref offset, piece.BoardLocalRotation.w);
            }
            error = null;
            return true;
        }

        public static bool TryDecode(byte[] snapshot, out ChessPiecePose[] pieces, out string error)
        {
            pieces = null;
            if (snapshot == null || snapshot.Length < HeaderLength ||
                WhiteboardSnapshotCodec.ReadInt(snapshot, 0) != Magic)
            {
                error = "Chess snapshot header is invalid.";
                return false;
            }
            int count = WhiteboardSnapshotCodec.ReadInt(snapshot, 4);
            if (count < 0 || count > 128)
            {
                error = "Chess snapshot piece count is invalid.";
                return false;
            }
            var candidate = new ChessPiecePose[count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int offset = HeaderLength;
            try
            {
                for (int index = 0; index < count; ++index)
                {
                    if (offset + 2 > snapshot.Length)
                        throw new ArgumentException();
                    int idLength = snapshot[offset++] | snapshot[offset++] << 8;
                    if (idLength == 0 || idLength > 128 || offset + idLength + PoseLength > snapshot.Length)
                        throw new ArgumentException();
                    string id = StrictUtf8.GetString(snapshot, offset, idLength);
                    offset += idLength;
                    Vector3 position = new Vector3(ReadFloat(snapshot, ref offset),
                                                   ReadFloat(snapshot, ref offset),
                                                   ReadFloat(snapshot, ref offset));
                    Quaternion rotation = new Quaternion(ReadFloat(snapshot, ref offset),
                                                         ReadFloat(snapshot, ref offset),
                                                         ReadFloat(snapshot, ref offset),
                                                         ReadFloat(snapshot, ref offset));
                    if (!FreeformValidation.ValidPiece(id) ||
                        !FreeformValidation.ValidPose(position, rotation) || !seen.Add(id))
                        throw new ArgumentException();
                    candidate[index] = new ChessPiecePose(id, position, rotation);
                }
            }
            catch (ArgumentException)
            {
                error = "Chess snapshot payload is invalid.";
                return false;
            }
            if (offset != snapshot.Length)
            {
                error = "Chess snapshot has trailing bytes.";
                return false;
            }
            pieces = candidate;
            error = null;
            return true;
        }

        private static bool TryValidatePieces(IReadOnlyList<ChessPiecePose> pieces,
                                              out byte[][] ids, out int totalLength,
                                              out string error)
        {
            ids = null;
            totalLength = HeaderLength;
            if (pieces == null || pieces.Count > 128)
            {
                error = "Chess snapshot has an invalid piece count.";
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ids = new byte[pieces.Count][];
            for (int index = 0; index < pieces.Count; ++index)
            {
                ChessPiecePose piece = pieces[index];
                if (!FreeformValidation.ValidPiece(piece.PieceId) ||
                    !FreeformValidation.ValidPose(piece.BoardLocalPosition,
                                                  piece.BoardLocalRotation) || !seen.Add(piece.PieceId))
                {
                    error = "Chess snapshot contains an invalid or duplicate piece.";
                    return false;
                }
                byte[] id = StrictUtf8.GetBytes(piece.PieceId);
                if (id.Length == 0 || id.Length > 128 || totalLength > int.MaxValue - 2 - id.Length - PoseLength)
                {
                    error = "Chess piece ID is too long.";
                    return false;
                }
                ids[index] = id;
                totalLength += 2 + id.Length + PoseLength;
            }
            error = null;
            return true;
        }

        private static void WriteFloat(byte[] bytes, ref int offset, float value)
        {
            WhiteboardSnapshotCodec.WriteInt(bytes, offset, BitConverter.SingleToInt32Bits(value));
            offset += 4;
        }

        private static float ReadFloat(byte[] bytes, ref int offset)
        {
            float value = BitConverter.Int32BitsToSingle(
                WhiteboardSnapshotCodec.ReadInt(bytes, offset));
            offset += 4;
            return value;
        }
    }

    internal static class FreeformValidation
    {
        internal static bool ValidContentId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool ValidPiece(string value)
        {
            if (!ValidContentId(value))
                return false;
            try
            {
                return StrictUtf8.GetByteCount(value) <= 128;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
        internal static bool ValidUv(Vector2 value) =>
            IsFinite(value) && value.x >= 0f && value.x <= 1f && value.y >= 0f && value.y <= 1f;
        internal static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        internal static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        internal static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        internal static bool ValidPose(Vector3 position, Quaternion rotation)
        {
            float rotationMagnitudeSquared = rotation.x * rotation.x + rotation.y * rotation.y +
                                             rotation.z * rotation.z + rotation.w * rotation.w;
            return IsFinite(position) && IsFinite(rotation) && rotationMagnitudeSquared > 1e-8f &&
                   IsFinite(rotationMagnitudeSquared);
        }
        internal static bool ValidTransform(Transform transform)
        {
            if (transform == null)
                return false;
            Matrix4x4 matrix = transform.localToWorldMatrix;
            for (int index = 0; index < 16; ++index)
                if (!IsFinite(matrix[index]))
                    return false;
            return IsFinite(matrix.determinant) && Mathf.Abs(matrix.determinant) > 1e-8f;
        }
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
