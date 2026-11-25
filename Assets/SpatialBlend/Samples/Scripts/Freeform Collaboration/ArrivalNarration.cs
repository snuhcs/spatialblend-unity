// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Samples
{
    /// <summary>
    /// One spoken line for arrivals, the same caption channel as the other short notices. A person joining and a shared
    /// object each have a line. Several objects in one moment are one line, and nothing new is said again until that
    /// line has finished. A person who arrives while an object line is up is said once it ends.
    /// </summary>
    public sealed class ArrivalNarration
    {
        /// <summary>How long one line holds the channel, matching the caption it is shown in.</summary>
        public const float HoldSeconds = 3f;

        public const string PersonLine = "Someone joined.";
        public const string ObjectLine = "A new object arrived. Hover it to place it.";
        public const string ObjectsLine = "New objects arrived. Hover one to place it.";
        public const string PersonAndObjectLine = "Someone joined, and a new object arrived. Hover it to place it.";
        public const string PersonAndObjectsLine = "Someone joined, and new objects arrived. Hover one to place it.";

        public const string PersonClip = "a_person";
        public const string ObjectClip = "a_object";
        public const string ObjectsClip = "a_objects";
        public const string PersonAndObjectClip = "a_person_object";
        public const string PersonAndObjectsClip = "a_person_objects";

        /// <summary>The ElevenLabs clip for <paramref name="line"/>, under TutorialAssets/Arrival. Null for an unknown line.</summary>
        public static string ClipFor(string line)
        {
            if (line == PersonLine) return PersonClip;
            if (line == ObjectLine) return ObjectClip;
            if (line == ObjectsLine) return ObjectsClip;
            if (line == PersonAndObjectLine) return PersonAndObjectClip;
            if (line == PersonAndObjectsLine) return PersonAndObjectsClip;
            return null;
        }

        private bool _person;
        private int _objects;
        private bool _saidPerson;
        private bool _saidObjects;
        private float _holdUntil = -1f;

        public void NotePerson() => _person = true;

        public void NoteObject() => _objects++;

        /// <summary>The line to say now, or null when there is nothing new to say.</summary>
        public string Flush(float now)
        {
            if (_holdUntil >= 0f && now < _holdUntil)
            {
                if (_saidPerson) _person = false;
                if (_saidObjects) _objects = 0;
                return null;
            }

            _saidPerson = false;
            _saidObjects = false;
            if (!_person && _objects == 0)
            {
                _holdUntil = -1f;
                return null;
            }

            string line = Compose(_person, _objects);
            _saidPerson = _person;
            _saidObjects = _objects > 0;
            _person = false;
            _objects = 0;
            _holdUntil = now + HoldSeconds;
            return line;
        }

        /// <summary>The one line for this burst. Neither flag and no objects is null.</summary>
        public static string Compose(bool person, int objects)
        {
            if (person && objects > 1) return PersonAndObjectsLine;
            if (person && objects == 1) return PersonAndObjectLine;
            if (person) return PersonLine;
            if (objects > 1) return ObjectsLine;
            if (objects == 1) return ObjectLine;
            return null;
        }
    }
}
