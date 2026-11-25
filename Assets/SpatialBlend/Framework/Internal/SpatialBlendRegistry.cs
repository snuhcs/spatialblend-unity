// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace SpatialBlend.Internal
{
    using SpaceID = System.Int64;

    /// <summary>
    /// Manages the global registry of all SpatialBlendManager instances.
    /// This static class is responsible for mapping space_id to the corresponding manager
    /// and routing native callbacks to the correct instance, which is essential for IL2CPP compatibility.
    /// </summary>
    internal static class SpatialBlendRegistry
    {
        private static readonly Dictionary<SpaceID, SpatialBlendManager> _managers = new Dictionary<SpaceID, SpatialBlendManager>();
        private static readonly object _managersLock = new object();

        internal static void Register(SpaceID spaceId, SpatialBlendManager manager)
        {
            lock (_managersLock)
            {
                _managers[spaceId] = manager;
            }
        }

        internal static void Unregister(SpaceID spaceId)
        {
            lock (_managersLock)
            {
                _managers.Remove(spaceId);
            }
        }

        internal static bool TryGetManager(SpaceID spaceId, out SpatialBlendManager manager)
        {
            lock (_managersLock)
            {
                return _managers.TryGetValue(spaceId, out manager);
            }
        }
    }
}
