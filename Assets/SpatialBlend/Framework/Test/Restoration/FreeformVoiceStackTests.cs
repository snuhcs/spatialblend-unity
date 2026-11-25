// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Fusion;
using NUnit.Framework;
using Oculus.Avatar2;
using SpatialBlend.Avatar;
using SpatialBlend.Editor;
using SpatialBlend.Samples;
using Photon.Voice;
using Photon.Voice.Fusion;
using Photon.Voice.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Assert = NUnit.Framework.Assert;
using ILogger = Photon.Voice.ILogger;
using LogLevel = Photon.Voice.LogLevel;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The voice stack of the Freeform scene, end to end except the Internet. On two Quest 3 headsets each user heard only their
    /// own voice; remote voice was not transmitted.
    ///
    /// Root cause: the APK's PhotonAppSettings had a Fusion App ID and an EMPTY Voice App ID, so FusionVoiceClient never connected
    /// (ConnectUsingSettings logs "Provide an AppId or a Server address" and returns false). The microphone still opened (permission
    /// granted, Recorder recording) and nothing was sent or received. The tests below pin every other link of the chain so that a
    /// configured Voice App ID is the only thing missing, and make a build without one impossible to ship unnoticed.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformVoiceStackTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private const string VoicePrefabPath = "Assets/SpatialBlend/Avatar/Prefabs/VoicePrefab.prefab";
        private const string ManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";

        // ---- capture --------------------------------------------------------------------------------------------------------------

        [Test]
        public void VoicePrefab_RecordsTheMicrophoneAndTransmitsWithoutGatesOrEcho()
        {
            var root = PrefabUtility.LoadPrefabContents(VoicePrefabPath);
            try
            {
                var recorder = root.GetComponent<Recorder>();
                Assert.That(recorder, Is.Not.Null, "the voice prefab must carry the Recorder that captures the local microphone");
                var so = new SerializedObject(recorder);
                Assert.That(Bool(so, "transmitEnabled"), Is.True, "TransmitEnabled false transmits nothing");
                Assert.That(Bool(so, "recordingEnabled"), Is.True, "RecordingEnabled false never opens the microphone");
                Assert.That(Bool(so, "voiceDetection"), Is.False, "voice detection would gate transmission on a threshold the Quest mic may never reach");
                Assert.That(Bool(so, "debugEchoMode"), Is.False, "echo mode sends the user's own voice back to them");
                Assert.That(Bool(so, "useTargetPlayers"), Is.False, "target players would restrict who receives the voice");
                Assert.That(so.FindProperty("interestGroup").intValue, Is.EqualTo(0), "interest group 0 reaches everyone in the room");
                Assert.That(so.FindProperty("sourceType").intValue, Is.EqualTo((int)Recorder.InputSourceType.Microphone));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void VoicePrefab_PlaysRemoteVoicePositionallyOnAnAudibleSpeaker()
        {
            var root = PrefabUtility.LoadPrefabContents(VoicePrefabPath);
            try
            {
                Assert.That(root.GetComponent<NetworkObject>(), Is.Not.Null, "Fusion must replicate the voice object so each client has a copy per player");
                Assert.That(root.GetComponent<VoiceNetworkObject>(), Is.Not.Null, "VoiceNetworkObject binds the Recorder and Speaker to the Fusion object id");
                Assert.That(root.GetComponent<VoiceObjectController>(), Is.Not.Null);
                var speaker = root.GetComponent<LipSyncSpeaker>();
                Assert.That(speaker, Is.Not.Null);
                var source = root.GetComponent<AudioSource>();
                Assert.That(source.mute, Is.False);
                Assert.That(source.volume, Is.GreaterThan(0.5f));
                Assert.That(source.spatialBlend, Is.EqualTo(1f).Within(1e-4f), "remote voice is positional on the speaker's head");
                Assert.That(source.maxDistance, Is.GreaterThanOrEqualTo(10f), "a room-scale scene must not attenuate a voice to silence");
                Assert.That(source.outputAudioMixerGroup, Is.Null, "no mixer group may silence the remote speaker");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void RecordAudioIsInTheManifestAndRequestedAtRuntime()
        {
            var manifest = XDocument.Load(ManifestPath);
            XNamespace android = "http://schemas.android.com/apk/res/android";
            var permissions = manifest.Descendants("uses-permission").Select(e => (string)e.Attribute(android + "name")).ToList();
            Assert.That(permissions, Does.Contain("android.permission.RECORD_AUDIO"));
            Assert.That(SbPermissionManager.RequestedPermissions, Does.Contain("android.permission.RECORD_AUDIO"));
        }

        // ---- the Fusion session and the voice room ----------------------------------------------------------------------------------

        [Test]
        public void TheRunnerPrefabCarriesTheVoiceClientAndUsesTheFusionSession()
        {
            using (var scene = OpenScene())
            {
                var bootstraps = scene.Components<FusionBootstrap>();
                Assert.That(bootstraps, Has.Length.EqualTo(1));
                NetworkRunner runnerPrefab = bootstraps[0].RunnerPrefab;
                Assert.That(runnerPrefab, Is.Not.Null);
                // VoiceNetworkObject.Spawned reads Runner.GetComponent<VoiceConnection>(): the voice client must be on the runner object.
                var voiceClient = runnerPrefab.GetComponent<FusionVoiceClient>();
                Assert.That(voiceClient, Is.Not.Null, "FusionVoiceClient must sit on the NetworkRunner object, or VoiceNetworkObject.Spawned throws");
                Assert.That(voiceClient.UseFusionAppSettings, Is.True, "the Voice App ID comes from PhotonAppSettings.AppIdVoice");
                Assert.That(voiceClient.UseFusionAuthValues, Is.True);
                Assert.That(voiceClient.AutoConnectAndJoin, Is.True, "voice joins the room named after the Fusion session");
                Assert.That(voiceClient.Settings.AppIdVoice, Is.Empty.Or.Null,
                            "the scene must not carry an App ID; it is a per-user value in the git-ignored PhotonAppSettings");
            }
        }

        [Test]
        public void TheSpawnerSpawnsOneVoiceObjectPerPlayerFromTheVoicePrefab()
        {
            using (var scene = OpenScene())
            {
                var spawner = scene.Components<SbAvatarSpawnerFusion>().Single();
                var voice = new SerializedObject(spawner).FindProperty("voice").objectReferenceValue as GameObject;
                Assert.That(voice, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(voice), Is.EqualTo(VoicePrefabPath));
                Assert.That(scene.Components<RemoteAvatarVoiceManager>(), Has.Length.EqualTo(1));
            }
        }

        // ---- the missing credential ------------------------------------------------------------------------------------------------

        [Test]
        public void VoiceIsConfiguredOnlyByAVoiceAppIdOrAServer()
        {
            Assert.That(VoiceConfiguration.IsVoiceConfigured("", ""), Is.False);
            Assert.That(VoiceConfiguration.IsVoiceConfigured(null, null), Is.False);
            Assert.That(VoiceConfiguration.IsVoiceConfigured("   ", "\t"), Is.False);
            Assert.That(VoiceConfiguration.IsVoiceConfigured("00000000-0000-0000-0000-000000000000", ""), Is.True);
            Assert.That(VoiceConfiguration.IsVoiceConfigured("", "photon.example.invalid"), Is.True);
        }

        [Test]
        public void ABuildWithoutAVoiceAppIdIsRefusedForTheQuestAndWindowsButNotForAHeadlessOptOut()
        {
            // The general guard (RequiredCredentialsTests has every combination); this is the voice case.
            const string id = "00000000-0000-0000-0000-000000000000";
            Func<string, string> noEnvironment = _ => null;
            Func<string, string> optOut = name => name == RequiredCredentials.Voice.AllowVariable ? "1" : null;
            CredentialFacts Facts(string voice, string server)
            {
                var facts = CredentialFacts.FromPhoton(id, voice, server);
                facts.TurnClientSecret = CredentialState.OfText("fake-secret");
                return facts;
            }
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Facts("", ""), noEnvironment), Does.Contain("AppIdVoice").Or.Contain("photon-voice-app-id"));
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneWindows64, Facts(null, null), noEnvironment), Is.Not.Null);
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Facts(id, ""), noEnvironment), Is.Null);
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Facts("", "photon.example.invalid"), noEnvironment), Is.Null);
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Facts("", ""), optOut), Is.Null, "an explicit opt-out for a build with no voice");
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, Facts("", ""), noEnvironment), Is.Null, "the headless spectator has no microphone");
            Assert.That(RequiredCredentials.StampMembers(Facts(id, "")), Does.Contain("\"voiceAppIdConfigured\":true"));
            Assert.That(RequiredCredentials.StampMembers(Facts("", "")), Does.Contain("\"voiceAppIdConfigured\":false"));
        }

        // ---- the batchmode microphone guard ----------------------------------------------------------------------------------------

        [Test]
        public void TheBatchModeMicrophoneGuardNeverTouchesTheVoiceStackOutsideBatchMode()
        {
            var go = new GameObject("recorder-under-guard");
            try
            {
                var recorder = go.AddComponent<Recorder>();
                var before = new SerializedObject(recorder);
                bool recording = Bool(before, "recordingEnabled"), transmit = Bool(before, "transmitEnabled");

                Assert.That(BatchModeMicrophoneGuard.Apply(false), Is.EqualTo(0), "a player build is not batchmode");
                Assert.That(recorder.enabled, Is.True);
                var after = new SerializedObject(recorder);
                Assert.That(Bool(after, "recordingEnabled"), Is.EqualTo(recording));
                Assert.That(Bool(after, "transmitEnabled"), Is.EqualTo(transmit));

                Assert.That(BatchModeMicrophoneGuard.Apply(true), Is.EqualTo(1), "the guard still disables the recorder when it is batchmode");
                Assert.That(recorder.enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---- local monitoring ------------------------------------------------------------------------------------------------------

        [Test]
        public void TheLocalMicrophoneLoopIsSilencedAfterTheLipSyncContextHasReadIt()
        {
            using (var scene = OpenScene())
            {
                var input = scene.Components<OvrAvatarLipSyncContext>().Select(c => c.gameObject).Distinct().ToList();
                Assert.That(input, Has.Count.EqualTo(1), "one LipSyncInput object feeds the local avatar");
                var all = input[0].GetComponents<Component>().ToList();
                int context = all.FindIndex(c => c is OvrAvatarLipSyncContext);
                int silencer = all.FindIndex(c => c is LocalMicMonitorSilencer);
                Assert.That(silencer, Is.GreaterThan(context), "OnAudioFilterRead runs in component order: the context must read the samples first");
            }

            var samples = new[] { 0.5f, -0.25f, 0.125f, 1f };
            LocalMicMonitorSilencer.Silence(samples);
            Assert.That(samples, Is.All.EqualTo(0f));
            Assert.DoesNotThrow(() => LocalMicMonitorSilencer.Silence(null));
        }

        // ---- the voice path, sender to speaker, over an in-process transport --------------------------------------------------------

        /// <summary>
        /// Process A sends a generated 440 Hz tone through the real Photon Voice client (Opus encoder, voice info, frames); process B's client
        /// decodes it and links it to the REAL LipSyncSpeaker, the component on the voice prefab, through the same RemoteVoiceLink and
        /// Speaker.Link calls that FusionVoiceClient makes (a LipSyncSpeaker added in the test, not the prefab instance, with no lip-sync context). Only the Internet is replaced (a transport that hands A's events to B), so this
        /// proves remote audio frames arrive and the speaker plays them, but not Photon Cloud reachability, which needs a Voice App ID.
        /// </summary>
        [Test, Timeout(60000)]
        public void ARemoteTonePlaysOnTheLipSyncSpeaker()
        {
            using (var rig = new VoiceRig())
            {
                rig.StartTone(440);
                Assert.That(rig.WaitForSamples(48000 / 2, 15000), Is.True, "no decoded audio reached the speaker: " + rig.Describe());

                Assert.That(rig.Speaker.IsLinked, Is.True);
                Assert.That(rig.Speaker.RemoteVoice.PlayerId, Is.EqualTo(VoiceRig.SenderPlayerId));
                Assert.That(rig.Receiver.FramesReceived, Is.GreaterThan(0));
                Assert.That(rig.Sender.FramesSent, Is.GreaterThan(0));
                Assert.That(rig.Tap.StartedFrequency, Is.EqualTo(48000));
                Assert.That(rig.Tap.StartedChannels, Is.EqualTo(1));

                float[] samples = rig.Tap.Snapshot(48000 / 4);
                double tone = Goertzel(samples, 440, 48000), other = Goertzel(samples, 1000, 48000);
                Assert.That(tone, Is.GreaterThan(other * 20), "the received audio is the 440 Hz tone that was sent, not silence or noise");
                Assert.That(Rms(samples), Is.GreaterThan(0.02), "the decoded tone is audible, not near-zero");
            }
        }

        [Test, Timeout(60000)]
        public void NothingArrivesWhileTransmissionIsDisabled()
        {
            using (var rig = new VoiceRig())
            {
                rig.StartTone(440);
                Assert.That(rig.WaitForSamples(48000 / 4, 15000), Is.True, rig.Describe());

                // The sender keeps being serviced (that is what reads the source) through the whole window, so only TransmitEnabled can stop it.
                int flowingBefore = rig.Tap.TotalSamples;
                rig.PumpFor(500);
                Assert.That(rig.Tap.TotalSamples - flowingBefore, Is.GreaterThan(48000 / 10), "audio was not flowing before the disable: " + rig.Describe());

                rig.Voice.TransmitEnabled = false;
                Assert.That(rig.Voice.TransmitEnabled, Is.False);
                rig.PumpFor(400);
                int sentSettled = rig.Sender.FramesSent, settled = rig.Tap.TotalSamples;
                rig.PumpFor(1500);
                Assert.That(rig.Sender.FramesSent, Is.EqualTo(sentSettled), "frames were still sent after TransmitEnabled was set to false");
                Assert.That(rig.Tap.TotalSamples - settled, Is.LessThan(48000 / 20), "audio kept arriving after TransmitEnabled was set to false");
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------------------------------

        private static bool Bool(SerializedObject so, string name)
        {
            var property = so.FindProperty(name);
            Assert.That(property, Is.Not.Null, "serialized field " + name + " is gone: update this test with the Photon Voice version");
            return property.boolValue;
        }

        private static double Goertzel(float[] x, double hz, int rate)
        {
            double w = 2 * Math.PI * hz / rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (float v in x)
            {
                double s = v + coeff * s1 - s2;
                s2 = s1;
                s1 = s;
            }
            return s1 * s1 + s2 * s2 - coeff * s1 * s2;
        }

        private static double Rms(float[] x) => Math.Sqrt(x.Sum(v => (double)v * v) / Math.Max(1, x.Length));

        private static SceneHandle OpenScene() => new SceneHandle();

        private sealed class SceneHandle : IDisposable
        {
            private readonly Scene _scene;
            private readonly bool _opened;

            public SceneHandle()
            {
                _scene = SceneManager.GetSceneByPath(ScenePath);
                _opened = !_scene.isLoaded;
                if (_opened) _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            }

            public T[] Components<T>() where T : Component =>
                _scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();

            public void Dispose()
            {
                if (_opened && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true);
            }
        }

        private sealed class QuietLogger : ILogger
        {
            public LogLevel Level => LogLevel.Error;
            public void Log(LogLevel level, string fmt, params object[] args) { }
        }

        /// <summary>Hands one client's voice events to another's, as the Photon Cloud transport does between two players.</summary>
        private sealed class PairTransport : IVoiceTransport
        {
            private readonly int _playerId;
            public VoiceClient Peer;

            public PairTransport(int playerId) { _playerId = playerId; }

            public bool IsChannelJoined(int channelId) => true;

            public void SendVoiceInfo(LocalVoice voice, int channelId, bool targetMe, int[] targetPlayers) =>
                Peer?.onVoiceInfo(channelId, _playerId, voice.ID, voice.EventBufferSize, voice.Info);

            public void SendVoiceRemove(LocalVoice voice, int channelId, bool targetMe, int[] targetPlayers) =>
                Peer?.onVoiceRemove(_playerId, new[] { voice.ID });

            public void SendFrame(ArraySegment<byte> data, FrameFlags flags, ushort evNumber, byte frNumber, byte voiceId, int channelId, SendFrameParams par)
            {
                var copy = new byte[data.Count];
                Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
                var buffer = new FrameBuffer(copy, 0, copy.Length, flags, frNumber, null);
                Peer?.onFrame(_playerId, voiceId, evNumber, ref buffer, false);
                buffer.Release();
            }

            public string ChannelIdStr(int channelId) => null;
            public string PlayerIdStr(int playerId) => null;
            public int GetPayloadFragmentSize(SendFrameParams par) => 0;
        }

        /// <summary>Records what the speaker's audio output receives, then forwards it.</summary>
        private sealed class TapAudioOut : IAudioOut<float>
        {
            private readonly object _lock = new object();
            private readonly List<float> _samples = new List<float>();
            public IAudioOut<float> Inner;
            public int StartedFrequency, StartedChannels;

            public bool IsPlaying => Inner.IsPlaying;
            public int Lag => Inner.Lag;
            public int TotalSamples { get { lock (_lock) return _samples.Count; } }

            public void Start(int frequency, int channels, int frameSamples)
            {
                StartedFrequency = frequency;
                StartedChannels = channels;
                Inner.Start(frequency, channels, frameSamples);
            }

            public void Push(float[] frame)
            {
                lock (_lock) _samples.AddRange(frame);
                Inner.Push(frame);
            }

            public void Flush() => Inner.Flush();
            // UnityAudioOut.Stop calls Destroy, which Unity refuses in edit mode (it logs an error); the test destroys the object itself.
            public void Stop() { }
            public void Service() => Inner.Service();

            public float[] Snapshot(int count)
            {
                lock (_lock) return _samples.Skip(Math.Max(0, _samples.Count - count)).ToArray();
            }
        }

        private sealed class ProbeSpeaker : LipSyncSpeaker
        {
            public readonly TapAudioOut Tap = new TapAudioOut();

            protected override IAudioOut<float> CreateAudioOut()
            {
                Tap.Inner = base.CreateAudioOut();
                return Tap;
            }

            public void RunAwake() => Awake();
        }

        private sealed class VoiceRig : IDisposable
        {
            public const int SenderPlayerId = 7;
            private readonly GameObject _speakerObject;
            private readonly AudioUtil.ToneAudioReader<float> _tone;
            private readonly List<string> _notes = new List<string>();
            public readonly VoiceClient Sender, Receiver;
            public readonly ProbeSpeaker Speaker;
            public LocalVoice Voice { get; private set; }
            public TapAudioOut Tap => Speaker.Tap;

            public VoiceRig()
            {
                var logger = new QuietLogger();
                var toReceiver = new PairTransport(SenderPlayerId);
                var toSender = new PairTransport(2);
                Sender = new VoiceClient(toReceiver, logger);
                Receiver = new VoiceClient(toSender, logger);
                toReceiver.Peer = Receiver;
                toSender.Peer = Sender;

                _speakerObject = new GameObject("remote-speaker");
                _speakerObject.AddComponent<AudioSource>();
                Speaker = _speakerObject.AddComponent<ProbeSpeaker>();
                Speaker.RunAwake();

                var link = typeof(Speaker).GetMethod("Link", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(link, Is.Not.Null, "Speaker.Link changed in this Photon Voice version: update this test");
                Receiver.OnRemoteVoiceInfoAction = (int channelId, int playerId, byte voiceId, VoiceInfo info, ref RemoteVoiceOptions options) =>
                {
                    var remote = new RemoteVoiceLink(info, playerId, voiceId, channelId, ref options);
                    _notes.Add("voice info p#" + playerId + " v#" + voiceId + " " + info.Codec);
                    bool linked = (bool)link.Invoke(Speaker, new object[] { remote });
                    _notes.Add("linked=" + linked);
                };
                _tone = new AudioUtil.ToneAudioReader<float>(null, 440, 48000, 1);
            }

            public void StartTone(int hz)
            {
                var info = VoiceInfo.CreateAudioOpus(POpusCodec.Enums.SamplingRate.Sampling48000, 1, OpusCodec.FrameDuration.Frame20ms, 30000);
                Voice = Sender.CreateLocalVoiceAudioFromSource(info, _tone, AudioSampleType.Float, 1);
                Assert.That(Voice, Is.Not.Null, "the Opus encoder could not be created");
            }

            public bool WaitForSamples(int count, int timeoutMs)
            {
                var until = Environment.TickCount + timeoutMs;
                while (Environment.TickCount < until)
                {
                    Sender.Service();
                    if (Tap.TotalSamples >= count) return true;
                    Thread.Sleep(10);
                }
                return Tap.TotalSamples >= count;
            }

            public void PumpFor(int ms)
            {
                var until = Environment.TickCount + ms;
                while (Environment.TickCount < until)
                {
                    Sender.Service();
                    Thread.Sleep(10);
                }
            }

            public string Describe() =>
                "sent=" + Sender.FramesSent + " received=" + Receiver.FramesReceived + " decodedSamples=" + Tap.TotalSamples +
                " linked=" + Speaker.IsLinked + " [" + string.Join("; ", _notes) + "]";

            public void Dispose()
            {
                if (Voice != null) Sender.RemoveLocalVoice(Voice);
                Sender.Dispose();
                Receiver.Dispose();
                _tone.Dispose();
                UnityEngine.Object.DestroyImmediate(_speakerObject);
            }
        }
    }
}
#endif
