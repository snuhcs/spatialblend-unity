// Copyright (c) Meta Platforms, Inc. and affiliates.

#if FUSION2
using Fusion;
using Meta.XR.Samples;
using UnityEngine;

namespace SpatialBlend.Samples.Objects.Whiteboard
{
    [RequireComponent(typeof(PointerHandlerMotif))]
    public class PointerDrawingMotif : NetworkBehaviour
    {
        [SerializeField] private Color penColor = Color.blue;
        [SerializeField] private int brushRadius = 6;

        private bool m_isReady;
        private bool m_isDrawing;
        private Vector2 m_lastUV;
        private PointerHandlerMotif m_pointerHandlerMotif;
        private WhiteboardManagerMotif m_whiteboardManagerMotif;

        public void SetPenColor(Color newColor, int newBrushRadius = 6)
        {
            penColor = newColor;
            brushRadius = newBrushRadius;
        }

        public override void Spawned()
        {
            base.Spawned();
            m_pointerHandlerMotif = GetComponent<PointerHandlerMotif>();
            m_whiteboardManagerMotif = WhiteboardManagerMotif.Instance;
            m_isReady = true;
        }

        private void Update()
        {
            if (!m_isReady) return;

            if (!m_whiteboardManagerMotif)
            {
                m_whiteboardManagerMotif = WhiteboardManagerMotif.Instance;
            }

            InteractionStateManagerMotif interactionState = InteractionStateManagerMotif.Instance;
            if (!m_pointerHandlerMotif || !m_whiteboardManagerMotif || !interactionState)
            {
                ReleaseDrawing();
                return;
            }

            HandlePointerDrawing(
                m_pointerHandlerMotif.HitPoint,
                m_pointerHandlerMotif.IsHovered && m_pointerHandlerMotif.HasValidHit,
                m_pointerHandlerMotif.IsSelected,
                ref m_isDrawing,
                ref m_lastUV,
                InteractionMode.DrawingPointer,
                interactionState
            );
        }

        private void HandlePointerDrawing(Vector3 hitPoint,
            bool isHovered,
            bool isSelected,
            ref bool isDrawing,
            ref Vector2 lastUV,
            InteractionMode mode,
            InteractionStateManagerMotif interactionState)
        {
            // The interactor triggers IsSelected when we interact.
            bool inputDown = isHovered && isSelected && !isDrawing;
            bool inputHeld = isHovered && isSelected;
            bool inputUp = isDrawing && (!isHovered || !isSelected);
            // The mode gate is read only on the draw-start path.
            bool canDrawWithPointer = inputDown && interactionState.CanDrawWithPointer();

            if (canDrawWithPointer)
            {
                Object.RequestStateAuthority();
                interactionState.SetMode(mode);
                isDrawing = true;
                lastUV = m_whiteboardManagerMotif.WorldToUV(hitPoint);
                m_whiteboardManagerMotif.RPC_DrawLine(lastUV, lastUV, penColor, brushRadius);
            }
            else if (inputHeld && isDrawing)
            {
                Object.RequestStateAuthority();
                var currentUV = m_whiteboardManagerMotif.WorldToUV(hitPoint);
                if (!(Vector2.Distance(lastUV, currentUV) > 0.001f))
                {
                    return;
                }

                m_whiteboardManagerMotif.RPC_DrawLine(lastUV, currentUV, penColor, brushRadius);
                lastUV = currentUV;
            }
            else if (inputUp && isDrawing)
            {
                ReleaseDrawing();
            }
        }

        private void OnDisable()
        {
            ReleaseDrawing();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            StopForDespawn();
            base.Despawned(runner, hasState);
        }

        public void StopForDespawn()
        {
            m_isReady = false;
            ReleaseDrawing();
        }

        private void ReleaseDrawing()
        {
            if (!m_isDrawing)
            {
                return;
            }

            m_isDrawing = false;
            if (InteractionStateManagerMotif.Instance)
            {
                InteractionStateManagerMotif.Instance.ResetMode(InteractionMode.DrawingPointer);
            }
        }
    }
}
#endif
