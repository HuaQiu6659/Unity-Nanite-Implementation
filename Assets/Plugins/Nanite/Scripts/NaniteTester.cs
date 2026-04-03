using UnityEngine;
using UnityNanite;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
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

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("NaniteTester: No Mesh found on the MeshFilter!");
            return;
        }

        Mesh mesh = mf.sharedMesh;
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
        var meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            meshRenderer.enabled = false;
        }

        Debug.Log("NaniteTester: Load complete! Switch to Debug Mode on the Camera to see clusters.");
    }
}