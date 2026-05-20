// Copyright (c) NanoSight.
//
// Lets OVRInputModule respond to either hand: switches its rayTransform to whichever hand
// is currently pinching, or — if neither is pinching — to whichever is being tracked.

using UnityEngine;
using UnityEngine.EventSystems;

namespace NanoSight.FaceID
{
    /// <summary>
    /// OVRInputModule only has one <c>rayTransform</c> slot, so out of the box it can be driven
    /// by exactly one hand / controller. This driver dynamically rewrites that slot every frame
    /// based on hand activity: if you pinch with the left hand the left's PointerPose drives the
    /// ray (and that's where the click registers); same for the right. With neither pinching, the
    /// last active hand stays as the hover source.
    ///
    /// Drop on a GO, wire the OVRInputModule reference (sits on your EventSystem) and the two
    /// OVRHand components (one per hand, inside the [BuildingBlock] Hand Tracking left/right GOs).
    /// </summary>
    public class OvrBimanualRayDriver : MonoBehaviour
    {
        [Tooltip("The OVRInputModule on your EventSystem. Its rayTransform gets reassigned each " +
                 "frame based on which hand is active.")]
        [SerializeField] private OVRInputModule m_inputModule;

        [Tooltip("OVRHand on [BuildingBlock] Hand Tracking left.")]
        [SerializeField] private OVRHand m_leftHand;

        [Tooltip("OVRHand on [BuildingBlock] Hand Tracking right.")]
        [SerializeField] private OVRHand m_rightHand;

        private Transform m_lastActive;

        private void Update()
        {
            if (m_inputModule == null) return;

            var leftPinch = IsPinching(m_leftHand);
            var rightPinch = IsPinching(m_rightHand);

            // Priority: hand currently pinching wins. Otherwise stick with the last active so
            // hover/highlight doesn't flicker between hands.
            Transform desired = null;
            if (leftPinch && m_leftHand != null) desired = m_leftHand.PointerPose;
            else if (rightPinch && m_rightHand != null) desired = m_rightHand.PointerPose;
            else if (m_lastActive != null) desired = m_lastActive;
            else desired = FirstTrackedPointerPose();

            if (desired != null && m_inputModule.rayTransform != desired)
            {
                m_inputModule.rayTransform = desired;
                m_lastActive = desired;
            }
        }

        private Transform FirstTrackedPointerPose()
        {
            if (m_rightHand != null && m_rightHand.IsTracked && m_rightHand.IsPointerPoseValid)
                return m_rightHand.PointerPose;
            if (m_leftHand != null && m_leftHand.IsTracked && m_leftHand.IsPointerPoseValid)
                return m_leftHand.PointerPose;
            return null;
        }

        private static bool IsPinching(OVRHand hand)
        {
            return hand != null && hand.IsTracked &&
                   hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        }
    }
}
