// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Internal
{
    // Managed bags for the inspector query wrappers. v2 has no sb_get_* for
    // these, and they are not marshalled to a native library.

    internal struct SbSpaceInfo
    {
        public ulong num_entities;
        public ulong num_connected_spaces;
        public bool is_host;
        public bool is_connected;
        public bool has_walkable_geometry;
        public bool has_wall_geometry;
    }

    internal struct SbEntityInfo
    {
        public long entity_id;
        public bool is_local;
        public bool is_valid;
        public ulong user_data_size;
        public double[] position_2d;
        public double[] position_3d;
        public double rotation_yaw;
        public double rotation_pitch;
    }

    internal struct SbEntityCounts
    {
        public ulong total_entities;
        public ulong local_entities;
        public ulong remote_entities;
        public ulong dirty_entities;
    }

    internal struct SbStateVector
    {
        public double[] values;
        public ulong num_values;
        public long state_type;
    }

    internal struct SbEntityState
    {
        public long entity_id;
        public SbStateVector[] state_vectors;
        public ulong num_state_vectors;
        public byte[] user_data;
        public ulong user_data_size;
    }

    internal struct SbGeometryBounds
    {
        public double min_x, min_y;
        public double max_x, max_y;
        public double width, height;
        public double center_x, center_y;
    }

    internal struct SbSpaceStateSummary
    {
        public SbSpaceInfo space_info;
        public SbEntityCounts entity_counts;
        public SbGeometryBounds walkable_bounds;
        public SbGeometryBounds wall_bounds;
        public ulong num_proximity_types;
        public bool has_any_interactions;
    }

    internal struct SbInteractionInfo
    {
        public int proximity_type;
        public long source_entity_id;
        public long[] target_entity_ids;
        public ulong num_targets;
        public bool is_wildcard;
    }

    internal struct SbProximityInfo
    {
        public int proximity_type;
        public bool is_active;
        public double current_value;
        public double threshold;
    }

    internal struct SbPoint2D
    {
        public double x, y;
    }

    internal struct SbPoint3D
    {
        public double x, y, z;
    }

    internal struct SbPolygonRing
    {
        public double[] points;
        public ulong num_points;
        public bool is_inner;
    }

    internal struct SbPolygonRingEx
    {
        public double[] points;
        public ulong num_points;
        public bool is_inner;
        public int source;
    }

    internal struct SbPolygon
    {
        public SbPolygonRing[] rings;
        public ulong num_rings;
        public double min_x, min_y;
        public double max_x, max_y;
    }

    internal struct SbPolygonEx
    {
        public SbPolygonRingEx[] rings;
        public ulong num_rings;
        public double min_x, min_y;
        public double max_x, max_y;
    }

    internal struct SbEntityTargetInfo
    {
        public bool has_target;
        public long target_entity_id;
        public double[] target_position_2d;
        public double[] target_position_3d;
        public double steps_to_target;
        public int proximity_type;
        public double distance_to_target;
    }

    internal struct SbExpectedInteractionTime
    {
        public long target_id;
        public double steps;
        public int proximity_type;
        public bool has_remaining_distance;
        public double remaining_distance;
        public bool has_progress_distance;
        public double progress_distance;
        public bool has_path_distance;
        public double path_distance;
        public bool has_angular_distance;
        public double angular_distance;
        public bool has_angular_step;
        public double angular_step;
    }

    internal struct SbInteractionTimeline
    {
        public SbExpectedInteractionTime[] interactions;
        public ulong num_interactions;
        public long source_entity_id;
        public int proximity_type;
    }

    internal struct SbProximityDistanceInfo
    {
        public bool has_info;
        public long space_id;
        public long source_entity_id;
        public long target_entity_id;
        public int proximity_type;
        public bool has_remaining_distance;
        public double remaining_distance;
        public bool has_progress_distance;
        public double progress_distance;
        public bool has_path_distance;
        public double path_distance;
        public bool has_angular_distance;
        public double angular_distance;
        public bool has_angular_step;
        public double angular_step;
    }

    internal struct SbIntentTargetInfo
    {
        public long target_entity_id;
        public double remaining_steps;
        public bool has_approach;
        public float[] approach_position;
        public float[] approach_direction;
        public int proximity_type;
    }
}
