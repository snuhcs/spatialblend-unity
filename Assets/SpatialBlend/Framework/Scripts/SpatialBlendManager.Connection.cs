// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    public enum SpatialBlendConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Failed
    }

    public partial class SpatialBlendManager
    {
        private SpatialBlendConnectionState _connectionState;
        private string _connectionError = string.Empty;
        private bool _connectionOperationActive;
        private bool _retiringConnection;
        private bool _destroyed;
        private readonly HashSet<SbApp> _startedApps = new HashSet<SbApp>();

        public SpatialBlendConnectionState ConnectionState => _connectionState;
        public string ConnectionError => _connectionError;
        public long ConnectionGeneration => Interlocked.Read(ref _lifecycleGeneration);
        public bool NativeLibraryCompatible { get; private set; }
        public event Action<SpatialBlendConnectionState> ConnectionStateChanged;

        // All entry points and observers are main-thread only. A synchronous
        // native return is not success until the completion handle is accepted.
        //
        // tunneled: the core link rides a byte stream the application carries (ABI 4.5); there is no address
        // and no port. A tunneled client does not wait here: its handle completes only while its link is
        // pumped, so the connection stays Connecting until PollTunneledJoin sees it finish.
        private void EstablishConnection(SpaceManagerType role, string host, int port, bool tunneled = false)
        {
            if (_destroyed || _retiringConnection || _connectionOperationActive || Initialized ||
                _pendingJoinHandle != 0)
                return;

            _connectionOperationActive = true;
            BeginManagedLifecycle();
            long generation = ConnectionGeneration;
            try
            {
                SetConnectionState(SpatialBlendConnectionState.Connecting);
                if (!IsCurrentLifecycleGeneration(generation)) return;

                // The native host accepts zero but has no bound-port query. A
                // zero endpoint must never be advertised to another participant.
                if (!tunneled && (port < 1 || port > 65535))
                    throw new ArgumentOutOfRangeException(nameof(port), "Port must be in 1..65535.");
                if (!tunneled && role == SpaceManagerType.Client && string.IsNullOrWhiteSpace(host))
                    throw new ArgumentException("A nonempty host address is required.", nameof(host));

                NativeLibraryCompatible = false;
                InitializeNativeLibrary();
                NativeLibraryCompatible = true;

                _sessionID = CreateNativeSession();
                if (_sessionID <= 0)
                    throw new InvalidOperationException("Failed to create the SpatialBlend core session.");
                _spaceID = AddNativeSpace(_sessionID);
                if (_spaceID <= 0)
                    throw new InvalidOperationException("Failed to add the SpatialBlend core space.");

                ConfigureNativeSpace(_spaceID, generation);
                if (!IsCurrentLifecycleGeneration(generation)) return;
                SpatialBlendRegistry.Register(_spaceID, this);

                if (tunneled && role == SpaceManagerType.Client)
                {
                    long joinHandle = ConnectNativeSpaceTunneled(_spaceID, out long linkId);
                    if (joinHandle <= 0 || linkId <= 0)
                        throw new InvalidOperationException("The tunneled join could not start.");
                    _joinLink = new CoreLink(this, linkId, generation);
                    _pendingJoinHandle = joinHandle;
                    return;
                }

                long handle = tunneled
                    ? HostNativeSpaceTunneled(_spaceID)
                    : role == SpaceManagerType.Host
                        ? HostNativeSpace(_spaceID, port)
                        : ConnectNativeSpace(_spaceID, host.Trim(), port);
                if (!WaitForNativeConnection(handle, out string error))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                        ? "Native connection failed or did not complete before its deadline."
                        : error);
                if (!IsCurrentLifecycleGeneration(generation)) return;

                CommitConnection(role, host, port, generation);
            }
            catch (Exception exception)
            {
                if (IsCurrentLifecycleGeneration(generation))
                {
                    RetireConnectionResources();
                    SetConnectionState(SpatialBlendConnectionState.Failed, exception.Message);
                    Debug.LogWarning($"[SpatialBlend] Connection failed: {exception.Message}");
                }
            }
            finally
            {
                _connectionOperationActive = false;
            }
        }

        // ---- Tunneled core link (ABI 4.5) -------------------------------------------------------------------

        private long _pendingJoinHandle;
        private CoreLink _joinLink;

        /// <summary>Host with no listening socket. Each joining peer is attached with <see cref="AcceptCoreLink"/>.</summary>
        public virtual void HostTunneled() => EstablishConnection(SpaceManagerType.Host, null, 0, tunneled: true);

        /// <summary>
        /// Join the host over a tunneled link, returned at once. The caller must pump it
        /// (<see cref="CoreLink.Read"/> out to the host, <see cref="CoreLink.Write"/> in from the host) for the join to
        /// finish; the connection stays Connecting until <see cref="PollTunneledJoin"/> sees it complete. Null when a
        /// connection is already up or starting.
        /// </summary>
        public virtual CoreLink JoinTunneled()
        {
            if (IsJoining || Initialized) return null;
            EstablishConnection(SpaceManagerType.Client, null, 0, tunneled: true);
            return _pendingJoinHandle != 0 ? _joinLink : null;
        }

        /// <summary>The link of a tunneled join in progress or completed; null otherwise.</summary>
        public CoreLink JoinLink => _joinLink;

        /// <summary>True while a tunneled join waits for the host.</summary>
        public bool IsJoining => _pendingJoinHandle != 0;

        /// <summary>
        /// Host side: attach one joining peer. The returned link carries that peer's whole core session; close it when
        /// the peer leaves. Null when this manager is not hosting tunneled.
        /// </summary>
        public virtual CoreLink AcceptCoreLink()
        {
            if (!Initialized || _networkRole != SpaceManagerType.Host || _spaceID <= 0) return null;
            return AcceptNativeTunnel(_spaceID, out long linkId) ? new CoreLink(this, linkId, ConnectionGeneration) : null;
        }

        /// <summary>
        /// Main thread, every frame while <see cref="IsJoining"/>: finishes the tunneled join when the host's answer
        /// has been applied, or fails the connection when the link ended first.
        /// </summary>
        public void PollTunneledJoin()
        {
            if (_pendingJoinHandle == 0) return;
            long handle = _pendingJoinHandle;
            long generation = ConnectionGeneration;
            if (!IsCurrentLifecycleGeneration(generation))
            {
                _pendingJoinHandle = 0;
                return;
            }
            if (PollNativeJoin(handle, out string error))
            {
                _pendingJoinHandle = 0;
                try { CommitConnection(SpaceManagerType.Client, null, 0, generation); }
                catch (Exception exception)
                {
                    RetireConnectionResources();
                    SetConnectionState(SpatialBlendConnectionState.Failed, exception.Message);
                }
                return;
            }
            if (string.IsNullOrEmpty(error)) return;  // still waiting for the host
            _pendingJoinHandle = 0;
            RetireConnectionResources();
            SetConnectionState(SpatialBlendConnectionState.Failed, error);
            Debug.LogWarning($"[SpatialBlend] Tunneled join failed: {error}");
        }

        internal int ReadCoreLink(long link, byte[] buffer, int capacity, out int count) =>
            ReadNativeTunnel(link, buffer, capacity, out count);
        internal int WriteCoreLink(long link, byte[] bytes, int count) => WriteNativeTunnel(link, bytes, count);
        internal void CloseCoreLink(long link) => CloseNativeTunnel(link);

        // The connection is up: publish it, then start the apps. Shared by the TCP and the tunneled paths.
        private void CommitConnection(SpaceManagerType role, string host, int port, long generation)
        {
            _networkRole = role;
            _port = port;
            if (role == SpaceManagerType.Client) _hostIP = host == null ? string.Empty : host.Trim();
#if UNITY_EDITOR
            if (role == SpaceManagerType.Host && port > 0) latestEditorPort = port;
#endif
            _initialized = true;
            _spaceCreated = true;
            SetConnectionState(SpatialBlendConnectionState.Connected);
            if (!IsCurrentLifecycleGeneration(generation) || !Initialized) return;

            // No creation event is published for a rolled-back connection.
            var observers = OnSpaceCreated;
            if (observers != null)
            {
                foreach (Action<SpatialBlendManager> observer in observers.GetInvocationList())
                {
                    if (!IsCurrentLifecycleGeneration(generation) || !Initialized) break;
                    try { observer(this); }
                    catch (Exception exception) { ReportObserverFailure(exception); }
                }
            }
            if (_connectOnAwake && IsCurrentLifecycleGeneration(generation)) StartApps();
        }

        // Retires only this manager's native session; never calls CleanupAll.
        // Calling twice is harmless. A later Host/Connect creates fresh handles.
        public virtual void Disconnect()
        {
            if (_retiringConnection) return;
            RetireConnectionResources();
            SetConnectionState(SpatialBlendConnectionState.Disconnected);
        }

        private void RetireConnectionResources()
        {
            if (_retiringConnection) return;
            _retiringConnection = true;
            long retiredSpace = _spaceID;
            SpatialBlendAPI.ClearSceneError(retiredSpace);
            long retiredSession = _sessionID;
            try
            {
                BeginManagedTeardown();
                // Retire identities before app or native cleanup can re-enter.
                _spaceID = -1;
                _sessionID = -1;
                _listenerHandle = -1;
                _pendingJoinHandle = 0;
                _joinLink = null;
                foreach (var app in new List<SbApp>(_startedApps)) StopRegisteredApp(app);
                if (retiredSpace > 0)
                {
                    SpatialBlendRegistry.Unregister(retiredSpace);
                    try { UnregisterNativeSpace(retiredSpace); }
                    catch (Exception exception) { ReportObserverFailure(exception); }
                }
            }
            finally
            {
                // An unregister/observer exception must not leak the session.
                if (retiredSession > 0)
                {
                    try { DestroyNativeSession(retiredSession); }
                    catch (Exception exception) { ReportObserverFailure(exception); }
                }
                _retiringConnection = false;
            }
        }

        private void StartRegisteredApp(SbApp app, long generation)
        {
            if (app == null || !_apps.Contains(app) || !Initialized ||
                !_appsStarted || !IsCurrentLifecycleGeneration(generation) ||
                !_startedApps.Add(app)) return;
            try { app.StartInSpace(); }
            catch (Exception exception)
            {
                StopRegisteredApp(app);
                ReportObserverFailure(exception);
            }
        }

        private void StopRegisteredApp(SbApp app)
        {
            if (!_startedApps.Remove(app) || app == null) return;
            try { app.OnAppStop(); }
            catch (Exception exception) { ReportObserverFailure(exception); }
        }

        private void SetConnectionState(SpatialBlendConnectionState state, string error = "")
        {
            bool changed = _connectionState != state || _connectionError != error;
            _connectionState = state;
            _connectionError = error;
            var observers = ConnectionStateChanged;
            if (!changed || observers == null) return;
            long generation = ConnectionGeneration;
            foreach (Action<SpatialBlendConnectionState> observer in observers.GetInvocationList())
            {
                // A listener may disconnect. Later listeners must never receive
                // the now-retired Connected notification after Disconnected.
                if (_connectionState != state || ConnectionGeneration != generation) break;
                try { observer(state); }
                catch (Exception exception) { ReportObserverFailure(exception); }
            }
        }

        private bool TryInitializeNativeLibrary()
        {
            NativeLibraryCompatible = false;
            try
            {
                InitializeNativeLibrary();
                NativeLibraryCompatible = true;
                return true;
            }
            catch (Exception exception)
            {
                SetConnectionState(SpatialBlendConnectionState.Failed, exception.Message);
                Debug.LogWarning($"[SpatialBlend] Native core library unavailable: {exception.Message}");
                return false;
            }
        }

        private static void ReportObserverFailure(Exception exception)
        {
            Debug.LogWarning($"[SpatialBlend] Connection lifecycle callback failed: {exception.Message}");
        }

        // Narrow native seams keep the production transaction testable without
        // native loading, network traffic, Unity coroutines or an XR device.
        protected virtual void InitializeNativeLibrary() =>
            SpatialBlendAPI.Initialize(CurrentSettings != null && CurrentSettings.useDebugLibraries);
        protected virtual long CreateNativeSession() => SpatialBlendAPI.CreateSession();
        protected virtual long AddNativeSpace(long session) => SpatialBlendAPI.AddSpace(session);
        protected virtual long HostNativeSpace(long space, int port) => SpatialBlendAPI.HostSpace(space, port);
        protected virtual long ConnectNativeSpace(long space, string host, int port) =>
            SpatialBlendAPI.ConnectSpace(space, host, port);
        protected virtual long HostNativeSpaceTunneled(long space) => SpatialBlendAPI.HostSpaceTunneled(space);
        protected virtual long ConnectNativeSpaceTunneled(long space, out long link) =>
            SpatialBlendAPI.ConnectSpaceTunneled(space, out link);
        protected virtual bool AcceptNativeTunnel(long space, out long link) => SpatialBlendAPI.AcceptTunnel(space, out link);
        protected virtual int ReadNativeTunnel(long link, byte[] buffer, int capacity, out int count) =>
            SpatialBlendAPI.TunnelRead(link, buffer, capacity, out count);
        protected virtual int WriteNativeTunnel(long link, byte[] bytes, int count) => SpatialBlendAPI.TunnelWrite(link, bytes, count);
        protected virtual void CloseNativeTunnel(long link) => SpatialBlendAPI.TunnelClose(link);
        // True when the join finished. Otherwise error is empty while it still waits, and says why once it failed.
        protected virtual bool PollNativeJoin(long handle, out string error)
        {
            if (SpatialBlendAPI.TryWait(handle, 0)) { error = string.Empty; return true; }
            error = SpatialBlendAPI.GetError(handle);
            return false;
        }
        protected virtual bool WaitForNativeConnection(long handle, out string error)
        {
            bool completed = SpatialBlendAPI.TryWait(handle, Constants.EVENT_WAIT_TIMEOUT_MS);
            error = completed ? string.Empty : SpatialBlendAPI.GetError(handle);
            return completed;
        }
        protected virtual void UnregisterNativeSpace(long space)
        {
            RetireContainmentState();
            ReleaseNativeCallbacks(space);
        }
        protected virtual void ReleaseNativeCallbacks(long space) => SpatialBlendAPI.UnregisterCallbacks(space);
        protected virtual void DestroyNativeSession(long session) => SpatialBlendAPI.DestroySession(session);
    }
}
