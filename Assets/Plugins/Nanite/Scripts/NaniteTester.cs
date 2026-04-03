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

        // 1. 尝试找普通的 MeshFilter
        MeshFilter mf = GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            mesh = mf.sharedMesh;
            targetRenderer = mf.GetComponent<MeshRenderer>();
        }
        else
        {
            // 2. 如果没有 MeshFilter，尝试找带蒙皮的 SkinnedMeshRenderer
            SkinnedMeshRenderer smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                targetRenderer = smr;
                Debug.LogWarning("NaniteTester: Found a SkinnedMeshRenderer! Note that Nanite currently only supports static meshes. The character will be rendered in its T-Pose/Bind-Pose without animation.");
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
            bvhNodes.ToArray()
        );

        // Disable normal Unity rendering
        if (targetRenderer != null)
        {
            targetRenderer.enabled = false;
        }

        Debug.Log("NaniteTester: Load complete! Switch to Debug Mode on the Camera to see clusters.");
    }
}