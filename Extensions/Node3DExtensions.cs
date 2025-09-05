using System.Collections.Generic;
using Godot;

public static class Node3DExtensions
{
    public static Aabb GetBoundingBoxWithChildren(this Node3D node, bool includeChildren = true)
    {
        var aabb = new Aabb();
        bool first = true;

        var visualNodes = includeChildren ? node.GetAllVisualNodes() : new List<Node3D>();

        // Add the node itself if it's visual
        if (node is VisualInstance3D)
            visualNodes.Insert(0, node);

        foreach (var visualNode in visualNodes)
        {
            if (visualNode is VisualInstance3D visualInstance)
            {
                var nodeAabb = visualInstance.GetAabb();

                // The commented-out code avoids an error, but it also stops the thing from working, so   
                // var relativeTransform = new Transform3D();
                // // Transform to the original node's local space
                // if (node.IsInsideTree())
                // {
                //     relativeTransform = node.GlobalTransform.AffineInverse() * visualNode.GlobalTransform;
                // }
                var relativeTransform = node.GlobalTransform.AffineInverse() * visualNode.GlobalTransform;
                
                nodeAabb = relativeTransform * nodeAabb;

                if (first)
                {
                    aabb = nodeAabb;
                    first = false;
                }
                else
                {
                    aabb = aabb.Merge(nodeAabb);
                }
            }
        }

        // If no visual nodes found, return a zero-sized AABB at origin
        if (first)
        {
            aabb = new Aabb(Vector3.Zero, Vector3.Zero);
        }

        return aabb;
    }

    private static List<Node3D> GetAllVisualNodes(this Node3D node)
    {
        var visualNodes = new List<Node3D>();

        foreach (var child in node.GetChildren())
        {
            if (child is Node3D child3D)
            {
                if (child3D is VisualInstance3D)
                    visualNodes.Add(child3D);

                visualNodes.AddRange(child3D.GetAllVisualNodes());
            }
        }

        return visualNodes;
    }
}