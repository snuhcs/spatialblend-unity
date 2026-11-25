// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The spectator's 3-D room view (<see cref="SpectatorRoomView"/>) driven by a session with fakes: placed objects are posed into the
    /// viewed room and written only when their room pose changes, a person's jump over 1 m is snapped and counted, the proxies' kind and
    /// placement follow the selected room (Tab), the room is rebuilt only when its furniture content changes, and clearing releases the
    /// materials it made.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SpectatorRoomViewTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private FreeformSpectatorSessionTests.TestSession _session;
        private SpectatorRoomView _view;
        private readonly Dictionary<string, Transform> _locals = new Dictionary<string, Transform>();

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("spectator-session");
            _objects.Add(go);
            var connection = go.AddComponent<FreeformSpectatorSessionTests.TestConnection>();
            _session = go.AddComponent<FreeformSpectatorSessionTests.TestSession>();
            _session.Configure(null, connection, null, null);
            typeof(FreeformSpectatorSession).GetProperty("Phase").GetSetMethod(true).Invoke(_session, new object[] { SpectatorPhase.Spectating });
            _view = SpectatorRoomView.Create(_session, null);
            _objects.Add(_view.gameObject);
            _view.LocalObjectResolver = key => _locals.TryGetValue(key, out var t) ? t : null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_view != null) _view.Clear();
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            _locals.Clear();
        }

        private static readonly IReadOnlyList<ObserverFurniture> NoFurniture = Array.Empty<ObserverFurniture>();

        private static ObserverFurniture[] Room4x3() => new[]
        {
            new ObserverFurniture("floor", "FLOOR", new Vector3(0f, 0f, 0f), Quaternion.Euler(-90f, 0f, 0f), new Vector3(4f, 3f, 0f)),
            new ObserverFurniture("w1", "WALL_FACE", new Vector3(0f, 1.25f, 1.5f), Quaternion.Euler(0f, 180f, 0f), new Vector3(4f, 2.5f, 0f)),
            new ObserverFurniture("t1", "TABLE", new Vector3(1f, 0.4f, 0f), Quaternion.identity, new Vector3(1.2f, 0.8f, 0.6f)),
        };

        private void Publish(string owner, Vector3 head, IReadOnlyList<ObserverFurniture> furniture = null, IReadOnlyList<ObserverPlacedObject> objects = null,
                             IReadOnlyList<ObserverAvatarPose> avatars = null)
        {
            var room = ParticipantRoomSnapshot.Create(owner, null, 1, true, head, Quaternion.identity, furniture ?? NoFurniture,
                objects ?? Array.Empty<ObserverPlacedObject>(), avatars ?? Array.Empty<ObserverAvatarPose>());
            Assert.That(_session.ReceiveRoomSnapshot(default, new ArraySegment<byte>(ObserverRoomCodec.Write(room))), Is.True);
        }

        private static ObserverAvatarPose Avatar(string id, Vector3 head) =>
            new ObserverAvatarPose(id, null, new Vector3(head.x, 0f, head.z), Quaternion.identity, true, head, Quaternion.identity);

        private Transform Local(string key)
        {
            var go = new GameObject(key);
            _objects.Add(go);
            _locals[key] = go.transform;
            return go.transform;
        }

        [Test]
        public void PlacedObjectsArePosedIntoTheRoomAndWrittenOnlyWhenTheirPoseChanges()
        {
            var board = Local("net:7");
            var pose = new ObserverPlacedObject("net:7", new Vector3(0.35f, 1.40f, 4.17f), Quaternion.Euler(0f, 90f, 0f));
            Publish("1", new Vector3(0f, 1.6f, 0f), Room4x3(), new[] { pose });
            _view.Step(0.016f);
            Assert.That(Vector3.Distance(board.position, pose.Position), Is.LessThan(1e-4f), "the Whiteboard at its pose in the viewed room, on its wall");
            Assert.That(Quaternion.Angle(board.rotation, pose.Rotation), Is.LessThan(0.01f));
            Assert.That(_view.PoseWrites, Is.EqualTo(1));
            for (int i = 0; i < 30; ++i) _view.Step(0.016f);
            Assert.That(_view.PoseWrites, Is.EqualTo(1), "no write while its room pose is unchanged (the core is not pushed every frame)");
            board.position += new Vector3(0f, -1.4f, 0f);   // something moved it (the core's own write)
            _view.Step(0.016f);
            Assert.That(_view.PoseWrites, Is.EqualTo(2), "put back once");
            Assert.That(board.position.y, Is.EqualTo(1.40f).Within(1e-4f));
        }

        [Test]
        public void AJumpOverAMetreIsSnappedAndCountedAStepIsNot()
        {
            Publish("1", new Vector3(0f, 1.6f, 0f), avatars: new[] { Avatar("2", new Vector3(0f, 1.6f, 1f)) });
            _view.Step(0.016f);
            Publish("1", new Vector3(0f, 1.6f, 0f), avatars: new[] { Avatar("2", new Vector3(0.5f, 1.6f, 1f)) });
            _view.Step(0.016f);
            Assert.That(_view.JumpCount, Is.EqualTo(0), "half a metre in half a second is a walk");
            Publish("1", new Vector3(0f, 1.6f, 0f), avatars: new[] { Avatar("2", new Vector3(4.2f, 1.6f, 1f)) });
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("P2 jumped"));
            _view.Step(0.016f);
            Assert.That(_view.JumpCount, Is.EqualTo(1), "3.7 m between two samples is a teleport, shown and counted");
        }

        [Test]
        public void TabSwapsWhoIsHereAndWhoIsAnAvatarWithoutCountingAJump()
        {
            Publish("1", new Vector3(0f, 1.6f, 0f), avatars: new[] { Avatar("2", new Vector3(2f, 1.6f, 0f)) });
            Publish("2", new Vector3(-3f, 1.6f, 5f), avatars: new[] { Avatar("1", new Vector3(-4f, 1.6f, 5f)) });
            _session.Rooms.SelectRoom("1");
            _view.Step(0.016f);
            Assert.That(_view.TryGetProxy("1", out var kind1, out _), Is.True);
            Assert.That(kind1, Is.EqualTo(MinimapMarkerKind.Person));
            Assert.That(_view.TryGetProxy("2", out var kind2, out var head2), Is.True);
            Assert.That(kind2, Is.EqualTo(MinimapMarkerKind.Avatar));
            Assert.That(head2.x, Is.EqualTo(2f).Within(1e-4f));

            _session.Rooms.SelectRoom("2");
            _view.Step(0.016f);
            Assert.That(_view.TryGetProxy("2", out kind2, out head2), Is.True);
            Assert.That(kind2, Is.EqualTo(MinimapMarkerKind.Person), "in P2's room P2 is the person");
            Assert.That(head2.x, Is.EqualTo(-3f).Within(1e-4f));
            Assert.That(_view.TryGetProxy("1", out kind1, out var head1), Is.True);
            Assert.That(kind1, Is.EqualTo(MinimapMarkerKind.Avatar));
            Assert.That(head1.z, Is.EqualTo(5f).Within(1e-4f), "placed where P2's room has P1's avatar");
            Assert.That(_view.JumpCount, Is.EqualTo(0), "another room is another frame, not a jump");
        }

        [Test]
        public void TheRoomIsRebuiltOnlyWhenItsFurnitureContentChanges()
        {
            Publish("1", new Vector3(0f, 1.6f, 0f), Room4x3());
            _view.Step(0.016f);
            Assert.That(_view.GeometryBuilds, Is.EqualTo(1));
            Assert.That(_view.GeometryCount, Is.EqualTo(3), "floor, wall, table");
            Publish("1", new Vector3(0.2f, 1.6f, 0f), Room4x3());   // the 2 s resend: a new list, the same content
            _view.Step(0.016f);
            Assert.That(_view.GeometryBuilds, Is.EqualTo(1), "an identical resend does not rebuild");
            var moved = Room4x3();
            moved[2] = new ObserverFurniture("t1", "TABLE", new Vector3(1.3f, 0.4f, 0f), Quaternion.identity, new Vector3(1.2f, 0.8f, 0.6f));
            Publish("1", new Vector3(0.2f, 1.6f, 0f), moved);
            _view.Step(0.016f);
            Assert.That(_view.GeometryBuilds, Is.EqualTo(2), "a moved table rebuilds");
        }

        [Test]
        public void ClearingReleasesTheMaterialsItMade()
        {
            Publish("1", new Vector3(0f, 1.6f, 0f), Room4x3(), avatars: new[] { Avatar("2", new Vector3(1f, 1.6f, 1f)), Avatar("3", new Vector3(2f, 1.6f, 1f)) });
            _view.Step(0.016f);
            int live = _view.LiveMaterialCount;
            Assert.That(live, Is.GreaterThan(0));
            Assert.That(live, Is.LessThanOrEqualTo(4 + SpectatorMinimapLayout.ParticipantPalette.Length), "one per surface kind and one per colour, not one per proxy");
            for (int i = 0; i < 5; ++i)
            {
                Publish("1", new Vector3(0f, 1.6f, 0f), Room4x3(), avatars: i % 2 == 0 ? new[] { Avatar("2", new Vector3(1f, 1.6f, 1f)) } : new[] { Avatar("3", new Vector3(2f, 1.6f, 1f)) });
                _view.Step(0.016f);
            }
            Assert.That(_view.LiveMaterialCount, Is.EqualTo(live), "proxies coming and going make no new materials");
            typeof(FreeformSpectatorSession).GetProperty("Phase").GetSetMethod(true).Invoke(_session, new object[] { SpectatorPhase.Idle });
            _view.Step(0.016f);
            Assert.That(_view.LiveMaterialCount, Is.EqualTo(0), "leaving the room releases them");
            Assert.That(_view.ProxyCount, Is.EqualTo(0));
            Assert.That(_view.GeometryCount, Is.EqualTo(0));
        }
    }
}
#endif
