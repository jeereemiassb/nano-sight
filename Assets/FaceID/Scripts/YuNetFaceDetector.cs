// Copyright (c) NanoSight.
//
// On-device face detector around the YuNet ONNX model (OpenCV Model Zoo).

using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Lightweight on-device face detector wrapping the YuNet ONNX model (OpenCV Model Zoo —
    /// face_detection_yunet_2023mar, ~75k parameters). It is far lighter than the
    /// RetinaFace-ResNet50 used by com.meta.utilities.objectclassifier, so it runs at realtime
    /// rates on the Quest.
    ///
    /// Plain [Serializable] class held as a field on <see cref="FaceIdentifier"/>, so the model
    /// asset and thresholds are configured straight from the Inspector. Runs on Unity Inference
    /// Engine; no server is needed for the bounding boxes.
    ///
    /// Model I/O (verified from the .onnx): input "input" [1,3,640,640]; 12 outputs in fixed
    /// declaration order — cls_{8,16,32}, obj_{8,16,32}, bbox_{8,16,32}, kps_{8,16,32}. YuNet is
    /// anchor-free with one prior per feature-map cell.
    /// </summary>
    [Serializable]
    public class YuNetFaceDetector : IDisposable
    {
        // YuNet's fixed input resolution and the 3 feature-pyramid strides it predicts at.
        private const int InputSize = 640;
        private static readonly int[] Strides = { 8, 16, 32 };

        // Output indices for the 12-output model. cls/obj/bbox are indices 0-8; the 5-point
        // landmarks (kps, indices 9-11) are not needed here and are skipped.
        private const int OutCls0 = 0;   // cls_8, cls_16, cls_32
        private const int OutObj0 = 3;   // obj_8, obj_16, obj_32
        private const int OutBbox0 = 6;  // bbox_8, bbox_16, bbox_32

        [Tooltip("YuNet ONNX model asset — assign Assets/FaceID/Models/yunet.onnx here.")]
        [SerializeField] private ModelAsset m_modelAsset;

        [Tooltip("Minimum detection score (sqrt of cls*obj) for a face to be kept.")]
        [SerializeField, Range(0.1f, 0.95f)] private float m_scoreThreshold = 0.6f;

        [Tooltip("IoU threshold for non-maximum suppression of overlapping detections.")]
        [SerializeField, Range(0.1f, 0.9f)] private float m_nmsThreshold = 0.3f;

        [Tooltip("Inference backend. GPUCompute is usually fastest for a model this small; " +
                 "switch to CPU if you hit GPU issues on device.")]
        [SerializeField] private BackendType m_backend = BackendType.GPUCompute;

        private Worker m_worker;
        private Tensor<float> m_inputTensor;
        private bool m_initialized;

        // Reused across passes so a detection doesn't allocate.
        private readonly List<Rect> m_candidateBoxes = new();
        private readonly List<float> m_candidateScores = new();
        private readonly List<int> m_order = new();
        private readonly List<Rect> m_results = new();

        /// <summary>True once a model asset has been assigned in the Inspector.</summary>
        public bool IsReady => m_modelAsset != null;

        private void EnsureInitialized()
        {
            if (m_initialized)
                return;

            // YuNet expects pixels in the 0-255 range, but TextureConverter.ToTensor produces
            // 0-1. Bake the *255 scale into the model graph so the per-frame conversion stays a
            // plain ToTensor call (same trick the objectclassifier package uses for RetinaFace).
            var sourceModel = ModelLoader.Load(m_modelAsset);
            var graph = new FunctionalGraph();
            var inputs = graph.AddInputs(sourceModel);
            inputs[0] = inputs[0] * 255f;
            var outputs = Functional.Forward(sourceModel, inputs);
            graph.AddOutputs(outputs);
            var model = graph.Compile();

            m_worker = new Worker(model, m_backend);
            m_inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
            m_initialized = true;
        }

        /// <summary>
        /// Detects faces in <paramref name="input"/>. Returns boxes in normalized image
        /// coordinates (origin top-left, x right, y down, range 0..1) — the same convention the
        /// objectclassifier package used, so downstream code (tracker, unprojection) is unchanged.
        /// The returned list is reused between calls; copy it if you need to keep it.
        /// </summary>
        public async Awaitable<IReadOnlyList<Rect>> Detect(Texture input)
        {
            m_results.Clear();
            if (m_modelAsset == null || input == null)
                return m_results;

            EnsureInitialized();

            // YuNet was trained on BGR images, so swizzle RGBA->BGRA. ToTensor also resizes the
            // source texture to the input tensor's 640x640.
            var transform = new TextureTransform().SetChannelSwizzle(ChannelSwizzle.BGRA);
            TextureConverter.ToTensor(input, m_inputTensor, transform);

            m_worker.Schedule(m_inputTensor);

            m_candidateBoxes.Clear();
            m_candidateScores.Clear();

            for (var s = 0; s < Strides.Length; s++)
            {
                var stride = Strides[s];
                var grid = InputSize / stride;   // 80, 40, 20
                var anchorCount = grid * grid;   // 6400, 1600, 400

                using var clsT = await ((Tensor<float>)m_worker.PeekOutput(OutCls0 + s)).ReadbackAndCloneAsync();
                using var objT = await ((Tensor<float>)m_worker.PeekOutput(OutObj0 + s)).ReadbackAndCloneAsync();
                using var bboxT = await ((Tensor<float>)m_worker.PeekOutput(OutBbox0 + s)).ReadbackAndCloneAsync();

                var cls = clsT.DownloadToArray();
                var obj = objT.DownloadToArray();
                var bbox = bboxT.DownloadToArray();

                if (cls.Length < anchorCount || obj.Length < anchorCount || bbox.Length < anchorCount * 4)
                {
                    Debug.LogError($"[{nameof(YuNetFaceDetector)}] Unexpected output size for stride " +
                                   $"{stride} (cls {cls.Length}, obj {obj.Length}, bbox {bbox.Length}). " +
                                   "Model output order/shape does not match the expected YuNet layout.");
                    continue;
                }

                DecodeStride(stride, grid, anchorCount, cls, obj, bbox);
            }

            NonMaxSuppression();
            return m_results;
        }

        /// <summary>Decodes one feature-pyramid level into normalized candidate boxes.</summary>
        private void DecodeStride(int stride, int grid, int anchorCount, float[] cls, float[] obj, float[] bbox)
        {
            for (var idx = 0; idx < anchorCount; idx++)
            {
                // cls and obj are post-sigmoid probabilities; YuNet's score is their geometric mean.
                var score = Mathf.Sqrt(Mathf.Clamp01(cls[idx]) * Mathf.Clamp01(obj[idx]));
                if (score < m_scoreThreshold)
                    continue;

                // Anchor grid is row-major: idx = row * grid + col.
                var row = idx / grid;
                var col = idx % grid;
                var b = idx * 4;

                // YuNet box decode: centre = (cell + offset) * stride, size = exp(reg) * stride.
                var cx = (col + bbox[b + 0]) * stride;
                var cy = (row + bbox[b + 1]) * stride;
                var w = Mathf.Exp(bbox[b + 2]) * stride;
                var h = Mathf.Exp(bbox[b + 3]) * stride;
                var x = cx - w * 0.5f;
                var y = cy - h * 0.5f;

                // Normalize from 640px space to 0..1 (origin top-left, y down).
                m_candidateBoxes.Add(new Rect(x / InputSize, y / InputSize, w / InputSize, h / InputSize));
                m_candidateScores.Add(score);
            }
        }

        /// <summary>Greedy non-maximum suppression; fills <see cref="m_results"/> by descending score.</summary>
        private void NonMaxSuppression()
        {
            m_order.Clear();
            for (var i = 0; i < m_candidateBoxes.Count; i++)
                m_order.Add(i);
            // Sort candidate indices by score, highest first.
            m_order.Sort((a, b) => m_candidateScores[b].CompareTo(m_candidateScores[a]));

            for (var i = 0; i < m_order.Count; i++)
            {
                var idx = m_order[i];
                if (idx < 0)
                    continue; // already suppressed

                var box = m_candidateBoxes[idx];
                m_results.Add(box);

                // Suppress every lower-scored box that overlaps this one too much.
                for (var j = i + 1; j < m_order.Count; j++)
                {
                    var other = m_order[j];
                    if (other < 0)
                        continue;
                    if (IntersectionOverUnion(box, m_candidateBoxes[other]) > m_nmsThreshold)
                        m_order[j] = -1;
                }
            }
        }

        private static float IntersectionOverUnion(Rect a, Rect b)
        {
            var interW = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            var interH = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            if (interW <= 0f || interH <= 0f)
                return 0f;
            var inter = interW * interH;
            var union = a.width * a.height + b.width * b.height - inter;
            return union > 0f ? inter / union : 0f;
        }

        /// <summary>Releases the inference worker and input tensor. Call from the owner's OnDestroy.</summary>
        public void Dispose()
        {
            m_worker?.Dispose();
            m_worker = null;
            m_inputTensor?.Dispose();
            m_inputTensor = null;
            m_initialized = false;
        }
    }
}
