using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

namespace VRM.SpringBone
{
    public readonly struct SphereCollider
    {
        public readonly float3 Position;
        public readonly float Radius;

        public SphereCollider(Transform colliderTransform, VRMSpringBoneColliderGroup.SphereCollider collider)
        {
            Position = colliderTransform.TransformPoint(collider.Offset);
            Radius = colliderTransform.UniformedLossyScale() * collider.Radius; // Uses abs max for robustness
        }

        [BurstCompile]
        public bool TryCollide(float hitRadius, float boneUniformScale, float3 nextTail, out float3 posFromCollider)
        {
            float m_radius = hitRadius * boneUniformScale;
            float r = m_radius + Radius;
            float r_squared = r * r;

            float3 delta = nextTail - Position;
            float sqrDist = math.dot(delta, delta);

            if (sqrDist <= r_squared && sqrDist > 1e-9f) // Avoid zero-distance NaN; tweak epsilon if needed
            {
                float3 normal = math.normalize(delta);
                posFromCollider = Position + normal * r;
                return true;
            }

            // Rare zero-distance case: push along arbitrary axis (e.g., up)
            if (sqrDist <= r_squared)
            {
                posFromCollider = Position + new float3(0, r, 0);
                return true;
            }

            posFromCollider = default;
            return false;
        }
    }
}