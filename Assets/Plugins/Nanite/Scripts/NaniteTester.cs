using UnityEngine;
using UnityNanite;
using System.Collections.Generic;

public class NaniteTester : MonoBehaviour
{
    [Header("Nanite Camera Renderer")]
    public NaniteRenderer naniteRenderer;

    void Start()
    {
        if (naniteRenderer == null)
        {
            Debug.LogError("NaniteTester: Please assign the NaniteRenderer from your Main Camera!");
            return;
        }

        // 自动在自身及所有子节点中寻找可用的 Mesh
        Mesh mesh = null;
        Renderer targetRenderer = null;

        NaniteSkinWeight[] skinWeights = null;
        Transform[] bones = null;
        Matrix4x4[] bindposes = null;

        // 1. 尝试找普通的 MeshFilter
        MeshFilter mf = GetComponentInChildren<MeshFilter>();
        // 2. 如果没有 MeshFilter，尝试找带蒙皮的 SkinnedMeshRenderer
        SkinnedMeshRenderer smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (mf != null && mf.sharedMesh != null)
        {
            mesh = mf.sharedMesh;
            targetRenderer = mf.GetComponent<MeshRenderer>();
        }
        else if (smr != null && smr.sharedMesh != null)
        {
            mesh = smr.sharedMesh;
            targetRenderer = smr;
            
            BoneWeight[] bws = mesh.boneWeights;
            if (bws != null && bws.Length > 0)
            {
                skinWeights = new NaniteSkinWeight[bws.Length];
                for (int i = 0; i < bws.Length; i++)
                {
                    skinWeights[i].w0 = bws[i].weight0;
                    skinWeights[i].w1 = bws[i].weight1;
                    skinWeights[i].w2 = bws[i].weight2;
                    skinWeights[i].w3 = bws[i].weight3;
                    skinWeights[i].i0 = bws[i].boneIndex0;
                    skinWeights[i].i1 = bws[i].boneIndex1;
                    skinWeights[i].i2 = bws[i].boneIndex2;
                    skinWeights[i].i3 = bws[i].boneIndex3;
                }
                bones = smr.bones;
                bindposes = mesh.bindposes;
                Debug.Log("NaniteTester: Found a SkinnedMeshRenderer! Extracting bone weights for GPU Skinning.");
            }
            else
            {
                Debug.LogWarning("NaniteTester: SkinnedMeshRenderer found but no bone weights present.");
            }
        }

        if (mesh == null)
        {
            Debug.LogError("NaniteTester: No MeshFilter or SkinnedMeshRenderer found on this object or its children!");
            return;
        }
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        int[] indices = mesh.triangles;

        Debug.Log($"NaniteTester: Building Nanite data for {mesh.name} ({indices.Length / 3} triangles)...");

        // 1. Build Clusters and DAG
        NaniteSubMesh naniteMesh = NaniteBuilder.BuildNanite(vertices, normals, indices);

        // 2. Build BVH
        List<BVHNode> bvhNodes = NaniteBVHBuilder.BuildBVH(naniteMesh.clusterGroups);

        // 3. Load into GPU
        naniteRenderer.LoadModelData(
            naniteMesh.vertices, 
            naniteMesh.indices, 
            naniteMesh.clusters, 
            naniteMesh.clusterGroups, 
            bvhNodes.ToArray(),
            skinWeights,
            bones,
            bindposes
        );

        // Disable normal Unity rendering
        if (targetRenderer != null)
        {
            targetRenderer.enabled = false;
        }

        Debug.Log("NaniteTester: Load complete! Switch to Debug Mode on the Camera to see clusters.");
    }

    void Update()
    {
        if (naniteRenderer != null)
        {
            naniteRenderer.objectToWorldMatrix = transform.localToWorldMatrix;
        }
    }
}