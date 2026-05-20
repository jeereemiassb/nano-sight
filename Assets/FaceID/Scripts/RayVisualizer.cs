// Copyright (c) NanoSight.
//
// LineRenderer + reticle dot that stops at the AppMenu canvas surface.

using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Visual feedback for the OVR pointer: draws a thin line from a "ray origin" transform up
    /// to the point where it hits a target RectTransform (the AppMenu's world-space canvas),
    /// and places a small dot at the hit point. When the ray misses the canvas, both the line
    /// and the dot are hidden — no clutter outside the menu.
    ///
    /// The target canvas is spawned at runtime by <see cref="AppMenu"/>, so AppMenu calls
    /// <see cref="SetTarget"/> to wire the reference after it builds the panel. Manually wiring
    /// <see cref="m_targetCanvas"/> in the Inspector also works if the canvas pre-exists.
    /// </summary>
    public class RayVisualizer : MonoBehaviour
    {
        [Tooltip("Transform whose .forward is the pointing direction. Same one assigned to " +
                 "OVRInputModule's Ray Transform (RightHandAnchor or [BuildingBlock] Hand Tracking right).")]
        [SerializeField] private Transform m_rayOrigin;

        [Tooltip("RectTransform of the menu Canvas to test against. Usually assigned at runtime " +
                 "by AppMenu after it spawns the panel; can also be set manually here.")]
        [SerializeField] private RectTransform m_targetCanvas;

        [Tooltip("Maximum ray distance considered for a hit, in metres.")]
        [SerializeField, Range(0.2f, 10f)] private float m_maxDistance = 5f;

        [Tooltip("Thickness of the line in metres.")]
        [SerializeField, Range(0.001f, 0.02f)] private float m_lineWidth = 0.003f;

        [Tooltip("Size of the dot at the hit point, in metres.")]
        [SerializeField, Range(0.003f, 0.05f)] private float m_dotSize = 0.012f;

        [SerializeField] private Color m_color = new(0.31f, 0.93f, 0.78f, 1f);

        private LineRenderer m_line;
        private Transform m_dot;
        private Material m_mat;
        private OVRHand m_ovrHand;  // cached for PointerPose lookup

        public void SetTarget(RectTransform target) => m_targetCanvas = target;

        private void Awake()
        {
            BuildVisuals();
        }

        private void OnDestroy()
        {
            if (m_mat != null) Destroy(m_mat);
        }

        private void LateUpdate()
        {
            if (m_rayOrigin == null || m_targetCanvas == null)
            {
                SetVisible(false);
                return;
            }

            // If the ray origin sits on (or under) an OVRHand, Meta's PointerPose is the canonical
            // pointing pose — same one OVRInputModule uses for hit-testing. Fall back to the raw
            // transform when no hand is present (controllers, etc.).
            if (m_ovrHand == null)
                m_ovrHand = m_rayOrigin.GetComponentInParent<OVRHand>();

            Transform source = m_rayOrigin;
            if (m_ovrHand != null && m_ovrHand.IsTracked &&
                m_ovrHand.IsPointerPoseValid && m_ovrHand.PointerPose != null)
            {
                source = m_ovrHand.PointerPose;
            }

            var origin = source.position;
            var direction = source.forward;

            // Intersect the ray with the canvas plane. The canvas RectTransform's local Z=0
            // plane (with normal = transform.forward) defines the menu's surface.
            var plane = new Plane(m_targetCanvas.forward, m_targetCanvas.position);
            if (!plane.Raycast(new Ray(origin, direction), out var t) || t <= 0f || t > m_maxDistance)
            {
                SetVisible(false);
                return;
            }

            var hitWorld = origin + direction * t;

            // Is the hit inside the rect's bounds? (rect is in the canvas's local space)
            var local = m_targetCanvas.InverseTransformPoint(hitWorld);
            var rect = m_targetCanvas.rect;
            if (local.x < rect.xMin || local.x > rect.xMax ||
                local.y < rect.yMin || local.y > rect.yMax)
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            m_line.SetPosition(0, origin);
            m_line.SetPosition(1, hitWorld);
            m_dot.position = hitWorld;
            // Orient the dot so its flat side faces the user (matches the canvas orientation).
            m_dot.rotation = m_targetCanvas.rotation;
        }

        private void SetVisible(bool visible)
        {
            if (m_line != null) m_line.enabled = visible;
            if (m_dot != null) m_dot.gameObject.SetActive(visible);
        }

        private void BuildVisuals()
        {
            // Shared overlay material — renders on top, no lighting, draws ahead of everything.
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                m_mat = new Material(shader);
                m_mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                m_mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
                m_mat.color = m_color;
            }

            // Line.
            m_line = gameObject.AddComponent<LineRenderer>();
            m_line.positionCount = 2;
            m_line.useWorldSpace = true;
            m_line.startWidth = m_lineWidth;
            m_line.endWidth = m_lineWidth;
            m_line.startColor = m_color;
            m_line.endColor = m_color;
            m_line.numCornerVertices = 0;
            m_line.numCapVertices = 2;
            m_line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_line.receiveShadows = false;
            if (m_mat != null) m_line.sharedMaterial = m_mat;

            // Dot — a tiny sphere, no collider.
            var dotGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dotGo.name = "RayVisualizerDot";
            dotGo.transform.SetParent(transform, false);
            dotGo.transform.localScale = Vector3.one * m_dotSize;
            var col = dotGo.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var renderer = dotGo.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (m_mat != null) renderer.sharedMaterial = m_mat;
            m_dot = dotGo.transform;

            SetVisible(false);
        }
    }
}
