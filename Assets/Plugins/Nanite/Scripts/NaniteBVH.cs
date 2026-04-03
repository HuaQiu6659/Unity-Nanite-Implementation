using System.Collections.Generic;
using UnityEngine;

namespace UnityNanite
{
    public struct BVHNode
    {
        public Vector3 boundsMin;
        public Vector3 boundsMax;
        public int leftChildOrClusterGroup; // If >= 0, it's a cluster group index. If < 0, it's a child node index (~leftChildOrClusterGroup).
        public int rightChild;
        public float maxParentLODError;
        public float minLODError;
    }

    public class NaniteBVHBuilder
    {
        public static List<BVHNode> BuildBVH(List<ClusterGroup> clusterGroups)
        {
            List<BVHNode> nodes = new List<BVHNode>();
            
            // Collect leaf indices
            List<int> groupIndices = new List<int>();
            for (int i = 0; i < clusterGroups.Count; i++)
            {
                groupIndices.Add(i);
            }

            BuildRecursive(nodes, clusterGroups, groupIndices);
            return nodes;
        }

        private static int BuildRecursive(List<BVHNode> nodes, List<ClusterGroup> clusterGroups, List<int> groupIndices)
        {
            if (groupIndices.Count == 1)
            {
                int groupIndex = groupIndices[0];
                ClusterGroup group = clusterGroups[groupIndex];
                
                BVHNode leaf = new BVHNode();
                leaf.boundsMin = group.Bounds - new Vector3(group.radius, group.radius, group.radius);
                leaf.boundsMax = group.Bounds + new Vector3(group.radius, group.radius, group.radius);
                leaf.leftChildOrClusterGroup = groupIndex;
                leaf.rightChild = -1;
                leaf.maxParentLODError = group.MaxParentLODError;
                leaf.minLODError = group.MinLODError;
                
                nodes.Add(leaf);
                return nodes.Count - 1;
            }

            // Compute bounds for all elements
            Vector3 overallMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 overallMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 centroidMin = overallMin;
            Vector3 centroidMax = overallMax;

            foreach (int idx in groupIndices)
            {
                ClusterGroup g = clusterGroups[idx];
                Vector3 bMin = g.Bounds - new Vector3(g.radius, g.radius, g.radius);
                Vector3 bMax = g.Bounds + new Vector3(g.radius, g.radius, g.radius);
                
                overallMin = Vector3.Min(overallMin, bMin);
                overallMax = Vector3.Max(overallMax, bMax);
                
                centroidMin = Vector3.Min(centroidMin, g.Bounds);
                centroidMax = Vector3.Max(centroidMax, g.Bounds);
            }

            // Split
            int splitAxis = 0;
            Vector3 extent = centroidMax - centroidMin;
            if (extent.y > extent.x && extent.y > extent.z) splitAxis = 1;
            else if (extent.z > extent.x && extent.z > extent.y) splitAxis = 2;

            float splitPos = (centroidMin[splitAxis] + centroidMax[splitAxis]) * 0.5f;

            List<int> leftIndices = new List<int>();
            List<int> rightIndices = new List<int>();

            foreach (int idx in groupIndices)
            {
                if (clusterGroups[idx].Bounds[splitAxis] < splitPos)
                    leftIndices.Add(idx);
                else
                    rightIndices.Add(idx);
            }

            // Fallback if split fails
            if (leftIndices.Count == 0 || rightIndices.Count == 0)
            {
                int mid = groupIndices.Count / 2;
                leftIndices = groupIndices.GetRange(0, mid);
                rightIndices = groupIndices.GetRange(mid, groupIndices.Count - mid);
            }

            int leftNodeIdx = BuildRecursive(nodes, clusterGroups, leftIndices);
            int rightNodeIdx = BuildRecursive(nodes, clusterGroups, rightIndices);

            BVHNode node = new BVHNode();
            node.boundsMin = overallMin;
            node.boundsMax = overallMax;
            node.leftChildOrClusterGroup = ~leftNodeIdx; // negative means internal node
            node.rightChild = rightNodeIdx;
            node.maxParentLODError = Mathf.Max(nodes[leftNodeIdx].maxParentLODError, nodes[rightNodeIdx].maxParentLODError);
            node.minLODError = Mathf.Min(nodes[leftNodeIdx].minLODError, nodes[rightNodeIdx].minLODError);

            nodes.Add(node);
            return nodes.Count - 1;
        }
    }
}