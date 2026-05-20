// Copyright (c) NanoSight.
//
// In-process ring buffer of face-identification server requests. Read by the wrist menu.

using System;
using System.Collections.Generic;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Static, in-memory log of every call to <see cref="FaceServerClient.IdentifyAsync"/>.
    /// Used by the wrist menu HUD to surface a request history without coupling networking
    /// code to any UI. Keeps the last <see cref="Capacity"/> entries; older ones are dropped.
    /// </summary>
    public static class ServerRequestLog
    {
        public const int Capacity = 50;

        /// <summary>Outcome bucket for a single request — drives the icon/colour in the UI.</summary>
        public enum Status
        {
            Recognised,    // server returned a name + confident enough to display
            Unknown,       // server answered OK but the face is not in the database
            Error,         // network error, timeout, parse failure, etc.
        }

        public readonly struct Entry
        {
            public readonly DateTime TimeUtc;
            public readonly int TrackId;
            public readonly Status Outcome;
            public readonly string Name;
            public readonly float Confidence;
            public readonly float LatencyMs;
            public readonly string Detail;

            public Entry(DateTime timeUtc, int trackId, Status outcome, string name,
                         float confidence, float latencyMs, string detail)
            {
                TimeUtc = timeUtc;
                TrackId = trackId;
                Outcome = outcome;
                Name = name;
                Confidence = confidence;
                LatencyMs = latencyMs;
                Detail = detail;
            }
        }

        private static readonly LinkedList<Entry> s_entries = new();
        private static readonly object s_lock = new();

        /// <summary>Fired AFTER an entry is appended. Subscribers read <see cref="Snapshot"/>.</summary>
        public static event Action OnEntryAdded;

        public static void Record(Entry entry)
        {
            lock (s_lock)
            {
                s_entries.AddFirst(entry);
                while (s_entries.Count > Capacity)
                    s_entries.RemoveLast();
            }
            OnEntryAdded?.Invoke();
        }

        /// <summary>Newest-first snapshot. Safe to enumerate from the main thread.</summary>
        public static Entry[] Snapshot()
        {
            lock (s_lock)
            {
                var arr = new Entry[s_entries.Count];
                var i = 0;
                foreach (var e in s_entries) arr[i++] = e;
                return arr;
            }
        }

        public static void Clear()
        {
            lock (s_lock) s_entries.Clear();
            OnEntryAdded?.Invoke();
        }
    }
}
