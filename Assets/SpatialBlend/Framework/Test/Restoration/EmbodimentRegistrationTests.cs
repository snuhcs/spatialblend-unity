// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Embodiment registration (core C ABI 4.7), Unity side. No native library is needed
    /// except where a test says so. The numbers in the in-memory fixtures below are TEST DATA (a three-joint skeleton and two
    /// made-up offsets); the shipped data files are checked against their own derivation, not against constants.
    ///
    /// The oracle in this file is deliberately a second implementation: explicit double matrices and quaternions, the Z reflection
    /// written out, and the displacement of a measured point computed on UnityEngine's own Vector3/Quaternion.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EmbodimentRegistrationTests
    {
        // The glTF/OVR native-to-canonical basis, as the artefacts state it.
        private static readonly double[] GltfBasis = { 0, 0, 1, 1, 0, 0, 0, 1, 0 };
        private const long Actor = 7;

        // ------------------------------------------------------------------ fixtures (test data)

        private const string Structural = @"{
 ""format"":""spatialblend.embodiment"",""schema_version"":""1.0.0"",""id"":""fixture"",""units"":""m"",
 ""frame"":""z-up, right-handed"",""bone_frame"":""fixture frame"",
 ""skeleton"":{""joints"":[{""name"":""Root"",""parent"":-1},{""name"":""Hips"",""parent"":0},{""name"":""Head"",""parent"":1}]},
 ""anchors"":[
  {""name"":""root"",""bone"":null,""families"":[""Spacing""]},
  {""name"":""head"",""bone"":""Head"",""families"":[""Facing""],""offset_from"":""measurement""},
  {""name"":""pelvis"",""bone"":""Hips"",""families"":[""AffordancePose""],""offset_from"":""measurement""}]}";

        private const string Measurement = @"{
 ""registration"":{""native_basis_rows"":[0,0,1,1,0,0,0,1,0],""procedure"":""fixture"",""anchors"":[
  {""name"":""head"",""bone"":""Head"",""offset"":{""position"":[0,0,0],""rotation"":[0,0,0,1]},
   ""provenance"":{""procedure"":""fixture head"",""asset_sha256"":""abababababababababababababababababababababababababababababababab""}},
  {""name"":""pelvis"",""bone"":""Hips"",""offset"":{""position"":[0.0,-0.17,0.025],""rotation"":[0.5,0.5,0.5,0.5]},
   ""provenance"":{""procedure"":""fixture pelvis"",""asset_sha256"":""abababababababababababababababababababababababababababababababab"",""caveat"":{""nested"":[1,2,{""deep"":null}]}}}]}}";

        private const string Chain = @"{
 ""native_basis_rows"":[0,0,1,1,0,0,0,1,0],
 ""chain"":{""rest_pose"":""A"",""provenance"":{""procedure"":""fixture"",""asset_sha256"":""abababababababababababababababababababababababababababababababab""},""joints"":[
  {""name"":""Root"",""rest"":{""position"":[0,0,0],""rotation"":[0,0,0,1]}},
  {""name"":""Hips"",""rest"":{""position"":[0,0,1],""rotation"":[0.5,0.5,0.5,0.5]}},
  {""name"":""Head"",""rest"":{""position"":[0,0,1.5],""rotation"":[0,0,0,1]}}]}}";

        private static readonly EmbodimentFiles TestFiles = OvrAvatarEmbodiment.Files;

        private sealed class MemorySource : IEmbodimentTextSource
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            public bool TryRead(string name, out string text) => Files.TryGetValue(name, out text);

            public static MemorySource All() => new MemorySource
            {
                Files = { [TestFiles.Structural] = Structural, [TestFiles.Measurement] = Measurement, [TestFiles.Chain] = Chain }
            };
        }

        internal static MiniJsonObject Parse(string json)
        {
            Assert.That(MiniJson.TryParse(json, out object value, out string error), Is.True, error);
            return (MiniJsonObject)value;
        }

        internal static MiniJsonObject Anchor(MiniJsonObject spec, string name) =>
            ((List<object>)spec.Get("anchors")).Cast<MiniJsonObject>().Single(a => (string)a.Get("name") == name);

        // ------------------------------------------------------------------ MiniJson

        [Test]
        public void MiniJsonKeepsOrderAndNullAndRoundTripsDoubles()
        {
            MiniJsonObject o = Parse("{\"b\":null,\"a\":[1,2.5,-3e-2,true,\"x\\n\\u0041\"],\"c\":{}}");
            Assert.That(o.Members.Select(m => m.Key), Is.EqualTo(new[] { "b", "a", "c" }));
            Assert.That(o.TryGet("b", out object b) && b == null, Is.True, "null is a value");
            string text = MiniJson.Write(o);
            Assert.That(text, Is.EqualTo("{\"b\":null,\"a\":[1,2.5,-0.03,true,\"x\\nA\"],\"c\":{}}"));
            foreach (double d in new[] { 0.1, 1.0 / 3.0, 1e-300, -0.0, 0.12345678901234568 })
            {
                var one = new List<object> { d };
                Assert.That(MiniJson.TryParse(MiniJson.Write(one), out object back, out _), Is.True);
                Assert.That(((List<object>)back)[0], Is.EqualTo(d));
            }
        }

        [Test]
        public void MiniJsonRefusesWhatIsNotAJsonDocument()
        {
            foreach (string bad in new[] { "", "{", "{\"a\":1,}", "{\"a\":1 \"b\":2}", "[1,]", "{\"a\":1}{", "{\"a\":1,\"a\":2}", "nul", "\"x", "{\"a\":NaN}", "[01x]" })
            {
                Assert.That(MiniJson.TryParse(bad, out _, out string error), Is.False, bad);
                Assert.That(error, Is.Not.Empty);
            }
        }

        // ------------------------------------------------------------------ the loader

        [Test]
        public void TheLoaderBuildsTheCoreSchemaFromTheStructureTheMeasurementAndTheChain()
        {
            Assert.That(EmbodimentSpecLoader.TryBuild(MemorySource.All(), TestFiles, out EmbodimentSpec spec, out string report), Is.True, report);
            Assert.That(spec.Notes, Is.Empty);
            Assert.That(spec.BoneBoundCount, Is.EqualTo(2));
            Assert.That(spec.HasChain, Is.True);
            Assert.That(spec.NativeBasisRows, Is.EqualTo(GltfBasis));

            MiniJsonObject json = Parse(spec.Json);
            Assert.That(json.Get("format"), Is.EqualTo("spatialblend.embodiment"));
            Assert.That(json.Get("id"), Is.EqualTo("fixture"));
            Assert.That(json.Members.Select(m => m.Key), Is.EqualTo(new[]
                { "format", "schema_version", "id", "units", "frame", "bone_frame", "skeleton", "anchors", "chain" }));

            MiniJsonObject root = Anchor(json, "root");
            Assert.That(root.TryGet("bone", out object rootBone) && rootBone == null, Is.True, "the actor root is bone null");
            Assert.That(root.TryGet("offset", out _), Is.False, "the root has no offset");

            MiniJsonObject pelvis = Anchor(json, "pelvis");
            Assert.That(pelvis.Get("bone"), Is.EqualTo("Hips"));
            Assert.That(pelvis.TryGet("offset_from", out _), Is.False, "the loader's own key does not reach the core");
            var offset = (MiniJsonObject)pelvis.Get("offset");
            Assert.That(((List<object>)offset.Get("position")).Cast<double>(), Is.EqualTo(new[] { 0.0, -0.17, 0.025 }));
            Assert.That(((List<object>)offset.Get("rotation")).Cast<double>(), Is.EqualTo(new[] { 0.5, 0.5, 0.5, 0.5 }));
            var provenance = (MiniJsonObject)pelvis.Get("provenance");
            Assert.That(provenance.Get("procedure"), Is.EqualTo("fixture pelvis"));
            Assert.That(provenance.Get("asset_sha256"), Is.EqualTo(string.Concat(Enumerable.Repeat("ab", 32))));
            Assert.That(MiniJson.Write(provenance.Get("caveat")), Is.EqualTo("{\"nested\":[1,2,{\"deep\":null}]}"), "provenance is passed verbatim");

            var chain = (MiniJsonObject)json.Get("chain");
            Assert.That(chain.Get("rest_pose"), Is.EqualTo("A"));
            Assert.That(((List<object>)chain.Get("joints")).Count, Is.EqualTo(3));
            Assert.That(spec.Anchors.Select(a => (a.Name, a.BoneName)), Is.EqualTo(new[] { ("root", (string)null), ("head", "Head"), ("pelvis", "Hips") }));
        }

        [Test]
        public void WithoutTheMeasurementNoAnchorThatNeedsOneIsRegisteredAndNothingTakesItsPlace()
        {
            MemorySource source = MemorySource.All();
            source.Files.Remove(TestFiles.Measurement);
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out string report), Is.True, report);
            Assert.That(spec.Anchors.Select(a => a.Name), Is.EqualTo(new[] { "root" }), "root only: no joint-centre fallback for the head or the pelvis");
            Assert.That(spec.BoneBoundCount, Is.EqualTo(0), "a registration with no bone-bound anchor is not worth making; the driver keeps the legacy anchors");
            Assert.That(spec.HasChain, Is.False);
            Assert.That(spec.Notes.Any(n => n.Contains("measurement")), Is.True);
            MiniJsonObject json = Parse(spec.Json);
            Assert.That(((List<object>)json.Get("anchors")).Count, Is.EqualTo(1));
            Assert.That(json.TryGet("chain", out _), Is.False);
        }

        [Test]
        public void AMeasurementForAnotherBoneOrWithNoProvenanceDropsThatAnchorOnly()
        {
            MemorySource source = MemorySource.All();
            source.Files[TestFiles.Measurement] = Measurement.Replace("\"bone\":\"Hips\"", "\"bone\":\"Head\"");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out _), Is.True);
            Assert.That(spec.Anchors.Select(a => a.Name), Is.EqualTo(new[] { "root", "head" }));
            Assert.That(spec.Notes.Single(), Does.Contain("pelvis"));

            // The core's rule: a non-identity offset needs a procedure and a 64-hex hash or a source. Each way to fail leaves that anchor out.
            string hash = "\"asset_sha256\":\"abababababababababababababababababababababababababababababababab\"";
            foreach (string broken in new[] { "\"asset_sha256\":\"00\"", "\"asset_sha256\":\"" + new string('g', 64) + "\"", "\"x\":0" })
            {
                source.Files[TestFiles.Measurement] = Measurement.Replace("\"procedure\":\"fixture pelvis\"," + hash, "\"procedure\":\"fixture pelvis\"," + broken);
                Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
                Assert.That(spec.Anchors.Select(a => a.Name), Is.EqualTo(new[] { "root", "head" }), broken);
            }
            source.Files[TestFiles.Measurement] = Measurement.Replace(hash, "\"source\":\"a vendor specification\"");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
            Assert.That(spec.Anchors.Select(a => a.Name), Is.EqualTo(new[] { "root", "head", "pelvis" }), "a source is as good as a hash");
            source.Files[TestFiles.Measurement] = Measurement.Replace("\"procedure\":\"fixture pelvis\"", "\"procedure\":\"\"");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
            Assert.That(spec.Anchors.Select(a => a.Name), Is.EqualTo(new[] { "root", "head" }), "an empty procedure");
        }

        [Test]
        public void TheChainIsRegisteredOnlyWhenItStatesTheSameBasisAsTheMeasurement()
        {
            MemorySource source = MemorySource.All();
            source.Files[TestFiles.Chain] = Chain.Replace("[0,0,1,1,0,0,0,1,0]", "[0,0,1,-1,0,0,0,1,0]");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out _), Is.True);
            Assert.That(spec.HasChain, Is.False);
            Assert.That(spec.BoneBoundCount, Is.EqualTo(2), "the offsets stay: they do not depend on the chain file");
            Assert.That(spec.Notes.Single(), Does.Contain("different native basis"));

            source.Files.Remove(TestFiles.Chain);
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
            Assert.That(spec.HasChain, Is.False);
        }

        [Test]
        public void TheChainNeedsTheCoresRestPoseLabelAndProvenance()
        {
            MemorySource source = MemorySource.All();
            foreach (string pose in new[] { "A", "T", "other" })
            {
                source.Files[TestFiles.Chain] = Chain.Replace("\"rest_pose\":\"A\"", "\"rest_pose\":\"" + pose + "\"");
                Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out _), Is.True);
                Assert.That(spec.HasChain, Is.True, pose);
            }
            foreach (string bad in new[] { "a-pose", "", "a" })
            {
                source.Files[TestFiles.Chain] = Chain.Replace("\"rest_pose\":\"A\"", "\"rest_pose\":\"" + bad + "\"");
                Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out _), Is.True);
                Assert.That(spec.HasChain, Is.False, "rest_pose '" + bad + "'");
            }
            source.Files[TestFiles.Chain] = Chain.Replace("\"asset_sha256\":\"abababababababababababababababababababababababababababababababab\"", "\"asset\":\"x\"");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec unsourced, out _), Is.True);
            Assert.That(unsourced.HasChain, Is.False, "chain provenance needs a hash or a source");
        }

        // The optional body floor-footprint radius (core spec section 13). The numbers are fixtures; the shipped artefact carries the radius measured from the avatar mesh.
        [Test]
        public void ABodyRadiusIsPassedWithItsProvenanceAndDroppedWhenItBreaksTheRule()
        {
            string hash = new string('a', 64);
            string Body(string radius, string provenance) =>
                Measurement.Replace("\"registration\":{", "\"registration\":{\"body\":{\"floor_footprint_radius_m\":" + radius + provenance + "},");
            MemorySource source = MemorySource.All();

            source.Files[TestFiles.Measurement] = Body("0.25", ",\"provenance\":{\"procedure\":\"fixture\",\"asset_sha256\":\"" + hash + "\"}");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out _), Is.True);
            Assert.That(spec.HasBody, Is.True);
            var body = (MiniJsonObject)Parse(spec.Json).Get("body");
            Assert.That(body.Get("floor_footprint_radius_m"), Is.EqualTo(0.25));
            Assert.That(((MiniJsonObject)body.Get("provenance")).Get("asset_sha256"), Is.EqualTo(hash));

            source.Files[TestFiles.Measurement] = Body("0.25", ",\"provenance\":{\"procedure\":\"fixture\",\"source\":\"a paper\"}");
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
            Assert.That(spec.HasBody, Is.True, "a source is as good as a hash");

            foreach (string bad in new[] { Body("0", ",\"provenance\":{\"procedure\":\"f\",\"source\":\"s\"}"),
                                           Body("-0.2", ",\"provenance\":{\"procedure\":\"f\",\"source\":\"s\"}"),
                                           Body("\"0.2\"", ",\"provenance\":{\"procedure\":\"f\",\"source\":\"s\"}"),
                                           Body("0.2", ""),
                                           Body("0.2", ",\"provenance\":{\"procedure\":\"f\"}") })
            {
                source.Files[TestFiles.Measurement] = bad;
                Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
                Assert.That(spec.HasBody, Is.False, bad);
                Assert.That(Parse(spec.Json).TryGet("body", out _), Is.False);
                Assert.That(spec.Notes.Any(n => n.StartsWith("body left out")), Is.True);
                Assert.That(spec.BoneBoundCount, Is.EqualTo(2), "the anchors are unaffected");
            }

            source.Files[TestFiles.Measurement] = Measurement;
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out spec, out _), Is.True);
            Assert.That(spec.HasBody, Is.False, "no body in the artefact: no radius, no default");
        }

        [Test]
        public void NoStructureFileOrAnUnparsableOneIsNotABuild()
        {
            var empty = new MemorySource();
            Assert.That(EmbodimentSpecLoader.TryBuild(empty, TestFiles, out EmbodimentSpec spec, out string report), Is.False);
            Assert.That(spec, Is.Null);
            Assert.That(report, Does.Contain("'" + TestFiles.Structural + "'"));
            empty.Files[TestFiles.Structural] = "{not json";
            Assert.That(EmbodimentSpecLoader.TryBuild(empty, TestFiles, out spec, out report), Is.False);
            empty.Files[TestFiles.Structural] = "{\"anchors\":[]}";
            Assert.That(EmbodimentSpecLoader.TryBuild(empty, TestFiles, out spec, out report), Is.False, "skeleton is required");
        }

        // The shipped structure file (the measurement artefacts it is registered with are checked in the private-only
        // PrivateData/ShippedEmbodimentDataTests: they are not redistributed).
        [Test]
        public void TheShippedStructureHasNoBodyNumber()
        {
            var source = new ResourcesEmbodimentTextSource();
            Assert.That(source.TryRead(OvrAvatarEmbodiment.Files.Structural, out string structuralText), Is.True, "the structure file ships with the code");
            MiniJsonObject structural = Parse(structuralText);
            Assert.That(structuralText, Does.Not.Contain("\"offset\""), "the structure carries no offset");
            Assert.That(structuralText, Does.Not.Contain("\"position\""));
            var joints = (List<object>)((MiniJsonObject)structural.Get("skeleton")).Get("joints");
            Assert.That(joints.Count, Is.EqualTo(64), "the 64 joints of ovrAvatar2JointType");
            for (int i = 0; i < joints.Count; ++i)
            {
                var j = (MiniJsonObject)joints[i];
                Assert.That(Enum.TryParse((string)j.Get("name"), out Oculus.Avatar2.CAPI.ovrAvatar2JointType type), Is.True, (string)j.Get("name"));
                Assert.That((int)type, Is.EqualTo(i), "joint names are the SDK enum, in enum order");
                Assert.That((double)j.Get("parent"), Is.LessThan(i));
            }
        }

        // ------------------------------------------------------------------ the frames

        // The Unity bone's local axes are the native ones relabelled by the SDK's z reflection (assumption A1), written out here.
        private static EmbodimentFrames Frames() =>
            new EmbodimentFrames(Mat3.FromRows(GltfBasis), new Mat3(1, 0, 0, 0, 1, 0, 0, 0, -1));

        [Test]
        public void TheLocalFrameChangeIsTheRotationTheDerivationSays()
        {
            EmbodimentFrames frames = Frames();
            // P (x, z, y), Z = diag(1, 1, -1), B^T: K = P Z B^T, worked by hand in the artefact's registration.procedure.
            Mat3 k = frames.K;
            double[] expected = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };
            double[] actual = { k.M00, k.M01, k.M02, k.M10, k.M11, k.M12, k.M20, k.M21, k.M22 };
            Assert.That(actual, Is.EqualTo(expected).Within(1e-15));
            Assert.That(k.Determinant, Is.EqualTo(1.0).Within(1e-15), "a proper rotation: a pure change of the local frame");
            Assert.That(frames.P.Determinant, Is.EqualTo(-1.0).Within(1e-15), "Unity to core is a handedness change");
            // P is the position map every other pose already uses.
            CoordinateConverter.UnityToSpatialBlend3D(new Vector3(2f, 3f, 5f), out double x, out double y, out double z);
            (double px, double py, double pz) = frames.P.Apply(2, 3, 5);
            Assert.That(new[] { px, py, pz }, Is.EqualTo(new[] { x, y, z }));
        }

        [Test]
        public void TheAnchorTheCoreComposesIsTheMeasuredPointOfTheUnityBone()
        {
            // A measured point n in the NATIVE local frame of a bone. In Unity it sits at pos + rot * (Z n); the core, given the bone as
            // ToCore publishes it and the registered offset B n, must compose the same point (in the core world, P of the Unity one).
            EmbodimentFrames frames = Frames();
            var rng = new System.Random(5);
            for (int trial = 0; trial < 20; ++trial)
            {
                Vector3 position = new Vector3(Rnd(rng), Rnd(rng), Rnd(rng));
                Quaternion rotation = RandomRotation(rng);
                double[] n = { Rnd(rng) * 0.2, Rnd(rng) * 0.2, Rnd(rng) * 0.2 };
                Vector3 unityPoint = position + rotation * new Vector3((float)n[0], (float)n[1], -(float)n[2]);
                (double ex, double ey, double ez) = (unityPoint.x, unityPoint.z, unityPoint.y);

                EmbodiedBonePose bone = frames.ToCore(position, rotation);
                (double ox, double oy, double oz) = Mat3.FromRows(GltfBasis).Apply(n[0], n[1], n[2]);
                (double dx, double dy, double dz) = Mat3.FromQuaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw).Apply(ox, oy, oz);
                Assert.That(new[] { bone.X + dx, bone.Y + dy, bone.Z + dz }, Is.EqualTo(new[] { ex, ey, ez }).Within(2e-6), "trial " + trial);
            }
        }

        [Test]
        public void ToCoreAndFromCoreAreInverseAndTheBoneRotationIsAProperRotation()
        {
            EmbodimentFrames frames = Frames();
            var rng = new System.Random(11);
            for (int trial = 0; trial < 50; ++trial)
            {
                var position = new Vector3(Rnd(rng), Rnd(rng), Rnd(rng));
                Quaternion rotation = RandomRotation(rng);
                EmbodiedBonePose bone = frames.ToCore(position, rotation);
                Assert.That(Math.Sqrt(bone.Qx * bone.Qx + bone.Qy * bone.Qy + bone.Qz * bone.Qz + bone.Qw * bone.Qw), Is.EqualTo(1.0).Within(1e-12));
                frames.FromCore(bone, out Vector3 backPosition, out Quaternion backRotation);
                Assert.That(Vector3.Distance(position, backPosition), Is.LessThan(1e-5f));
                Assert.That(RotationDifference(rotation, backRotation), Is.LessThan(1e-5f));
            }
        }

        [Test]
        public void AnAnchorPoseRoundTripsThroughTheCoreEulerConvention()
        {
            foreach ((float yaw, float pitch, double roll) in new[] { (0f, 0f, 0.0), (37f, 12f, 0.3), (-120f, -30f, -0.2), (170f, 45f, 0.0) })
            {
                EmbodiedBonePose anchor = EmbodimentFrames.UnityPoseToAnchor(new Vector3(1f, 2f, 3f), yaw, pitch, roll);
                EmbodiedPose back = EmbodimentFrames.AnchorToUnityPose(anchor, out double backRoll);
                Assert.That(Mathf.DeltaAngle(yaw, back.YawDegrees), Is.EqualTo(0f).Within(1e-3f), "yaw " + yaw);
                Assert.That(back.PitchDegrees, Is.EqualTo(pitch).Within(1e-3f), "pitch " + pitch);
                Assert.That(backRoll, Is.EqualTo(roll).Within(1e-9));
                Assert.That(back.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            }
            // yaw 0 in the core is +X, which is Unity yaw 90 (the existing angle conversion), +pitch is nose down in both.
            EmbodimentFrames.QuaternionFromEulerZyx(0, 0, 0, out double qx, out double qy, out double qz, out double qw);
            Assert.That(new[] { qx, qy, qz, qw }, Is.EqualTo(new[] { 0.0, 0.0, 0.0, 1.0 }));
            EmbodimentFrames.EulerZyx(0, 0, Math.Sin(Math.PI / 4), Math.Cos(Math.PI / 4), out double yaw0, out _, out _);
            Assert.That(yaw0, Is.EqualTo(Math.PI / 2).Within(1e-12), "a quarter turn about z faces +Y, Unity forward");
        }

        // Angle() through acos of a dot product near 1 only resolves ~0.03 degrees in float; the largest distance one rotation moves a unit axis from where the other does has no such limit.
        private static float RotationDifference(Quaternion a, Quaternion b) =>
            new[] { Vector3.right, Vector3.up, Vector3.forward }.Max(v => (a * v - b * v).magnitude);

        private static Quaternion RandomRotation(System.Random rng) =>
            Quaternion.Euler(Rnd(rng) * 90f, Rnd(rng) * 90f, Rnd(rng) * 90f);

        private static float Rnd(System.Random rng) => (float)(rng.NextDouble() * 4.0 - 2.0);

        // ------------------------------------------------------------------ the native layouts (sizes; offsets by the header)

        [Test]
        public void TheManagedStructsMirrorTheHeader()
        {
            Assert.That(Marshal.SizeOf<SbBonePose>(), Is.EqualTo(72));
            Assert.That(Marshal.SizeOf<SbNamedBonePose>(), Is.EqualTo(80));
            Assert.That(Marshal.SizeOf<SbChainJoint>(), Is.EqualTo(128));
            AssertOffsets(typeof(SbBonePose), ("struct_size", 0), ("flags", 4), ("position", 8), ("rotation", 32), ("epoch", 64));
            AssertOffsets(typeof(SbNamedBonePose), ("bone_name", 0), ("pose", 8));
            AssertOffsets(typeof(SbChainJoint), ("struct_size", 0), ("parent", 4), ("rest_position", 8), ("rest_rotation", 32),
                ("segment_length_m", 64), ("limit_mask", 72), ("limit_min", 80), ("limit_max", 104));
            Assert.That((int)SbStructId.BonePose, Is.EqualTo(26));
            Assert.That((int)SbStructId.NamedBonePose, Is.EqualTo(27));
            Assert.That((int)SbStructId.ChainJoint, Is.EqualTo(28));
            Assert.That(SpatialBlendAPI.EmbodimentAbiMinor, Is.EqualTo(7u));
            Assert.That(SpatialBlendAPI.RequiredAbiMinor, Is.GreaterThanOrEqualTo(SpatialBlendAPI.EmbodimentAbiMinor),
                "the minimum (4.11, the pursued-target proposals) covers embodiment registration (4.7); the driver still tolerates an Unsupported answer");
        }

        private static void AssertOffsets(Type type, params (string field, int offset)[] expected)
        {
            foreach ((string field, int offset) in expected)
            {
                Assert.That(Marshal.OffsetOf(type, field).ToInt32(), Is.EqualTo(offset), type.Name + "." + field);
            }
        }

        // Needs the native library; skipped without it. Compares the managed conversion with the core's own sb_convert_pose_basis for the
        // world part P (the part both have), so the two cannot drift apart unseen.
        [Test]
        public void TheManagedWorldBasisAgreesWithTheCoresConvertPoseBasisWhenTheLibraryIsLoaded()
        {
            bool usable;
            try { usable = SpatialBlendAPI.TryCheckEmbodimentRegistration(out _); }
            catch (Exception) { usable = false; }
            if (!usable) Assert.Ignore("the native library with ABI 4.7 is not loaded");
            double[] p = { 1, 0, 0, 0, 0, 1, 0, 1, 0 };
            EmbodimentFrames frames = Frames();
            var rng = new System.Random(3);
            for (int trial = 0; trial < 10; ++trial)
            {
                Quaternion rotation = RandomRotation(rng);
                var position = new Vector3(Rnd(rng), Rnd(rng), Rnd(rng));
                var native = new EmbodiedBonePose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
                Assert.That(SpatialBlendAPI.TryConvertPoseBasis(p, true, native, out EmbodiedBonePose theirs), Is.True);
                EmbodiedBonePose mine = frames.ToCore(position, rotation);
                // theirs = P R P^T; mine = (P R P^T) K: the same position, and the rotations differ by exactly K on the right.
                Assert.That(new[] { mine.X, mine.Y, mine.Z }, Is.EqualTo(new[] { theirs.X, theirs.Y, theirs.Z }).Within(1e-12));
                Mat3 rt = Mat3.FromQuaternion(theirs.Qx, theirs.Qy, theirs.Qz, theirs.Qw) * frames.K;
                Mat3 rm = Mat3.FromQuaternion(mine.Qx, mine.Qy, mine.Qz, mine.Qw);
                Assert.That(Math.Abs(rt.M00 - rm.M00) + Math.Abs(rt.M11 - rm.M11) + Math.Abs(rt.M22 - rm.M22) + Math.Abs(rt.M01 - rm.M01) + Math.Abs(rt.M02 - rm.M02) +
                            Math.Abs(rt.M10 - rm.M10) + Math.Abs(rt.M12 - rm.M12) + Math.Abs(rt.M20 - rm.M20) + Math.Abs(rt.M21 - rm.M21), Is.LessThan(1e-9));
            }
        }

        // ------------------------------------------------------------------ the driver

        // A scripted core that supports registration. It parses the spec the driver sent and answers the registration's own arithmetic
        // (anchor = bone * offset; bone = anchor * offset^-1) from it with a SECOND implementation: plain double quaternions.
        private sealed class RegistrationCore : IEmbodiedCore
        {
            public EmbodimentRegistrationResult Result = EmbodimentRegistrationResult.Registered;
            public string RefusalText = "fixture refusal";
            public readonly List<string> Registrations = new List<string>();
            public readonly List<string> Joints = new List<string>();
            public readonly Dictionary<string, (string bone, double[] position, double[] rotation)> Anchors =
                new Dictionary<string, (string bone, double[] position, double[] rotation)>();
            public readonly List<(int bone, EmbodiedBonePose pose, ulong sequence, ulong epoch, bool teleported)> BonePublishes =
                new List<(int bone, EmbodiedBonePose pose, ulong sequence, ulong epoch, bool teleported)>();
            public readonly List<(uint anchor, ulong sequence)> AnchorPublishes = new List<(uint anchor, ulong sequence)>();
            public int RootPublishes;
            public readonly List<AnchorProposal> Proposals = new List<AnchorProposal>();
            public readonly List<ActorEvent> Events = new List<ActorEvent>();
            private readonly List<string> _anchors = new List<string> { "root" };

            public bool TryGetAnchorId(long actor, string anchorName, out uint anchorId)
            {
                int i = _anchors.IndexOf(anchorName);
                if (i < 0) { _anchors.Add(anchorName); i = _anchors.Count - 1; }
                anchorId = (uint)i;
                return true;
            }

            public bool TryGetAnchorName(long actor, uint anchorId, out string anchorName)
            {
                anchorName = anchorId < _anchors.Count ? _anchors[(int)anchorId] : null;
                return anchorName != null;
            }

            public void PublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds, ulong sequence, ulong epoch, bool teleported)
            {
                if (anchorId == 0) { ++RootPublishes; return; }
                Assert.That(Anchors.ContainsKey(_anchors[(int)anchorId]) && Anchors[_anchors[(int)anchorId]].bone != null, Is.False,
                    "the core refuses (-3) the own port of an anchor derived from a bone; the driver must not publish it");
                AnchorPublishes.Add((anchorId, sequence));
            }

            public bool TryReadProposals(long actor, ProposalSet proposals)
            {
                proposals.Clear();
                proposals.Revision = 1;
                proposals.Anchors.Add(new AnchorProposal { AnchorId = 0, Status = EmbodiedStatus.None });
                proposals.Anchors.AddRange(Proposals);
                proposals.TotalAnchorCount = (uint)proposals.Anchors.Count;
                return true;
            }

            public bool TryPollEvent(long actor, out ActorEvent actorEvent)
            {
                if (Events.Count == 0) { actorEvent = default; return false; }
                actorEvent = Events[0];
                Events.RemoveAt(0);
                return true;
            }

            public bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> waypoints) => false;

            public EmbodimentRegistrationResult RegisterEmbodiment(long actor, string specJson, out string error)
            {
                Registrations.Add(specJson);
                error = Result == EmbodimentRegistrationResult.Refused ? RefusalText : null;
                if (Result != EmbodimentRegistrationResult.Registered) return Result;
                MiniJsonObject spec = (MiniJsonObject)Parse(specJson);
                foreach (MiniJsonObject j in ((List<object>)((MiniJsonObject)spec.Get("skeleton")).Get("joints")).Cast<MiniJsonObject>())
                {
                    Joints.Add((string)j.Get("name"));
                }
                foreach (MiniJsonObject a in ((List<object>)spec.Get("anchors")).Cast<MiniJsonObject>())
                {
                    var offset = a.Get("offset") as MiniJsonObject;
                    Anchors[(string)a.Get("name")] = (a.Get("bone") as string,
                        offset != null ? ((List<object>)offset.Get("position")).Cast<double>().ToArray() : new double[] { 0, 0, 0 },
                        offset != null ? ((List<object>)offset.Get("rotation")).Cast<double>().ToArray() : new double[] { 0, 0, 0, 1 });
                }
                return Result;
            }

            public bool TryGetBoneId(long actor, string boneName, out int boneId)
            {
                boneId = Joints.IndexOf(boneName);
                return boneId >= 0;
            }

            public void PublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence, ulong epoch, bool teleported) =>
                BonePublishes.Add((boneId, pose, sequence, epoch, teleported));

            public bool TryAnchorPoseFromBone(long actor, string anchorName, in EmbodiedBonePose bone, out EmbodiedBonePose anchor)
            {
                anchor = default;
                if (!Anchors.TryGetValue(anchorName, out var a) || a.bone == null) return false;
                double[] qb = { bone.Qx, bone.Qy, bone.Qz, bone.Qw };
                double[] d = Rotate(qb, a.position);
                double[] q = Multiply(qb, a.rotation);
                anchor = new EmbodiedBonePose(bone.X + d[0], bone.Y + d[1], bone.Z + d[2], q[0], q[1], q[2], q[3]);
                return true;
            }

            public bool TryBonePoseFromAnchor(long actor, string anchorName, in EmbodiedBonePose anchor, out EmbodiedBonePose bone)
            {
                bone = default;
                if (!Anchors.TryGetValue(anchorName, out var a) || a.bone == null) return false;
                double[] qa = { anchor.Qx, anchor.Qy, anchor.Qz, anchor.Qw };
                double[] qb = Multiply(qa, new[] { -a.rotation[0], -a.rotation[1], -a.rotation[2], a.rotation[3] });
                double[] d = Rotate(qb, a.position);
                bone = new EmbodiedBonePose(anchor.X - d[0], anchor.Y - d[1], anchor.Z - d[2], qb[0], qb[1], qb[2], qb[3]);
                return true;
            }

            private static double[] Normalize(double[] q)
            {
                double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
                return new[] { q[0] / n, q[1] / n, q[2] / n, q[3] / n };
            }

            // Hamilton product a * b, (x, y, z, w).
            private static double[] Multiply(double[] a, double[] b)
            {
                a = Normalize(a); b = Normalize(b);
                return Normalize(new[]
                {
                    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
                });
            }

            // v' = q v q^-1
            private static double[] Rotate(double[] q, double[] v)
            {
                q = Normalize(q);
                double[] p = Multiply(Multiply(q, new[] { v[0], v[1], v[2], 0.0 }), new[] { -q[0], -q[1], -q[2], q[3] });
                return new[] { p[0], p[1], p[2] };
            }
        }

        public sealed class BoneEntity : SbLocalEntity
        {
            public readonly List<string> SampledAnchors = new List<string>();
            public readonly Dictionary<string, (Vector3 position, Quaternion rotation)> AnchorPoses = new Dictionary<string, (Vector3, Quaternion)>();
            public readonly Dictionary<string, (Vector3 position, Quaternion rotation)> BonePoses = new Dictionary<string, (Vector3, Quaternion)>();
            public readonly List<(string name, Vector3 position, float yaw)> AnchorWrites = new List<(string, Vector3, float)>();
            public override IReadOnlyList<string> RealizedAnchorNames => SampledAnchors;
            public override void GetRealizedPose(out Vector3 position, out Quaternion rotation)
            {
                position = new Vector3(0f, 1.6f, 0f);
                rotation = Quaternion.identity;
            }

            public override bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
            {
                if (AnchorPoses.TryGetValue(anchorName, out var pose)) { position = pose.position; rotation = pose.rotation; return true; }
                position = default; rotation = Quaternion.identity; return false;
            }

            public override void ApplyAnchorTarget(string anchorName, Vector3 worldPosition, float yaw, bool hasHeight, double weight) =>
                AnchorWrites.Add((anchorName, worldPosition, yaw));

            public override void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation) { }
            public override void CancelRealizationTarget() { }
        }

        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private BoneEntity NewEntity()
        {
            var go = new GameObject("embodiment-fixture-avatar");
            go.SetActive(false); // no Awake: the driver only asks the entity for poses
            _objects.Add(go);
            var entity = go.AddComponent<BoneEntity>();
            entity.SampledAnchors.Add("head"); // what the legacy path would sample
            entity.AnchorPoses["head"] = (new Vector3(0f, 1.7f, 0f), Quaternion.identity);
            entity.BonePoses["Head"] = (new Vector3(0.1f, 1.7f, 0.2f), Quaternion.Euler(5f, 20f, 3f));
            entity.BonePoses["Hips"] = (new Vector3(0.3f, 0.9f, -0.4f), Quaternion.Euler(-8f, 130f, 4f));
            return entity;
        }

        [Test]
        // The registration half of the former ARegisteredAvatarPublishesItsBonesAndNotTheAnchorsDerivedFromThem. The bone ports it also
        // pinned belonged to the removed proposal-following frame: the checked driver publishes the root and the
        // direct head only, and reports the bone-bound anchors as unsupported typed targets.
        public void AnAvatarWithItsEmbodimentDataIsRegisteredOnceAtConstruction()
        {
            var core = new RegistrationCore();
            BoneEntity entity = NewEntity();
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, MemorySource.All());

            Assert.That(core.Registrations, Has.Count.EqualTo(1), "registered once, at construction");
            Assert.That(driver.EmbodimentRegistered, Is.True);
            Assert.That(Parse(core.Registrations[0]).Get("id"), Is.EqualTo("fixture"));
            Assert.That(core.BonePublishes, Is.Empty, "constructing publishes nothing");
            Assert.That(core.RootPublishes + core.AnchorPublishes.Count, Is.Zero);
        }



        [Test]
        public void WithoutTheMeasurementNothingIsRegistered()
        {
            var core = new RegistrationCore();
            BoneEntity entity = NewEntity();
            MemorySource source = MemorySource.All();
            source.Files.Remove(TestFiles.Measurement);
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, source);
            Assert.That(core.Registrations, Is.Empty, "nothing is registered, not even the root");
            Assert.That(driver.EmbodimentRegistered, Is.False);
        }

        // An unset radius is not a core error, and the radius is the embodiment's, for its driver. An artefact
        // that states the body and no usable anchor offset still registers the radius, and nothing else changes.
        [Test]
        public void ABodyWithoutAnyBoneBoundAnchorIsRegisteredAsABody()
        {
            var core = new RegistrationCore();
            BoneEntity entity = NewEntity();
            MemorySource source = MemorySource.All();
            source.Files[TestFiles.Measurement] = "{\"registration\":{\"native_basis_rows\":[0,0,1,1,0,0,0,1,0],\"procedure\":\"fixture\",\"anchors\":[]," +
                "\"body\":{\"floor_footprint_radius_m\":0.25,\"provenance\":{\"procedure\":\"fixture\",\"source\":\"a paper\"}}}}";
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, source);
            Assert.That(core.Registrations, Has.Count.EqualTo(1));
            MiniJsonObject registered = Parse(core.Registrations[0]);
            Assert.That(((MiniJsonObject)registered.Get("body")).Get("floor_footprint_radius_m"), Is.EqualTo(0.25));
            Assert.That(((List<object>)registered.Get("anchors")).Count, Is.EqualTo(1), "the root only");
            Assert.That(driver.EmbodimentRegistered, Is.False, "no anchor is driven from a bone");
        }

        // A bone sampled in the Head joint's frame and a target written to the tracked head INPUT
        // (the retarget head) in another frame would not close a loop. The head is therefore registered as a legacy anchor: no bone, no
        // offset, published and followed through its own port exactly as without a registration; pelvis and hands stay bone-bound.
        [Test]
        public void AnchorDrivenByTheEntitysInputIsRegisteredAsALegacyAnchorAndNeverFromABone()
        {
            MemorySource source = MemorySource.All();
            source.Files[TestFiles.Structural] = Structural.Replace(
                "{\"name\":\"head\",\"bone\":\"Head\",\"families\":[\"Facing\"],\"offset_from\":\"measurement\"}",
                "{\"name\":\"head\",\"bone\":\"Head\",\"families\":[\"Facing\"],\"driven_by\":\"entity_input\"}");
            Assert.That(source.Files[TestFiles.Structural], Does.Contain("entity_input"));
            Assert.That(EmbodimentSpecLoader.TryBuild(source, TestFiles, out EmbodimentSpec spec, out string report), Is.True, report);
            Assert.That(spec.Anchors.Select(a => (a.Name, a.BoneName)), Is.EqualTo(new[] { ("root", (string)null), ("head", (string)null), ("pelvis", "Hips") }));
            Assert.That(spec.BoneBoundCount, Is.EqualTo(1));
            var head = ((List<object>)Parse(spec.Json).Get("anchors")).Cast<MiniJsonObject>().Single(a => (string)a.Get("name") == "head");
            Assert.That(head.TryGet("bone", out object bone) && bone == null, Is.True, "bone is null in the core's spec");
            Assert.That(head.TryGet("offset", out _), Is.False);
            Assert.That(head.TryGet("provenance", out _), Is.False);
            Assert.That(head.TryGet("driven_by", out _), Is.False, "the member is ours, not the core's schema");

            var core = new RegistrationCore();
            BoneEntity entity = NewEntity();
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, source);
            Assert.That(driver.EmbodimentRegistered, Is.True, "the pelvis is bone-bound");
            List<object> registered = (List<object>)Parse(core.Registrations[0]).Get("anchors");
            var registeredHead = registered.Cast<MiniJsonObject>().Single(a => (string)a.Get("name") == "head");
            Assert.That(registeredHead.TryGet("bone", out object registeredBone) && registeredBone == null, Is.True,
                "the head the core is told about is not bone-bound");
        }

        [Test]
        public void ACoreWithoutRegistrationRegistersNothing()
        {
            var core = new RegistrationCore { Result = EmbodimentRegistrationResult.Unsupported };
            BoneEntity entity = NewEntity();
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, MemorySource.All());
            Assert.That(core.Registrations, Has.Count.EqualTo(1));
            Assert.That(driver.EmbodimentRegistered, Is.False);
        }

        [Test]
        public void ARefusalIsLoggedWithTheCoresOwnText()
        {
            var core = new RegistrationCore { Result = EmbodimentRegistrationResult.Refused, RefusalText = "anchor 'pelvis': a non-identity offset needs provenance.procedure" };
            BoneEntity entity = NewEntity();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("refused the embodiment.*non-identity offset needs provenance"));
            var driver = new OvrAvatarEmbodiedDriver(entity, Actor, core, EmbodiedDriverConfig.Default, MemorySource.All());
            Assert.That(driver.EmbodimentRegistered, Is.False);
        }

        // ------------------------------------------------------------------ the oracle (independent of EmbodimentFrames' matrices)

        // R_core = P R_u P^T K with P (x, z, y) and K = [[0,1,0],[-1,0,0],[0,0,1]] written out (the glTF basis, SDK z reflection).
        private static readonly double[,] OracleP = { { 1, 0, 0 }, { 0, 0, 1 }, { 0, 1, 0 } };
        private static readonly double[,] OracleK = { { 0, 1, 0 }, { -1, 0, 0 }, { 0, 0, 1 } };

        private static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; ++i) for (int j = 0; j < 3; ++j) for (int k = 0; k < 3; ++k) r[i, j] += a[i, k] * b[k, j];
            return r;
        }

        private static double[,] T(double[,] a)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; ++i) for (int j = 0; j < 3; ++j) r[i, j] = a[j, i];
            return r;
        }

        private static double[,] MatrixOf(Quaternion q)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w, n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n; y /= n; z /= n; w /= n;
            return new[,]
            {
                { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
                { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
                { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) },
            };
        }

        private static double[] QuaternionOf(double[,] m)
        {
            // From the largest diagonal, a different branch structure than the production code's.
            double[] d = { m[0, 0] + m[1, 1] + m[2, 2], m[0, 0] - m[1, 1] - m[2, 2], -m[0, 0] + m[1, 1] - m[2, 2], -m[0, 0] - m[1, 1] + m[2, 2] };
            int best = Array.IndexOf(d, d.Max());
            double s = Math.Sqrt(1 + d[best]) * 2;
            double[] q = new double[4];
            switch (best)
            {
                case 0: q[3] = s / 4; q[0] = (m[2, 1] - m[1, 2]) / s; q[1] = (m[0, 2] - m[2, 0]) / s; q[2] = (m[1, 0] - m[0, 1]) / s; break;
                case 1: q[0] = s / 4; q[3] = (m[2, 1] - m[1, 2]) / s; q[1] = (m[0, 1] + m[1, 0]) / s; q[2] = (m[0, 2] + m[2, 0]) / s; break;
                case 2: q[1] = s / 4; q[3] = (m[0, 2] - m[2, 0]) / s; q[0] = (m[0, 1] + m[1, 0]) / s; q[2] = (m[1, 2] + m[2, 1]) / s; break;
                default: q[2] = s / 4; q[3] = (m[1, 0] - m[0, 1]) / s; q[0] = (m[0, 2] + m[2, 0]) / s; q[1] = (m[1, 2] + m[2, 1]) / s; break;
            }
            return q;
        }

        private static EmbodiedBonePose OracleToCore(Vector3 position, Quaternion rotation)
        {
            double[,] r = Mul(Mul(Mul(OracleP, MatrixOf(rotation)), T(OracleP)), OracleK);
            double[] q = QuaternionOf(r);
            return new EmbodiedBonePose(position.x, position.z, position.y, q[0], q[1], q[2], q[3]);
        }

        private static void OracleFromCore(in EmbodiedBonePose core, out Vector3 position, out Quaternion rotation)
        {
            double[,] rc = MatrixOf(new Quaternion((float)core.Qx, (float)core.Qy, (float)core.Qz, (float)core.Qw));
            double[,] ru = Mul(Mul(Mul(T(OracleP), rc), T(OracleK)), OracleP);
            double[] q = QuaternionOf(ru);
            position = new Vector3((float)core.X, (float)core.Z, (float)core.Y);
            rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        }

        private static double QuatAngleDegrees(in EmbodiedBonePose a, in EmbodiedBonePose b)
        {
            double dot = Math.Abs(a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw);
            return 2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
        }
    }
}
#endif
