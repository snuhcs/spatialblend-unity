// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

// Native handles remain an implementation detail. The legacy integration and
// its verification assemblies reuse this binding, never a second native session API.
[assembly: InternalsVisibleTo("SpatialBlend")]
[assembly: InternalsVisibleTo("SpatialBlend.Editor")]
[assembly: InternalsVisibleTo("SpatialBlend.RestorationTests")]
[assembly: InternalsVisibleTo("SpatialBlend.Foundation.Tests")]
[assembly: InternalsVisibleTo("SpatialBlend.PhotonCloudTests")]
