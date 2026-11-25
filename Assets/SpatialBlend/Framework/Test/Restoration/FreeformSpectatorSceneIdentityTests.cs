// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A spectator binds to the room's connection manager only if its scene has the Freeform scene's NetworkObjects in the same order
    /// (Fusion names a scene object by its position among the scene's NetworkObjects sorted by SortKey). This reads both scene files
    /// and compares what is there, so it fails when the Freeform scene changes and the spectator scene is not regenerated
    /// (SpatialBlend.Editor.SpectatorBuild.CreateScene), instead of comparing to constants in the builder.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformSpectatorSceneIdentityTests
    {
        private const string ConnectionManagerName = "FusionConnectionManager";

        private sealed class SceneObject
        {
            public long SortKey;
            public int Behaviours;
            public bool IsConnectionManager;
            public string Origin;
        }

        private static string ScenePath(string name) => Path.Combine(Application.dataPath, "SpatialBlend", "Samples", "Scenes", name);

        /// <summary>
        /// The scene's NetworkObjects as the YAML states them: components of a plain scene object (SortKey, NetworkedBehaviours) and
        /// those of prefab instances (SortKey and NetworkedBehaviours overrides on the prefab's own NetworkObject components).
        /// </summary>
        private static List<SceneObject> Parse(string path, out List<string> prefabGuids)
        {
            var text = File.ReadAllText(path);
            var result = new List<SceneObject>();
            // Plain components: a NetworkObject has SortKey, ObjectInterest, Flags, NestedObjects and NetworkedBehaviours together.
            var gameObjectNames = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(text, @"--- !u!1 &(\d+)\nGameObject:(?:.|\n)*?\n  m_Name: ([^\n]*)"))
                gameObjectNames[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            foreach (Match m in Regex.Matches(text, @"--- !u!114 &\d+\nMonoBehaviour:\n(?:  .*\n)*?  m_GameObject: \{fileID: (\d+)\}\n(?:  .*\n)*?  SortKey: (\d+)\n(?:  .*\n)*?  NetworkedBehaviours:\n((?:  - \{fileID: \d+\}\n)*)"))
            {
                var name = gameObjectNames.TryGetValue(m.Groups[1].Value, out var n) ? n : "?";
                result.Add(new SceneObject
                {
                    SortKey = long.Parse(m.Groups[2].Value),
                    Behaviours = Regex.Matches(m.Groups[3].Value, @"- \{fileID").Count,
                    IsConnectionManager = name == ConnectionManagerName,
                    Origin = "scene object " + name,
                });
            }
            // Prefab instances: a SortKey override names the prefab's NetworkObject component by its file id; its behaviours are
            // overrides of NetworkedBehaviours.Array.data[i] on the same target.
            var keys = new Dictionary<string, long>();
            var behaviours = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(text, @"- target: \{fileID: (\d+), guid: ([0-9a-f]+), type: 3\}\n      propertyPath: (SortKey|'NetworkedBehaviours\.Array\.data\[\d+\]')\n      value: ([^\n]*)"))
            {
                var key = m.Groups[2].Value + ":" + m.Groups[1].Value;
                if (m.Groups[3].Value == "SortKey") keys[key] = long.Parse(m.Groups[4].Value);
                else behaviours[key] = behaviours.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            foreach (var kv in keys)
                // The Freeform scene's overrides of NetworkedBehaviours re-point the prefab's own references at the instance, so their
                // count says nothing about the layout: a prefab object is identified by the prefab and the component instead.
                result.Add(new SceneObject { SortKey = kv.Value, Behaviours = -1, Origin = "prefab component " + kv.Key });
            prefabGuids = Regex.Matches(text, @"m_SourcePrefab: \{fileID: 100100000, guid: ([0-9a-f]+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
            return result.OrderBy(o => o.SortKey).ToList();
        }

        [Test]
        public void TheSpectatorSceneHasTheFreeformScenesNetworkObjectsInTheSameOrder()
        {
            var freeform = Parse(ScenePath("FreeformCollaboration.unity"), out var freeformPrefabs);
            var spectator = Parse(ScenePath("FreeformSpectator.unity"), out var spectatorPrefabs);
            NUnitAssert.That(freeform.Count, Is.EqualTo(4), "the Freeform scene's scene NetworkObjects, as read from its YAML");
            NUnitAssert.That(spectator.Select(o => o.SortKey).ToArray(), Is.EqualTo(freeform.Select(o => o.SortKey).ToArray()),
                "the sorted SortKeys: Fusion names a scene object by its position in this order");
            NUnitAssert.That(spectator.FindIndex(o => o.IsConnectionManager), Is.EqualTo(freeform.FindIndex(o => o.IsConnectionManager)),
                "the connection manager's index");
            NUnitAssert.That(freeform.FindIndex(o => o.IsConnectionManager), Is.GreaterThanOrEqualTo(0));
            NUnitAssert.That(spectator.Select(o => o.Behaviours).ToArray(), Is.EqualTo(freeform.Select(o => o.Behaviours).ToArray()),
                "the number of networked behaviours of each plain scene NetworkObject (the state layout the host replicates)");
            NUnitAssert.That(spectator.Select(o => o.Origin.StartsWith("prefab") ? o.Origin : "scene object").ToArray(),
                Is.EqualTo(freeform.Select(o => o.Origin.StartsWith("prefab") ? o.Origin : "scene object").ToArray()),
                "each prefab-instance NetworkObject comes from the same prefab and component, so it has the same behaviours");
            foreach (var guid in spectatorPrefabs) NUnitAssert.That(freeformPrefabs, Does.Contain(guid), "the spectator scene instantiates only prefabs the Freeform scene does");
        }
    }
}
#endif
