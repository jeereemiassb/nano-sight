// Copyright (c) NanoSight.
//
// Behaviour for a single floating identity panel (lives on the FaceLabel prefab).

using System.Collections;
using TMPro;
using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Drives one floating identity panel. Responsibilities:
    ///  - <see cref="Show"/> / <see cref="Hide"/> set its content and animate visibility (fade).
    ///  - Follows a world-space target (the real face) with a smoothed, camera-relative offset.
    ///  - Billboards toward the camera every LateUpdate so the panel stays readable.
    ///
    /// It knows nothing about detection, tracking or networking — <see cref="FaceLabelManager"/>
    /// owns the pool and feeds each label its target position and identity.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class FaceLabel : MonoBehaviour
    {
        [Header("UI references")]
        [SerializeField] private TMP_Text m_nameText;
        [SerializeField] private TMP_Text m_confidenceText;
        [SerializeField] private CanvasGroup m_canvasGroup;

        [Header("Placement")]
        [Tooltip("Offset from the face in CAMERA-relative space (x = right, y = up, z = forward), in metres. " +
                 "Default places the panel 0.3 m to the viewer's right and 0.1 m above the face.")]
        [SerializeField] private Vector3 m_worldOffset = new Vector3(0.3f, 0.1f, 0f);

        [Tooltip("How quickly the panel chases its target position. Higher = snappier, lower = smoother.")]
        [SerializeField, Range(1f, 30f)] private float m_positionLerpSpeed = 8f;

        [Tooltip("If enabled, the panel rotates every frame so its front face looks back at the camera.")]
        [SerializeField] private bool m_billboard = true;

        [Header("Animation")]
        [Tooltip("Duration of the fade-in (Show) and fade-out (Hide) animations, in seconds.")]
        [SerializeField, Range(0f, 2f)] private float m_fadeDuration = 0.35f;

        private Transform m_cameraTransform;
        private Vector3 m_targetPosition;
        private bool m_hasTarget;
        private Coroutine m_fadeRoutine;
        // When HUD mode is on the label is parented to m_hudParent and m_targetPosition is
        // interpreted as a LOCAL position in that parent's frame (not a world position).
        private bool m_hudMode;
        private Transform m_hudParent;

        /// <summary>Id of the tracked face this label is bound to (-1 while idle / pooled). Set by the manager.</summary>
        public int BoundTrackId { get; set; } = -1;

        private void Awake()
        {
            if (m_canvasGroup == null)
                m_canvasGroup = GetComponent<CanvasGroup>();
            m_canvasGroup.alpha = 0f;
        }

        /// <summary>Injects the camera the panel should follow / face. Falls back to Camera.main when never set.</summary>
        public void SetCamera(Transform cameraTransform) => m_cameraTransform = cameraTransform;

        /// <summary>
        /// Switches the label to head-locked HUD layout: parented to <paramref name="parent"/>
        /// (the eye camera) with its target interpreted in that parent's LOCAL space — so the
        /// label rides with the head and depth becomes irrelevant. Pass enabled=false to revert
        /// to world-anchored mode.
        /// </summary>
        public void SetHudMode(bool enabled, Transform parent)
        {
            m_hudMode = enabled;
            m_hudParent = parent;
            transform.SetParent(enabled ? parent : null, worldPositionStays: false);
            if (enabled)
            {
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            }
            // Force a snap on the next SetTarget so the label doesn't slide across modes.
            m_hasTarget = false;
        }

        /// <summary>
        /// Updates the world-space position of the real face this label annotates. The label
        /// smoothly chases this target; the very first call snaps into place so the panel does
        /// not visibly fly in from the world origin.
        /// </summary>
        public void SetTarget(Vector3 position)
        {
            // 'position' is a world coordinate in world-anchored mode, or a local coordinate
            // relative to m_hudParent in HUD mode. The caller (FaceIdentifier) decides.
            m_targetPosition = position;
            if (!m_hasTarget)
            {
                m_hasTarget = true;
                if (m_hudMode)
                    transform.localPosition = ComputeDesiredLocalPosition();
                else
                    transform.position = ComputeDesiredPosition();
            }
        }

        /// <summary>Fills the panel texts and fades it in. Position comes from <see cref="SetTarget"/>.</summary>
        public void Show(string displayName, float confidence)
        {
            gameObject.SetActive(true);

            if (m_nameText != null)
                m_nameText.text = string.IsNullOrEmpty(displayName) ? "—" : displayName;
            if (m_confidenceText != null)
                m_confidenceText.text = $"{Mathf.RoundToInt(Mathf.Clamp01(confidence) * 100f)}%";

            StartFade(1f, deactivateWhenDone: false);
        }

        /// <summary>Fades the label out, then deactivates the GameObject so it can return to the pool.</summary>
        public void Hide()
        {
            BoundTrackId = -1;
            if (isActiveAndEnabled)
                StartFade(0f, deactivateWhenDone: true);
            else
                gameObject.SetActive(false);
        }

        /// <summary>Hard reset of transient state before the label is reused from the pool.</summary>
        public void ResetForPool()
        {
            BoundTrackId = -1;
            m_hasTarget = false;
            if (m_fadeRoutine != null)
            {
                StopCoroutine(m_fadeRoutine);
                m_fadeRoutine = null;
            }
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = 0f;
            gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!m_hasTarget)
                return;

            var t = 1f - Mathf.Exp(-m_positionLerpSpeed * Time.deltaTime);

            if (m_hudMode)
            {
                // Head-locked: chase the target in the parent's LOCAL space. The label inherits
                // the eye's orientation through its parent, so no billboard is needed.
                var localDesired = ComputeDesiredLocalPosition();
                transform.localPosition = Vector3.Lerp(transform.localPosition, localDesired, t);
                transform.localRotation = Quaternion.identity;
                return;
            }

            // World-anchored: chase a world position and billboard back at the camera.
            var desired = ComputeDesiredPosition();
            transform.position = Vector3.Lerp(transform.position, desired, t);
            if (m_billboard)
                ApplyBillboard();
        }

        /// <summary>Target face position plus the camera-relative offset (right / up / forward).</summary>
        private Vector3 ComputeDesiredPosition()
        {
            var cam = ResolveCamera();
            if (cam == null)
                return m_targetPosition + m_worldOffset;

            return m_targetPosition
                   + cam.right * m_worldOffset.x
                   + cam.up * m_worldOffset.y
                   + cam.forward * m_worldOffset.z;
        }

        /// <summary>HUD-mode target: a local position with the offset applied directly in the
        /// parent's local frame (which already aligns x=right, y=up, z=forward with the camera).</summary>
        private Vector3 ComputeDesiredLocalPosition()
        {
            return m_targetPosition + m_worldOffset;
        }

        /// <summary>Rotates the panel so its front face looks back at the camera.</summary>
        private void ApplyBillboard()
        {
            var cam = ResolveCamera();
            if (cam == null)
                return;

            // World-space UI faces along +Z, so +Z must point AWAY from the camera for the
            // front of the panel to be visible to the viewer.
            var awayFromCamera = transform.position - cam.position;
            if (awayFromCamera.sqrMagnitude < 1e-6f)
                return;

            transform.rotation = Quaternion.LookRotation(awayFromCamera, cam.up);
        }

        private Transform ResolveCamera()
        {
            if (m_cameraTransform != null)
                return m_cameraTransform;
            var main = Camera.main;
            return main != null ? main.transform : null;
        }

        private void StartFade(float targetAlpha, bool deactivateWhenDone)
        {
            if (m_fadeRoutine != null)
                StopCoroutine(m_fadeRoutine);
            m_fadeRoutine = StartCoroutine(FadeRoutine(targetAlpha, deactivateWhenDone));
        }

        private IEnumerator FadeRoutine(float targetAlpha, bool deactivateWhenDone)
        {
            var startAlpha = m_canvasGroup != null ? m_canvasGroup.alpha : 1f;

            if (m_fadeDuration > 0f && m_canvasGroup != null)
            {
                var elapsed = 0f;
                while (elapsed < m_fadeDuration)
                {
                    elapsed += Time.deltaTime;
                    m_canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / m_fadeDuration);
                    yield return null;
                }
            }

            if (m_canvasGroup != null)
                m_canvasGroup.alpha = targetAlpha;

            m_fadeRoutine = null;
            if (deactivateWhenDone)
                gameObject.SetActive(false);
        }
    }
}
