// Copyright (c) Meta Platforms, Inc. and affiliates.

#if FUSION2
using System;
using Fusion;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using System.Collections.Generic;
using Meta.XR.MultiplayerBlocks.Fusion;
using Meta.XR.Samples;
using SpatialBlend.Interactions;

namespace MRMotifs.SharedActivities.ChessSample
{
    /// <summary>
    /// Handles synchronization of chess piece positions and rotations across networked clients
    /// and manages interaction events for selecting and moving chess pieces.
    ///
    /// Edit lease: the board's Fusion state authority is the lease on its pieces. The NetworkObject does not allow its authority
    /// to be taken over (no AllowStateAuthorityOverride), so a request while someone holds it is not granted: the first grabber keeps
    /// the pieces and a second grabber is refused (a haptic, "Player N is moving this") instead of pulling the authority away mid-move
    /// (the old last-request-wins ping-pong). The holder keeps the authority from the grab until the dropped piece is at rest, then
    /// releases it; a holder that leaves loses it when Photon removes the player. See <see cref="EditLease"/> for the rule.
    /// </summary>
    [MetaCodeSample("MRMotifs-SharedActivities")]
    public class ChessBoardHandlerMotif : NetworkBehaviour, IStateAuthorityChanged
    {
        /// <summary>
        /// Networked array for storing chess piece positions.
        /// </summary>
        [Networked, Capacity(32)]
        private NetworkArray<Vector3> ChessPiecePositions => default;

        /// <summary>
        /// Networked array for storing chess piece rotations.
        /// </summary>
        [Networked, Capacity(32)]
        private NetworkArray<Quaternion> ChessPieceRotations => default;

        /// <summary>The piece the lease holder is moving, plus one (0: none). Written by the holder, read by the others for the outline.</summary>
        [Networked]
        private int MovingPiecePlusOne { get; set; }

        /// <summary>How high over the board the "Moving: Player N" tag floats, metres.</summary>
        public const float TagHeightMetres = 0.35f;

        /// <summary>The refused grab's feedback haptic (which hand pulled, when known). Replaceable for tests; null pulses the controller.</summary>
        public static Action<OVRInput.Controller> RefusalHaptic;

        /// <summary>Raised on this participant when its grab is refused: the holder and the piece.</summary>
        public event Action<int?, int> GrabRefused;

        private List<InteractableUnityEventWrapper> m_chessPieceInteractables = new();
        private readonly List<(InteractableUnityEventWrapper wrapper, UnityEngine.Events.UnityAction select, UnityEngine.Events.UnityAction unselect)> m_listeners = new();
        private readonly List<(HandGrabInteractable hand, Action<InteractableStateChangeArgs> changed)> m_handListeners = new();
        private readonly List<int> m_pendingDrops = new();
        private EditLease m_lease;
        // The pieces this user holds, in grab order (a hand on each counts), granted or waiting for the lease.
        private readonly List<int> m_held = new();
        private int m_finalTick = -1;
        private bool m_hasSpawned;
        private EditPresenceTag m_tag;
        private EditPresenceOutline m_outline;

        public EditLease Lease => m_lease;
        public int PieceCount => m_chessPieceInteractables.Count;
        public Transform Piece(int index) => m_chessPieceInteractables[index].transform;

        /// <summary>The piece the holder is moving (-1: none), as the network has it.</summary>
        public int MovingPiece => m_hasSpawned ? MovingPiecePlusOne - 1 : -1;

        public override void Spawned()
        {
            base.Spawned();
            m_hasSpawned = true;
            m_lease = new EditLease(Runner.LocalPlayer.PlayerId);
            InitializeChessPieces();
            FusionBBEvents.OnSceneLoadDone += SceneLoaded;
            SyncHolder();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            base.Despawned(runner, hasState);
            FusionBBEvents.OnSceneLoadDone -= SceneLoaded;
            UnsubscribeChessPieceEvents();
            if (m_tag != null) Destroy(m_tag.gameObject);
            if (m_outline != null) Destroy(m_outline.gameObject);
            m_hasSpawned = false;
        }

        /// <summary>
        /// This is a callback of the <see cref="FusionBBEvents"/> OnSceneLoadDone event, which every client gets. Only the board's
        /// state authority (the first client, before it gives the lease back) populates the networked piece poses for those who join
        /// later; anyone else writing them, or making its pieces dynamic, would fight the network state.
        /// </summary>
        private void SceneLoaded(NetworkRunner obj)
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority) return;
            UpdateInitialChessPieceStates();
        }

        private void InitializeChessPieces()
        {
            var chessPieces = new List<InteractableUnityEventWrapper>();
            var allChildren = GetComponentsInChildren<Transform>();

            foreach (var child in allChildren)
            {
                if (child == transform)
                {
                    continue;
                }

                var interactable = child.GetComponent<InteractableUnityEventWrapper>();
                if (interactable != null)
                {
                    chessPieces.Add(interactable);
                }
            }

            m_chessPieceInteractables = chessPieces;

            for (int i = 0; i < m_chessPieceInteractables.Count; i++)
            {
                int index = i;
                var wrapper = m_chessPieceInteractables[i];
                UnityEngine.Events.UnityAction select = () => BeginPieceGrab(index);
                UnityEngine.Events.UnityAction unselect = () => EndPieceGrab(index);
                wrapper.WhenSelect.AddListener(select);
                wrapper.WhenUnselect.AddListener(unselect);
                m_listeners.Add((wrapper, select, unselect));

                HandGrabInteractable hand = wrapper.GetComponent<HandGrabInteractable>();
                if (hand == null) continue;
                int piece = index;
                Action<InteractableStateChangeArgs> changed = args => OnHandGrabStateChanged(piece, args);
                hand.WhenStateChanged += changed;
                m_handListeners.Add((hand, changed));
            }
        }

        // The prefab wrapper watches the controller grab. A hand select is the original
        // signal and takes the same lease and pose-publish path.
        private void OnHandGrabStateChanged(int index, InteractableStateChangeArgs args)
        {
            if (args.NewState == InteractableState.Select)
                BeginPieceGrab(index);
            else if (args.PreviousState == InteractableState.Select)
                EndPieceGrab(index);
        }

        private void UpdateInitialChessPieceStates()
        {
            SetChessPieceRigidbodyState(false);

            for (var i = 0; i < ChessPiecePositions.Length; i++)
            {
                if (i < m_chessPieceInteractables.Count)
                {
                    ChessPiecePositions.Set(i, m_chessPieceInteractables[i].transform.localPosition);
                    ChessPieceRotations.Set(i, m_chessPieceInteractables[i].transform.localRotation);
                }
                else
                {
                    ChessPiecePositions.Set(i, Vector3.zero);
                    ChessPieceRotations.Set(i, Quaternion.identity);
                }
            }
        }

        /// <summary>The user grabs piece <paramref name="index"/> (the piece's select event; public so a test can drive it).</summary>
        public void BeginPieceGrab(int index)
        {
            if (!m_hasSpawned || m_lease == null || m_held.Contains(index)) return;
            EditLeaseAction action = m_lease.BeginGrab(Holder());
            if (action == EditLeaseAction.Refuse)
            {
                Refuse(index);
                return;
            }
            m_held.Remove(index);
            m_held.Add(index);
            switch (action)
            {
                case EditLeaseAction.Proceed:
                    StartMoving(index);
                    break;
                case EditLeaseAction.Request:
                    SetChessPieceRigidbodyState(false);
                    if (TryNetworkObject(out NetworkObject requested))
                        requested.RequestStateAuthority();
                    break;
                // None: a request is already out; this piece waits for the same answer.
            }
        }

        /// <summary>The user drops piece <paramref name="index"/> (the piece's unselect event).</summary>
        public void EndPieceGrab(int index)
        {
            if (!m_hasSpawned || m_lease == null) return;
            if (!m_held.Remove(index)) return;   // a refused piece that was let go: it never counted
            m_lease.EndGrab();
            if (TryNetworkObject(out NetworkObject held) && held.HasStateAuthority)
            {
                SendChessPieceOffset();
                if (m_held.Count > 0) StartMoving(m_held[m_held.Count - 1]);
            }
            // The authority is released by FixedUpdate once the last piece is dropped and every piece is at rest (EditLease.ShouldRelease).
        }

        private void StartMoving(int index)
        {
            if (TryNetworkObject(out NetworkObject networkObject) && networkObject.HasStateAuthority)
                MovingPiecePlusOne = index + 1;
        }

        // A refused grab lets go of the piece it tried, and (a request lost to someone else) of every piece this user was holding.
        private void Refuse(int index)
        {
            int? holder = m_lease.Holder;
            if (index >= 0) m_pendingDrops.Add(index);
            m_pendingDrops.AddRange(m_held);
            m_held.Clear();
            ModeSwitchAssist.ShowNotice(EditLeaseText.Refused(holder));
            (RefusalHaptic ?? DefaultHaptic)(GrabbingController());
            GrabRefused?.Invoke(holder, index);
        }

        private int? Holder()
        {
            if (!TryNetworkObject(out NetworkObject networkObject)) return null;
            PlayerRef authority = networkObject.StateAuthority;
            return authority.IsRealPlayer ? authority.PlayerId : (int?)null;
        }

        // An unspawned motif has no network object. Callers then see "nobody holds it"
        // instead of an exception, which is also what a test of the hand path observes.
        private bool TryNetworkObject(out NetworkObject networkObject)
        {
            networkObject = null;
            try
            {
                networkObject = Object;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return networkObject != null && networkObject.IsValid;
        }

        /// <summary>While this user holds the piece and does not yet have the lease, the live pose stays.</summary>
        internal bool LeavesHeldPoseAlone(int index, bool hasStateAuthority) =>
            !hasStateAuthority && m_held.Contains(index);

        // The holder the network reports, fed to the lease whenever it differs from what the lease last saw (the authority-changed
        // callback, and a poll for a change that has no callback, such as the holder leaving).
        private void SyncHolder()
        {
            if (m_lease == null) return;
            int? holder = Holder();
            if (holder == m_lease.Holder) return;
            switch (m_lease.HolderChanged(holder))
            {
                case EditLeaseAction.Proceed:
                    if (m_held.Count > 0) StartMoving(m_held[m_held.Count - 1]);
                    break;
                case EditLeaseAction.Refuse:
                    Refuse(-1);
                    break;
            }
        }

        private void FixedUpdate()
        {
            if (!m_hasSpawned)
            {
                return;
            }

            SyncHolder();
            if (Object.HasStateAuthority)
            {
                if (!m_lease.ShouldRelease(PiecesAtRest()))
                {
                    m_finalTick = -1;
                    SendChessPieceOffset();
                }
                else if (m_finalTick < 0)
                {
                    // The last state: written once, then the lease waits until the latest server tick this side has seen is past the
                    // tick of that write, so the write has had its send round before the authority goes. It is the server's clock, not a
                    // receipt: the two-process run measured the others' pose of the dropped piece within 0.001 mm.
                    SendChessPieceOffset();
                    MovingPiecePlusOne = 0;
                    m_finalTick = (int)Runner.Tick;
                }
                else if ((int)Runner.LatestServerTick > m_finalTick)
                {
                    m_finalTick = -1;
                    Object.ReleaseStateAuthority();
                }
            }

            UpdateRemoteChessPieces();
        }

        private void Update()
        {
            if (!m_hasSpawned) return;
            SyncHolder();
            DropRefusedPieces();
            UpdatePresence();
        }

        // A refused grab is let go of on the frame after its select (never inside the ISDK select callback).
        private void DropRefusedPieces()
        {
            if (m_pendingDrops.Count == 0) return;
            foreach (int index in m_pendingDrops)
            {
                if (index < 0 || index >= m_chessPieceInteractables.Count) continue;
                foreach (var interactable in m_chessPieceInteractables[index].GetComponents<IInteractable>())
                {
                    var ids = new List<int>();
                    foreach (IInteractorView view in interactable.SelectingInteractorViews) ids.Add(view.Identifier);
                    foreach (int id in ids) interactable.RemoveInteractorByIdentifier(id);
                }
            }
            m_pendingDrops.Clear();
        }

        // Others see who holds the board and which piece moves: the tag over the board and the outline on the piece.
        private void UpdatePresence()
        {
            if (m_lease.HeldByOther)
            {
                if (m_tag == null) m_tag = EditPresenceTag.Create(name + "-EditTag", transform, Vector3.up * TagHeightMetres);
                m_tag.Show(EditLeaseText.Moving(m_lease.Holder.Value));
                int piece = MovingPiece;
                if (piece >= 0 && piece < m_chessPieceInteractables.Count)
                {
                    if (m_outline == null) m_outline = EditPresenceOutline.Create(name + "-EditOutline");
                    if (m_outline.Target != m_chessPieceInteractables[piece].transform) m_outline.Show(m_chessPieceInteractables[piece].transform);
                }
                else if (m_outline != null) m_outline.Hide();
            }
            else
            {
                if (m_tag != null) m_tag.Hide();
                if (m_outline != null) m_outline.Hide();
            }
        }

        public EditPresenceTag PresenceTag => m_tag;
        public EditPresenceOutline PresenceOutline => m_outline;

        /// <summary>A piece this far under the board has left it (dropped off the table): nothing in the game can hold it, so it does not
        /// keep the lease waiting for a rest that may never come (a room with no floor collider).</summary>
        public const float OffBoardDropMetres = 1f;

        // At rest: every piece is kinematic, asleep, or has fallen off the board, so the state the holder last wrote is where the pieces
        // stay. Physics decides when, not a clock.
        private bool PiecesAtRest()
        {
            float boardY = transform.position.y;
            foreach (var piece in m_chessPieceInteractables)
            {
                var body = piece.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic || body.IsSleeping()) continue;
                if (body.position.y < boardY - OffBoardDropMetres) continue;
                return false;
            }
            return true;
        }

        private static OVRInput.Controller GrabbingController()
        {
            if (OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch) > 0.5f) return OVRInput.Controller.LTouch;
            if (OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch) > 0.5f) return OVRInput.Controller.RTouch;
            return OVRInput.Controller.None;
        }

        private void DefaultHaptic(OVRInput.Controller controller)
        {
            if (controller == OVRInput.Controller.None || !Application.isPlaying || !isActiveAndEnabled) return;
            StartCoroutine(Pulse(controller));
        }

        private static System.Collections.IEnumerator Pulse(OVRInput.Controller controller)
        {
            // Two short pulses: a "no", unlike the mode change's single one.
            for (int i = 0; i < 2; i++)
            {
                OVRInput.SetControllerVibration(1f, 0.6f, controller);
                yield return new WaitForSecondsRealtime(0.06f);
                OVRInput.SetControllerVibration(0f, 0f, controller);
                yield return new WaitForSecondsRealtime(0.06f);
            }
        }

        private void SendChessPieceOffset()
        {
            for (var i = 0; i < m_chessPieceInteractables.Count; i++)
            {
                var chessPiece = m_chessPieceInteractables[i];
                var localPosition = chessPiece.transform.localPosition;
                var localRotation = chessPiece.transform.localRotation;

                ChessPiecePositions.Set(i, localPosition);
                ChessPieceRotations.Set(i, localRotation);
            }
        }

        private void UpdateRemoteChessPieces()
        {
            for (var i = 0; i < m_chessPieceInteractables.Count; i++)
            {
                // A grab waiting on the lease keeps the live pose. Checked before the networked
                // arrays so the live transform is not read back from a pose this peer does not own.
                bool hasAuthority = TryNetworkObject(out NetworkObject authority) && authority.HasStateAuthority;
                if (LeavesHeldPoseAlone(i, hasAuthority))
                    continue;

                if (i >= ChessPiecePositions.Length)
                {
                    continue;
                }

                var targetLocalPosition = ChessPiecePositions.Get(i);
                var targetLocalRotation = ChessPieceRotations.Get(i);
                var chessPieceTransform = m_chessPieceInteractables[i].transform;

                if (!hasAuthority)
                {
                    chessPieceTransform.localPosition = Vector3.Lerp(
                        chessPieceTransform.localPosition, targetLocalPosition,
                        Time.deltaTime * 10f);
                    chessPieceTransform.localRotation = Quaternion.Slerp(
                        chessPieceTransform.localRotation,
                        targetLocalRotation, Time.deltaTime * 10f);
                }
                else
                {
                    chessPieceTransform.localPosition = targetLocalPosition;
                    chessPieceTransform.localRotation = targetLocalRotation;
                }
            }
        }

        /// <summary>The networked pose of piece <paramref name="index"/> (board-local), what every participant converges to.</summary>
        public (Vector3 position, Quaternion rotation) NetworkedPiece(int index) =>
            (ChessPiecePositions.Get(index), ChessPieceRotations.Get(index));

        private void UnsubscribeChessPieceEvents()
        {
            foreach (var (wrapper, select, unselect) in m_listeners)
            {
                if (wrapper == null) continue;
                wrapper.WhenSelect.RemoveListener(select);
                wrapper.WhenUnselect.RemoveListener(unselect);
            }
            m_listeners.Clear();
            foreach (var (hand, changed) in m_handListeners)
            {
                if (hand != null)
                    hand.WhenStateChanged -= changed;
            }
            m_handListeners.Clear();
        }

        /// <summary>
        /// This method comes from the <see cref="IStateAuthorityChanged"/> interface and is called
        /// when the State Authority has been changed. We use to execute commands immediately
        /// after StateAuthority has been assigned to the client who tried to move a chess piece.
        /// </summary>
        public void StateAuthorityChanged()
        {
            if (!Object.HasStateAuthority)
            {
                SetChessPieceRigidbodyState(true);
            }
            SyncHolder();
        }

        private void SetChessPieceRigidbodyState(bool isKinematic)
        {
            foreach (var chessPiece in m_chessPieceInteractables)
            {
                var chessPieceRb = chessPiece.GetComponent<Rigidbody>();
                chessPieceRb.isKinematic = isKinematic;
                chessPieceRb.useGravity = !isKinematic;
            }
        }
    }
}
#endif
