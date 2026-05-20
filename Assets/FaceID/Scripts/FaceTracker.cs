// Copyright (c) NanoSight.
//
// Lightweight IoU face tracker: gives each physical face a stable id across detection frames.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// A single face followed across detection frames. Carries the geometric state (the latest
    /// bounding box) AND the identity returned by the server, plus bookkeeping flags so the
    /// server is queried at most once per appearance of the face.
    /// </summary>
    public class TrackedFace
    {
        /// <summary>Stable id, unique for the lifetime of this appearance of the face.</summary>
        public int Id;

        /// <summary>Latest bounding box, normalized image coords (origin top-left, y down, range 0..1).</summary>
        public Rect Box;

        /// <summary>Time (Time.time) at which <see cref="Box"/> was last refreshed by a detection.</summary>
        public float LastSeenTime;

        /// <summary>True once an identification request has been started for this track (prevents re-querying).</summary>
        public bool IdentificationRequested;

        /// <summary>Name assigned by the server. Null until a successful response arrives.</summary>
        public string Name;

        /// <summary>Confidence reported by the server alongside <see cref="Name"/>.</summary>
        public float Confidence;
    }

    /// <summary>
    /// Lightweight per-frame face tracker based on Intersection-over-Union between the boxes of
    /// consecutive detection passes. Its only job is to give each physical face a STABLE id, so
    /// the rest of the pipeline can query the identification server once when a face appears
    /// (instead of every detection frame) and drop its label once the face is gone.
    ///
    /// It is a plain [Serializable] class (not a MonoBehaviour) so its tunables show up nested in
    /// the <see cref="FaceIdentifier"/> Inspector.
    /// </summary>
    [Serializable]
    public class FaceTracker
    {
        [Tooltip("Minimum IoU between a new detection and an existing track for them to count as the same face. " +
                 "Lower = more tolerant to fast head/face motion (avoids spawning duplicate tracks).")]
        [SerializeField, Range(0.05f, 0.9f)] private float m_iouMatchThreshold = 0.1f;

        [Tooltip("How long a track survives without a matching detection before it is declared lost. " +
                 "Short value = ghost boxes from quick motion fade fast; long value = anti-flicker grace.")]
        [SerializeField, Range(0f, 5f)] private float m_lostGraceSeconds = 1.5f;

        [Tooltip("Fallback: cuando el IoU falla (cabeza se movió y los bboxes ya no solapan), se " +
                 "intenta matchear por DISTANCIA entre centroides. Este valor es el máximo " +
                 "permitido en coords de viewport (0..1). 0.20 = 20% del frame: bastante " +
                 "tolerante; 0.10 = más conservador.")]
        [SerializeField, Range(0f, 0.5f)] private float m_centroidFallbackDistance = 0.20f;

        [Tooltip("En el fallback por centroide, la detección candidata no puede ser N veces más " +
                 "grande/pequeña que el track previo (evita confundir caras cercanas al moverse " +
                 "muy rápido). 1.8 = la nueva detección puede ser hasta 80% más grande o pequeña.")]
        [SerializeField, Range(1.1f, 3f)] private float m_maxSizeRatio = 1.8f;

        private readonly List<TrackedFace> m_tracks = new();
        private int m_nextId = 1;

        /// <summary>All faces currently tracked (including ones still inside their lost-grace window).</summary>
        public IReadOnlyList<TrackedFace> Tracks => m_tracks;

        // Runtime-configurable tuning knobs (exposed in the in-VR options menu).
        public float IouMatchThreshold
        {
            get => m_iouMatchThreshold;
            set => m_iouMatchThreshold = Mathf.Clamp(value, 0.05f, 0.9f);
        }
        public float LostGraceSeconds
        {
            get => m_lostGraceSeconds;
            set => m_lostGraceSeconds = Mathf.Clamp(value, 0f, 5f);
        }
        public float CentroidFallbackDistance
        {
            get => m_centroidFallbackDistance;
            set => m_centroidFallbackDistance = Mathf.Clamp(value, 0f, 0.5f);
        }
        public float MaxSizeRatio
        {
            get => m_maxSizeRatio;
            set => m_maxSizeRatio = Mathf.Clamp(value, 1.1f, 3f);
        }

        /// <summary>Raised once, when a brand-new face starts being tracked.</summary>
        public event Action<TrackedFace> TrackCreated;

        /// <summary>Raised once, when a face has been missing for longer than the grace period.</summary>
        public event Action<TrackedFace> TrackLost;

        /// <summary>
        /// Feeds one detection pass into the tracker: matches detections to existing tracks,
        /// spawns tracks for unmatched detections and retires tracks that have gone missing.
        /// Raises <see cref="TrackCreated"/> / <see cref="TrackLost"/> as appropriate.
        /// </summary>
        /// <param name="detections">Boxes from this pass, in normalized image coords.</param>
        /// <param name="now">Current time, typically Time.time.</param>
        public void Update(IReadOnlyList<Rect> detections, float now)
        {
            var detectionMatched = detections.Count > 0 ? new bool[detections.Count] : Array.Empty<bool>();

            // 1a. PASS 1 — IoU matching. Strict, prefers boxes that physically overlap.
            foreach (var track in m_tracks)
            {
                var bestIndex = -1;
                var bestIou = m_iouMatchThreshold; // any match must beat the threshold to count
                for (var i = 0; i < detections.Count; i++)
                {
                    if (detectionMatched[i])
                        continue;

                    var iou = IntersectionOverUnion(track.Box, detections[i]);
                    if (iou >= bestIou)
                    {
                        bestIou = iou;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    track.Box = detections[bestIndex];
                    track.LastSeenTime = now;
                    detectionMatched[bestIndex] = true;
                }
            }

            // 1b. PASS 2 — centroid-distance fallback for tracks that did NOT match by IoU.
            // Catches the case where the head moved fast enough that the new bbox no longer
            // overlaps the previous one, but it's clearly the same face (centroids are close +
            // similar size). Without this fallback the user gets duplicate tracks + spurious
            // server queries every time they look around.
            foreach (var track in m_tracks)
            {
                if (Mathf.Approximately(track.LastSeenTime, now))
                    continue; // already matched in pass 1

                var trackCenter = track.Box.center;
                var trackDiag = track.Box.size.magnitude;
                var bestIndex = -1;
                var bestDist = m_centroidFallbackDistance;
                for (var i = 0; i < detections.Count; i++)
                {
                    if (detectionMatched[i])
                        continue;

                    var detSize = detections[i].size.magnitude;
                    var bigger = Mathf.Max(trackDiag, detSize);
                    var smaller = Mathf.Max(Mathf.Min(trackDiag, detSize), 1e-4f);
                    if (bigger / smaller > m_maxSizeRatio)
                        continue; // size mismatch — probably a different face

                    var d = Vector2.Distance(trackCenter, detections[i].center);
                    if (d <= bestDist)
                    {
                        bestDist = d;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    track.Box = detections[bestIndex];
                    track.LastSeenTime = now;
                    detectionMatched[bestIndex] = true;
                }
            }

            // 2. Every detection that matched no existing track becomes a new track.
            for (var i = 0; i < detections.Count; i++)
            {
                if (detectionMatched[i])
                    continue;

                var track = new TrackedFace
                {
                    Id = m_nextId++,
                    Box = detections[i],
                    LastSeenTime = now,
                };
                m_tracks.Add(track);
                TrackCreated?.Invoke(track);
            }

            // 3. Retire tracks that have not been seen within the grace period.
            //    Iterate backwards so removals don't disturb the loop.
            for (var i = m_tracks.Count - 1; i >= 0; i--)
            {
                var track = m_tracks[i];
                if (now - track.LastSeenTime <= m_lostGraceSeconds)
                    continue;

                m_tracks.RemoveAt(i);
                TrackLost?.Invoke(track);
            }
        }

        /// <summary>Drops every track WITHOUT raising <see cref="TrackLost"/>. Use on teardown.</summary>
        public void Clear() => m_tracks.Clear();

        /// <summary>Standard Intersection-over-Union of two axis-aligned boxes. Returns 0 when disjoint.</summary>
        private static float IntersectionOverUnion(Rect a, Rect b)
        {
            var interWidth = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            var interHeight = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            if (interWidth <= 0f || interHeight <= 0f)
                return 0f;

            var intersection = interWidth * interHeight;
            var union = a.width * a.height + b.width * b.height - intersection;
            return union > 0f ? intersection / union : 0f;
        }
    }
}
