using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

public static class TransformExtensions
{
    // Original method (kept for legacy/non-job use)
    public static float UniformedLossyScale(this Transform transform)
    {
        var s = transform.lossyScale;
        return AbsoluteMaxValue(s);
    }

    // Burst-friendly version (call this inside jobs)
    [BurstCompile]
    public static float AbsoluteMaxValue(float3 s)
    {
        var x = math.abs(s.x);
        var y = math.abs(s.y);
        var z = math.abs(s.z);
        return math.max(math.max(x, y), z);
    }
}