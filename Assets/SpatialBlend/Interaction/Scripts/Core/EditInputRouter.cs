// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Connects the scene's two editing rays to <see cref="EditInput"/>: the right controller's <see cref="RayInteractor"/> (the one
    /// <see cref="InteractionModeManager"/> is given) and the right hand's (the <c>HandRayInteractor</c> of the Interaction SDK rig, found at run time
    /// from its hand reference unless the manager is given one). It forwards each interactor's hover and select events, and ticks the input once per
    /// frame before the tools run. <see cref="InteractionModeManager"/> adds it, so a scene needs nothing new.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class EditInputRouter : MonoBehaviour
    {
        private sealed class Link
        {
            public EditPointer Pointer;
            public Action<RayInteractable> Hover, Unhover, Select, Unselect;
        }

        private Link _controller, _hand;
        private float _nextSearch;

        private void Update()
        {
            if (_controller == null || _hand == null) TryLink();
            EditInput.Tick();
        }

        private void LateUpdate() => EditInput.EndFrame();

        private void TryLink()
        {
            if (Time.unscaledTime < _nextSearch) return;
            // Once the controller is linked, a scene with no hand rig (Foundation, desktop) is searched only now and then.
            _nextSearch = Time.unscaledTime + (_controller != null ? 5f : 1f);

            var controllerRay = InteractionModeManager.Interactor;
            if (_controller == null && controllerRay != null)
                _controller = Connect(new EditPointer(EditInputSource.Controller, controllerRay));
            if (_hand == null)
            {
                var handRay = InteractionModeManager.HandInteractor ?? FindRightHandRay(controllerRay);
                if (handRay != null) _hand = Connect(new EditPointer(EditInputSource.Hand, handRay));
            }
        }

        private static RayInteractor FindRightHandRay(RayInteractor except)
        {
            foreach (var ray in FindObjectsByType<RayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (ray == except) continue;
                var hand = ray.GetComponentInParent<IHand>();
                if (hand != null && hand.Handedness == Handedness.Right) return ray;
            }
            return null;
        }

        private static Link Connect(EditPointer pointer)
        {
            var interactor = pointer.Interactor;
            var link = new Link
            {
                Pointer = pointer,
                Hover = i => EditInput.RawHover(pointer, i),
                Unhover = i => EditInput.RawUnhover(pointer, i),
                Select = i => EditInput.RawSelect(pointer, i),
                Unselect = i => EditInput.RawUnselect(pointer, i),
            };
            interactor.WhenInteractableSet.Action += link.Hover;
            interactor.WhenInteractableUnset.Action += link.Unhover;
            interactor.WhenInteractableSelected.Action += link.Select;
            interactor.WhenInteractableUnselected.Action += link.Unselect;
            EditInput.Register(pointer);
            return link;
        }

        private static void Disconnect(Link link)
        {
            if (link == null) return;
            var interactor = link.Pointer.Interactor;
            if (interactor != null)
            {
                interactor.WhenInteractableSet.Action -= link.Hover;
                interactor.WhenInteractableUnset.Action -= link.Unhover;
                interactor.WhenInteractableSelected.Action -= link.Select;
                interactor.WhenInteractableUnselected.Action -= link.Unselect;
            }
            EditInput.Unregister(link.Pointer);
        }

        private void OnDestroy()
        {
            Disconnect(_controller);
            Disconnect(_hand);
            _controller = _hand = null;
        }
    }
}
