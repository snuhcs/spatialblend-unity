/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
// Modified by the SpatialBlend authors; this file stays under the license above (see NOTICE).
using Oculus.Avatar2;
using UnityEngine;
using Node = UnityEngine.XR.XRNode;
using SpatialBlend;

/*
 * MetaInputTrackingDelegate for SpatialBlend with Fusion networking
 */
public class MetaInputTrackingDelegate : OvrAvatarInputTrackingDelegate
{

    private OVRCameraRig? _ovrCameraRig = null;

    // Reference to SbAvatarBehaviourFusion for networking
    private SbAvatarBehaviourFusion _avatarBehaviour;

    private float _lastHandUpdateTime = 0f;
    private float handUpdateInterval = 0.05f;

    private readonly bool _localOnly;
    private bool _warnedNoBehaviour;

    /// <summary>The real tracking path without the network: for a local avatar that sends nothing (the lobby preview).</summary>
    public MetaInputTrackingDelegate(OVRCameraRig? ovrCameraRig) : this(ovrCameraRig, null)
    {
        _localOnly = true;
    }

    public MetaInputTrackingDelegate(OVRCameraRig? ovrCameraRig, SbAvatarBehaviourFusion? avatarBehaviour)
    {
        _ovrCameraRig = ovrCameraRig;
        _avatarBehaviour = avatarBehaviour;
    }

    /// <summary>
    /// Updates hand positions from OVRPlugin with head-relative coordinates
    /// </summary>
    private void UpdateHandPositionsToFusion(Vector3 headPos, Vector3 leftHandPos, Vector3 rightHandPos, Quaternion headRot, Quaternion leftHandRot, Quaternion rightHandRot)
    {
        if (_avatarBehaviour == null)
        {
            if (_localOnly || _warnedNoBehaviour) return;
            _warnedNoBehaviour = true; // once: this runs every tracking frame
            Debug.LogWarning("MetaInputTrackingDelegate: _avatarBehaviour is null; skipping hand updates.");
            return;
        }

        // Check if enough time has passed since last update
        if (Time.time - _lastHandUpdateTime < handUpdateInterval)
        {
            return;
        }

        // Calculate head-relative coordinates
        Vector3 headForward = headRot * Vector3.forward;
        Vector3 headRight = headRot * Vector3.right;
        Vector3 headUp = headRot * Vector3.up;

        // Calculate relative positions
        Vector3 leftHandRelativePos = leftHandPos - headPos;
        Vector3 rightHandRelativePos = rightHandPos - headPos;

        // Convert to head-relative local coordinates
        Vector3 leftHandLocalPos = new Vector3(
            Vector3.Dot(leftHandRelativePos, headRight),
            Vector3.Dot(leftHandRelativePos, headUp),
            Vector3.Dot(leftHandRelativePos, headForward)
        );

        Vector3 rightHandLocalPos = new Vector3(
            Vector3.Dot(rightHandRelativePos, headRight),
            Vector3.Dot(rightHandRelativePos, headUp),
            Vector3.Dot(rightHandRelativePos, headForward)
        );

        Quaternion leftHandRelativeRot = Quaternion.Inverse(headRot) * leftHandRot;
        Quaternion rightHandRelativeRot = Quaternion.Inverse(headRot) * rightHandRot;

        // Send to Fusion network
        _avatarBehaviour.UpdateHandPositions(leftHandLocalPos, rightHandLocalPos, leftHandRelativeRot, rightHandRelativeRot);

        // Update last values and time
        _lastHandUpdateTime = Time.time;
    }

    public override bool GetRawInputTrackingState(out OvrAvatarInputTrackingState inputTrackingState)
    {
        inputTrackingState = default;
        bool leftControllerActive = false;
        bool rightControllerActive = false;
        if (OVRInput.GetActiveController() != OVRInput.Controller.Hands)
        {
            leftControllerActive = OVRInput.GetControllerOrientationTracked(OVRInput.Controller.LTouch);
            rightControllerActive = OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch);
        }

        if (_ovrCameraRig is not null)
        {
            inputTrackingState.headsetActive = true;
            inputTrackingState.leftControllerActive = leftControllerActive;
            inputTrackingState.rightControllerActive = rightControllerActive;
            inputTrackingState.leftControllerVisible = false;
            inputTrackingState.rightControllerVisible = false;
            inputTrackingState.headset = (CAPI.ovrAvatar2Transform)_ovrCameraRig.centerEyeAnchor;
            inputTrackingState.leftController = (CAPI.ovrAvatar2Transform)_ovrCameraRig.leftHandAnchor;
            inputTrackingState.rightController = (CAPI.ovrAvatar2Transform)_ovrCameraRig.rightHandAnchor;
            UpdateHandPositionsToFusion(inputTrackingState.headset.position, inputTrackingState.leftController.position, inputTrackingState.rightController.position, 
                                        inputTrackingState.headset.orientation, inputTrackingState.leftController.orientation, inputTrackingState.rightController.orientation);
            return true;
        }
        else if (OVRNodeStateProperties.IsHmdPresent())
        {
            inputTrackingState.headsetActive = true;
            inputTrackingState.leftControllerActive = leftControllerActive;
            inputTrackingState.rightControllerActive = rightControllerActive;
            inputTrackingState.leftControllerVisible = true;
            inputTrackingState.rightControllerVisible = true;

            if (OVRNodeStateProperties.GetNodeStatePropertyVector3(Node.CenterEye, NodeStatePropertyType.Position,
                OVRPlugin.Node.EyeCenter, OVRPlugin.Step.Render, out var headPos))
            {
                inputTrackingState.headset.position = headPos;
            }
            else
            {
                inputTrackingState.headset.position = Vector3.zero;
            }

            if (OVRNodeStateProperties.GetNodeStatePropertyQuaternion(Node.CenterEye, NodeStatePropertyType.Orientation,
                OVRPlugin.Node.EyeCenter, OVRPlugin.Step.Render, out var headRot))
            {
                inputTrackingState.headset.orientation = headRot;
            }
            else
            {
                inputTrackingState.headset.orientation = Quaternion.identity;
            }

            inputTrackingState.headset.scale = Vector3.one;

            inputTrackingState.leftController.position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
            inputTrackingState.rightController.position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            inputTrackingState.leftController.orientation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);
            inputTrackingState.rightController.orientation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
            inputTrackingState.leftController.scale = Vector3.one;
            inputTrackingState.rightController.scale = Vector3.one;
            
            // Update hand positions to Fusion network
            UpdateHandPositionsToFusion(inputTrackingState.headset.position, inputTrackingState.leftController.position, inputTrackingState.rightController.position, 
                                        inputTrackingState.headset.orientation, inputTrackingState.leftController.orientation, inputTrackingState.rightController.orientation);
            
            return true;
        }

        return false;
    }
}
