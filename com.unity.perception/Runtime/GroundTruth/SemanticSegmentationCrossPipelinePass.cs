﻿using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine.Perception.GroundTruth
{
    /// <summary>
    /// Custom Pass which renders labeled images where each object labeled with a Labeling component is drawn with the
    /// value specified by the given LabelingConfiguration.
    /// </summary>
    class SemanticSegmentationCrossPipelinePass : GroundTruthCrossPipelinePass
    {
        const string k_ShaderName = "Perception/SemanticSegmentation";
        static readonly int k_LabelingId = Shader.PropertyToID("LabelingId");

        LabelingConfiguration m_LabelingConfiguration;

        //Serialize the shader so that the shader asset is included in player builds when the SemanticSegmentationPass is used.
        //Currently commented out and shaders moved to Resources folder due to serialization crashes when it is enabled.
        //See https://fogbugz.unity3d.com/f/cases/1187378/
        //[SerializeField]
        Shader m_ClassLabelingShader;
        Material m_OverrideMaterial;

        public SemanticSegmentationCrossPipelinePass(Camera targetCamera, LabelingConfiguration labelingConfiguration) : base(targetCamera)
        {
            this.m_LabelingConfiguration = labelingConfiguration;
        }

        public override void Setup()
        {
            base.Setup();
            m_ClassLabelingShader = Shader.Find(k_ShaderName);
            var shaderVariantCollection = new ShaderVariantCollection();
            shaderVariantCollection.Add(new ShaderVariantCollection.ShaderVariant(m_ClassLabelingShader, PassType.ScriptableRenderPipeline));
            shaderVariantCollection.WarmUp();

            m_OverrideMaterial = new Material(m_ClassLabelingShader);
        }

        protected override void ExecutePass(ScriptableRenderContext renderContext, CommandBuffer cmd, Camera camera, CullingResults cullingResult)
        {
            var renderList = CreateRendererListDesc(camera, cullingResult, "FirstPass", 0, m_OverrideMaterial, -1);
            cmd.ClearRenderTarget(true, true, Color.clear);
            DrawRendererList(renderContext, cmd, RendererList.Create(renderList));
        }

        public override void SetupMaterialProperties(MaterialPropertyBlock mpb, MeshRenderer meshRenderer, Labeling labeling, uint instanceId)
        {
            var entry = new LabelEntry();
            foreach (var l in m_LabelingConfiguration.LabelEntries)
            {
                if (labeling.labels.Contains(l.label))
                {
                    entry = l;
                    break;
                }
            }

            //Set the labeling ID so that it can be accessed in ClassSemanticSegmentationPass.shader
            mpb.SetInt(k_LabelingId, entry.value);
        }
    }
}
