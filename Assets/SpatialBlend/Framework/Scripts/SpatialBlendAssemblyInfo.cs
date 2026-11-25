// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

// The restoration tests drive the manager's embodied-driver seams (CreateEmbodiedCore,
// ConfigureEmbodiedDriver) with a fake core. They are internal because their types are.
[assembly: InternalsVisibleTo("SpatialBlend.RestorationTests")]
