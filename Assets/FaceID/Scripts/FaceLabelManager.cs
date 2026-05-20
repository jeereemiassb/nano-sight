// Copyright (c) NanoSight.
//
// Owns the pool of FaceLabel panels and the tracked-face -> label mapping.

using System.Collections.Generic;
using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Owns the pool of <see cref="FaceLabel"/> panels and the mapping between a tracked-face id
    /// and its on-screen label. Guarantees one label per identity (no duplicates), recycles
    /// panels through an object pool, and runs a backstop cleanup that hides any label whose
    /// face stopped being reported for too long.
    ///
    /// <see cref="FaceIdentifier"/> drives it: it calls <see cref="ShowLabel"/> when a face is
    /// identified, <see cref="UpdateLabelTarget"/> every detection pass while it stays visible,
    /// and <see cref="RemoveLabel"/> when the tracker reports the face lost.
    /// </summary>
    public class FaceLabelManager : MonoBehaviour
    {
        [Header("Pool")]
        [SerializeField] private FaceLabel m_labelPrefab;

        [Tooltip("How many label instances to pre-instantiate on Awake.")]
        [SerializeField, Range(0, 32)] private int m_initialPoolSize = 8;

        [Tooltip("Optional parent for spawned labels. Leave empty to spawn them at the scene root. " +
                 "Do NOT parent labels under the camera rig — they are positioned directly in world space.")]
        [SerializeField] private Transform m_labelParent;

        [Header("Following")]
        [Tooltip("Camera the labels billboard towards / offset from. Leave empty to use Camera.main.")]
        [SerializeField] private Transform m_cameraTransform;

        [Header("Cleanup")]
        [Tooltip("Backstop: if a label's face has not been refreshed for this long it is hidden. " +
                 "This catches faces that vanish without a clean 'lost' signal from the tracker.")]
        [SerializeField, Range(0.5f, 10f)] private float m_hideAfterUnseenSeconds = 2f;

        // trackId -> the label currently showing that identity.
        private readonly Dictionary<int, FaceLabel> m_activeLabels = new();
        // trackId -> last time the label's target was refreshed (drives the backstop cleanup).
        private readonly Dictionary<int, float> m_lastSeenTime = new();
        private readonly Queue<FaceLabel> m_pool = new();
        // Scratch list reused by the cleanup pass so it doesn't allocate every frame.
        private readonly List<int> m_cleanupScratch = new();

        // HUD mode is applied to every label (pooled + active + newly created).
        private bool m_hudMode;
        private Transform m_hudParent;

        private void Awake()
        {
            if (m_labelPrefab == null)
            {
                Debug.LogError($"[{nameof(FaceLabelManager)}] Label prefab is not assigned.", this);
                enabled = false;
                return;
            }

            for (var i = 0; i < m_initialPoolSize; i++)
                m_pool.Enqueue(CreatePooledLabel());
        }

        /// <summary>
        /// Switches every pooled and active label to head-locked HUD mode (parented to
        /// <paramref name="parent"/>, target interpreted as a local position) or back to
        /// world-anchored mode. Called once by <see cref="FaceIdentifier"/> at startup.
        /// </summary>
        public void SetHudMode(bool enabled, Transform parent)
        {
            m_hudMode = enabled;
            m_hudParent = parent;
            ForEachLabel(l => l.SetHudMode(enabled, parent));
        }

        // ---- Runtime broadcasters used by the in-VR options menu ----

        public void SetNameFontSize(float v) => ForEachLabel(l => l.NameFontSize = v);
        public void SetDetailsFontSize(float v) => ForEachLabel(l => l.DetailsFontSize = v);
        public void SetReferenceDistance(float v) => ForEachLabel(l => l.ReferenceDistance = v);
        public void SetScaleByDistance(bool v) => ForEachLabel(l => l.ScaleByDistance = v);
        public void SetPanelScale(float v) => ForEachLabel(l => l.PanelScale = v);

        /// <summary>
        /// Returns the current name font size from the prefab. Used to seed the in-VR options
        /// menu so the slider starts at the value the labels are actually using.
        /// </summary>
        public float CurrentNameFontSize => m_labelPrefab != null ? m_labelPrefab.NameFontSize : 18f;
        public float CurrentDetailsFontSize => m_labelPrefab != null ? m_labelPrefab.DetailsFontSize : 11f;
        public float CurrentReferenceDistance => m_labelPrefab != null ? m_labelPrefab.ReferenceDistance : 1f;
        public bool CurrentScaleByDistance => m_labelPrefab != null && m_labelPrefab.ScaleByDistance;
        public float CurrentPanelScale => m_labelPrefab != null ? m_labelPrefab.PanelScale : 1f;

        private void ForEachLabel(System.Action<FaceLabel> fn)
        {
            foreach (var l in m_activeLabels.Values)
                if (l != null) fn(l);
            foreach (var l in m_pool)
                if (l != null) fn(l);
        }

        /// <summary>
        /// Shows — or updates, if it already exists — the label for <paramref name="trackId"/>.
        /// Calling this more than once for the same id never spawns a duplicate. The optional
        /// <paramref name="infoText"/> is the multi-line description the server included in its
        /// JSON response under "info_text" — rendered verbatim under the name.
        /// </summary>
        public void ShowLabel(int trackId, string displayName, float confidence,
                              Vector3 worldPosition, string infoText = null)
        {
            if (!m_activeLabels.TryGetValue(trackId, out var label))
            {
                label = GetPooledLabel();
                label.BoundTrackId = trackId;
                m_activeLabels[trackId] = label;
            }

            label.SetTarget(worldPosition);
            label.Show(displayName, confidence, infoText);
            m_lastSeenTime[trackId] = Time.time;
        }

        /// <summary>
        /// Refreshes the world-space target of an existing label — called every detection pass
        /// while the face is still visible. No-op if the id has no active label yet.
        /// </summary>
        public void UpdateLabelTarget(int trackId, Vector3 worldPosition)
        {
            if (!m_activeLabels.TryGetValue(trackId, out var label))
                return;

            label.SetTarget(worldPosition);
            m_lastSeenTime[trackId] = Time.time;
        }

        /// <summary>True if a label is currently bound to <paramref name="trackId"/>.</summary>
        public bool HasLabel(int trackId) => m_activeLabels.ContainsKey(trackId);

        /// <summary>Hides the label bound to <paramref name="trackId"/> and returns it to the pool.</summary>
        public void RemoveLabel(int trackId)
        {
            if (!m_activeLabels.TryGetValue(trackId, out var label))
                return;

            m_activeLabels.Remove(trackId);
            m_lastSeenTime.Remove(trackId);

            label.Hide(); // fades out, then deactivates itself
            m_pool.Enqueue(label);
        }

        private void Update()
        {
            // Backstop cleanup: hide labels whose face stopped being refreshed a while ago.
            var now = Time.time;
            m_cleanupScratch.Clear();
            foreach (var pair in m_lastSeenTime)
            {
                if (now - pair.Value > m_hideAfterUnseenSeconds)
                    m_cleanupScratch.Add(pair.Key);
            }

            foreach (var trackId in m_cleanupScratch)
                RemoveLabel(trackId);
        }

        private FaceLabel GetPooledLabel()
        {
            FaceLabel label = null;
            // Skip pooled instances that were destroyed out from under us (e.g. on scene reload).
            while (m_pool.Count > 0 && label == null)
                label = m_pool.Dequeue();

            label ??= CreatePooledLabel();

            label.ResetForPool();              // clears stale state, leaves it deactivated
            label.SetCamera(m_cameraTransform);
            if (m_hudMode)
                label.SetHudMode(true, m_hudParent);
            return label;
        }

        private FaceLabel CreatePooledLabel()
        {
            var label = Instantiate(m_labelPrefab, m_labelParent);
            label.name = $"{m_labelPrefab.name} (pooled)";
            label.SetCamera(m_cameraTransform);
            if (m_hudMode)
                label.SetHudMode(true, m_hudParent);
            label.ResetForPool();
            return label;
        }
    }
}
