// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// What the canned "server" answers to a Segment request on the sample photo: the real SAM3 mask of the vase (generated offline, see
    /// ASSETS.md). It answers as the server does for click points: the object the points are on, as a mask the size of the image. Points on the vase
    /// give the vase's mask (any one point inside is enough, so a second tap elsewhere refines nothing and breaks nothing); points all
    /// outside it select no object, which the server reports as a failed Segment: the panel then shows no overlay and holds no mask, so
    /// "Reconstruct 3D" asks for a tap on the object first.
    /// </summary>
    public sealed class CannedSegmenter
    {
        private readonly byte[] _mask;

        public CannedSegmenter(byte[] mask, int width, int height)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (width <= 0 || height <= 0 || (long)width * height != mask.Length)
                throw new ArgumentException("The mask must have one byte per pixel of a " + width + " x " + height + " image.", nameof(mask));
            _mask = mask;
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>The bundled sample mask, or null when the file is missing or the wrong size.</summary>
        public static CannedSegmenter LoadSample()
        {
            byte[] mask = TutorialAssets.LoadSampleMask();
            if (mask == null || mask.Length != TutorialAssets.SampleImageWidth * TutorialAssets.SampleImageHeight) return null;
            return new CannedSegmenter(mask, TutorialAssets.SampleImageWidth, TutorialAssets.SampleImageHeight);
        }

        /// <summary>Whether an image point (x right, y down from the top-left, in pixels) is on the vase.</summary>
        public bool IsInside(ScanPoint point)
        {
            int x = (int)point.X, y = (int)point.Y;
            return x >= 0 && y >= 0 && x < Width && y < Height && _mask[y * Width + x] != 0;
        }

        /// <summary>
        /// The mask bytes for this prompt (a copy the caller may keep), or null when it selects nothing: a positive click on the
        /// vase, or a box (corners labelled z = 2 and 3, as the real server reads them) that holds at least half of the vase,
        /// selects it. Negative clicks are ignored (the sample photo has one object).
        /// </summary>
        public byte[] Answer(ScanPoint[] points) => Answer(points, out _);

        /// <summary>As <see cref="Answer(ScanPoint[])"/>; <paramref name="usedBox"/> says whether the prompt carried a box.</summary>
        public byte[] Answer(ScanPoint[] points, out bool usedBox)
        {
            usedBox = false;
            if (points == null) return null;
            ScanPoint? topLeft = null, bottomRight = null;
            foreach (ScanPoint point in points)
            {
                if (point.Label == ScanPointLabel.BoxTopLeft) topLeft = point;
                else if (point.Label == ScanPointLabel.BoxBottomRight) bottomRight = point;
            }
            if (topLeft.HasValue && bottomRight.HasValue)
            {
                usedBox = true;
                var box = new ScanPixelBox(topLeft.Value.X, topLeft.Value.Y, bottomRight.Value.X, bottomRight.Value.Y);
                if (BoxHolds(box, 0.5)) return (byte[])_mask.Clone();
            }
            foreach (ScanPoint point in points)
                if (point.Label == ScanPointLabel.Positive && IsInside(point)) return (byte[])_mask.Clone();
            return null;
        }

        /// <summary>Whether at least <paramref name="fraction"/> of the vase's pixels lie in the box.</summary>
        public bool BoxHolds(ScanPixelBox box, double fraction)
        {
            long total = 0, inside = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    if (_mask[y * Width + x] == 0) continue;
                    total++;
                    if (box.Contains(x + 0.5, y + 0.5)) inside++;
                }
            return total > 0 && inside >= fraction * total;
        }
    }

    /// <summary>
    /// The scan session library with nobody on the other end: the lobby tutorial's canned scan. A request is accepted, waits a
    /// short fake time, and then "the server" writes the bundled sample model where a real server would write its reconstruction
    /// and reports success. Nothing leaves the process. The real scan service is only used when one is already running
    /// (see <see cref="TutorialScanBackend"/>).
    /// </summary>
    public sealed class CannedScanTransport : IScanSessionTransport
    {
        /// <summary>Never dialled: <see cref="Connect"/> accepts it. It only has to be a valid host:port for the config.</summary>
        public const string Address = "tutorial.local:1";
        public const double DefaultDelaySeconds = 3.0;
        /// <summary>A mask comes back much faster than a reconstruction, as it does from the real server.</summary>
        public const double DefaultSegmentDelaySeconds = 0.8;

        private sealed class Job
        {
            public long Session;
            public long Event;
            public string OutputPath;
            public double DueAt;
            public ScanKind Kind;
            public ScanPoint[] Points;
        }

        private readonly ITutorialClock _clock;
        private readonly Func<byte[]> _modelBytes;
        private CannedSegmenter _segmenter;
        private readonly Action<string, byte[]> _writeFile;
        private readonly List<Job> _jobs = new List<Job>();
        private readonly Dictionary<long, Action<ScanCompletion>> _callbacks = new Dictionary<long, Action<ScanCompletion>>();
        private long _nextSession = 7000;
        private long _nextEvent = 9000;

        public CannedScanTransport(ITutorialClock clock, Func<byte[]> modelBytes, Action<string, byte[]> writeFile = null,
                                   double delaySeconds = DefaultDelaySeconds,
                                   CannedSegmenter segmenter = null)
        {
            _segmenter = segmenter;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _modelBytes = modelBytes ?? throw new ArgumentNullException(nameof(modelBytes));
            _writeFile = writeFile ?? WriteToDisk;
            DelaySeconds = delaySeconds;
        }

        /// <summary>The masks this server answers with; the tutorial's hologram capture sets the one rendered with each photo.</summary>
        public CannedSegmenter Segmenter { get => _segmenter; set => _segmenter = value; }

        /// <summary>The fake wait between the request and the result.</summary>
        public double DelaySeconds { get; set; }
        /// <summary>The fake wait before a Segment request is answered.</summary>
        public double SegmentDelaySeconds { get; set; } = DefaultSegmentDelaySeconds;
        /// <summary>The next reconstruction fails (the server "could not build it"); used to show the failure message. A Segment request is never the one that fails.</summary>
        public bool FailNext { get; set; }
        public int Submitted { get; private set; }
        public int SegmentsSubmitted { get; private set; }
        public int PendingCount => _jobs.Count;
        public int Connects { get; private set; }

        /// <summary>
        /// The config the canned service starts with. Both addresses are set, so the service offers the same two-step pipeline as a real
        /// primary server: Segment on every tap, then Reconstruct from the photo and the mask.
        /// </summary>
        public static ScanSessionConfig MakeConfig(string outputDirectory) => new ScanSessionConfig
        {
            ServerAddress = Address,
            FallbackServerAddress = Address,
            ClientId = "tutorial",
            CacheDirectory = outputDirectory,
            OutputDirectory = outputDirectory,
            ReconstructTimeoutSeconds = 60.0,
        };

        public long CreateSession(ScanKind kind) => _nextSession++;

        public bool RegisterCompletion(long session, Action<ScanCompletion> onCompletion)
        {
            if (onCompletion == null) return false;
            _callbacks[session] = onCompletion;
            return true;
        }

        public bool Connect(long session, string address, bool useTls, out string error)
        {
            Connects++;
            error = null;
            return true;
        }

        public long Submit(long session, ScanTransportRequest request)
        {
            if (request == null) return 0;
            Submitted++;
            bool segment = request.Kind == ScanKind.Segment;
            if (segment) SegmentsSubmitted++;
            long handle = _nextEvent++;
            _jobs.Add(new Job
            {
                Session = session, Event = handle, OutputPath = request.OutputPath, Kind = request.Kind, Points = request.Points,
                DueAt = _clock.Now + (segment ? SegmentDelaySeconds : DelaySeconds),
            });
            return handle;
        }

        public void DestroySession(long session)
        {
            _callbacks.Remove(session);
            _jobs.RemoveAll(job => job.Session == session);
        }

        /// <summary>Call every frame: finishes the requests whose fake wait is over.</summary>
        public void Tick()
        {
            if (_jobs.Count == 0) return;
            double now = _clock.Now;
            for (int i = 0; i < _jobs.Count; i++)
            {
                Job job = _jobs[i];
                if (job.DueAt > now) continue;
                _jobs.RemoveAt(i--);
                Finish(job);
            }
        }

        private void Finish(Job job)
        {
            if (!_callbacks.TryGetValue(job.Session, out Action<ScanCompletion> callback)) return;
            if (job.Kind == ScanKind.Segment)
            {
                FinishSegment(job, callback);
                return;
            }
            if (FailNext)
            {
                FailNext = false;
                callback(new ScanCompletion(job.Session, job.Event, false, "The sample server could not build this one."));
                return;
            }
            try
            {
                _writeFile(job.OutputPath + ScanResponseParser.ModelSuffix, _modelBytes());
            }
            catch (Exception e)
            {
                callback(new ScanCompletion(job.Session, job.Event, false, "The sample model could not be written: " + e.Message));
                return;
            }
            callback(new ScanCompletion(job.Session, job.Event, true, string.Empty));
        }

        private void FinishSegment(Job job, Action<ScanCompletion> callback)
        {
            if (_segmenter == null)
            {
                callback(new ScanCompletion(job.Session, job.Event, false, "The sample server has no mask for this photo."));
                return;
            }
            byte[] mask = _segmenter.Answer(job.Points, out bool usedBox);
            if (mask == null)
            {
                callback(new ScanCompletion(job.Session, job.Event, false,
                    usedBox ? "No object was found in that box." : "No object was found at those points."));
                return;
            }
            try
            {
                _writeFile(job.OutputPath, mask);
            }
            catch (Exception e)
            {
                callback(new ScanCompletion(job.Session, job.Event, false, "The sample mask could not be written: " + e.Message));
                return;
            }
            // The real server's marker: the canned server always honours a box.
            callback(new ScanCompletion(job.Session, job.Event, true, usedBox ? "Segment OK (sample) | box:1" : "Segment OK (sample) | box:0"));
        }

        private static void WriteToDisk(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, bytes);
        }
    }
}
