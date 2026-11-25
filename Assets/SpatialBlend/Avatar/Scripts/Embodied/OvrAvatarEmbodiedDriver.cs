// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The embodied driver of one received Meta/OVR avatar (design A*, ABI 4), on the checked target-event path, the only
    /// one: <see cref="TryEnableTargetEvents"/> admits it (SpatialBlendManager.ConfigureEmbodiedDriver), and it
    /// then publishes the root and the direct head with exact yaw, drains the core's typed events and pursues them as
    /// persistent goals through the retarget's checked realization lease (OvrAvatarEmbodiedDriver.TargetEvents.cs).
    ///
    /// The proposal set is status metadata only: the manager reads the interaction value from it, and the driver takes the
    /// ABI 4.14 anchor charts from it (<see cref="CollectAnchorCharts"/>), which the entity keeps as state for the raw body; the head's
    /// chart also sets the Sb head's yaw inside a transfer leaf (OvrAvatarEmbodiedDriver.HeadChart.cs).
    /// The proposal-following frame (v1 pacing toward each proposal, per-anchor and bone ports, the default teleport of a
    /// source exception) is removed.
    ///
    /// Embodiment registration (ABI 4.7): when the avatar's embodiment data is present and the core supports it, the driver
    /// registers it once at construction, before any pose is published. Its bone-bound anchors are the driver's own (never a
    /// realization-target handler's) and are reported as unsupported typed targets: the checked path publishes and writes the
    /// root and the direct head only.
    ///
    /// Single owner: this is the only writer of the avatar's root and head, and the only publisher of their poses. It places
    /// the body only on a typed placement event and at the avatar's arrival (<see cref="PlaceAtArrival"/>).
    /// </summary>
    internal sealed partial class OvrAvatarEmbodiedDriver : IEmbodiedDriver
    {
        private readonly SbEntity _entity;
        private readonly long _actor;
        private readonly IEmbodiedCore _core;

        // Anchor name <-> core id (stable for the actor's lifetime).
        private readonly Dictionary<string, uint> _anchorIds = new Dictionary<string, uint>();
        private readonly Dictionary<uint, string> _anchorNames = new Dictionary<uint, string>();

        // Embodiment registration. Empty unless the core registered this avatar's embodiment.
        private sealed class BoneBinding
        {
            internal string AnchorName;
            internal uint AnchorId;
            internal string BoneName;
            internal int BoneId;
        }

        private readonly List<BoneBinding> _bound = new List<BoneBinding>();
        private readonly Dictionary<string, BoneBinding> _boundByAnchorName = new Dictionary<string, BoneBinding>();
        private EmbodiedFramePlan _currentMotionPlan;
        private readonly object _registrationToken = new object();
        private static readonly HashSet<string> ReportedOnce = new HashSet<string>();

        /// <summary>True when this avatar's embodiment is registered with the core (its bone-bound anchors are derived from bones).</summary>
        internal bool EmbodimentRegistered => _bound.Count > 0;
        internal SbEntity DrivenEntity => _entity;
        internal object RegistrationToken => _registrationToken;

        /// <summary>The kinetic bounds (<see cref="EmbodiedDriverConfig"/>): the typed goals' speed and turn caps.</summary>
        internal EmbodiedDriverConfig Config { get; }

        internal OvrAvatarEmbodiedDriver(SbEntity entity, long actor, IEmbodiedCore core, EmbodiedDriverConfig config)
            : this(entity, actor, core, config, OvrAvatarEmbodiment.DefaultTextSource)
        {
        }

        /// <param name="embodimentSource">Where the embodiment data files are read from (a test supplies its own).</param>
        internal OvrAvatarEmbodiedDriver(SbEntity entity, long actor, IEmbodiedCore core, EmbodiedDriverConfig config,
                                         IEmbodimentTextSource embodimentSource)
        {
            _entity = entity;
            _actor = actor;
            _core = core;
            Config = config;
            RegisterEmbodiment(embodimentSource);
        }

        // Once, before the first pose is published (the core refuses a registration after an anchor it would derive has a port).
        private void RegisterEmbodiment(IEmbodimentTextSource source)
        {
            // The measurement data (ovr-avatar.measurement/chain/sitbone-presets) is not redistributed with the open-source release. Without it
            // no anchor needs a measured offset, so none is bone-bound and the avatar keeps the legacy anchor path: said once per run, as a
            // warning, never per avatar or per frame.
            bool measurementMissing = MeasurementDataMissing(source);
            // The sit-bone point is the avatar's own: the preset it shows states which measurement registers.
            if (!EmbodimentSpecLoader.TryBuild(source, OvrAvatarEmbodiment.Files, _entity != null ? _entity.AvatarPresetIndex : null,
                    out EmbodimentSpec spec, out string report))
            {
                ReportOnce("[EmbodiedDriver] no embodiment registration (" + report + "): no anchor is bone-bound.");
                return;
            }
            // A registration with no bone-bound anchor still carries the body floor-footprint radius (the core
            // proposes destinations, the body's clearance is the driver's, from the embodiment's radius): no anchor is bone-bound,
            // the radius is registered. Nothing keeps it clear now (the checked root step queries no course): an open gap.
            bool bodyOnly = spec.BoneBoundCount == 0 && spec.HasBody;
            if (!bodyOnly && (spec.BoneBoundCount == 0 || spec.NativeBasisRows == null))
            {
                if (measurementMissing) return; // already said once, by MeasurementDataMissing
                ReportOnce("[EmbodiedDriver] no embodiment registration: " + EmbodimentSpecLoader.Describe(spec) +
                           ". No anchor is bone-bound.");
                return;
            }
            EmbodimentRegistrationResult result = _core.RegisterEmbodiment(_actor, spec.Json, out string error);
            if (result == EmbodimentRegistrationResult.Unsupported)
            {
                if (!string.IsNullOrEmpty(error))
                {
                    ReportOnce("[EmbodiedDriver] embodiment registration is not available (" + error + "): no anchor is bone-bound.");
                }
                return;
            }
            if (result == EmbodimentRegistrationResult.Refused)
            {
                Debug.LogError($"[EmbodiedDriver] the core refused the embodiment of '{(_entity != null ? _entity.name : "?")}': {error}. " +
                               "No anchor is bone-bound.");
                return;
            }
            if (bodyOnly) return; // a body radius only: no anchor is bone-bound
            var bindings = new List<BoneBinding>();
            foreach (EmbodimentAnchorInfo anchor in spec.Anchors)
            {
                if (!anchor.IsBoneBound) continue;
                if (!_core.TryGetBoneId(_actor, anchor.BoneName, out int boneId) || !TryAnchorId(anchor.Name, out uint anchorId))
                {
                    // Registered, yet the core does not know the bone or the anchor it was just told: the registration and
                    // the driver disagree. Loud, and not half-applied.
                    Debug.LogError($"[EmbodiedDriver] embodiment registered, but the core has no bone '{anchor.BoneName}' or anchor " +
                                   $"'{anchor.Name}': the embodiment is not driven.");
                    return;
                }
                bindings.Add(new BoneBinding { AnchorName = anchor.Name, AnchorId = anchorId, BoneName = anchor.BoneName, BoneId = boneId });
            }
            foreach (BoneBinding binding in bindings)
            {
                _bound.Add(binding);
                _boundByAnchorName[binding.AnchorName] = binding;
            }
        }

        private static void ReportOnce(string message)
        {
            if (ReportedOnce.Add(message)) Debug.Log(message);
        }

        private static bool _measurementMissingReported;

        // True when the app's own data source (not a test's) has no measurement artefact; the first time, one warning.
        private static bool MeasurementDataMissing(IEmbodimentTextSource source)
        {
            if (!ReferenceEquals(source, OvrAvatarEmbodiment.DefaultTextSource)) return false;
            string measurement = OvrAvatarEmbodiment.Files.Measurement;
            if (measurement == null || source.TryRead(measurement, out _)) return false;
            if (!_measurementMissingReported)
            {
                _measurementMissingReported = true;
                Debug.LogWarning("[EmbodiedDriver] the avatar measurement data ('" + measurement + "' and its companions in " +
                                 "Resources/Embodiment) is not installed: avatars are driven without bone-bound anchors (the legacy anchor path).");
            }
            return true;
        }

        public ulong Epoch => TargetEventsEnabled ? TargetEpoch : 0UL;

        /// <summary>The checked root (and direct head) sample; nothing before admission or after the lifetime closed.</summary>
        public EmbodiedPose PublishPose() => TargetEventsEnabled ? PublishCheckedPose() : default;

        private bool TryAnchorId(string name, out uint id)
        {
            if (_anchorIds.TryGetValue(name, out id)) return true;
            if (!_core.TryGetAnchorId(_actor, name, out id)) return false;
            _anchorIds[name] = id;
            _anchorNames[id] = name;
            return true;
        }

        private bool TryAnchorName(uint id, out string name)
        {
            if (_anchorNames.TryGetValue(id, out name)) return true;
            if (!_core.TryGetAnchorName(_actor, id, out name)) return false;
            _anchorNames[id] = name;
            _anchorIds[name] = id;
            return true;
        }

        public bool OwnsAnchor(string anchorName) => TargetEventsEnabled && OwnsCheckedAnchor(anchorName);

        public bool TryReadProposals(ProposalSet proposals) => _core.TryReadProposals(_actor, proposals);

        /// <summary>Drains the typed event stream; the list stays empty (the events are the driver's goals, not the caller's).</summary>
        public void PollEvents(List<ActorEvent> events)
        {
            events.Clear();
            if (TargetEventsEnabled) DrainTargetEvents();
        }

        public EmbodiedFramePlan Plan(in EmbodiedPose embodied, ProposalSet proposals, IReadOnlyList<ActorEvent> events,
                                      float dtSeconds)
        {
            if (TargetLifetimeOpen)
            {
                ReadHeadChart(proposals);   // before the frame is planned: a chart alone is head work (OvrAvatarEmbodiedDriver.HeadChart.cs)
                EmbodiedFramePlan targetPlan = PlanTargetFrame(dtSeconds);
                CollectAnchorCharts(proposals, targetPlan);
                return targetPlan;
            }
            // Not admitted, or a closed lifetime: nothing is written; a chart the entity still holds is dropped by the Apply of
            // this plan.
            var closing = new EmbodiedFramePlan { ChartsCleared = _chartsApplied };
            _lastCharts.Clear(); ClearHeadChartYaw();
            return closing;
        }

        // True while the entity holds the charts of an earlier plan (so a plan without any clears them, once).
        private bool _chartsApplied;
        // The last chart read per anchor: a chart answered for another revision of the set (the set was replaced between the two
        // calls) keeps the anchor's last chart instead of reading as "no chart" (which would blend the hands out for a frame).
        private readonly Dictionary<uint, EmbodiedAnchorChart> _lastCharts = new Dictionary<uint, EmbodiedAnchorChart>();

        // ABI 4.14: every anchor of the set that carries a chart, by name, from the same set the plan answers.
        private void CollectAnchorCharts(ProposalSet proposals, EmbodiedFramePlan plan)
        {
            for (int i = 0; proposals != null && i < proposals.Anchors.Count; ++i)
            {
                AnchorProposal proposal = proposals.Anchors[i];
                if (proposal.HasChart && TryAnchorName(proposal.AnchorId, out string name))
                {
                    var chart = new EmbodiedAnchorChart(name, proposal.ChartPosition, proposal.ChartYawDegrees);
                    _lastCharts[proposal.AnchorId] = chart;
                    plan.AddChart(chart);
                }
                else if (proposal.ChartStale && _lastCharts.TryGetValue(proposal.AnchorId, out EmbodiedAnchorChart last))
                {
                    plan.AddChart(last);
                }
                else
                {
                    _lastCharts.Remove(proposal.AnchorId);
                }
            }
            plan.ChartsCleared = plan.Charts.Count == 0 && _chartsApplied;
        }

        // The charts reach the entity whatever else the frame writes: they compose with the body, they do not move it.
        private void ApplyAnchorCharts(EmbodiedFramePlan plan)
        {
            if (!plan.HasChartWork) return;
            _entity.ApplyAnchorCharts(plan.Charts);
            _chartsApplied = plan.Charts.Count > 0;
        }

        // A close or a closed target lifetime: no chart of the old state survives, at once (the hands snap back to the head; the avatar
        // is going away). A reset only forgets the bookkeeping: the next plan decides, and a plan without a chart blends it out.
        private void DropAnchorCharts()
        {
            _lastCharts.Clear(); ClearHeadChartYaw();
            _chartsApplied = false;
            if (_entity != null) _entity.ClearAnchorCharts();
        }

        public void Apply(EmbodiedFramePlan plan)
        {
            if (_targetClosed || !TargetEventsEnabled) { if (_chartsApplied || _lastCharts.Count > 0) DropAnchorCharts(); return; }
            if (plan == null || !ReferenceEquals(plan, _currentMotionPlan)) return;
            ApplyAnchorCharts(plan);
            // A plan that is applied only for its charts writes nothing else.
            if (plan.HasChartWork && plan.IsEmptyWithoutCharts) return;
            ApplyTargetFrame(plan);
        }

        /// <summary>
        /// A received avatar's arrival: its head placed at the v1 spawn height (<see cref="EmbodiedDriverConfig.SpawnHeightMeters"/>),
        /// whatever height it was observed at, as before, then published as a placement, so the core learns it.
        /// </summary>
        internal void PlaceAtArrival(Vector3 position, Quaternion rotation)
        {
            position.y = EmbodiedDriverConfig.SpawnHeightMeters;
            Place(position, rotation);
        }

        public void Place(Vector3 position, Quaternion rotation)
        {
            if (TargetEventsEnabled) PlaceCheckedBody(position, rotation);
        }

        /// <summary>
        /// Places an avatar's body without a core: the arrival of an avatar the core cannot drive, which must still
        /// be visible. It is the driver's own write, kept here so that no other class writes a remote root.
        /// </summary>
        internal static void PlaceWithoutCore(SbEntity entity, Vector3 position, Quaternion rotation)
        {
            position.y = EmbodiedDriverConfig.SpawnHeightMeters;
            WriteBody(entity, position, rotation);
        }

        // Placement goes through the retarget head (it also rebases the hands), else through the entity.
        private static void WriteBody(SbEntity entity, Vector3 position, Quaternion rotation)
        {
            if (entity.TryGetComponent(out MetaAvatarEntity meta) && meta._sbAvatar != null &&
                meta._sbAvatar._retargetTransform != null)
            {
                meta._sbAvatar._retargetTransform.SetHeadTransform(position, rotation);
            }
            else
            {
                entity.ApplyRealizationTarget(position, rotation);
            }
        }

        public void Reset()
        {
            _lastCharts.Clear(); ClearHeadChartYaw();
            if (TargetEventsEnabled) ResetCheckedEpochs();
        }

        /// <summary>Release the native target-event subscription when this actor leaves the manager.</summary>
        internal void Close()
        {
            DropAnchorCharts();
            CloseTargetEvents();
            _currentMotionPlan = null;
        }
    }
}
