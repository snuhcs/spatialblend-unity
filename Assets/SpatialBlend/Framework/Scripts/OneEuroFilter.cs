// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Low-pass filter for smoothing signals while minimizing latency.
    /// Automatically adjusts filtering based on signal speed.
    /// </summary>
    public class LowPassFilter
    {
        private float _y;
        private float _a;
        private bool _initialized;

        public LowPassFilter(float alpha)
        {
            _a = alpha;
            _initialized = false;
        }

        public void SetAlpha(float alpha)
        {
            _a = Mathf.Clamp01(alpha);
        }

        public float Filter(float value)
        {
            if (!_initialized)
            {
                _y = value;
                _initialized = true;
            }
            else
            {
                _y = _a * value + (1.0f - _a) * _y;
            }
            return _y;
        }

        public float LastValue => _y;

        public void Reset()
        {
            _initialized = false;
        }
    }

    /// <summary>
    /// One Euro Filter implementation for Unity.
    /// Reduces jitter while maintaining responsiveness during fast movements.
    /// 
    /// Reference: "1€ Filter: A Simple Speed-based Low-pass Filter for Noisy Input in Interactive Systems"
    /// by Géry Casiez, Nicolas Roussel, and Daniel Vogel (CHI 2012)
    /// </summary>
    public class OneEuroFilter
    {
        private float _minCutoff;
        private float _beta;
        private float _dCutoff;
        private LowPassFilter _xFilter;
        private LowPassFilter _dxFilter;
        private float _lastTime;
        private bool _initialized;

        /// <summary>
        /// Creates a new One Euro Filter.
        /// </summary>
        /// <param name="minCutoff">Minimum cutoff frequency (Hz). Lower = smoother but more lag. Recommended: 0.5-2.0</param>
        /// <param name="beta">Speed coefficient. Higher = faster response to quick movements. Recommended: 0.0-1.0</param>
        /// <param name="dCutoff">Cutoff frequency for derivative (Hz). Usually 1.0 works well.</param>
        public OneEuroFilter(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
            _xFilter = new LowPassFilter(1.0f);
            _dxFilter = new LowPassFilter(1.0f);
            _initialized = false;
        }

        private float ComputeAlpha(float cutoff, float dt)
        {
            float tau = 1.0f / (2.0f * Mathf.PI * cutoff);
            return 1.0f / (1.0f + tau / dt);
        }

        /// <summary>
        /// Filters the input value.
        /// </summary>
        /// <param name="x">Raw input value</param>
        /// <param name="dt">Delta time since last call (seconds)</param>
        /// <returns>Filtered value</returns>
        public float Filter(float x, float dt)
        {
            if (dt <= 0)
            {
                dt = 1.0f / 60.0f; // Default to 60 FPS if dt is invalid
            }

            // Estimate derivative
            float dx = _initialized ? (x - _xFilter.LastValue) / dt : 0.0f;
            _initialized = true;

            // Filter the derivative
            float edx = _dxFilter.Filter(dx);
            _dxFilter.SetAlpha(ComputeAlpha(_dCutoff, dt));

            // Compute cutoff based on speed (adaptive filtering)
            float cutoff = _minCutoff + _beta * Mathf.Abs(edx);

            // Filter the signal
            _xFilter.SetAlpha(ComputeAlpha(cutoff, dt));
            return _xFilter.Filter(x);
        }

        /// <summary>
        /// Updates filter parameters at runtime.
        /// </summary>
        public void UpdateParams(float minCutoff, float beta, float dCutoff)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        /// <summary>
        /// Resets the filter state.
        /// </summary>
        public void Reset()
        {
            _xFilter.Reset();
            _dxFilter.Reset();
            _initialized = false;
        }
    }

    /// <summary>
    /// One Euro Filter for Vector3 values (position).
    /// </summary>
    public class OneEuroFilterVector3
    {
        private OneEuroFilter _xFilter;
        private OneEuroFilter _yFilter;
        private OneEuroFilter _zFilter;

        public OneEuroFilterVector3(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            _xFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
            _yFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
            _zFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
        }

        public Vector3 Filter(Vector3 v, float dt)
        {
            return new Vector3(
                _xFilter.Filter(v.x, dt),
                _yFilter.Filter(v.y, dt),
                _zFilter.Filter(v.z, dt)
            );
        }

        public void UpdateParams(float minCutoff, float beta, float dCutoff)
        {
            _xFilter.UpdateParams(minCutoff, beta, dCutoff);
            _yFilter.UpdateParams(minCutoff, beta, dCutoff);
            _zFilter.UpdateParams(minCutoff, beta, dCutoff);
        }

        public void Reset()
        {
            _xFilter.Reset();
            _yFilter.Reset();
            _zFilter.Reset();
        }
    }
}
