// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Free/orbit camera of the desktop spectator: right mouse orbits around a pivot, WASD flies the pivot along the view,
    /// Q/E lowers and raises it, the scroll wheel zooms, F frames the viewed room (else the received content). It reads only the keyboard and mouse.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class SpectatorCamera : MonoBehaviour
    {
        [SerializeField] private float _orbitSpeed = 3f;
        [SerializeField] private float _flySpeed = 3f;
        [SerializeField] private float _zoomSpeed = 1.2f;
        [SerializeField] private float _distance = 4f;

        private Vector3 _pivot = new Vector3(0f, 1f, 0f);
        private float _yaw = 30f, _pitch = 20f;

        public Vector3 Pivot => _pivot;
        public float Distance => _distance;

        private void Start() => Apply();

        /// <summary>Pivots on the middle of <paramref name="bounds"/> and backs off so all of it is in view.</summary>
        public void Frame(Bounds bounds)
        {
            _pivot = bounds.center;
            _distance = Mathf.Clamp(bounds.extents.magnitude * 2.2f, 1f, 200f);
            Apply();
        }

        private void Update()
        {
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * _orbitSpeed;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * _orbitSpeed, -85f, 85f);
            }
            // Keys typed into the room field are text, not flying: the camera reads no key while a field has the focus.
            bool typing = TypingInTextField();
            Func<KeyCode, bool> held = HeldKeys(typing, Input.GetKey);
            var move = KeyMove(held, transform);
            if (move != Vector3.zero)
            {
                var boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
                _pivot += move.normalized * _flySpeed * boost * Time.unscaledDeltaTime;
            }
            var scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) _distance = Mathf.Clamp(_distance * Mathf.Pow(1f / _zoomSpeed, scroll), 0.3f, 300f);
            if (KeyPressed(typing, Input.GetKeyDown, KeyCode.F))
            {
                // The viewed room when one is drawn, else the received content.
                var view = FindFirstObjectByType<SpectatorRoomView>();
                var app = FindFirstObjectByType<FreeformSpectatorApp>();
                if (view != null && view.TryGetBounds(out var room)) Frame(room);
                else if (app != null && app.TryGetContentBounds(out var bounds)) Frame(bounds);
            }
            Apply();
        }

        /// <summary>The keys the camera sees: none while a text field has the keyboard.</summary>
        public static Func<KeyCode, bool> HeldKeys(bool typing, Func<KeyCode, bool> raw) => key => !typing && raw(key);

        /// <summary>A key press (F, frame content) the camera acts on: never one typed into a text field.</summary>
        public static bool KeyPressed(bool typing, Func<KeyCode, bool> raw, KeyCode key) => !typing && raw(key);

        /// <summary>The direction the held keys ask for (WASD along the view, Q/E down and up); zero when none is held.</summary>
        public static Vector3 KeyMove(Func<KeyCode, bool> held, Transform view)
        {
            var move = Vector3.zero;
            if (held(KeyCode.W)) move += view.forward;
            if (held(KeyCode.S)) move -= view.forward;
            if (held(KeyCode.D)) move += view.right;
            if (held(KeyCode.A)) move -= view.right;
            if (held(KeyCode.E)) move += Vector3.up;
            if (held(KeyCode.Q)) move -= Vector3.up;
            return move;
        }

        /// <summary>True while a text field (the overlay's room field) has the keyboard.</summary>
        public static bool TypingInTextField() => TypingInTextField(EventSystem.current);

        public static bool TypingInTextField(EventSystem events)
        {
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null) return false;
            var field = selected.GetComponent<TMP_InputField>();
            return field != null && field.isFocused;
        }

        private void Apply()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_pivot - rotation * Vector3.forward * _distance, rotation);
        }
    }
}
