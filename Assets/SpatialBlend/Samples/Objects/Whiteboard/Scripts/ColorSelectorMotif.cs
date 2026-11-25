using System;
using UnityEngine;
using Oculus.Interaction;
using SpatialBlend.Samples.Objects.Whiteboard;
using SpatialBlend.Samples.FreeformCollaboration;


/// <summary>
/// Changes the pen color of the PointerDrawingMotif in the scene
/// when this GameObject is selected.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ColorSelectorMotif : MonoBehaviour
{
    private sealed class LocalPenLease : IDisposable
    {
        private ColorSelectorMotif _owner;
        private readonly FreeformSceneWhiteboardInput _pen;
        private readonly object _token;

        public LocalPenLease(ColorSelectorMotif owner, FreeformSceneWhiteboardInput pen,
                             object token)
        {
            _owner = owner;
            _pen = pen;
            _token = token;
        }

        public void Dispose()
        {
            if (_owner == null)
                return;
            _owner.ReleaseLocalPen(_pen, _token);
            _owner = null;
        }
    }

    [Tooltip("The color to apply when this object is selected.")]
    [SerializeField]
    private Color selectionColor = Color.white;
    [SerializeField]
    private int selectionBrushRadius = 6;
    [SerializeField]
    private RayInteractable m_rayInteractable;
    [SerializeField]
    private OVRHand m_leftHand;
    [SerializeField]
    private OVRHand m_rightHand;
    [SerializeField]
    private PointerDrawingMotif m_pointerDrawingMotif;

    private FreeformSceneWhiteboardInput _localPen;
    private object _localPenLeaseToken;

    private bool m_wasLeftSelecting;
    private bool m_wasRightSelecting;

    public bool TryBindLocalPen(FreeformSceneWhiteboardInput pen, out IDisposable lease,
                                out string error)
    {
        lease = null;
        if (pen == null)
        {
            error = "A live local whiteboard pen is required.";
            return false;
        }
        if (_localPen != null)
        {
            error = "This color selector already owns a local pen lease.";
            return false;
        }
        object token = new object();
        _localPen = pen;
        _localPenLeaseToken = token;
        lease = new LocalPenLease(this, pen, token);
        error = null;
        return true;
    }

    private void Start()
    {
        if (m_rayInteractable == null)
        {
            m_rayInteractable = GetComponent<RayInteractable>();
        }

        if (m_pointerDrawingMotif == null)
        {
            Debug.LogError("PointerDrawingMotif not found in the scene.");
        }

        if (m_rayInteractable == null)
        {
            Debug.LogError("RayInteractable not found on this GameObject.");
        }
    }

    private void Update()
    {
        // A missing RayInteractable was reported once at Start.
        if (m_rayInteractable == null || m_rayInteractable.State != InteractableState.Hover)
        {
            return;
        }

        bool leftSelectDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        bool rightSelectDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        bool activeSelectDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.Active);
        bool controllerSelectDown = leftSelectDown || rightSelectDown || activeSelectDown;
        bool handSelectDown = false;

        if (!controllerSelectDown)
        {
            if (m_leftHand != null && m_leftHand.IsTracked)
            {
                bool isLeftPinching = m_leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                handSelectDown = isLeftPinching && !m_wasLeftSelecting;
                m_wasLeftSelecting = isLeftPinching;
            }

            if (m_rightHand != null && m_rightHand.IsTracked)
            {
                bool isRightPinching = m_rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                handSelectDown = handSelectDown || (isRightPinching && !m_wasRightSelecting);
                m_wasRightSelecting = isRightPinching;
            }
        }

        if (controllerSelectDown || handSelectDown)
        {
            ChangePenColor();
        }
    }

    /// <summary>
    /// Public method to change the pen color.
    /// Can be called from events, such as the InteractableUnityEventWrapper.
    /// </summary>
    public void ChangePenColor()
    {
        if (_localPen != null)
        {
            _localPen.SetPenColor(selectionColor, selectionBrushRadius);
            return;
        }
        if (m_pointerDrawingMotif != null)
        {
            m_pointerDrawingMotif.SetPenColor(selectionColor, selectionBrushRadius);
        }
        else
        {
            Debug.LogError("PointerDrawingMotif reference is not set.");
        }
    }

    private void OnDestroy()
    {
        ReleaseLocalPen(_localPen, _localPenLeaseToken);
    }

    private void ReleaseLocalPen(FreeformSceneWhiteboardInput pen, object token)
    {
        if (!ReferenceEquals(_localPen, pen) || !ReferenceEquals(_localPenLeaseToken, token))
            return;
        _localPen = null;
        _localPenLeaseToken = null;
    }
}
