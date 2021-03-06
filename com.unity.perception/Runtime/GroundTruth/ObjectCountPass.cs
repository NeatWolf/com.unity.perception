#if HDRP_PRESENT

using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;

namespace UnityEngine.Perception.GroundTruth
{
    /// <summary>
    /// CustomPass which computes object count for each label in the given LabelingConfiguration in the frame.
    /// Requires the texture produced by an InstanceSegmentationPass.
    /// </summary>
    public class ObjectCountPass : GroundTruthPass
    {
        const int k_StartingObjectCount = 1 << 8;
        public RenderTexture SegmentationTexture;
        public LabelingConfiguration LabelingConfiguration;

        ComputeShader m_ComputeShader;
        ComputeBuffer m_InstanceIdPresenceMask;
        ComputeBuffer m_InstanceIdToClassId;
        ComputeBuffer m_ClassCounts;
        NativeList<int> m_InstanceIdToLabelIndexLookup;
        HashSet<Camera> m_CamerasRendered = new HashSet<Camera>();
        bool m_IdBuffersNeedUpdating;
        bool m_DidComputeLastFrame;

        public ObjectCountPass(Camera camera) : base(camera)
        {
        }

        // ReSharper disable once UnusedMember.Global
        public ObjectCountPass() : base(null)
        {
        }

        public override void SetupMaterialProperties(MaterialPropertyBlock mpb, MeshRenderer meshRenderer, Labeling labeling, uint instanceId)
        {
            if (!m_InstanceIdToLabelIndexLookup.IsCreated)
            {
                m_InstanceIdToLabelIndexLookup = new NativeList<int>(k_StartingObjectCount, Allocator.Persistent);
            }
            if (LabelingConfiguration.TryGetMatchingConfigurationEntry(labeling, out LabelEntry labelEntry, out var index))
            {
                if (m_InstanceIdToLabelIndexLookup.Length <= instanceId)
                {
                    m_InstanceIdToLabelIndexLookup.Resize((int)instanceId + 1, NativeArrayOptions.ClearMemory);
                }
                m_IdBuffersNeedUpdating = true;
                m_InstanceIdToLabelIndexLookup[(int)instanceId] = index + 1;
            }
        }

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            base.Setup(renderContext, cmd);
            m_ComputeShader = Resources.Load<ComputeShader>("LabeledObjectHistogram");

            var objectCount = k_StartingObjectCount;
            UpdateIdBufferSizes(objectCount);
            m_ClassCounts = new ComputeBuffer(LabelingConfiguration.LabelEntries.Count + 1, UnsafeUtility.SizeOf<uint>(), ComputeBufferType.Structured);

            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnEndCameraRendering(ScriptableRenderContext renderContext, Camera camera)
        {
        }

        void UpdateIdBufferSizes(int objectCount)
        {
            var presenceMaskSizeNeeded = objectCount;
            if (m_InstanceIdPresenceMask == null || presenceMaskSizeNeeded > m_InstanceIdPresenceMask.count)
            {
                m_InstanceIdPresenceMask?.Release();
                m_InstanceIdPresenceMask = new ComputeBuffer(presenceMaskSizeNeeded, UnsafeUtility.SizeOf<uint>(), ComputeBufferType.Structured);
            }

            if (m_InstanceIdToClassId == null || m_InstanceIdToClassId.count < objectCount)
            {
                m_InstanceIdToClassId?.Release();
                m_InstanceIdToClassId = new ComputeBuffer(objectCount, UnsafeUtility.SizeOf<uint>(), ComputeBufferType.Structured);
            }
        }

        protected override void ExecutePass(ScriptableRenderContext renderContext, CommandBuffer cmd, HDCamera hdCamera, CullingResults cullingResult)
        {
            //If there are no objects to label, skip the pass
            if (!m_InstanceIdToLabelIndexLookup.IsCreated || m_InstanceIdToLabelIndexLookup.Length == 0)
            {
                var counts = new NativeArray<uint>(LabelingConfiguration.LabelEntries.Count + 1, Allocator.Temp);
                OnClassCountReadback(Time.frameCount, counts);
                counts.Dispose();
                return;
            }

            m_CamerasRendered.Add(hdCamera.camera);

            if (m_IdBuffersNeedUpdating)
            {
                UpdateIdBufferSizes(m_InstanceIdToLabelIndexLookup.Capacity);
                m_InstanceIdToClassId.SetData(m_InstanceIdToLabelIndexLookup.AsArray());
            }

            //The following section kicks off the four kernels in LabeledObjectHistogram.compute

            //clear ClassCounts
            cmd.SetComputeBufferParam(m_ComputeShader, 1, "ClassCounts", m_ClassCounts);
            cmd.DispatchCompute(m_ComputeShader, 1, m_ClassCounts.count, 1, 1);

            //clear InstanceIdPresenceMask
            cmd.SetComputeBufferParam(m_ComputeShader, 2, "InstanceIdPresenceMask", m_InstanceIdPresenceMask);
            cmd.DispatchCompute(m_ComputeShader, 2, m_InstanceIdPresenceMask.count, 1, 1);

            //clear InstanceIdPresenceMask
            cmd.SetComputeTextureParam(m_ComputeShader, 0, "SegmentationTexture", SegmentationTexture);
            cmd.SetComputeBufferParam(m_ComputeShader, 0, "InstanceIdPresenceMask", m_InstanceIdPresenceMask);
            cmd.SetComputeIntParam(m_ComputeShader, "Width", SegmentationTexture.width);
            cmd.SetComputeIntParam(m_ComputeShader, "Height", SegmentationTexture.height);
            cmd.DispatchCompute(m_ComputeShader, 0, SegmentationTexture.width, SegmentationTexture.height, 1);

            //clear InstanceIdPresenceMask
            cmd.SetComputeBufferParam(m_ComputeShader, 3, "InstanceIdPresenceMask", m_InstanceIdPresenceMask);
            cmd.SetComputeBufferParam(m_ComputeShader, 3, "InstanceIdToClassId", m_InstanceIdToClassId);
            cmd.SetComputeBufferParam(m_ComputeShader, 3, "ClassCounts", m_ClassCounts);
            cmd.DispatchCompute(m_ComputeShader, 3, m_InstanceIdToLabelIndexLookup.Length, 1, 1);

            var requestFrameCount = Time.frameCount;
            cmd.RequestAsyncReadback(m_ClassCounts, request => OnClassCountReadback(requestFrameCount, request.GetData<uint>()));
        }

        protected override void Cleanup()
        {
            base.Cleanup();
            m_InstanceIdPresenceMask?.Dispose();
            m_InstanceIdPresenceMask = null;
            m_InstanceIdToClassId?.Dispose();
            m_InstanceIdToClassId = null;
            m_ClassCounts?.Dispose();
            m_ClassCounts = null;
            WaitForAllRequests();

            if (m_InstanceIdToLabelIndexLookup.IsCreated)
            {
                m_InstanceIdToLabelIndexLookup.Dispose();
                m_InstanceIdToLabelIndexLookup = default;
            }
        }

        internal event Action<NativeSlice<uint>, IReadOnlyList<LabelEntry>, int> ClassCountsReceived;

        void OnClassCountReadback(int requestFrameCount, NativeArray<uint> counts)
        {
#if PERCEPTION_DEBUG
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Histogram data. Frame {0}", requestFrameCount.ToString());
            for (int i = 0; i < LabelingConfiguration.LabelingConfigurations.Count; i++)
            {
                sb.AppendFormat("{0}: {1}", LabelingConfiguration.LabelingConfigurations[i].label,
                    counts[i + 1].ToString());
                sb.AppendLine();
            }
            Debug.Log(sb);
#endif

            ClassCountsReceived?.Invoke(new NativeSlice<uint>(counts, 1), LabelingConfiguration.LabelEntries, requestFrameCount);
        }

        public void WaitForAllRequests()
        {
            var commandBuffer = CommandBufferPool.Get("LabelHistorgramCleanup");
            commandBuffer.WaitAllAsyncReadbackRequests();
            Graphics.ExecuteCommandBuffer(commandBuffer);
            CommandBufferPool.Release(commandBuffer);
        }
    }
}
#endif
