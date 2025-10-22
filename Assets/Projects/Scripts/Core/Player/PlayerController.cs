using System;
using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using Mirror;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private Animator animator;
    [SerializeField] private CinemachineVirtualCamera vCamera;
    
    
    [SerializeField] private float speed = 10f;
    [SerializeField] private float jumpForce = 10f;
    [SerializeField] private float groundCheckRadius = 0.18f;
    
    public LayerMask groundMask;
    public Transform groundCheck;

    private float srvInputX;
    private bool srvWantJump;
    private float srvJumpBufferTime ;
    private const float JumpBufferMax = 0.2f;

    [SyncVar] private Vector2 clPos;
    [SyncVar] private Vector2 clVel;
    [SyncVar] private Vector3 clScale;
    [SyncVar] private PlayerState clAnimationState;
    
    private PlayerState state = PlayerState.Idle;
    private PlayerState previousState = PlayerState.Idle;

    private float _lerpPos = 12f;
    private float sendEvery = 0.05f;
    private float sendTimer;
    
    // Animation parameters
    private const string ANIM_SPEED = "Speed";
    private const string ANIM_IS_GROUNDED = "IsGrounded";
    private const string ANIM_IS_JUMPING = "IsJumping";
    private const string ANIM_HORIZONTAL_INPUT = "HorizontalInput";
    
    // Animation optimization
    private int animSpeedHash;
    private int animIsGroundedHash;
    private int animIsJumpingHash;
    private int animHorizontalInputHash;

    [SerializeField]
    private int ownerPriority = 15;
    public override void OnStartServer()
    {
        rb.isKinematic = false;
        rb.bodyType = RigidbodyType2D.Dynamic;              // đảm bảo Dynamic
        rb.simulated = true;                                 // bật simulation
        rb.gravityScale = 3f;                                // Unity lo gravity
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        
        // Cache animation parameter hashes for better performance
        CacheAnimationHashes();
    }

    public override void OnStartClient()
    {
        // Tắt camera cho tất cả players trước, chỉ local player sẽ được bật lại
        if (vCamera != null)
        {
            vCamera.gameObject.SetActive(false);
        }
        
        if (!isServer) // các client remote
        {
            rb.isKinematic = true;                           // không chạy physics
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.simulated = true;                             // vẫn cho phép set position
            rb.gravityScale = 0f;                            // không rơi ở client
            rb.interpolation = RigidbodyInterpolation2D.None;
        }
        
        // Cache animation parameter hashes for better performance
        CacheAnimationHashes();
    }

    public override void OnStartLocalPlayer()
    {
        // Chỉ local player mới được bật camera
        if (vCamera != null)
        {
            vCamera.gameObject.SetActive(true);
            vCamera.Priority = ownerPriority;
            // Đảm bảo camera follow đúng target (nhân vật này)
            vCamera.Follow = transform;
            vCamera.LookAt = transform;
            Debug.Log($"Local player camera activated with priority: {ownerPriority}");
        }
    }

    public override void OnStopLocalPlayer()
    {
        // Tắt camera khi local player disconnect
        if (vCamera != null)
        {
            vCamera.gameObject.SetActive(false);
            Debug.Log("Local player camera deactivated");
        }
    }
    
    

    private void Update()
    {
        if (!isLocalPlayer) return;
        
        sendTimer += Time.deltaTime;
        float x = Input.GetAxis("Horizontal");
        bool wantJump = Input.GetButtonDown("Jump");
        if (sendTimer >= sendEvery || wantJump)
        {
            sendTimer = 0;
            CmdSetInput(x, wantJump);
        }

        // Scale sẽ được xử lý trên server thông qua CmdSetInput
    }
    [Command]
    private void CmdSetInput(float x, bool wantJump)
    {
        srvInputX = Mathf.Clamp(x,-1f,1f);
        if (wantJump)
        {
            srvWantJump = true;
            srvJumpBufferTime = JumpBufferMax;
        }
        
        // Cập nhật scale trên server khi có input
        if (x != 0)
        {
            var scale = transform.localScale;
            scale.x = Mathf.Sign(x) * Mathf.Abs(scale.x == 0 ? 1 : scale.x);
            transform.localScale = scale;
        }
    }

    private void FixedUpdate()
    {
        if (isServer)
        {
            
            bool grounded = IsGrounded();
            var v = rb.velocity;
            v.x = srvInputX*speed;

            if (srvWantJump && grounded)
            {
                v.y = jumpForce;
                srvWantJump = false;
                srvJumpBufferTime = 0f;
            }
            rb.velocity = v;
            
            if (srvJumpBufferTime > 0)
            {
                srvJumpBufferTime -= Time.fixedDeltaTime;
                if (srvJumpBufferTime <= 0) srvWantJump = false;
            }
            
            PlayerState newState = grounded ? (Mathf.Abs(v.x) > 0.005f ? PlayerState.Walk : PlayerState.Idle) : PlayerState.Jump;
            
            if (newState != state)
            {
                state = newState;
            }
            
            // Cập nhật animation cho server
            UpdateAnimation();
            
            clPos = rb.position;
            clVel = rb.velocity;
            clScale = transform.localScale;
            clAnimationState = state;
        }
        else
        {
            Vector2 pos = rb.position;
            pos = Vector2.Lerp(pos, clPos, 1f - Mathf.Exp(-_lerpPos * Time.fixedDeltaTime));
            rb.position = pos;
            
            // Cập nhật animation cho client
            UpdateClientAnimation();

            transform.localScale = clScale;
        }
    }
    private bool IsGrounded()
    {
        if (!groundCheck) return false;
        return Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundMask);
    }
    
    /// <summary>
    /// Cập nhật animation parameters dựa trên state hiện tại
    /// </summary>
    private void UpdateAnimation()
    {
        if (animator == null) return;
        
        bool grounded = IsGrounded();
        float horizontalInput = Mathf.Abs(srvInputX);
        
        // Cập nhật animation parameters (sử dụng hash để tối ưu performance)
        animator.SetFloat(animSpeedHash, horizontalInput);
        animator.SetBool(animIsGroundedHash, grounded);
        animator.SetBool(animIsJumpingHash, !grounded);
        animator.SetFloat(animHorizontalInputHash, srvInputX);
        
        // Trigger animation transitions dựa trên state changes
        if (state != previousState)
        {
            HandleStateTransition(previousState, state);
            previousState = state;
        }
    }
    
    /// <summary>
    /// Xử lý chuyển đổi giữa các animation states
    /// </summary>
    private void HandleStateTransition(PlayerState fromState, PlayerState toState)
    {
        switch (toState)
        {
            case PlayerState.Idle:
                // Animation idle sẽ tự động chạy khi Speed = 0 và IsGrounded = true
                break;
                
            case PlayerState.Walk:
                // Animation walk sẽ tự động chạy khi Speed > 0 và IsGrounded = true
                break;
                
            case PlayerState.Jump:
                // Trigger jump animation nếu cần
                if (fromState != PlayerState.Jump)
                {
                    // Có thể thêm trigger jump animation ở đây nếu cần
                    Debug.Log("Jump animation triggered");
                }
                break;
        }
    }
    
    /// <summary>
    /// Đồng bộ animation cho client (chỉ hiển thị, không điều khiển)
    /// </summary>
    private void UpdateClientAnimation()
    {
        if (animator == null) return;
        
        // Sử dụng animation state được đồng bộ từ server
        float horizontalSpeed = Mathf.Abs(clVel.x);
        bool grounded = clVel.y <= 0.1f; // Ước tính grounded từ velocity
        
        // Cập nhật animation parameters dựa trên state được đồng bộ (sử dụng hash để tối ưu performance)
        switch (clAnimationState)
        {
            case PlayerState.Idle:
                animator.SetFloat(animSpeedHash, 0f);
                animator.SetBool(animIsGroundedHash, true);
                animator.SetBool(animIsJumpingHash, false);
                break;
                
            case PlayerState.Walk:
                animator.SetFloat(animSpeedHash, horizontalSpeed);
                animator.SetBool(animIsGroundedHash, true);
                animator.SetBool(animIsJumpingHash, false);
                break;
                
            case PlayerState.Jump:
                animator.SetFloat(animSpeedHash, horizontalSpeed);
                animator.SetBool(animIsGroundedHash, false);
                animator.SetBool(animIsJumpingHash, true);
                break;
        }
        
        animator.SetFloat(animHorizontalInputHash, clVel.x);
    }
    
    /// <summary>
    /// Cache animation parameter hashes để tối ưu performance
    /// </summary>
    private void CacheAnimationHashes()
    {
        if (animator == null) return;
        
        animSpeedHash = Animator.StringToHash(ANIM_SPEED);
        animIsGroundedHash = Animator.StringToHash(ANIM_IS_GROUNDED);
        animIsJumpingHash = Animator.StringToHash(ANIM_IS_JUMPING);
        animHorizontalInputHash = Animator.StringToHash(ANIM_HORIZONTAL_INPUT);
    }
}

public enum PlayerState
{
    Idle, Walk, Jump
}
