// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private ReceiverBodyProfileSpec BodySpec()
        {
            var p = _binding.Profile;
            Assert.That(ReceiverBodyProfileSpec.TryCreate(p.Input.RootAnchor, p.Input.AnchorNames,
                p.Input.RigRevision, p.Input.CalibrationRevision, p.ProviderId, p.ProviderEpoch,
                p.ClockMapping, p.EvidenceKind, p.EvidenceReference, p.Skeleton, p.ReceiverWorldFromBody,
                p.Anchors, out var spec, out string error), Is.True, error);
            return spec;
        }

        [Test]
        public void BodySpecMaterializesOnlyMatchingAdmittedLayoutClockAndRevisions()
        {
            var spec = BodySpec(); var input = _binding.Profile.Input;
            Assert.That(spec.TryMaterialize(input, out var profile, out string error), Is.True, error);
            Assert.That(profile.Input, Is.SameAs(input));
            Assert.That(profile.Fingerprint, Is.EqualTo(_binding.Profile.Fingerprint));
            Assert.That(spec.TryMaterialize(null, out _, out _), Is.False);
            Assert.That(_receiver.TryBeginReceiverInput(input.RigRevision + 1, input.CalibrationRevision,
                out _writer, out error), Is.True, error);
            Assert.That(spec.TryMaterialize(_writer.Input, out _, out _), Is.False);
        }

        [Test]
        public void BodySpecFreezesDefinitionAndRejectsGeneratedOrMissingCalibration()
        {
            var p = _binding.Profile; var names = p.Input.AnchorNames.ToArray(); var anchors = p.Anchors.ToArray();
            bool Create(ReceiverBodyEvidenceKind evidence, IReadOnlyList<string> layout,
                IReadOnlyList<ReceiverBodyAnchorBinding> mappings, out ReceiverBodyProfileSpec spec) =>
                ReceiverBodyProfileSpec.TryCreate(p.Input.RootAnchor, layout, p.Input.RigRevision,
                    p.Input.CalibrationRevision, p.ProviderId, p.ProviderEpoch, p.ClockMapping, evidence,
                    p.EvidenceReference, p.Skeleton, p.ReceiverWorldFromBody, mappings, out spec, out _);
            Assert.That(Create(p.EvidenceKind, names, anchors, out var frozen), Is.True);
            names[0] = "changed"; anchors[0] = default;
            Assert.That(frozen.AnchorNames[0], Is.EqualTo(p.Input.AnchorNames[0]));
            Assert.That(frozen.Anchors[0].Anchor, Is.EqualTo(p.Anchors[0].Anchor));
            Assert.That(Create(ReceiverBodyEvidenceKind.GeneratedOutput, p.Input.AnchorNames, p.Anchors, out _), Is.False);
            Assert.That(Create(p.EvidenceKind, p.Input.AnchorNames, Array.Empty<ReceiverBodyAnchorBinding>(), out _), Is.False);
        }

        private sealed class UnstableSpecList<T> : IReadOnlyList<T>
        {
            private readonly T[] _values; private readonly bool _throws; private bool _read;
            internal UnstableSpecList(T[] values, bool throws = false) { _values = values; _throws = throws; }
            public int Count => _throws ? throw new InvalidOperationException("unstable count") : _read ? int.MaxValue : _values.Length;
            public T this[int index] { get { _read = true; return _values[index]; } }
            public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Test]
        public void BodySpecChangingOrThrowingCollectionsRejectWithoutEscapingOrMutation()
        {
            var p = _binding.Profile;
            bool Create(IReadOnlyList<string> layout, IReadOnlyList<ReceiverBodyAnchorBinding> anchors) =>
                ReceiverBodyProfileSpec.TryCreate(p.Input.RootAnchor, layout, p.Input.RigRevision,
                    p.Input.CalibrationRevision, p.ProviderId, p.ProviderEpoch, p.ClockMapping,
                    p.EvidenceKind, p.EvidenceReference, p.Skeleton, p.ReceiverWorldFromBody, anchors, out _, out _);
            Assert.DoesNotThrow(() => Assert.That(Create(new UnstableSpecList<string>(p.Input.AnchorNames.ToArray()), p.Anchors), Is.False));
            Assert.DoesNotThrow(() => Assert.That(Create(new UnstableSpecList<string>(p.Input.AnchorNames.ToArray(), true), p.Anchors), Is.False));
            Assert.DoesNotThrow(() => Assert.That(Create(p.Input.AnchorNames, new UnstableSpecList<ReceiverBodyAnchorBinding>(p.Anchors.ToArray())), Is.False));
            Assert.That(_binding.TryReadAcquisitionClock(out _, out _), Is.True);
        }

        [Test]
        public void RigPreflightChecksReferenceWithoutReservingOrRequiringARecordedBody()
        {
            var cp = CarrierFixture(out var rig, out var carrier, false); var spec = BodySpec();
            bool Preflight() => CalibratedTransformRigOutputOwner.TryPreflightWithLocomotion(_receiver, spec,
                rig.Origin, carrier, CarrierCalibration(cp), rig.Joints, Array.Empty<int>(), rig.WorldFromReceiver, "preflight", out _);
            Assert.That(Preflight(), Is.True); Assert.That(Preflight(), Is.True, "Preflight must not reserve a writer.");
            Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero));
            BindCarrier(rig, carrier, cp);
            Assert.That(Preflight(), Is.False, "A real owner excludes new reference binding.");
            Assert.That(rig.Owner.IsClosed, Is.False);
        }

        [Test]
        public void RigPreflightRejectsBadTopologyScaleAndComponentsBeforeActivation()
        {
            var cp = CarrierFixture(out var rig, out var carrier, false); var spec = BodySpec();
            bool Preflight() => CalibratedTransformRigOutputOwner.TryPreflightWithLocomotion(_receiver, spec,
                rig.Origin, carrier, CarrierCalibration(cp), rig.Joints, Array.Empty<int>(), rig.WorldFromReceiver, "preflight", out _);
            carrier.localScale = new Vector3(1, 2, 1); Assert.That(Preflight(), Is.False); carrier.localScale = Vector3.one;
            var animator = rig.Origin.gameObject.AddComponent<Animator>(); Assert.That(Preflight(), Is.False);
            UnityEngine.Object.DestroyImmediate(animator);
            Assert.That(Preflight(), Is.True); Assert.That(cp.TryRevalidate(out _, out _), Is.True);
        }

        [Test]
        public void RigPreflightWrongThreadRejectsBeforeTransformAccess()
        {
            var cp = CarrierFixture(out var rig, out var carrier, false); var spec = BodySpec(); var calibration = CarrierCalibration(cp);
            bool accepted = true; Exception failure = null;
            var worker = new Thread(() => { try {
                accepted = CalibratedTransformRigOutputOwner.TryPreflightWithLocomotion(_receiver, spec, rig.Origin,
                    carrier, calibration, rig.Joints, Array.Empty<int>(), rig.WorldFromReceiver, "preflight", out _);
            } catch (Exception e) { failure = e; } });
            worker.Start(); Assert.That(worker.Join(2000), Is.True);
            Assert.That(failure, Is.Null); Assert.That(accepted, Is.False);
            BindCarrier(rig, carrier, cp);
        }
    }
}
#endif
