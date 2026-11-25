// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.Samples;
using UnityEngine;

namespace SpatialBlend.Samples.Objects.Whiteboard
{
    public enum InteractionMode
    {
        None,
        DrawingPointer,
        DrawingRaycast,
        PanelManipulation
    }

    public class InteractionStateManagerMotif : MonoBehaviour
    {
        public sealed class ModeLease : System.IDisposable
        {
            private InteractionStateManagerMotif _owner;
            private readonly InteractionMode _mode;

            internal ModeLease(InteractionStateManagerMotif owner, InteractionMode mode)
            {
                _owner = owner;
                _mode = mode;
            }

            public bool IsCurrent => _owner != null && _owner.IsCurrentLease(this, _mode);

            public void Dispose()
            {
                if (_owner == null)
                    return;
                _owner.ReleaseLease(this, _mode);
                _owner = null;
            }
        }

        public static InteractionStateManagerMotif Instance { get; private set; }

        private InteractionMode _currentMode = InteractionMode.None;
        private ModeLease _currentLease;

        public InteractionMode CurrentMode
        {
            get => _currentMode;
            set
            {
                InvalidateLease();
                _currentMode = value;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public bool CanDrawWithPointer() => CurrentMode == InteractionMode.None;
        public bool CanDrawWithRaycast() => CurrentMode == InteractionMode.None;
        public bool CanManipulatePanel() => CurrentMode == InteractionMode.None;

        public bool TryAcquireMode(InteractionMode mode, out ModeLease lease)
        {
            lease = null;
            if (mode == InteractionMode.None || _currentMode != InteractionMode.None)
                return false;
            lease = new ModeLease(this, mode);
            _currentLease = lease;
            _currentMode = mode;
            return true;
        }

        public void SetMode(InteractionMode mode)
        {
            CurrentMode = mode;
        }

        public void ResetMode(InteractionMode mode)
        {
            if (_currentMode != mode)
                return;
            InvalidateLease();
            _currentMode = InteractionMode.None;
        }

        private bool IsCurrentLease(ModeLease lease, InteractionMode mode)
        {
            return ReferenceEquals(_currentLease, lease) && _currentMode == mode;
        }

        private void ReleaseLease(ModeLease lease, InteractionMode mode)
        {
            if (!IsCurrentLease(lease, mode))
                return;
            _currentLease = null;
            _currentMode = InteractionMode.None;
        }

        private void InvalidateLease()
        {
            _currentLease = null;
        }
    }
}
