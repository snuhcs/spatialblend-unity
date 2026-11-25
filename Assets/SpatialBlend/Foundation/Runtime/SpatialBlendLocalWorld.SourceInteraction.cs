// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    public sealed partial class SpatialBlendLocalWorld
    {
        private bool OwnsSourceEvidence(LocalActorRegistration registration)
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThread && IsInitialized && !_shuttingDown &&
                registration != null && registration.IsLive && ReferenceEquals(registration.World, this) &&
                registration.Generation == _generation && registration.Owner != null &&
                ReferenceEquals(registration.Owner.Registration, registration);
        }
        internal SourceInteractionEvidenceStatus TryEnableSourceEvidence(LocalActorRegistration registration, ulong epoch, bool enabled)
            => OwnsSourceEvidence(registration) ? SpatialBlendAPI.TryEnableSourceEvidence(_session,
                registration.NativeSpace, registration.NativeActor, epoch, enabled) : SourceInteractionEvidenceStatus.Unavailable;

        internal SourceInteractionEvidenceStatus TryCopySourceEvidence(LocalActorRegistration registration,
            ulong epoch, long sequence, out SourceInteractionEvidence evidence)
        {
            evidence = null;
            if (!OwnsSourceEvidence(registration)) return SourceInteractionEvidenceStatus.Unavailable;
            return SpatialBlendAPI.TryCopySourceEvidence(_session, registration.NativeSpace, registration.NativeActor,
                epoch, sequence, SpatialBlendAPI.SourceEvidenceMaximumAgeNs, out evidence);
        }
        internal SourceInteractionEvidenceStatus TryValidateSourceEvidence(LocalActorRegistration registration,
            SourceInteractionEvidence evidence, long maximumAgeNs)
        {
            if (!OwnsSourceEvidence(registration) || evidence == null || evidence.NativeSpace != registration.NativeSpace ||
                evidence.NativeActor != registration.NativeActor) return SourceInteractionEvidenceStatus.Unavailable;
            return SpatialBlendAPI.TryValidateSourceEvidence(_session, evidence, maximumAgeNs);
        }
    }
}
