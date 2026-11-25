// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The scanned rooms Meta's MR Utility Kit ships (Core/Rooms/Json, three bedrooms, three living rooms, two offices),
    /// as the observed geometry the app registers from them: each anchor's volume and plane through the same descriptor
    /// the MRUK adapter builds. The files are read in place from the package, never copied into this repository.
    /// </summary>
    internal static class MrukSampleRoomFixtures
    {
        internal sealed class Room
        {
            public string Name;
            public readonly List<SceneGeometryDescriptor> Anchors = new List<SceneGeometryDescriptor>();
            public readonly List<string> Labels = new List<string>();
        }

        internal static List<Room> Load()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.meta.xr.mrutilitykit/package.json");
            string directory = Path.Combine(info.resolvedPath, "Core", "Rooms", "Json");
            var rooms = new List<Room>();
            string[] files = Directory.GetFiles(directory, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                var root = (Dictionary<string, object>)new Parser(File.ReadAllText(file)).Read();
                var list = (List<object>)root["Rooms"];
                for (int r = 0; r < list.Count; ++r)
                {
                    var room = new Room { Name = Path.GetFileNameWithoutExtension(file) + (list.Count > 1 ? "#" + r : "") };
                    int key = 0;
                    foreach (object item in (List<object>)((Dictionary<string, object>)list[r])["Anchors"])
                    {
                        var anchor = (Dictionary<string, object>)item;
                        ++key;
                        string label = (string)((List<object>)anchor["SemanticClassifications"])[0];
                        if (label == "GLOBAL_MESH") continue;
                        var xf = (Dictionary<string, object>)anchor["Transform"];
                        Vector3 t = V3(xf["Translation"]), e = V3(xf["Rotation"]);
                        Matrix4x4 world = Matrix4x4.TRS(t, Quaternion.Euler(e), Vector3.one);
                        Bounds? volume = null;
                        if (anchor.TryGetValue("VolumeBounds", out object vb))
                        {
                            var b = (Dictionary<string, object>)vb;
                            Vector3 min = V3(b["Min"]), max = V3(b["Max"]);
                            volume = new Bounds((min + max) * 0.5f, new Vector3(Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y), Mathf.Abs(max.z - min.z)));
                        }
                        Vector2[] boundary = null;
                        if (anchor.TryGetValue("PlaneBoundary2D", out object pb))
                        {
                            var points = (List<object>)pb;
                            boundary = new Vector2[points.Count];
                            for (int i = 0; i < points.Count; ++i)
                            {
                                var p = (List<object>)points[i];
                                boundary[i] = new Vector2(F(p[0]), F(p[1]));
                            }
                        }
                        Rect? rect = null;
                        if (anchor.TryGetValue("PlaneBounds", out object pr))
                        {
                            var b = (Dictionary<string, object>)pr;
                            var min = (List<object>)b["Min"]; var max = (List<object>)b["Max"];
                            rect = Rect.MinMaxRect(F(min[0]), F(min[1]), F(max[0]), F(max[1]));
                        }
                        if (SceneGeometryDescriptor.TryCreate(key, world, volume, boundary, rect, out SceneGeometryDescriptor descriptor))
                        {
                            room.Anchors.Add(descriptor);
                            room.Labels.Add(label);
                        }
                    }
                    rooms.Add(room);
                }
            }
            return rooms;
        }

        private static float F(object value) => (float)(double)value;
        private static Vector3 V3(object value)
        {
            var a = (List<object>)value;
            return new Vector3(F(a[0]), F(a[1]), F(a[2]));
        }

        // A minimal JSON reader: objects, arrays, numbers, strings, booleans and null.
        private sealed class Parser
        {
            private readonly string _text;
            private int _index;
            internal Parser(string text) { _text = text; }

            internal object Read()
            {
                Skip();
                char c = _text[_index];
                if (c == '{')
                {
                    ++_index;
                    var map = new Dictionary<string, object>();
                    Skip();
                    if (_text[_index] == '}') { ++_index; return map; }
                    while (true)
                    {
                        Skip();
                        string key = (string)Read();
                        Skip(); ++_index; // ':'
                        map[key] = Read();
                        Skip();
                        if (_text[_index++] == '}') return map;
                    }
                }
                if (c == '[')
                {
                    ++_index;
                    var list = new List<object>();
                    Skip();
                    if (_text[_index] == ']') { ++_index; return list; }
                    while (true)
                    {
                        list.Add(Read());
                        Skip();
                        if (_text[_index++] == ']') return list;
                    }
                }
                if (c == '"')
                {
                    ++_index;
                    var sb = new StringBuilder();
                    while (_text[_index] != '"')
                    {
                        if (_text[_index] == '\\') ++_index;
                        sb.Append(_text[_index++]);
                    }
                    ++_index;
                    return sb.ToString();
                }
                if (string.CompareOrdinal(_text, _index, "true", 0, 4) == 0) { _index += 4; return true; }
                if (string.CompareOrdinal(_text, _index, "false", 0, 5) == 0) { _index += 5; return false; }
                if (string.CompareOrdinal(_text, _index, "null", 0, 4) == 0) { _index += 4; return null; }
                int start = _index;
                while (_index < _text.Length && "+-0123456789.eE".IndexOf(_text[_index]) >= 0) ++_index;
                return double.Parse(_text.Substring(start, _index - start), CultureInfo.InvariantCulture);
            }

            private void Skip()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) ++_index;
            }
        }
    }
}
#endif
