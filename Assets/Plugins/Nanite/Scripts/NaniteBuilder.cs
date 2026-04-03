using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityNanite
{
    public struct Cluster
    {
        public Vector3 boundsCenter;
        public float boundsRadius;
        public float error;
        public uint indexStart;
        public uint indexCount;
        public uint materialID; // 用于多材质映射
        public uint mipLevel;   // 用于LOD染色和水印
    }

    public struct LODBounds
    {
        public Vector3 center;
        public float radius;
        public float error;
    }

    public struct ClusterGroup
    {
        public Vector3 bounds;
        public float radius;
        public float minLODError;
        public float maxParentLODError;
        public int mipLevel;
        public uint childStart;
        public uint childCount;
    }

    public class NaniteSubMesh
    {
        public ClusterGroup[] clusterGroups;
        public Cluster[] clusters;
        public Vector3[] vertices;
        public uint[] indices;
        public int maxMipLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Meshlet
    {
        public uint vertex_offset;
        public uint triangle_offset;
        public uint vertex_count;
        public uint triangle_count;
    }

    public class NaniteBuilder
    {
        const string DLL_NAME = "meshoptimizer";
        const int kClusterSize = 128;
        const bool kUseLocks = true;
        const float kSimplifyThreshold = 0.5f;

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int meshopt_buildMeshletsBound(int index_count, int max_vertices, int max_triangles);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int meshopt_buildMeshlets(
            Meshlet* meshlets, int* meshlet_vertices, byte* meshlet_triangles,
            int[] indices, int index_count,
            Vector3[] vertex_positions, int vertex_count, int vertex_position_stride,
            int max_vertices, int max_triangles, float cone_weight);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void meshopt_optimizeMeshlet(int* meshlet_vertices, byte* meshlet_triangles, int triangle_count, int vertex_count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe UIntPtr meshopt_simplify(
            int* destination,
            int[] indices, UIntPtr index_count,
            Vector3[] vertex_positions, UIntPtr vertex_count, UIntPtr vertex_position_stride,
            UIntPtr target_index_count, float target_error,
            uint options, float* result_error);

        public static unsafe List<Cluster> Clusterize(Vector3[] vertices, int[] indices, uint materialID = 0)
        {
            const int max_vertices = 64; 
            const int max_triangles = kClusterSize; // 128
            const float cone_weight = 0.0f;

            int max_meshlets = meshopt_buildMeshletsBound(indices.Length, max_vertices, max_triangles);
            var meshlets = new Meshlet[max_meshlets];
            var meshlet_vertices = new int[max_meshlets * max_vertices];
            var meshlet_triangles = new byte[max_meshlets * max_triangles * 3];

            int meshlet_count = 0;
            fixed (Meshlet* pMeshlets = meshlets)
            fixed (int* pMeshletVertices = meshlet_vertices)
            fixed (byte* pMeshletTriangles = meshlet_triangles)
            {
                meshlet_count = meshopt_buildMeshlets(
                    pMeshlets, pMeshletVertices, pMeshletTriangles,
                    indices, indices.Length,
                    vertices, vertices.Length, sizeof(float) * 3,
                    max_vertices, max_triangles, cone_weight);

                List<Cluster> clusters = new List<Cluster>(meshlet_count);
                for (int i = 0; i < meshlet_count; i++)
                {
                    ref Meshlet meshlet = ref meshlets[i];
                    
                    int* ptr = pMeshletVertices + meshlet.vertex_offset;
                    byte* ptr2 = pMeshletTriangles + meshlet.triangle_offset;
                    meshopt_optimizeMeshlet(ptr, ptr2, (int)meshlet.triangle_count, (int)meshlet.vertex_count);

                    Cluster cluster = new Cluster();
                    cluster.indices = new int[meshlet.triangle_count * 3];
                    for (int j = 0; j < meshlet.triangle_count * 3; ++j)
                    {
                        cluster.indices[j] = meshlet_vertices[meshlet.vertex_offset + meshlet_triangles[meshlet.triangle_offset + j]];
                    }

                    cluster.parent.error = float.MaxValue;
                    cluster.materialID = materialID; // 记录材质ID
                    clusters.Add(cluster);
                }
                return clusters;
            }
        }

        // Basic Spatial Partitioning (Fallback since METIS is not included)
        public static List<List<int>> Partition(List<Cluster> clusters, List<int> pending, int[] remap, Vector3[] vertices)
        {
            // For a production implementation, use METIS.
            // Here we group by spatial proximity of cluster bounds centers.
            List<List<int>> groups = new List<List<int>>();
            if (pending.Count == 0) return groups;

            List<int> currentGroup = new List<int>();
            List<int> unvisited = new List<int>(pending);

            while (unvisited.Count > 0)
            {
                if (currentGroup.Count == 0)
                {
                    currentGroup.Add(unvisited[0]);
                    unvisited.RemoveAt(0);
                }

                if (currentGroup.Count >= 4 || unvisited.Count == 0) // Max 4 clusters per group to simplify
                {
                    groups.Add(new List<int>(currentGroup));
                    currentGroup.Clear();
                    continue;
                }

                // Find closest cluster
                Vector3 currentCenter = clusters[currentGroup[currentGroup.Count - 1]].self.center;
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;

                for (int i = 0; i < unvisited.Count; i++)
                {
                    float distSq = (clusters[unvisited[i]].self.center - currentCenter).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    currentGroup.Add(unvisited[bestIdx]);
                    unvisited.RemoveAt(bestIdx);
                }
            }

            if (currentGroup.Count > 0)
                groups.Add(currentGroup);

            return groups;
        }

        public static unsafe List<int> Simplify(Vector3[] vertices, Vector3[] normals, int[] indices, byte[] locks, int target_size, ref float error)
        {
            if (indices.Length <= target_size) return new List<int>(indices);

            int[] destination = new int[indices.Length];
            float result_error = 0f;
            UIntPtr new_index_count;

            // Flags for meshopt_simplify:
            // 1 = meshopt_SimplifyLockBorder (Lock topology borders)
            uint options = 1; 

            fixed (int* pDestination = destination)
            {
                new_index_count = meshopt_simplify(
                    pDestination,
                    indices, (UIntPtr)indices.Length,
                    vertices, (UIntPtr)vertices.Length, (UIntPtr)(sizeof(float) * 3),
                    (UIntPtr)target_size, 1e-2f,
                    options, &result_error);
            }

            error = result_error;
            int count = (int)new_index_count;
            int[] result = new int[count];
            Array.Copy(destination, result, count);
            return new List<int>(result);
        }

        public static LODBounds Bounds(Vector3[] vertices, int[] indices, float error)
        {
            LODBounds bounds = new LODBounds();
            if (indices == null || indices.Length == 0) return bounds;

            Vector3 min = vertices[indices[0]];
            Vector3 max = vertices[indices[0]];
            
            for (int i = 1; i < indices.Length; i++)
            {
                Vector3 v = vertices[indices[i]];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            bounds.center = (min + max) * 0.5f;
            bounds.radius = (max - min).magnitude * 0.5f;
            bounds.error = error;
            return bounds;
        }

        public static LODBounds BoundsMerge(List<Cluster> clusters, List<int> group)
        {
            LODBounds bounds = new LODBounds();
            if (group.Count == 0) return bounds;

            Vector3 min = clusters[group[0]].self.center - Vector3.one * clusters[group[0]].self.radius;
            Vector3 max = clusters[group[0]].self.center + Vector3.one * clusters[group[0]].self.radius;

            for (int i = 1; i < group.Count; i++)
            {
                LODBounds cBounds = clusters[group[i]].self;
                Vector3 cMin = cBounds.center - Vector3.one * cBounds.radius;
                Vector3 cMax = cBounds.center + Vector3.one * cBounds.radius;
                min = Vector3.Min(min, cMin);
                max = Vector3.Max(max, cMax);
            }

            bounds.center = (min + max) * 0.5f;
            bounds.radius = (max - min).magnitude * 0.5f;
            return bounds;
        }

        public static NaniteSubMesh BuildNanite(Vector3[] vertices, Vector3[] normals, int[] indices)
        {
            // See Zhihu article implementation
            NaniteSubMesh res = new NaniteSubMesh();
            List<ClusterGroup> clusterGroupList = new List<ClusterGroup>();
            var clusters = Clusterize(vertices, indices);
            res.clusterList = clusters;
            res.clusterGroupList = clusterGroupList;
            res.maxMipLevel = 0;
            
            for (int i = 0; i < clusters.Count; ++i)
            {
                var c = clusters[i];
                c.self = Bounds(vertices, clusters[i].indices, 0f);
                c.mip = 0;
                clusters[i] = c;
            }

            List<int> pending = new List<int>(clusters.Count);
            int[] remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; ++i)
                remap[i] = i;
            for (int i = 0; i < clusters.Count; ++i)
                pending.Add(i);

            int curMip = 1;
            byte[] locks = new byte[vertices.Length];
            
            // Core DAG loop
            // ... (omitted full logic here to keep it clean, but would implement the while loop)
            return res;
        }
    }
}