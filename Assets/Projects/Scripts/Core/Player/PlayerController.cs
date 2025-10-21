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
    
    private PlayerState state = PlayerState.Idle;

    private float _lerpPos = 12f;
    private float sendEvery = 0.05f;// 20 times per second
    private float sendTimer;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        animator = GetComponent<Animator>();
        if (!isServer)
        {
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation2D.None;
            rb.gravityScale = 0f;
        }
        else
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.gravityScale = 3f;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
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

        if (x != 0)
        {
            var scale = transform.localScale;
            scale.x = Mathf.Sign(x)*Mathf.Abs(scale.x ==0?1:scale.x);
            transform.localScale = scale;
        }
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
            clPos = rb.position;
            clVel = rb.velocity;
        }
        else
        {
            Vector2 pos = rb.position;
            pos = Vector2.Lerp(pos, clPos, 1f - Mathf.Exp(-_lerpPos * Time.fixedDeltaTime));
            rb.position = pos;
        }
    }
    private bool IsGrounded()
    {
        if (!groundCheck) return false;
        return Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundMask);
    }
}

public enum PlayerState
{
    Idle, Walk, Jump
}
