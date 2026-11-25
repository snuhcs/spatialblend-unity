// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>Seconds on a monotonic clock. Injected so the timing is tested without waiting.</summary>
    public interface ITutorialClock
    {
        double Now { get; }
    }

    public sealed class FuncTutorialClock : ITutorialClock
    {
        private readonly Func<double> _now;
        public FuncTutorialClock(Func<double> now) => _now = now ?? throw new ArgumentNullException(nameof(now));
        public double Now => _now();
    }

    /// <summary>
    /// The subtitle and sentence timing of one narration clip. The clip itself is played by an AudioSource; this class only
    /// follows it: the line is cut into sentences, and each sentence owns the share of the clip's length that its characters
    /// make up. It can be paused (the headset lost focus) and holds the work that waits for the end of the current sentence.
    /// </summary>
    public sealed class TutorialNarrator
    {
        private readonly ITutorialClock _clock;
        private readonly List<Action> _atSentenceEnd = new List<Action>();
        private string[] _sentences = Array.Empty<string>();
        private double[] _ends = Array.Empty<double>();
        private double _duration;
        private double _elapsed;
        private double _lastNow;
        private bool _playing;
        private bool _paused;
        private int _lastSentence = -1;
        private int _generation;

        public TutorialNarrator(ITutorialClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        public bool Playing => _playing;
        public bool Paused => _paused;
        public double Duration => _duration;
        public double Elapsed => _elapsed;
        public IReadOnlyList<string> Sentences => _sentences;

        /// <summary>Raised when the subtitle changes: the sentence index, and its text (null when nothing is spoken).</summary>
        public event Action<int, string> SubtitleChanged;
        public event Action Finished;

        /// <summary>The sentence being spoken, or -1 when nothing is.</summary>
        public int SentenceIndex => _playing ? SentenceAt(_elapsed) : -1;
        public string Subtitle => SentenceIndex >= 0 ? _sentences[SentenceIndex] : string.Empty;

        /// <summary>Seconds until the current sentence is over; 0 when nothing is playing.</summary>
        public double SecondsToSentenceEnd => _playing ? Math.Max(0.0, _ends[SentenceAt(_elapsed)] - _elapsed) : 0.0;

        /// <summary>True when no sentence is mid-way: nothing plays, or the clip has ended.</summary>
        public bool AtSentenceEnd => !_playing;

        /// <summary>Splits a line into sentences at ".", "!" and "?" followed by a space or the end. Abbreviations are not special-cased: the lines are ours.</summary>
        public static string[] SplitSentences(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return result.ToArray();
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                bool terminal = c == '.' || c == '!' || c == '?';
                if (!terminal) continue;
                bool end = i == line.Length - 1 || char.IsWhiteSpace(line[i + 1]);
                if (!end) continue;
                string piece = line.Substring(start, i - start + 1).Trim();
                if (piece.Length > 0) result.Add(piece);
                start = i + 1;
            }
            string tail = line.Substring(start).Trim();
            if (tail.Length > 0) result.Add(tail);
            return result.ToArray();
        }

        /// <summary>
        /// How long a line stays up as a subtitle when no narration clip is bundled for it (the public repository ships none):
        /// a reading pace of <see cref="ReadingWordsPerSecond"/> words a second, at least <see cref="MinReadingSeconds"/> and at most
        /// <see cref="MaxReadingSeconds"/>. Zero for an empty line, so an empty line is still "nothing said".
        /// </summary>
        public static double ReadingSeconds(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return 0.0;
            int words = line.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return Math.Min(MaxReadingSeconds, Math.Max(MinReadingSeconds, words / ReadingWordsPerSecond));
        }

        public const double ReadingWordsPerSecond = 2.5;
        public const double MinReadingSeconds = 2.0;
        public const double MaxReadingSeconds = 12.0;

        /// <summary>
        /// The end time of each sentence in a clip of <paramref name="duration"/> seconds, by character share. The last end is
        /// the duration itself.
        /// </summary>
        public static double[] SentenceEnds(IReadOnlyList<string> sentences, double duration)
        {
            var ends = new double[sentences.Count];
            double total = 0;
            for (int i = 0; i < sentences.Count; i++) total += Math.Max(1, sentences[i].Length);
            double running = 0;
            for (int i = 0; i < sentences.Count; i++)
            {
                running += Math.Max(1, sentences[i].Length);
                ends[i] = i == sentences.Count - 1 ? duration : duration * running / total;
            }
            return ends;
        }

        public void Play(string line, double durationSeconds)
        {
            Stop(raiseFinished: false);
            _generation++;
            _sentences = SplitSentences(line);
            _duration = Math.Max(0.0, durationSeconds);
            if (_sentences.Length == 0 || _duration <= 0.0)
            {
                _sentences = Array.Empty<string>();
                _ends = Array.Empty<double>();
                return;
            }
            _ends = SentenceEnds(_sentences, _duration);
            _elapsed = 0;
            _lastNow = _clock.Now;
            _paused = false;
            _playing = true;
            _lastSentence = 0;
            SubtitleChanged?.Invoke(0, _sentences[0]);
        }

        /// <summary>Stops the narration (a frame change); Finished is raised only when the clip ends by itself. Work waiting for a sentence end runs, since nothing is being said any more.</summary>
        public void Stop(bool raiseFinished = false)
        {
            bool was = _playing;
            _playing = false;
            _paused = false;
            _elapsed = 0;
            if (was) SubtitleChanged?.Invoke(-1, null);
            _lastSentence = -1;
            RunWaiting();
            if (was && raiseFinished) Finished?.Invoke();
        }

        public void Pause()
        {
            if (!_playing || _paused) return;
            Advance();
            _paused = true;
        }

        public void Resume()
        {
            if (!_playing || !_paused) return;
            _paused = false;
            _lastNow = _clock.Now;
        }

        /// <summary>Queues work for the end of the current sentence. Runs at once when nothing is playing.</summary>
        public void RunAtSentenceEnd(Action work)
        {
            if (work == null) return;
            if (!_playing) { work(); return; }
            _atSentenceEnd.Add(work);
        }

        /// <summary>Call every frame.</summary>
        public void Tick()
        {
            if (!_playing || _paused) return;
            Advance();
            if (!_playing) return;
            int sentence = SentenceAt(_elapsed);
            if (sentence != _lastSentence)
            {
                // The previous sentence is over: waiting work runs before the new subtitle shows.
                _lastSentence = sentence;
                int generation = _generation;
                RunWaiting();
                // The waiting work can move the tutorial on, which starts another line: this one's subtitle is then stale.
                if (generation != _generation || !_playing) return;
                SubtitleChanged?.Invoke(sentence, _sentences[sentence]);
            }
            if (_elapsed >= _duration) Complete();
        }

        private void Advance()
        {
            double now = _clock.Now;
            _elapsed += Math.Max(0.0, now - _lastNow);
            _lastNow = now;
        }

        private int SentenceAt(double time)
        {
            for (int i = 0; i < _ends.Length; i++) if (time < _ends[i]) return i;
            return _ends.Length - 1;
        }

        private void Complete()
        {
            _playing = false;
            _elapsed = _duration;
            SubtitleChanged?.Invoke(-1, null);
            _lastSentence = -1;
            RunWaiting();
            Finished?.Invoke();
        }

        private void RunWaiting()
        {
            if (_atSentenceEnd.Count == 0) return;
            var work = _atSentenceEnd.ToArray();
            _atSentenceEnd.Clear();
            foreach (var item in work) item();
        }
    }
}
