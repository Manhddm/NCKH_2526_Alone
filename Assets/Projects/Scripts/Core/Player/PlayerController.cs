using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private Animator animator;
    
    [SerializeField] private float speed = 10f;
    
    public float horizontalX;
    private bool isFacingRight = true;
    private bool isMoving = false;
    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        animator = GetComponent<Animator>();
    }

    void Start()
    {
        
    }


    void Update()
    {
        horizontalX = Input.GetAxis("Horizontal");

        CmdMove(horizontalX);
    }

    
    [Command]
    void CmdMove(float horizontal)
    {
        Vector2 movement = new Vector2(horizontal * speed, rb.velocity.y);
        rb.velocity = movement;
        isMoving = horizontal != 0;
        RpcAnimate(isMoving);
        RpcFlip(horizontal);
    }
    
    [ClientRpc]
    private void RpcAnimate(bool isMoving)
    {
        animator.SetBool("isMoving", isMoving);
    }
    
    
    [ClientRpc]
    private void RpcFlip(float horizontal)
    {
        if (horizontal > 0 && !isFacingRight)
        {
            Flip();
        }
        else if (horizontal < 0 && isFacingRight)
        {
            Flip();
        }
    }

    private void Flip()
    {
        isFacingRight = !isFacingRight;
        Vector3 scale = transform.localScale;
        scale.x *= -1;
        transform.localScale = scale;
    }
}
