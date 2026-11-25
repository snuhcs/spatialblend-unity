// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend;
using SpatialBlend.Samples;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Desktop observer mode: who is an observer, which participant room is on screen, and which camera mode is active.
    /// No XR device and no runner. The freeform app's share and start gates are included because they are the seam
    /// the decision plugs into.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class DesktopObserverTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _objects.Count; ++i)
            {
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [Test]
        public void ADesktopWithoutAHeadsetIsAnObserverAndAHeadsetOrAQuestIsNot()
        {
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.WindowsPlayer, false, false, false), Is.True);
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.OSXPlayer, false, false, false), Is.True);
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.LinuxPlayer, false, false, false), Is.True);
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.OSXEditor, false, false, false), Is.True);
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.WindowsPlayer, true, false, false), Is.False, "an HMD stays a participant");
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.Android, false, false, false), Is.False, "Quest is a participant");
        }

        [Test]
        public void ForceParticipantWinsAndForceObserverDoesNotOverrideAHeadset()
        {
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.WindowsPlayer, false, true, true), Is.False);
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.Android, false, false, true), Is.False, "Quest is never an observer");
            Assert.That(DesktopObserverDecision.IsObserver(RuntimePlatform.Android, true, false, true), Is.False);

            Assert.That(DesktopObserverDecision.FromRuntime(RuntimePlatform.WindowsPlayer, false,
                name => name == DesktopObserverDecision.ForceParticipantVariable ? "1" : null, Array.Empty<string>()), Is.False);
            Assert.That(DesktopObserverDecision.FromRuntime(RuntimePlatform.Android, false,
                _ => null, new[] { DesktopObserverDecision.ForceObserverArgument }), Is.False, "Quest ignores -observer");
            Assert.That(DesktopObserverDecision.FromRuntime(RuntimePlatform.OSXPlayer, false,
                name => string.Equals(name, DesktopObserverDecision.ForceObserverVariable, StringComparison.Ordinal) ? "true" : null,
                new[] { DesktopObserverDecision.ForceParticipantArgument }), Is.False);
        }

        [Test]
        public void AThrowingHeadsetProbeDoesNotCountAsAHeadset()
        {
            Assert.That(DesktopObserverXr.IsActive(() => throw new InvalidOperationException("no ovr"), () => false), Is.False);
            Assert.That(DesktopObserverXr.IsActive(() => true, () => throw new InvalidOperationException("no xr")), Is.True);
            Assert.That(DesktopObserverXr.IsActive(() => false, () => true), Is.True);
        }

        [Test]
        public void AnObserverRejectsAvatarsAndAnchorsAndAcceptsOtherContent()
        {
            Assert.That(ObserverSharePolicy.Classify(true, true), Is.EqualTo(ObserverIncomingKind.Avatar));
            Assert.That(ObserverSharePolicy.Response(ObserverIncomingKind.Avatar).result, Is.EqualTo(OnShareResponse.Result.Reject));
            Assert.That(ObserverSharePolicy.Response(ObserverIncomingKind.Anchor).result, Is.EqualTo(OnShareResponse.Result.Reject));
            Assert.That(ObserverSharePolicy.Response(ObserverIncomingKind.Content).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
        }

        [Test]
        public void TheFreeformAppObservesByRejectingAnAvatarAndNotRegisteringOnStart()
        {
            int stored = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, -1);
            EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
            var host = new GameObject("observer-app");
            _objects.Add(host);
            var avatar = new GameObject("remote-avatar");
            _objects.Add(avatar);
            avatar.tag = "Avatar";
            var cube = new GameObject("placed");
            _objects.Add(cube);
            try
            {
                var app = host.AddComponent<FreeformCollaborationApp>();
                Assert.That(app.IsDesktopObserver, Is.False, "the role is decided in Start, not when the component is added");
                app.ApplyRole(true);
                Assert.That(app.IsDesktopObserver, Is.True);
                Assert.That(app.ObserverRoleResolved, Is.True);
                Assert.That(app.OnShare(avatar).result, Is.EqualTo(OnShareResponse.Result.Reject));
                Assert.That(app.OnShare(cube).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
                app.OnAppStart();
                Assert.That(app.IsAppStarted, Is.True);

                var participant = new GameObject("participant-app");
                _objects.Add(participant);
                var participantApp = participant.AddComponent<FreeformCollaborationApp>();
                participantApp.ApplyRole(false);
                Assert.That(participantApp.OnShare(avatar).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            }
            finally
            {
                if (stored < 0) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
                else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, stored);
            }
        }

        [Test]
        public void ChoosingARoomShowsThatRoomsRealizationOfAHead()
        {
            var state = new ObserverSessionState();
            Quaternion facing = Quaternion.Euler(0f, 90f, 0f);
            ParticipantRoomSnapshot roomA = Room("1", "Ada", 11, null, Quaternion.identity,
                Avatar("2", "Bea", new Vector3(1f, 1.6f, 2f), facing));
            ParticipantRoomSnapshot roomB = Room("2", "Bea", 22, new Vector3(4f, 1.7f, 5f), Quaternion.identity,
                Avatar("2", "Bea", new Vector3(8f, 1.6f, 9f), Quaternion.identity));

            Assert.That(state.UpsertRoom(roomA, 0d), Is.True);
            Assert.That(state.Rooms.SelectedParticipantId, Is.EqualTo("1"), "the first room is selected");
            Assert.That(state.FollowParticipantId, Is.EqualTo("1"));
            Assert.That(state.SetFollow("2"), Is.True);
            Assert.That(state.SetMode(ObserverCameraMode.FollowHead), Is.True);
            Assert.That(state.TryCameraPose(out Vector3 inAdaRoom, out Quaternion inAdaFacing), Is.True);
            Assert.That(inAdaRoom, Is.EqualTo(new Vector3(1f, 1.6f, 2f)));
            Assert.That(Quaternion.Angle(inAdaFacing, facing), Is.LessThan(0.01f));

            Assert.That(state.UpsertRoom(roomB, 0.5d), Is.True, "Bea's room arrives");
            Assert.That(state.Rooms.SelectedParticipantId, Is.EqualTo("1"), "a second room does not change the selection");
            Assert.That(state.SelectRoom("2"), Is.True);
            Assert.That(state.FollowParticipantId, Is.EqualTo("2"), "Bea is in Bea's room, so the follow target stays");
            Assert.That(state.TryCameraPose(out Vector3 inBeaRoom, out _), Is.True);
            Assert.That(inBeaRoom, Is.EqualTo(new Vector3(8f, 1.6f, 9f)), "the head is the one realized in the selected room");
            Assert.That(state.SelectRoom("missing"), Is.False);
            Assert.That(state.Rooms.SelectedParticipantId, Is.EqualTo("2"));

            state.UpsertRoom(Room("9", "Zoe", 90, new Vector3(0f, 1.5f, 0f), Quaternion.identity), 1d);
            Assert.That(state.SelectRoom("9"), Is.True);
            Assert.That(state.FollowParticipantId, Is.EqualTo("9"), "a follow target that is not in the new room becomes that room's owner");
        }

        [Test]
        public void FollowFallsBackToTheRoomOwnersHeadsetWhenTheirAvatarIsNotSampledYet()
        {
            var state = new ObserverSessionState();
            state.UpsertRoom(Room("4", "Ada", 40, new Vector3(0.2f, 1.5f, 0.4f), Quaternion.identity), 0d);
            state.SetMode(ObserverCameraMode.FollowHead);
            Assert.That(state.TryCameraPose(out Vector3 position, out _), Is.True);
            Assert.That(position, Is.EqualTo(new Vector3(0.2f, 1.5f, 0.4f)));
        }

        [Test]
        public void SwitchingToFreeFlyKeepsTheHeadPoseAndIgnoresFlyInputWhileFollowing()
        {
            var state = new ObserverSessionState();
            Vector3 head = new Vector3(3f, 1.6f, 1f);
            state.UpsertRoom(Room("1", "Ada", 1, head, Quaternion.identity), 0d);
            state.SetMode(ObserverCameraMode.FollowHead);
            state.StepFly(new ObserverFly.Input { Forward = true, DeltaTime = 1f });
            Assert.That(state.TryCameraPose(out Vector3 followed, out _), Is.True);
            Assert.That(followed, Is.EqualTo(head));

            Assert.That(state.SetMode(ObserverCameraMode.FreeFly), Is.True);
            Assert.That(state.TryCameraPose(out Vector3 flown, out _), Is.True);
            Assert.That(flown, Is.EqualTo(head));
            Assert.That(state.SetMode(ObserverCameraMode.FreeFly), Is.False);
        }

        [Test]
        public void FreeFlyUsesWasdMouseLookVerticalKeysAndASpeedModifier()
        {
            ObserverFly.State forward = ObserverFly.Step(default, new ObserverFly.Input { Forward = true, DeltaTime = 1f });
            Assert.That(forward.Position.z, Is.EqualTo(ObserverFly.BaseSpeed).Within(1e-4f));
            Assert.That(forward.Position.x, Is.EqualTo(0f).Within(1e-4f));

            ObserverFly.State fast = ObserverFly.Step(default, new ObserverFly.Input { Forward = true, Fast = true, DeltaTime = 1f });
            Assert.That(fast.Position.z, Is.EqualTo(ObserverFly.BaseSpeed * ObserverFly.FastMultiplier).Within(1e-4f));

            ObserverFly.State up = ObserverFly.Step(default, new ObserverFly.Input { Up = true, DeltaTime = 1f });
            Assert.That(up.Position.y, Is.EqualTo(ObserverFly.BaseSpeed).Within(1e-4f));
            ObserverFly.State down = ObserverFly.Step(default, new ObserverFly.Input { Down = true, DeltaTime = 1f });
            Assert.That(down.Position.y, Is.EqualTo(-ObserverFly.BaseSpeed).Within(1e-4f));

            var pitched = new ObserverFly.State { Pitch = 80f };
            ObserverFly.State vertical = ObserverFly.Step(pitched, new ObserverFly.Input { Up = true, DeltaTime = 1f });
            Assert.That(vertical.Position.y, Is.EqualTo(ObserverFly.BaseSpeed).Within(1e-4f));
            Assert.That(vertical.Position.z, Is.EqualTo(0f).Within(1e-4f));

            var lookingEast = new ObserverFly.State { Yaw = 90f };
            ObserverFly.State east = ObserverFly.Step(lookingEast, new ObserverFly.Input { Forward = true, DeltaTime = 1f });
            Assert.That(east.Position.x, Is.EqualTo(ObserverFly.BaseSpeed).Within(1e-4f));

            ObserverFly.State looked = ObserverFly.Step(default, new ObserverFly.Input { MouseX = 10f, MouseY = 10f, DeltaTime = 0f });
            Assert.That(looked.Yaw, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(looked.Pitch, Is.EqualTo(-20f).Within(1e-4f));
            Assert.That(looked.Position, Is.EqualTo(Vector3.zero));

            ObserverFly.State clamped = ObserverFly.Step(default, new ObserverFly.Input { MouseY = -100f });
            Assert.That(clamped.Pitch, Is.EqualTo(ObserverFly.PitchLimit).Within(1e-4f));
        }

        [Test]
        public void ARoomThatStopsPublishingIsDroppedAndTheNextRoomIsSelected()
        {
            var state = new ObserverSessionState();
            state.UpsertRoom(Room("2", "Bea", 2, Vector3.up, Quaternion.identity), 0d);
            state.UpsertRoom(Room("1", "Ada", 1, Vector3.one, Quaternion.identity), 0d);
            Assert.That(state.Rooms.Rooms[0].ParticipantId, Is.EqualTo("1"), "rooms are ordered by display name");
            state.SelectRoom("2");
            Assert.That(state.Expire(3.1d, 3d), Is.EqualTo(2));
            Assert.That(state.Rooms.Selected, Is.Null);
            Assert.That(state.FollowParticipantId, Is.Null);

            state.UpsertRoom(Room("1", "Ada", 1, Vector3.one, Quaternion.identity), 10d);
            state.UpsertRoom(Room("2", "Bea", 2, Vector3.up, Quaternion.identity), 10d);
            state.SelectRoom("2");
            Assert.That(state.Expire(12d, 3d), Is.EqualTo(0));
            Assert.That(state.Rooms.SelectedParticipantId, Is.EqualTo("2"));
        }

        [Test]
        public void SceneKeysRoundTripAndASnapshotRoundTripsThroughTheCodec()
        {
            Assert.That(ObserverSceneMap.NetworkKey(42u), Is.EqualTo("net:42"));
            Assert.That(ObserverSceneMap.TryParseNetworkKey("net:42", out uint raw), Is.True);
            Assert.That(raw, Is.EqualTo(42u));
            Assert.That(ObserverSceneMap.TryParseNetworkKey("native:lamp", out _), Is.False);
            Assert.That(ObserverSceneMap.ParticipantId(7), Is.EqualTo("7"));

            ParticipantRoomSnapshot snapshot = ParticipantRoomSnapshot.Create("3", "Cara", 30, true,
                new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 20f, 0f),
                new[]
                {
                    new ObserverFurniture("table-1", "TABLE", new Vector3(2f, 0.4f, 1f), Quaternion.identity, new Vector3(1.2f, 0.5f, 0.8f)),
                    new ObserverFurniture("", "dropped", Vector3.zero, Quaternion.identity, Vector3.one),
                },
                new[] { new ObserverPlacedObject(ObserverSceneMap.NetworkKey(9u), new Vector3(0.5f, 1f, 0.5f), Quaternion.identity) },
                new[]
                {
                    new ObserverAvatarPose("3", "Cara", Vector3.zero, Quaternion.identity, true, new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 20f, 0f)),
                    new ObserverAvatarPose("4", "Dan", new Vector3(2f, 0f, 2f), Quaternion.identity, false, Vector3.zero, Quaternion.identity),
                });
            byte[] bytes = ObserverRoomCodec.Write(snapshot);
            Assert.That(ObserverRoomCodec.TryRead(bytes, out ParticipantRoomSnapshot read), Is.True);
            Assert.That(read.ParticipantId, Is.EqualTo("3"));
            Assert.That(read.SpaceId, Is.EqualTo(30L));
            Assert.That(read.Furniture.Count, Is.EqualTo(1));
            Assert.That(read.Furniture[0].Label, Is.EqualTo("TABLE"));
            Assert.That(read.Furniture[0].Size, Is.EqualTo(new Vector3(1.2f, 0.5f, 0.8f)));
            Assert.That(read.Objects.Count, Is.EqualTo(1));
            Assert.That(read.Objects[0].Key, Is.EqualTo("net:9"));
            Assert.That(read.Avatars.Count, Is.EqualTo(2));
            Assert.That(read.TryViewpoint("4", out _, out _), Is.False, "Dan has no head in this room");
            Assert.That(read.TryViewpoint("3", out Vector3 head, out _), Is.True);
            Assert.That(head, Is.EqualTo(new Vector3(1f, 2f, 3f)));

            var extra = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, extra, 0, bytes.Length);
            extra[bytes.Length] = 1;
            Assert.That(ObserverRoomCodec.TryRead(extra, out _), Is.False);
            Assert.That(ObserverRoomCodec.TryRead(bytes, 0, bytes.Length - 1, out _), Is.False);
            Assert.That(ObserverRoomCodec.TryRead(new byte[] { 9 }, out _), Is.False);

            Assert.That(ObserverRoomCodec.TryReadCount(new BinaryReader(new MemoryStream(new byte[] { 0xFF, 0xFF })),
                new MemoryStream(new byte[8]), ParticipantRoomSnapshot.MaxFurniture, ObserverRoomCodec.MinFurnitureBytes, out _), Is.False);
            var motion = ParticipantRoomSnapshot.Create("3", "Cara", 30, false, Vector3.zero, Quaternion.identity,
                null, Array.Empty<ObserverPlacedObject>(), Array.Empty<ObserverAvatarPose>(), false);
            var catalog = new ObserverRoomCatalog();
            catalog.Upsert(snapshot, 0d);
            catalog.Upsert(motion, 1d);
            Assert.That(catalog.Selected.Furniture.Count, Is.EqualTo(1), "a motion update keeps the furniture already shown");
            Assert.That(ObserverSceneMap.FurnitureCenter(new Vector3(1f, 0f, 2f), Quaternion.identity, new Vector3(0f, 0.4f, 0f)),
                Is.EqualTo(new Vector3(1f, 0.4f, 2f)));
            Assert.That(Vector3.Distance(ObserverSceneMap.FurnitureCenter(Vector3.zero, Quaternion.Euler(0f, 180f, 0f), Vector3.right),
                new Vector3(-1f, 0f, 0f)), Is.LessThan(1e-5f), "a half turn, up to float rounding");

            var audience = new ObserverAudience();
            audience.Note(4, 0d);
            Assert.That(audience.Contains(4), Is.True);
            Assert.That(audience.Expire(3.1d, ObserverAudience.TimeoutSeconds), Is.EqualTo(1));
            Assert.That(audience.Count, Is.EqualTo(0));
            float allowance = ObserverTraffic.Refill(0f, 1f);
            Assert.That(allowance, Is.EqualTo(ObserverTraffic.BytesPerSecond).Within(0.1f));
            Assert.That(ObserverTraffic.TrySpend(ref allowance, ObserverTraffic.BytesPerSecond + 1), Is.False);
            Assert.That(allowance, Is.EqualTo(ObserverTraffic.BytesPerSecond).Within(0.1f));
            Assert.That(ObserverTraffic.TrySpend(ref allowance, 1000), Is.True);

            var bulkyObjects = new ObserverPlacedObject[ParticipantRoomSnapshot.MaxObjects];
            for (int i = 0; i < bulkyObjects.Length; ++i)
                bulkyObjects[i] = new ObserverPlacedObject(i.ToString("D4") + new string('k', 76), Vector3.one, Quaternion.identity);
            var bulkyAvatars = new ObserverAvatarPose[ParticipantRoomSnapshot.MaxAvatars];
            for (int i = 0; i < bulkyAvatars.Length; ++i)
                bulkyAvatars[i] = new ObserverAvatarPose(i.ToString("D2") + new string('a', 78), new string('n', 80),
                    Vector3.zero, Quaternion.identity, true, Vector3.up, Quaternion.identity);
            var bulky = ParticipantRoomSnapshot.Create("9", "Room", 9, false, Vector3.zero, Quaternion.identity,
                new[] { new ObserverFurniture("table", "TABLE", Vector3.zero, Quaternion.identity, Vector3.one) },
                bulkyObjects, bulkyAvatars);
            byte[] oversized = ObserverRoomCodec.Write(bulky);
            Assert.That(oversized.Length, Is.GreaterThanOrEqualTo(10000), "two observers times this payload used to exceed the bucket forever");
            byte[] fitted = ObserverRoomCodec.WriteWithin(bulky, ObserverTraffic.MaxSnapshotBytes);
            Assert.That(fitted.Length, Is.LessThanOrEqualTo(ObserverTraffic.MaxSnapshotBytes));
            Assert.That(ObserverRoomCodec.TryRead(fitted, out ParticipantRoomSnapshot reduced), Is.True);
            Assert.That(reduced.IncludesFurniture, Is.False, "a snapshot that does not fit falls back to motion without furniture");
            float bucket = ObserverTraffic.BudgetCapBytes;
            Assert.That(oversized.Length * 2, Is.GreaterThan(ObserverTraffic.BudgetCapBytes));
            Assert.That(ObserverTraffic.SendPerRecipient(ref bucket, oversized.Length, 2), Is.EqualTo(0), "an oversized payload is not held");
            bucket = 0f;
            bucket = ObserverTraffic.Refill(bucket, 1f);
            int sent = ObserverTraffic.SendPerRecipient(ref bucket, fitted.Length, 2);
            if (sent < 2)
            {
                bucket = ObserverTraffic.Refill(bucket, 1f);
                sent += ObserverTraffic.SendPerRecipient(ref bucket, fitted.Length, 2 - sent);
            }
            Assert.That(sent, Is.EqualTo(2), "each of two observers is charged for one capped snapshot, and the fresh one is what is sent");

            int payload = 1000;
            float rotating = 2000f;
            int cursor = 0;
            int first = ObserverTraffic.SendPerRecipient(ref rotating, payload, 3, ref cursor, out int start);
            Assert.That(first, Is.EqualTo(2));
            Assert.That(start, Is.EqualTo(0));
            Assert.That(cursor, Is.EqualTo(1), "the next send starts at the next observer");
            rotating = 2000f;
            int second = ObserverTraffic.SendPerRecipient(ref rotating, payload, 3, ref cursor, out start);
            Assert.That(second, Is.EqualTo(2));
            Assert.That(start, Is.EqualTo(1));
            Assert.That((start + 1) % 3, Is.EqualTo(2), "the third observer is paid on the later send");
            var served = new bool[3];
            for (int n = 0; n < second; ++n) served[(start + n) % 3] = true;
            Assert.That(served[2], Is.True);
        }

        // ---------------------------------------------------------------- observer x room list

        // An observer has no MRUK room and shares nothing: it does not wait for scene geometry it will never register (a
        // participant does), it never hosts or creates a room, and it joins only a listed room.
        [Test]
        public void AnObserverJoinsOnlyAListedRoomAndNeverHosts()
        {
            int stored = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, -1);
            EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, (int)FreeformCollaborationRuntimeProfile.Fusion);
            try
            {
                var participant = NewApp("participant-app");
                participant.ApplyRole(false);
                Assert.That(WaitsForSceneGeometry(participant), Is.True);
                UnityEngine.Object.DestroyImmediate(participant.gameObject);

                var app = NewApp("observer-app");
                app.ApplyRole(true);
                Assert.That(WaitsForSceneGeometry(app), Is.False, "the observer never registers scene geometry, so it must not wait for it");

                ObserverTestBrowser browser = NewBrowser("observer-browser");
                browser.PresentLobby(new[] { new FusionSessionEntry("Room 1", 1, 8) });
                browser.HostRoom("Room 2");
                Assert.That(browser.Calls.Any(c => c.StartsWith("start:", StringComparison.Ordinal)), Is.False, "an observer never hosts");
                Assert.That(browser.LastError, Is.EqualTo(FreeformMatchmaking.ObserverHostError));
                browser.PresentLobby(new[] { new FusionSessionEntry("Room 1", 1, 8) });
                browser.CreateRoom();
                Assert.That(browser.Calls.Any(c => c.StartsWith("start:", StringComparison.Ordinal)), Is.False, "nor creates a room");

                browser.PresentLobby(new[] { new FusionSessionEntry("Room 1", 1, 8) });
                Task<bool> join = browser.JoinAsync("Room 1");
                Assert.That(join.IsCompleted, Is.True);
                Assert.That(join.Result, Is.True, "a listed room is joined");
                Assert.That(browser.Calls, Does.Contain("start:Room 1"));

                ObserverTestBrowser unlisted = NewBrowser("observer-browser-no-list");
                Task<bool> blind = unlisted.JoinAsync("Room 9");
                Assert.That(blind.IsCompleted && !blind.Result, Is.True, "without a list the name cannot be checked: a Shared start would create it");
                Assert.That(unlisted.Calls.Any(c => c.StartsWith("start:", StringComparison.Ordinal)), Is.False);
            }
            finally
            {
                if (stored < 0) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
                else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, stored);
            }
        }

        // The bootstrap itself (the path Connect takes when there is no room list) starts an observer only into a listed,
        // joinable room. A participant is not affected.
        [Test]
        public void AnObserverBootstrapStartsOnlyAListedRoom()
        {
            int stored = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, -1);
            EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, (int)FreeformCollaborationRuntimeProfile.Fusion);
            try
            {
                var app = NewApp("observer-app");
                app.ApplyRole(true);

                var bootObject = new GameObject("observer-boot");
                bootObject.SetActive(false);
                _objects.Add(bootObject);
                CountingBootstrap boot = bootObject.AddComponent<CountingBootstrap>();
                boot.DefaultRoomName = "Room 1";
                boot.StartSharedClient();
                Assert.That(boot.Starts, Is.EqualTo(0), "no list: the room may not exist, and starting it would create it");
                var refusals = new List<Fusion.ShutdownReason>();
                boot.StartFailed += refusals.Add;

                ObserverTestBrowser browser = bootObject.AddComponent<ObserverTestBrowser>();
                browser.PresentLobby(new[] { new FusionSessionEntry("Room 1", 1, 8), new FusionSessionEntry("Full", 8, 8) });
                boot.StartSharedClient();
                Assert.That(boot.Starts, Is.EqualTo(1), "a listed room is joined");
                boot.DefaultRoomName = "Room 7";
                boot.StartSharedClient();
                boot.DefaultRoomName = "Full";
                boot.StartSharedClient();
                Assert.That(boot.Starts, Is.EqualTo(1), "an unlisted or full room is not started");
                Assert.That(refusals, Is.EqualTo(new[] { Fusion.ShutdownReason.GameNotFound, Fusion.ShutdownReason.GameNotFound }),
                    "each refused start is reported as a failed start, so a Starting browser returns to the lobby");

                app.ApplyRole(false);
                boot.DefaultRoomName = "Room 7";
                boot.StartSharedClient();
                Assert.That(boot.Starts, Is.EqualTo(2), "a participant may start a new room");
            }
            finally
            {
                if (stored < 0) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
                else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, stored);
            }
        }

        /// <summary>A bootstrap that counts the Fusion starts its gates let through, without opening a socket.</summary>
        public sealed class CountingBootstrap : SbFusionBootstrap
        {
            public int Starts;
            protected override void StartSharedClientCore() => Starts++;
        }

        private FreeformCollaborationApp NewApp(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.AddComponent<FreeformCollaborationApp>();
        }

        private ObserverTestBrowser NewBrowser(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.AddComponent<ObserverTestBrowser>();
        }

        private static bool WaitsForSceneGeometry(FreeformCollaborationApp app) =>
            (bool)typeof(FreeformCollaborationApp).GetProperty("WaitsForLocalSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(app);

        /// <summary>A browser that records starts instead of running Fusion, with a lobby that answers at once.</summary>
        public sealed class ObserverTestBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new List<string>();
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override Task<string> JoinLobbyAsync() => Task.FromResult<string>(null);
            protected override Task CloseLobbyAsync() => Task.CompletedTask;

            /// <summary>Presents a lobby list without joining Photon (the lobby answers at once).</summary>
            public void PresentLobby(IEnumerable<FusionSessionEntry> sessions)
            {
                _ = OpenLobbyAsync();
                ApplySessions(sessions);
            }
        }

        private static ParticipantRoomSnapshot Room(string id, string name, long space, Vector3? head, Quaternion rotation, params ObserverAvatarPose[] avatars)
        {
            bool hasHead = head.HasValue;
            return ParticipantRoomSnapshot.Create(id, name, space, hasHead, head ?? Vector3.zero, rotation,
                Array.Empty<ObserverFurniture>(), Array.Empty<ObserverPlacedObject>(), avatars);
        }

        private static ObserverAvatarPose Avatar(string id, string name, Vector3 head, Quaternion rotation) =>
            new ObserverAvatarPose(id, name, head, rotation, true, head, rotation);
    }
}
#endif
