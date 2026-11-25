using System;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Items;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Characters;

internal sealed class CharacterPoseSampler
{
    internal const float SendInterval = 1f / 30f;
    private const float FastInterval = 1f / 60f;
    private readonly AnimatorLayerPose[] bodyBuffer = new AnimatorLayerPose[CharacterPoseWire.MaxLayers];
    private readonly AnimatorLayerPose[] armBuffer = new AnimatorLayerPose[CharacterPoseWire.MaxLayers];
    private readonly HeldItemPose[] heldBuffer = new HeldItemPose[CharacterPoseWire.MaxHeldItems];
    private CharacterPose last;
    private Body? sampledBody;
    private CharacterFace? face;
    private float sentAt;
    private uint sequence;

    internal bool TrySample(Body body, SharedItemReplication items, ulong player, float now, out CharacterPose pose)
    {
        pose = default;
        float elapsed = now - sentAt;
        if (last.Player != 0 && elapsed < FastInterval * 0.95f)
        {
            return false;
        }

        ReadOnlySpan<AnimatorLayerPose> bodyLayers = ReadLayers(body.bodyAnimator, bodyBuffer);
        ReadOnlySpan<AnimatorLayerPose> armLayers = ReadLayers(body.armsAnimator, armBuffer);
        if (body != sampledBody)
        {
            sampledBody = body;
            FacialExpression expression = body.GetComponentInChildren<FacialExpression>(true);
            face = expression ? new CharacterFace(expression) : null;
        }
        FacePose expressionPose = face?.Capture() ?? default;
        bool graphChanged = last.Player == 0 || Changed(bodyLayers, last.BodyLayers) || Changed(armLayers, last.ArmLayers);
        bool urgent = graphChanged || body.standing != last.Standing || body.isRight != last.FacingRight ||
            body.dogShakeIntensity > 0f || expressionPose.ChangedExpression(last.Face) ||
            body.brainShakeIntensity > 0f || body.miscShakeIntensity > 0f;
        if (!urgent && elapsed < SendInterval * 0.95f)
        {
            return false;
        }

        Vector2 origin = body.transform.position;
        LimbPose[] limbs = CaptureLimbs(body, origin);
        Vector2 velocity = body.standing || !body.baseLimb || !body.baseLimb.rb ? body.rb.velocity : body.baseLimb.rb.velocity;
        pose = new CharacterPose(player, ++sequence, origin.x, origin.y,
            Mathf.Clamp(velocity.x, -1000f, 1000f), Mathf.Clamp(velocity.y, -1000f, 1000f), body.isRight, body.standing,
            limbs, bodyLayers.ToArray(), armLayers.ToArray(), expressionPose, CaptureHeldItems(body, items));
        if (CoopDiagnostics.Enabled && (graphChanged || last.Standing != pose.Standing))
        {
            AnimatorLayerPose bodyState = bodyLayers.IsEmpty ? default : bodyLayers[0];
            AnimatorLayerPose armState = armLayers.IsEmpty ? default : armLayers[0];
            CoopDiagnostics.Trace("Character", $"Pose {pose.Sequence}, standing={pose.Standing}, " +
                $"body={bodyState.State:X8}->{bodyState.NextState:X8} at {bodyState.Phase:F2}, " +
                $"arms={armState.State:X8}->{armState.NextState:X8} at {armState.Phase:F2}.");
        }
        last = pose;
        sentAt = now;
        return true;
    }

    internal void Resume(uint confirmed)
    {
        if (sequence == 0 || unchecked((int)(confirmed - sequence)) > 0)
        {
            sequence = confirmed;
        }
        last = default;
    }

    private HeldItemPose[] CaptureHeldItems(Body body, SharedItemReplication items)
    {
        int count = 0;
        for (int index = 0; index < body.slots.Length && index < byte.MaxValue && count < heldBuffer.Length; index++)
        {
            Item? item = body.GetItem(index);
            if (!item || !item!.gameObject.activeInHierarchy || !items.TryGetId(item, out ulong id))
            {
                continue;
            }

            Vector2 right = item.transform.TransformVector(Vector3.right);
            Vector2 up = item.transform.TransformVector(Vector3.up);
            float determinant = right.x * up.y - right.y * up.x;
            if (!float.IsFinite(determinant) || determinant == 0f)
            {
                continue;
            }
            bool mirrored = determinant < 0f;
            if (mirrored)
            {
                right = -right;
            }
            float angle = Mathf.Repeat(Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg, 360f);
            heldBuffer[count++] = new HeldItemPose(id, (byte)index, angle, mirrored);
        }
        return count == 0 ? Array.Empty<HeldItemPose>() : heldBuffer.AsSpan(0, count).ToArray();
    }

    private static LimbPose[] CaptureLimbs(Body body, Vector2 origin)
    {
        LimbPose[] limbs = new LimbPose[Math.Min(body.limbs.Length, CharacterPoseWire.MaxLimbs)];
        for (int index = 0; index < limbs.Length; index++)
        {
            Limb limb = body.limbs[index];
            if (!limb || limb.dismembered)
            {
                continue;
            }

            Transform at = limb.transform;
            Vector2 offset = (Vector2)at.position - origin;
            Vector3 scale = at.lossyScale;
            if (offset.sqrMagnitude <= 256f * 256f)
            {
                limbs[index] = new LimbPose(offset.x, offset.y, at.eulerAngles.z, Mathf.Min(8f, Mathf.Abs(scale.x)),
                    Mathf.Min(8f, Mathf.Abs(scale.y)), limb.gameObject.activeInHierarchy);
            }
        }
        return limbs;
    }

    private static ReadOnlySpan<AnimatorLayerPose> ReadLayers(Animator animator, AnimatorLayerPose[] buffer)
    {
        if (!animator || !animator.isInitialized || !animator.runtimeAnimatorController)
        {
            return ReadOnlySpan<AnimatorLayerPose>.Empty;
        }

        int count = Math.Min(animator.layerCount, buffer.Length);
        for (int index = 0; index < count; index++)
        {
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(index);
            AnimatorLayerPose layer = new(state.fullPathHash, SafePhase(state.normalizedTime));
            if (animator.IsInTransition(index))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(index);
                float progress = animator.GetAnimatorTransitionInfo(index).normalizedTime;
                layer = new AnimatorLayerPose(layer.State, layer.Phase, next.fullPathHash, SafePhase(next.normalizedTime),
                    float.IsFinite(progress) ? Mathf.Clamp01(progress) : 0f);
            }
            buffer[index] = layer;
        }
        return buffer.AsSpan(0, count);
    }

    private static float SafePhase(float time) => float.IsFinite(time)
        ? Mathf.Clamp(time, -CharacterPoseWire.MaxPhase, CharacterPoseWire.MaxPhase) : 0f;

    private static bool Changed(ReadOnlySpan<AnimatorLayerPose> current, AnimatorLayerPose[] previous)
    {
        if (current.Length != previous.Length)
        {
            return true;
        }
        for (int index = 0; index < current.Length; index++)
        {
            if (current[index].ChangedSince(previous[index]))
            {
                return true;
            }
        }
        return false;
    }
}
