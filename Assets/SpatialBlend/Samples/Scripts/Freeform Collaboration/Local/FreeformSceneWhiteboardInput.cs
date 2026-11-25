// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Freeform;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// Adapts the authored whiteboard pointer to a bound local Freeform model.
    /// It owns only its drawing mode and has no transport or authority behavior.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneWhiteboardInput : MonoBehaviour
    {
        private WhiteboardContentView _view;
        private WhiteboardContentModel _model;
        private PointerHandlerMotif _pointer;
        private InteractionStateManagerMotif _modes;
        private Color _penColor = Color.blue;
        private int _brushRadius = 6;
        private bool _isDrawing;
        private Vector3 _lastPoint;
        private Vector2 _lastUv;
        private int _configurationVersion;
        private InteractionStateManagerMotif.ModeLease _modeLease;

        public bool Configure(WhiteboardContentView view, PointerHandlerMotif pointer,
                              InteractionStateManagerMotif modes, out string error)
        {
            if (view == null || !view.isActiveAndEnabled || view.Model == null || !view.Model.IsLive ||
                pointer == null || modes == null)
            {
                error = "A bound live whiteboard view, pointer, and interaction state manager are required.";
                return false;
            }

            // Validate the candidate before ending a valid, already configured stroke.
            ReleaseDrawing();
            _view = view;
            _model = view.Model;
            _pointer = pointer;
            _modes = modes;
            ++_configurationVersion;
            error = null;
            return true;
        }

        public void SetPenColor(Color color, int brushRadius = 6)
        {
            _penColor = color;
            _brushRadius = brushRadius;
        }

        public bool ProcessPointer(Vector3 hitPoint, bool isHovered, bool isSelected, out string error)
        {
            if (!HasLiveView())
            {
                ReleaseDrawing();
                error = "A configured, enabled whiteboard view bound to a live model is required.";
                return false;
            }
            if (!IsFinite(hitPoint))
            {
                ReleaseDrawing();
                error = "Whiteboard pointer hits must be finite.";
                return false;
            }

            bool inputDown = isHovered && isSelected && !_isDrawing;
            bool inputHeld = isHovered && isSelected;
            bool inputUp = _isDrawing && (!isHovered || !isSelected);

            if (_isDrawing && (_modeLease == null || !_modeLease.IsCurrent))
            {
                ReleaseDrawing();
                error = "Drawing was cancelled because its interaction-mode lease changed.";
                return false;
            }
            if (inputUp)
            {
                ReleaseDrawing();
                error = null;
                return true;
            }
            if (inputDown)
            {
                if (!_modes.CanDrawWithPointer())
                {
                    error = "The current interaction mode does not allow pointer drawing.";
                    return false;
                }
                WhiteboardContentView view = _view;
                WhiteboardContentModel model = _model;
                InteractionStateManagerMotif modes = _modes;
                int version = _configurationVersion;
                Vector2 hitUv = view.WorldToUv(hitPoint);
                if (!modes.TryAcquireMode(InteractionMode.DrawingPointer,
                                          out InteractionStateManagerMotif.ModeLease lease))
                {
                    error = "The current interaction mode does not allow pointer drawing.";
                    return false;
                }
                // The model publishes Changed synchronously. Stage ownership before
                // invoking it so a callback can safely release or reconfigure us.
                _isDrawing = true;
                _modeLease = lease;
                _lastPoint = hitPoint;
                _lastUv = hitUv;
                if (!view.TryDrawWorldLine(hitPoint, hitPoint, _penColor, _brushRadius, out error))
                {
                    ReleaseCurrentStroke(version, view, model, modes, lease);
                    return false;
                }
                if (!IsCurrentStroke(version, view, model, modes, lease))
                {
                    error = "Drawing ownership changed while the whiteboard model updated.";
                    return false;
                }
                return true;
            }
            if (inputHeld && _isDrawing)
            {
                WhiteboardContentView view = _view;
                WhiteboardContentModel model = _model;
                InteractionStateManagerMotif modes = _modes;
                int version = _configurationVersion;
                InteractionStateManagerMotif.ModeLease lease = _modeLease;
                Vector2 hitUv = view.WorldToUv(hitPoint);
                if (Vector2.Distance(_lastUv, hitUv) <= .001f)
                {
                    error = null;
                    return true;
                }
                if (!view.TryDrawWorldLine(_lastPoint, hitPoint, _penColor, _brushRadius, out error))
                {
                    ReleaseCurrentStroke(version, view, model, modes, lease);
                    return false;
                }
                if (!IsCurrentStroke(version, view, model, modes, lease))
                {
                    error = "Drawing ownership changed while the whiteboard model updated.";
                    return false;
                }
                _lastPoint = hitPoint;
                _lastUv = hitUv;
            }

            error = null;
            return true;
        }

        public void ReleaseDrawing()
        {
            if (!_isDrawing)
                return;
            _isDrawing = false;
            InteractionStateManagerMotif.ModeLease lease = _modeLease;
            _modeLease = null;
            lease?.Dispose();
        }

        private void LateUpdate()
        {
            if (_pointer == null)
            {
                ReleaseDrawing();
                return;
            }
            ProcessPointer(_pointer.HitPoint,
                           _pointer.IsHovered && _pointer.HasValidHit,
                           _pointer.IsSelected, out _);
        }

        private void OnDisable() => ReleaseDrawing();
        private void OnDestroy() => ReleaseDrawing();

        private bool HasLiveView()
        {
            return isActiveAndEnabled && _pointer != null && _pointer.isActiveAndEnabled &&
                   _view != null && _view.isActiveAndEnabled && _view.Model == _model &&
                   _model != null && _model.IsLive && _modes != null;
        }

        private bool IsCurrentStroke(int version, WhiteboardContentView view,
                                     WhiteboardContentModel model, InteractionStateManagerMotif modes,
                                     InteractionStateManagerMotif.ModeLease lease)
        {
            return _isDrawing && _configurationVersion == version && _view == view &&
                   _model == model && _modes == modes && ReferenceEquals(_modeLease, lease) &&
                   lease != null && lease.IsCurrent && HasLiveView();
        }

        private void ReleaseCurrentStroke(int version, WhiteboardContentView view,
                                          WhiteboardContentModel model,
                                          InteractionStateManagerMotif modes,
                                          InteractionStateManagerMotif.ModeLease lease)
        {
            if (IsCurrentStroke(version, view, model, modes, lease))
                ReleaseDrawing();
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }
    }
}
