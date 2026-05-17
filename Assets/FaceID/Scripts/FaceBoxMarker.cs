// Copyright (c) NanoSight.
//
// A code-spawned 3D corner-bracket marker that tracks a detected face.

using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Four corner-bracket "L" shapes that hug a detected face. Code-spawned by
    /// <see cref="FaceIdentifier"/> (no prefab needed): each corner is its own
    /// <see cref="LineRenderer"/> drawing a 3-point L from one edge tip → corner → adjacent edge
    /// tip. The four corners smoothly lerp toward the latest detection so the brackets visibly
    /// track the face between detection passes.
    /// </summary>
    public class FaceBoxMarker : MonoBehaviour
    {
        private const float LerpSpeed = 12f;
        private const int CornerCount = 4;

        // Order: 0=bottomLeft, 1=topLeft, 2=topRight, 3=bottomRight.
        // For each corner, the L's two legs run toward these neighbour corners along the
        // adjacent edges. Hard-coded since the box wind order is fixed.
        private static readonly int[,] s_NeighbourIndices =
        {
            { 3, 1 }, // BL -> BR (along bottom), TL (along left)
            { 2, 0 }, // TL -> TR (along top),    BL (along left)
            { 1, 3 }, // TR -> TL (along top),    BR (along right)
            { 0, 2 }, // BR -> BL (along bottom), TR (along right)
        };

        private readonly LineRenderer[] m_lines = new LineRenderer[CornerCount];
        private Material m_material;
        private float m_cornerLengthFraction;

        // Smoothed and target corner positions (in world or local space depending on parent).
        private readonly Vector3[] m_currentCorners = new Vector3[CornerCount];
        private readonly Vector3[] m_targetCorners = new Vector3[CornerCount];
        // Reused 3-point scratch buffer for SetPositions, so each frame doesn't allocate.
        private readonly Vector3[] m_lScratch = new Vector3[3];

        private bool m_hasTarget;

        /// <summary>
        /// Spawns a marker GameObject with 4 child LineRenderers (one per corner). When
        /// <paramref name="parent"/> is non-null, the marker is parented and renders in LOCAL
        /// space (head-locked HUD); otherwise it lives at scene root in world space.
        /// </summary>
        /// <param name="cornerLengthFraction">Length of each L's leg as a fraction of the box edge (0..0.5).</param>
        public static FaceBoxMarker Create(
            string objectName,
            Color color,
            float lineWidth,
            Transform parent = null,
            float cornerLengthFraction = 0.2f)
        {
            var go = new GameObject(objectName);
            if (parent != null)
                go.transform.SetParent(parent, worldPositionStays: false);
            var marker = go.AddComponent<FaceBoxMarker>();
            marker.m_cornerLengthFraction = Mathf.Clamp(cornerLengthFraction, 0.05f, 0.5f);

            // One shared material across the 4 LineRenderers. ZTest=Always + Overlay queue makes
            // the brackets render ON TOP of the world (HUD-overlay feel) regardless of depth.
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                marker.m_material = new Material(shader);
                marker.m_material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                marker.m_material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
            }

            var useWorldSpace = parent == null;
            for (int i = 0; i < CornerCount; i++)
            {
                var child = new GameObject($"Corner{i}");
                child.transform.SetParent(go.transform, worldPositionStays: false);

                var line = child.AddComponent<LineRenderer>();
                line.useWorldSpace = useWorldSpace;
                line.loop = false;
                line.positionCount = 3;
                line.startWidth = lineWidth;
                line.endWidth = lineWidth;
                line.numCornerVertices = 2;
                line.numCapVertices = 2;
                line.alignment = LineAlignment.View;   // brackets always face the camera
                line.startColor = color;
                line.endColor = color;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                if (marker.m_material != null)
                    line.sharedMaterial = marker.m_material;

                marker.m_lines[i] = line;
            }

            go.SetActive(false);
            return marker;
        }

        /// <summary>
        /// Feeds the latest detected corner positions (in winding order: bottom-left, top-left,
        /// top-right, bottom-right). The brackets lerp toward them; the first call snaps so they
        /// don't fly in from the origin.
        /// </summary>
        public void SetCorners(Vector3 bottomLeft, Vector3 topLeft, Vector3 topRight, Vector3 bottomRight)
        {
            m_targetCorners[0] = bottomLeft;
            m_targetCorners[1] = topLeft;
            m_targetCorners[2] = topRight;
            m_targetCorners[3] = bottomRight;

            if (!m_hasTarget)
            {
                m_hasTarget = true;
                for (int i = 0; i < CornerCount; i++)
                    m_currentCorners[i] = m_targetCorners[i];
                UpdateBracketGeometry();
            }

            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

        /// <summary>Hides the marker (caller is responsible for destroying it when done).</summary>
        public void Hide()
        {
            m_hasTarget = false;
            gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!m_hasTarget)
                return;

            var t = 1f - Mathf.Exp(-LerpSpeed * Time.deltaTime);
            for (int i = 0; i < CornerCount; i++)
                m_currentCorners[i] = Vector3.Lerp(m_currentCorners[i], m_targetCorners[i], t);

            UpdateBracketGeometry();
        }

        /// <summary>Refreshes the 4 L-shaped brackets from the smoothed corner positions.</summary>
        private void UpdateBracketGeometry()
        {
            var f = m_cornerLengthFraction;
            for (int i = 0; i < CornerCount; i++)
            {
                var corner = m_currentCorners[i];
                var leg1Target = m_currentCorners[s_NeighbourIndices[i, 0]];
                var leg2Target = m_currentCorners[s_NeighbourIndices[i, 1]];
                m_lScratch[0] = Vector3.Lerp(corner, leg1Target, f);
                m_lScratch[1] = corner;
                m_lScratch[2] = Vector3.Lerp(corner, leg2Target, f);
                m_lines[i].SetPositions(m_lScratch);
            }
        }

        private void OnDestroy()
        {
            if (m_material != null)
                Destroy(m_material);
        }
    }
}
