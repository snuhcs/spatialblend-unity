// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>Where the embodiment files come from; a test supplies its own.</summary>
    internal interface IEmbodimentTextSource
    {
        /// <summary>The text of the named file (no extension), or false when there is none.</summary>
        bool TryRead(string name, out string text);
    }

    /// <summary>The files in a Resources folder (<c>Resources/Embodiment/&lt;name&gt;.json</c>).</summary>
    internal sealed class ResourcesEmbodimentTextSource : IEmbodimentTextSource
    {
        internal const string Folder = "Embodiment/";

        public bool TryRead(string name, out string text)
        {
            TextAsset asset = Resources.Load<TextAsset>(Folder + name);
            text = asset != null ? asset.text : null;
            return text != null;
        }
    }

    /// <summary>The three files of one embodiment (names without extension).</summary>
    internal readonly struct EmbodimentFiles
    {
        /// <summary>The structure in the core's schema: skeleton, anchors, bones, families. No body number.</summary>
        internal readonly string Structural;
        /// <summary>The measurement artefact: the offsets (registered frame) of the anchors that need one, with provenance.</summary>
        internal readonly string Measurement;
        /// <summary>The chain artefact: each joint's rest pose in the core's canonical axes (optional).</summary>
        internal readonly string Chain;
        /// <summary>
        /// The sit-bone offset measured on EVERY shipped preset avatar (optional). The measurement artefact's pelvis offset is
        /// one preset's; the spread over the presets is up to 12 cm, more than any contact error worth reporting, so
        /// an avatar that states its preset is registered with that preset's own measured offset.
        /// </summary>
        internal readonly string SitBonePresets;

        internal EmbodimentFiles(string structural, string measurement, string chain, string sitBonePresets = null)
        {
            Structural = structural;
            Measurement = measurement;
            Chain = chain;
            SitBonePresets = sitBonePresets;
        }
    }

    /// <summary>One anchor of a built registration, as the driver needs it.</summary>
    internal readonly struct EmbodimentAnchorInfo
    {
        internal readonly string Name;
        /// <summary>The bone the anchor is attached to, or null for the actor root.</summary>
        internal readonly string BoneName;

        internal EmbodimentAnchorInfo(string name, string boneName)
        {
            Name = name;
            BoneName = boneName;
        }

        internal bool IsBoneBound => BoneName != null;
    }

    /// <summary>A built registration: the core-schema JSON and what the driver must know about it.</summary>
    internal sealed class EmbodimentSpec
    {
        /// <summary>The spec for <c>sb_register_embodiment_json</c>.</summary>
        internal string Json;
        /// <summary>The anchors that are in <see cref="Json"/> (the ones whose data was present).</summary>
        internal readonly List<EmbodimentAnchorInfo> Anchors = new List<EmbodimentAnchorInfo>();
        /// <summary>The native-to-canonical basis rows (3x3, row-major) the measurement states; null when no bone-bound anchor is registered.</summary>
        internal double[] NativeBasisRows;
        /// <summary>True when the chain (rest data) is part of <see cref="Json"/>.</summary>
        internal bool HasChain;
        /// <summary>True when a floor-footprint radius (with provenance) is part of <see cref="Json"/>. The radius is per actor (ABI 4.7): avatars of different sizes coexist in one space.</summary>
        internal bool HasBody;
        /// <summary>What was left out and why, for the log.</summary>
        internal readonly List<string> Notes = new List<string>();

        internal int BoneBoundCount
        {
            get
            {
                int n = 0;
                foreach (EmbodimentAnchorInfo a in Anchors) if (a.IsBoneBound) ++n;
                return n;
            }
        }

        internal bool TryFindAnchor(string name, out EmbodimentAnchorInfo anchor)
        {
            foreach (EmbodimentAnchorInfo a in Anchors)
            {
                if (string.Equals(a.Name, name, StringComparison.Ordinal))
                {
                    anchor = a;
                    return true;
                }
            }
            anchor = default;
            return false;
        }
    }

    /// <summary>
    /// Builds the registration of an embodiment from its data files, and nothing else: no body number is in code. The
    /// structural file states the skeleton and which bone each anchor is attached to and what it serves; an anchor that
    /// needs a measured offset says <c>"offset_from": "measurement"</c>, and its offset and provenance are taken from the
    /// measurement artefact (<c>registration.anchors[]</c>) verbatim. An anchor whose measurement is absent is left out (no
    /// joint-centre or other fallback). The chain artefact, when present, is added as the spec's <c>chain</c>.
    /// </summary>
    internal static class EmbodimentSpecLoader
    {
        internal const string OffsetSourceKey = "offset_from";
        /// <summary>
        /// An anchor member of the structure file: <c>"driven_by": "entity_input"</c> names an anchor the entity reads and writes through its
        /// own tracked-input channel (the Meta avatar's retarget head), which is not a bone of the skeleton. It is registered as a LEGACY
        /// anchor (no bone, no offset: published and followed through its own port, exactly as without a registration), because a
        /// bone sampled in one frame and a target written to an input in another would not close a loop.
        /// </summary>
        internal const string DrivenByKey = "driven_by";
        internal const string DrivenByEntityInput = "entity_input";
        internal const string OffsetSourceMeasurement = "measurement";

        /// <summary>
        /// False when there is no structural file or it is unusable (the caller then keeps the legacy anchor path); the
        /// reason is in <paramref name="report"/>. True otherwise, possibly with fewer anchors than the structural file
        /// declares: see <see cref="EmbodimentSpec.Notes"/>.
        /// </summary>
        internal static bool TryBuild(IEmbodimentTextSource source, EmbodimentFiles files, out EmbodimentSpec spec,
                                      out string report) =>
            TryBuild(source, files, null, out spec, out report);

        /// <summary>
        /// As above, for an avatar that states its <paramref name="avatarPreset"/>: an anchor the per-preset artefact
        /// (<see cref="EmbodimentFiles.SitBonePresets"/>) measured takes that preset's offset and provenance, in place of the
        /// measurement artefact's. A preset the artefact does not hold leaves the anchor out (a number measured on another avatar is
        /// not this avatar's); null keeps the measurement artefact's offset, as an avatar that states no preset always had.
        /// </summary>
        internal static bool TryBuild(IEmbodimentTextSource source, EmbodimentFiles files, int? avatarPreset,
                                      out EmbodimentSpec spec, out string report)
        {
            spec = null;
            report = null;
            if (source == null || !source.TryRead(files.Structural, out string structuralText))
            {
                report = "no embodiment structure file '" + files.Structural + "'";
                return false;
            }
            if (!MiniJson.TryParse(structuralText, out object structuralValue, out string error) ||
                !(structuralValue is MiniJsonObject structural))
            {
                report = "embodiment structure file '" + files.Structural + "' is not a JSON object" +
                         (error != null ? ": " + error : "");
                return false;
            }
            if (!(structural.Get("anchors") is List<object> declared) || !(structural.Get("skeleton") is MiniJsonObject))
            {
                report = "embodiment structure file '" + files.Structural + "' needs skeleton and anchors";
                return false;
            }

            var result = new EmbodimentSpec();
            MiniJsonObject measurementRegistration = ReadMeasurement(source, files, result, out double[] basis);
            Dictionary<string, MiniJsonObject> measured = IndexMeasuredAnchors(measurementRegistration);
            MiniJsonObject body = ReadBody(measurementRegistration, files, result);
            PresetOffsets presets = avatarPreset.HasValue ? ReadPresetOffsets(source, files, avatarPreset.Value, result) : null;

            var anchors = new List<object>();
            foreach (object item in declared)
            {
                if (!(item is MiniJsonObject anchor) || !(anchor.Get("name") is string name) || name.Length == 0)
                {
                    report = "an anchor of '" + files.Structural + "' has no name";
                    return false;
                }
                bool entityInput = string.Equals(anchor.Get(DrivenByKey) as string, DrivenByEntityInput, StringComparison.Ordinal);
                string bone = entityInput ? null : anchor.Get("bone") as string;
                bool needsMeasurement = !entityInput && string.Equals(anchor.Get(OffsetSourceKey) as string, OffsetSourceMeasurement,
                    StringComparison.Ordinal);
                var built = new MiniJsonObject();
                foreach (KeyValuePair<string, object> member in anchor.Members)
                {
                    if (member.Key == OffsetSourceKey || member.Key == DrivenByKey) continue;
                    if (entityInput && (member.Key == "bone" || member.Key == "offset" || member.Key == "provenance")) continue;
                    built.Members.Add(member);
                }
                if (entityInput) built.Set("bone", null);
                if (needsMeasurement)
                {
                    if (basis == null)
                    {
                        result.Notes.Add("anchor '" + name + "' left out: it needs the measurement artefact '" + files.Measurement + "'");
                        continue;
                    }
                    if (!measured.TryGetValue(name, out MiniJsonObject entry))
                    {
                        result.Notes.Add("anchor '" + name + "' left out: '" + files.Measurement + "' has no registration for it");
                        continue;
                    }
                    if (!string.Equals(entry.Get("bone") as string, bone, StringComparison.Ordinal))
                    {
                        result.Notes.Add("anchor '" + name + "' left out: the measurement is for bone '" + entry.Get("bone") +
                                         "', the structure attaches it to '" + bone + "'");
                        continue;
                    }
                    if (!(entry.Get("offset") is MiniJsonObject offset) || !(entry.Get("provenance") is MiniJsonObject provenance))
                    {
                        result.Notes.Add("anchor '" + name + "' left out: its registration has no offset or no provenance");
                        continue;
                    }
                    if (!IsIdentityOffset(offset) && !ProvenanceAcceptable(provenance))
                    {
                        result.Notes.Add("anchor '" + name + "' left out: a non-identity offset needs provenance.procedure and either a 64-hex asset_sha256 or a source (the core refuses it otherwise)");
                        continue;
                    }
                    if (presets != null && string.Equals(presets.Anchor, name, StringComparison.Ordinal))
                    {
                        if (!string.Equals(presets.Bone, bone, StringComparison.Ordinal))
                        {
                            result.Notes.Add("anchor '" + name + "' left out: the per-preset measurement is for bone '" + presets.Bone +
                                             "', the structure attaches it to '" + bone + "'");
                            continue;
                        }
                        if (presets.Offset == null || presets.Provenance == null || !ProvenanceAcceptable(presets.Provenance))
                        {
                            result.Notes.Add("anchor '" + name + "' left out: preset " + avatarPreset.Value + " has no measured offset in '" +
                                             files.SitBonePresets + "'");
                            continue;
                        }
                        offset = WithPosition(offset, presets.Offset);
                        provenance = WithPresetProvenance(provenance, presets.Provenance);
                    }
                    built.Set("offset", offset);
                    built.Set("provenance", provenance);
                }
                anchors.Add(built);
                result.Anchors.Add(new EmbodimentAnchorInfo(name, string.IsNullOrEmpty(bone) ? null : bone));
            }

            var output = new MiniJsonObject();
            foreach (KeyValuePair<string, object> member in structural.Members)
            {
                output.Members.Add(member.Key == "anchors" ? new KeyValuePair<string, object>("anchors", anchors) : member);
            }
            if (body != null) output.Set("body", body);
            if (result.BoneBoundCount > 0)
            {
                result.NativeBasisRows = basis;
                if (TryReadChain(source, files, basis, result, out MiniJsonObject chain))
                {
                    output.Set("chain", chain);
                    result.HasChain = true;
                }
            }
            result.Json = MiniJson.Write(output);
            spec = result;
            return true;
        }

        /// <summary>The offset and provenance one preset measured for one anchor.</summary>
        private sealed class PresetOffsets
        {
            internal string Anchor;
            internal string Bone;
            internal double[] Offset;
            internal MiniJsonObject Provenance;
        }

        // The per-preset artefact's entry for `preset`: null (with a note) when the artefact is absent or does not hold it. The
        // artefact states the one anchor it measured and its bone, the registered position of each preset (canonical axes) and the
        // sha256 of the asset it was measured on.
        private static PresetOffsets ReadPresetOffsets(IEmbodimentTextSource source, EmbodimentFiles files, int preset,
                                                       EmbodimentSpec result)
        {
            // The anchor the per-preset artefact measures. An avatar that states its preset gets that preset's number or no such anchor: a missing or
            // unusable artefact leaves the anchor out like a preset it does not hold, never the measurement artefact's one-preset offset.
            const string SitBoneAnchor = "pelvis", SitBoneBone = "Hips";
            if (files.SitBonePresets == null || !source.TryRead(files.SitBonePresets, out string text))
            {
                result.Notes.Add("no per-preset artefact '" + files.SitBonePresets + "': preset " + preset + " has no measured offset");
                return new PresetOffsets { Anchor = SitBoneAnchor, Bone = SitBoneBone };
            }
            if (!MiniJson.TryParse(text, out object value, out string error) || !(value is MiniJsonObject root) ||
                !(root.Get("presets") is List<object> list) || !(root.Get("anchor") is string anchor) ||
                !(root.Get("bone") is string bone))
            {
                result.Notes.Add("per-preset artefact '" + files.SitBonePresets + "' is unusable" + (error != null ? ": " + error : ""));
                return new PresetOffsets { Anchor = SitBoneAnchor, Bone = SitBoneBone };
            }
            foreach (object item in list)
            {
                if (!(item is MiniJsonObject entry) || !(entry.Get("preset") is double id) || (int)id != preset) continue;
                var offset = new double[3];
                if (!(entry.Get("offset_registered_canonical_m") is List<object> position) || position.Count != 3) break;
                for (int i = 0; i < 3; ++i)
                {
                    if (!(position[i] is double d) || double.IsNaN(d) || double.IsInfinity(d)) return new PresetOffsets { Anchor = anchor, Bone = bone };
                    offset[i] = d;
                }
                var provenance = new MiniJsonObject();
                provenance.Set("procedure", (root.Get("procedure") as string ?? "") + " Preset " + preset + ".");
                provenance.Set("asset_sha256", entry.Get("sha256"));
                provenance.Set("source", files.SitBonePresets + ".json");
                provenance.Set("preset", (double)preset);
                if (entry.Get("sensitivity_native_bone_local_m") != null)
                    provenance.Set("sensitivity_native_bone_local_m", entry.Get("sensitivity_native_bone_local_m"));
                if (entry.Get("n_patch_vertices") != null) provenance.Set("n_patch_vertices", entry.Get("n_patch_vertices"));
                if (entry.Get("sha256_decompressed_glb") != null)
                    provenance.Set("asset_sha256_decompressed_glb", entry.Get("sha256_decompressed_glb"));
                return new PresetOffsets { Anchor = anchor, Bone = bone, Offset = offset, Provenance = provenance };
            }
            result.Notes.Add("preset " + preset + " is not in the per-preset artefact '" + files.SitBonePresets + "'");
            return new PresetOffsets { Anchor = anchor, Bone = bone };
        }

        // The measurement artefact's offset with the position of another measurement; the rotation (the bone's frame) is the artefact's.
        private static MiniJsonObject WithPosition(MiniJsonObject offset, double[] position)
        {
            var copy = new MiniJsonObject();
            foreach (KeyValuePair<string, object> member in offset.Members)
            {
                copy.Members.Add(member.Key == "position"
                    ? new KeyValuePair<string, object>("position", new List<object> { position[0], position[1], position[2] })
                    : member);
            }
            return copy;
        }

        // The base provenance (reference pose, rotation, caveats) with the preset's own procedure, asset hash and numbers on top.
        private static MiniJsonObject WithPresetProvenance(MiniJsonObject baseProvenance, MiniJsonObject preset)
        {
            var merged = new MiniJsonObject();
            foreach (KeyValuePair<string, object> member in baseProvenance.Members)
            {
                if (member.Key == "offset_native_bone_local_m" || member.Key == "sensitivity_native_bone_local_m" ||
                    member.Key == "n_patch_vertices_10mm" || member.Key == "cross_preset_spread_33_Quest_presets_10mm_bone_local") continue;
                merged.Members.Add(member);
            }
            foreach (KeyValuePair<string, object> member in preset.Members) merged.Set(member.Key, member.Value);
            return merged;
        }

        // The measurement artefact's `registration` block and the basis it states. Missing or malformed: no measured anchor.
        private static MiniJsonObject ReadMeasurement(IEmbodimentTextSource source, EmbodimentFiles files,
                                                      EmbodimentSpec result, out double[] basis)
        {
            basis = null;
            if (files.Measurement == null || !source.TryRead(files.Measurement, out string text))
            {
                result.Notes.Add("no measurement artefact '" + files.Measurement + "': the anchors that need a measured offset are not registered");
                return null;
            }
            if (!MiniJson.TryParse(text, out object value, out string error) || !(value is MiniJsonObject root) ||
                !(root.Get("registration") is MiniJsonObject registration))
            {
                result.Notes.Add("measurement artefact '" + files.Measurement + "' is unusable" + (error != null ? ": " + error : "") +
                                 ": the anchors that need a measured offset are not registered");
                return null;
            }
            if (!TryReadBasis(registration.Get("native_basis_rows"), out basis))
            {
                result.Notes.Add("measurement artefact '" + files.Measurement + "' states no valid native_basis_rows");
                return null;
            }
            return registration;
        }

        // The optional body floor-footprint radius (core spec section 13): finite and > 0, with the core's provenance rule. There is no default
        // and none is invented: absent means the core uses a point body. A violating `body` is dropped and logged, never repaired.
        private static MiniJsonObject ReadBody(MiniJsonObject registration, EmbodimentFiles files, EmbodimentSpec result)
        {
            if (registration == null || !registration.TryGet("body", out object value)) return null;
            if (!(value is MiniJsonObject body) || !(body.Get("floor_footprint_radius_m") is double radius) ||
                double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0.0)
            {
                result.Notes.Add("body left out: floor_footprint_radius_m must be a finite number above 0");
                return null;
            }
            if (!(body.Get("provenance") is MiniJsonObject provenance) || !ProvenanceAcceptable(provenance))
            {
                result.Notes.Add("body left out: it needs provenance.procedure and a 64-hex asset_sha256 or a source (the core refuses it otherwise)");
                return null;
            }
            result.HasBody = true;
            return body;
        }

        private static Dictionary<string, MiniJsonObject> IndexMeasuredAnchors(MiniJsonObject registration)
        {
            var index = new Dictionary<string, MiniJsonObject>(StringComparer.Ordinal);
            if (registration != null && registration.Get("anchors") is List<object> list)
            {
                foreach (object item in list)
                {
                    if (item is MiniJsonObject entry && entry.Get("name") is string name) index[name] = entry;
                }
            }
            return index;
        }

        private static bool TryReadChain(IEmbodimentTextSource source, EmbodimentFiles files, double[] basis,
                                         EmbodimentSpec result, out MiniJsonObject chain)
        {
            chain = null;
            if (files.Chain == null || !source.TryRead(files.Chain, out string text))
            {
                result.Notes.Add("no chain artefact '" + files.Chain + "': registered without the kinematic chain");
                return false;
            }
            if (!MiniJson.TryParse(text, out object value, out string error) || !(value is MiniJsonObject root) ||
                !(root.Get("chain") is MiniJsonObject found))
            {
                result.Notes.Add("chain artefact '" + files.Chain + "' is unusable" + (error != null ? ": " + error : "") +
                                 ": registered without the kinematic chain");
                return false;
            }
            string restPose = found.Get("rest_pose") as string;
            if (restPose != "A" && restPose != "T" && restPose != "other")
            {
                result.Notes.Add("chain artefact '" + files.Chain + "' rest_pose must be \"A\", \"T\" or \"other\": registered without the chain");
                return false;
            }
            if (!(found.Get("provenance") is MiniJsonObject chainProvenance) || !ProvenanceAcceptable(chainProvenance))
            {
                result.Notes.Add("chain artefact '" + files.Chain + "' needs provenance.procedure and a 64-hex asset_sha256 or a source: registered without the chain");
                return false;
            }
            // One basis, two files: they must say the same, else the rest frames and the offsets are in different frames.
            if (!TryReadBasis(root.Get("native_basis_rows"), out double[] chainBasis) || !SameRows(basis, chainBasis))
            {
                result.Notes.Add("chain artefact '" + files.Chain + "' states a different native basis than the measurement: registered without the chain");
                return false;
            }
            chain = found;
            return true;
        }

        /// <summary>The core's rule for a measured number: a non-empty procedure AND either asset_sha256 as 64 hex digits or a non-empty source.</summary>
        internal static bool ProvenanceAcceptable(MiniJsonObject provenance)
        {
            if (!(provenance.Get("procedure") is string procedure) || procedure.Length == 0) return false;
            if (provenance.Get("source") is string source && source.Length > 0) return true;
            if (!(provenance.Get("asset_sha256") is string hash) || hash.Length != 64) return false;
            foreach (char c in hash)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        // Exactly the identity (zero position, unit quaternion w = 1), as the core tests it.
        private static bool IsIdentityOffset(MiniJsonObject offset)
        {
            if (!(offset.Get("position") is List<object> p) || !(offset.Get("rotation") is List<object> q) || p.Count != 3 || q.Count != 4) return false;
            return Equals(p[0], 0.0) && Equals(p[1], 0.0) && Equals(p[2], 0.0) && Equals(q[0], 0.0) && Equals(q[1], 0.0) && Equals(q[2], 0.0) && Equals(q[3], 1.0);
        }

        internal static bool TryReadBasis(object value, out double[] rows)
        {
            rows = null;
            if (!(value is List<object> list) || list.Count != 9) return false;
            var parsed = new double[9];
            for (int i = 0; i < 9; ++i)
            {
                if (!(list[i] is double d)) return false;
                parsed[i] = d;
            }
            rows = parsed;
            return true;
        }

        private static bool SameRows(double[] a, double[] b)
        {
            for (int i = 0; i < 9; ++i) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>A one-line description of a build, for the log.</summary>
        internal static string Describe(EmbodimentSpec spec)
        {
            var sb = new StringBuilder();
            sb.Append(spec.Anchors.Count).Append(" anchors (").Append(spec.BoneBoundCount).Append(" bone-bound)")
              .Append(spec.HasChain ? ", with chain" : ", no chain")
              .Append(spec.HasBody ? ", with body radius" : ", no body radius (point body in the core)");
            foreach (string note in spec.Notes) sb.Append("; ").Append(note);
            return sb.ToString();
        }
    }
}
