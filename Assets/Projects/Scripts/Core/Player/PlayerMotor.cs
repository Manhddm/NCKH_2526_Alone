using System;
using UnityEngine;

namespace Game.Core.Player
{
    [Serializable]
    public struct MotorSettings
    {
        [Header("Horizontal")]
        public float accel;               // units/s^2
        public float decel;               // units/s^2
        public float maxSpeed;            // units/s

        [Header("Vertical")]
        public float gravity;             // units/s^2 (negative)
        public float jumpVelocity;        // units/s (instant set on jump)

        [Header("Jump Quality")]
        public float coyoteTime;          // seconds allowed after leaving ground
        public float jumpBuffer;          // seconds to buffer jump before landing

        [Header("Ground Detection")]
        public LayerMask groundLayers;
        public Vector2 groundCheckSize;   // box size for grounded check
        public Vector2 groundCheckOffset; // local offset for ground check box
        public float skinWidth;           // small inset for casts (0.01–0.05)

        [Header("Movement Collision")]
        public Vector2 colliderSize;      // player collider box size in world units
        public Vector2 colliderOffset;    // local offset of the collider box used for sweeps
    }

    public struct MotorState
    {
        public Vector2 position;
        public Vector2 velocity;
        public bool grounded;

        public float timeSinceGrounded;     // timers for coyote
        public float timeSinceJumpPressed;  // timers for jump buffer
    }

    public struct MotorInput
    {
        public float horizontal;   // -1..1
        public bool jumpPressed;   // edge-triggered for the tick
    }

    // Deterministic kinematic motor. No Rigidbody integration.
    public sealed class PlayerMotor
    {
        public MotorSettings settings;
        public MotorState state;

        public PlayerMotor(MotorSettings s)
        {
            settings = s;
            state = default;
        }

        public void Reset(Vector2 position, Vector2 velocity = default)
        {
            state.position = position;
            state.velocity = velocity;
            state.grounded = false;
            state.timeSinceGrounded = 999f;
            state.timeSinceJumpPressed = 999f;
        }

        public void SetPositionVelocity(Vector2 pos, Vector2 vel, bool grounded)
        {
            state.position = pos;
            state.velocity = vel;
            state.grounded = grounded;
            state.timeSinceGrounded = grounded ? 0f : state.timeSinceGrounded;
        }

        public void SimulateTick(Transform reference, MotorInput input, float dt)
        {
            // Update timers
            state.timeSinceGrounded += dt;
            state.timeSinceJumpPressed += dt;

            // Buffer jump
            if (input.jumpPressed)
                state.timeSinceJumpPressed = 0f;

            // Horizontal acceleration/deceleration
            float target = input.horizontal * settings.maxSpeed;
            float speed = state.velocity.x;
            float accel = Mathf.Abs(target) > 0.001f ? settings.accel : settings.decel;
            speed = Mathf.MoveTowards(speed, target, accel * dt);
            state.velocity.x = speed;

            // Gravity
            state.velocity.y += settings.gravity * dt;

            // Jump consume: if within buffers and allowed by coyote
            if (state.timeSinceJumpPressed <= settings.jumpBuffer &&
                state.timeSinceGrounded <= settings.coyoteTime)
            {
                state.velocity.y = settings.jumpVelocity;
                state.grounded = false;
                state.timeSinceGrounded = settings.coyoteTime + 1f; // leave ground
                state.timeSinceJumpPressed = settings.jumpBuffer + 1f; // consume
            }

            // Move horizontally with sweep
            Vector2 pos = state.position;
            Vector2 move = new Vector2(state.velocity.x * dt, 0f);
            pos += MoveWithCollisions(reference, pos, move, Vector2.right * Mathf.Sign(move.x));

            // Move vertically with sweep and grounded detection
            move = new Vector2(0f, state.velocity.y * dt);
            Vector2 normal = move.y >= 0 ? Vector2.up : Vector2.down;
            Vector2 delta = MoveWithCollisions(reference, pos, move, normal);

            // If hit floor, clamp and set grounded
            if (move.y < 0f && Mathf.Abs(delta.y - move.y) > 1e-6f)
            {
                // we were blocked moving down; ground
                state.velocity.y = 0f;
                state.grounded = true;
                state.timeSinceGrounded = 0f;
            }
            else if (move.y > 0f && Mathf.Abs(delta.y - move.y) > 1e-6f)
            {
                // hit ceiling
                state.velocity.y = 0f;
                state.grounded = false;
            }
            else
            {
                // If not resolved via collision, do a small grounded probe
                bool groundedNow = IsGrounded(reference, pos + delta);
                if (groundedNow)
                {
                    state.grounded = true;
                    state.timeSinceGrounded = 0f;
                }
                else
                {
                    state.grounded = false;
                }
            }

            pos += delta;
            state.position = pos;
        }

        bool IsGrounded(Transform reference, Vector2 atPosition)
        {
            Vector2 center = atPosition + (Vector2)reference.TransformVector(settings.groundCheckOffset);
            Vector2 size = settings.groundCheckSize - Vector2.one * (settings.skinWidth * 2f);
            if (size.x <= 0f || size.y <= 0f) return false;

            Collider2D hit = Physics2D.OverlapBox(center, size, 0f, settings.groundLayers);
            return hit != null;
        }

        Vector2 MoveWithCollisions(Transform reference, Vector2 startPos, Vector2 move, Vector2 axisNormal)
        {
            if (move == Vector2.zero)
                return Vector2.zero;

            // Build a small inset box for casts
            Vector2 size = settings.colliderSize - Vector2.one * (settings.skinWidth * 2f);
            if (size.x < 0.01f) size.x = 0.01f;
            if (size.y < 0.01f) size.y = 0.01f;

            Vector2 center = startPos + (Vector2)reference.TransformVector(settings.colliderOffset);
            float dist = move.magnitude;
            Vector2 dir = move.normalized;
            RaycastHit2D hit = Physics2D.BoxCast(center, size, 0f, dir, dist, settings.groundLayers);
            if (hit.collider != null)
            {
                float allowed = Mathf.Max(0f, hit.distance - settings.skinWidth);
                return dir * allowed;
            }

            return move;
        }

#if UNITY_EDITOR
        public void DrawGizmos(Transform reference, Color color)
        {
            Gizmos.color = color;
            // Ground check box
            Vector2 gcCenter = state.position + (Vector2)reference.TransformVector(settings.groundCheckOffset);
            UnityEditor.Handles.DrawWireCube(gcCenter, settings.groundCheckSize);

            // Collider box
            Vector2 colCenter = state.position + (Vector2)reference.TransformVector(settings.colliderOffset);
            UnityEditor.Handles.color = new Color(color.r, color.g, color.b, 0.5f);
            UnityEditor.Handles.DrawWireCube(colCenter, settings.colliderSize);
        }
#endif
    }
}

