// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.Input;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// ISelector implementation that fires events when a configurable button is pressed.
    /// Default is PrimaryButton (A button on right controller, X on left).
    /// Used by ManipulationPanelMotif to detect button presses through ISDK events.
    /// Add this component alongside the ControllerRayInteractor in the scene.
    /// </summary>
    public class CustomButtonSelector : MonoBehaviour, ISelector
    {
        [SerializeField, Interface(typeof(IController))]
        private UnityEngine.Object _controller;
        private IController Controller;

        [SerializeField, Tooltip("Which button(s) to monitor. Default is PrimaryButton (A/X).")]
        private ControllerButtonUsage _buttonUsage = ControllerButtonUsage.PrimaryButton;

        private bool _selected = false;

        public event Action WhenSelected = delegate { };
        public event Action WhenUnselected = delegate { };

        /// <summary>
        /// The button usage being monitored.
        /// </summary>
        public ControllerButtonUsage ButtonUsage => _buttonUsage;

        protected virtual void Awake()
        {
            Controller = _controller as IController;
        }

        protected virtual void Start()
        {
            // If not assigned, try to find from InteractionModeManager's interactor
            if (Controller == null)
            {
                var interactor = InteractionModeManager.Interactor;
                if (interactor != null)
                {
                    // Try to find IController in parent hierarchy
                    Controller = interactor.GetComponentInParent<IController>();
                }
            }

            if (Controller == null)
            {
                Debug.LogWarning($"[CustomButtonSelector:{name}] No IController found. Button detection won't work.");
            }
        }

        protected virtual void Update()
        {
            if (Controller == null) return;

            bool isPressed = Controller.IsButtonUsageAnyActive(_buttonUsage);

            if (isPressed && !_selected)
            {
                _selected = true;
                WhenSelected();
            }
            else if (!isPressed && _selected)
            {
                _selected = false;
                WhenUnselected();
            }
        }

        /// <summary>
        /// Manually inject the controller reference.
        /// </summary>
        public void InjectController(IController controller)
        {
            Controller = controller;
        }

        /// <summary>
        /// Change the button being monitored at runtime.
        /// </summary>
        public void SetButtonUsage(ControllerButtonUsage buttonUsage)
        {
            _buttonUsage = buttonUsage;
        }
    }
}
