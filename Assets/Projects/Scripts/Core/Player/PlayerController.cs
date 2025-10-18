using System;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;
using Game.Core.Player;

public class PlayerController : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] Rigidbody2D rb;
    [SerializeField] Animator animator;
    [SerializeField] SpriteRenderer sprite;

    [Header("Motor Settings")]
    [SerializeField] MotorSettings motorSettings = new MotorSettings
    {
        accel = 80f,
        decel = 80f,
        maxSpeed = 6f,
        gravity = -50f,
        jumpVelocity = 14f,
        coyoteTime = 0.12f,
        jumpBuffer = 0.1f,
        groundLayers = ~0,
        groundCheckSize = new Vector2(0.9f, 0.1f),
        groundCheckOffset = new Vector2(0f, -1.05f),
        skinWidth = 0.02f,
        colliderSize = new Vector2(0.95f, 1.9f),
        colliderOffset = Vector2.zero
    };

    [Header("Networking")]
    [SerializeField, Range(0.05f, 0.2f)] float interpolationDelay = 0.12f; // 120 ms
    [SerializeField, Range(30f, 60f)] float inputSendRate = 50f;            // Hz
    [SerializeField] int inputRateCapPerSecond = 60;                        // basic rate limit

    [Header("Debug")]
    [SerializeField] bool showDebugOverlay = true;

    const float FixedDt = 0.02f; // 50 Hz

    // Optional explicit sim vs render state for clarity and debugging
    [Serializable]
    public struct SimState { public Vector2 pos, vel; public bool facing; public int tick; }
    [Serializable]
    public struct RenderState { public Vector2 pos; public Vector2 vel; }

    SimState predictedState;    // local predicted state (owner)
    SimState authoritativeState; // last server snapshot state
    RenderState renderState;    // smoothed render state

    [Serializable]
    public struct InputState
    {
        public uint seq;
        public float clientTime;
        public float horizontalAxis;
        public bool jumpPressed;
    }

    [Serializable]
    public struct ServerState
    {
        public uint lastAckSeq;
        public Vector2 position;
        public Vector2 velocity;
        public bool grounded;
        public double serverTime;
    }

    PlayerMotor motor;
    RingBuffer<InputState> inputBuffer;
    uint nextSeq;
    float localTickAccumulator;

    float sendAccumulator;
    int sentThisSecond;
    float secondWindow;

    InputState serverLastInput;
    bool haveServerInput;
    double serverLastInputRecvTime;

    // Predicted/authoritative motor state drives a target position.
    Vector2 predictedTargetPosition;
    Vector2 predictedTargetVelocity;
    bool predictedGrounded;

    // Small-error reconciliation blend offset toward zero
    Vector2 correctionOffset;           // added to predicted target during smoothing
    Vector2 correctionVelocity;         // SmoothDamp velocity for correction offset
    [SerializeField, Range(0.03f, 0.15f)] float correctionSmoothTime = 0.08f; // ~80ms

    // Smoothed render state (visual only)
    Vector2 visualPosition;
    Vector2 visualVelocitySmoothing; // for SmoothDamp
    [SerializeField, Range(0.02f, 0.2f)] float visualSmoothTime = 0.06f;
    [SerializeField, Range(0.1f, 1.0f)] float snapDistance = 0.5f; // hard snap threshold
    [SerializeField, Range(0.05f, 0.5f)] float smallCorrectionDistance = 0.2f; // blend small errors

    RingBuffer<ServerState> snapshotBuffer;
    double serverTimeOffset;
    bool hasTimeOffset;

    // Diagnostics
    Vector2 lastAuthoritativePos;
    const int MaxReplaysPerFrame = 5;
    int lastReplayCount;
    uint lastAckSeqOwner;

    const float MoveAnimThreshold = 0.05f;

    void Awake()
    {
        if (!rb) rb = GetComponent<Rigidbody2D>();
        if (!animator) animator = GetComponent<Animator>();
        if (!sprite) sprite = GetComponent<SpriteRenderer>();

        motor = new PlayerMotor(motorSettings);
        inputBuffer = new RingBuffer<InputState>(64);
        snapshotBuffer = new RingBuffer<ServerState>(64);

        // Ensure animator never moves transform
        if (animator) animator.applyRootMotion = false;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (rb)
        {
            rb.isKinematic = true;
            rb.simulated = false;
            rb.velocity = Vector2.zero;
            rb.interpolation = RigidbodyInterpolation2D.None; // avoid transform fights
        }

        Vector2 pos = transform.position;
        motor.Reset(pos);
        predictedTargetPosition = pos;
        predictedTargetVelocity = Vector2.zero;
        predictedGrounded = false;
        visualPosition = pos;
        visualVelocitySmoothing = Vector2.zero;
        correctionOffset = Vector2.zero;
        correctionVelocity = Vector2.zero;

        // Seed debug structs
        predictedState = new SimState { pos = pos, vel = Vector2.zero, facing = false, tick = 0 };
        authoritativeState = predictedState;
        renderState = new RenderState { pos = pos, vel = Vector2.zero };

        inputBuffer.Clear();
        snapshotBuffer.Clear();
        nextSeq = 1;
        localTickAccumulator = 0f;

        sendAccumulator = 0f;
        sentThisSecond = 0;
        secondWindow = 0f;

        haveServerInput = false;
        serverLastInputRecvTime = 0.0;
        hasTimeOffset = false;
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        inputBuffer.Clear();
        snapshotBuffer.Clear();
    }

    void Update()
    {
        bool isHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        bool serverOnly = NetworkServer.active && !NetworkClient.active;

        // Update local visuals (anim/facing) only on clients with graphics.
        if (!isHeadless && NetworkClient.active)
            UpdateAnimationAndFacing(predictedTargetVelocity);

        if (!isLocalPlayer)
        {
            InterpolatedRenderUpdate();
            return;
        }

        float h = Input.GetAxisRaw("Horizontal");
        bool jumpPressed = Input.GetButtonDown("Jump");

        localTickAccumulator += Time.deltaTime;
        while (localTickAccumulator >= FixedDt)
        {
            localTickAccumulator -= FixedDt;

            var input = new InputState
            {
                seq = nextSeq++,
                clientTime = Time.unscaledTime,
                horizontalAxis = Mathf.Clamp(h, -1f, 1f),
                jumpPressed = jumpPressed
            };

            inputBuffer.Enqueue(input);
            motor.SimulateTick(transform, ToMotorInput(input), FixedDt);

            predictedTargetPosition = motor.state.position;
            predictedTargetVelocity = motor.state.velocity;
            predictedGrounded = motor.state.grounded;

            predictedState.pos = motor.state.position;
            predictedState.vel = motor.state.velocity;
            predictedState.facing = predictedTargetVelocity.x >= 0f;
            predictedState.tick++;

            sendAccumulator += FixedDt;
            secondWindow += FixedDt;

            bool shouldSend = sendAccumulator >= (1f / inputSendRate);
            if (shouldSend && sentThisSecond < inputRateCapPerSecond)
            {
                sendAccumulator = 0f;
                CmdSendInputUnreliable(input);
                sentThisSecond++;
            }

            if (secondWindow >= 1f)
            {
                secondWindow -= 1f;
                sentThisSecond = 0;
            }
        }
        // Visual is applied in LateUpdate via smoothing
    }

    void OnGUI()
    {
        bool isHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        if (!showDebugOverlay || isHeadless) return;

        const int pad = 6;
        int w = 520, h = 170;
        Rect r = new Rect(pad, pad, w, h);

        double nowLocal = Time.unscaledTimeAsDouble;
        double nowServer = hasTimeOffset ? nowLocal + serverTimeOffset : nowLocal;
        double estRttMs = NetworkTime.rtt * 1000.0;

        uint lastAck = 0;
        if (inputBuffer.Count > 0)
        {
            lastAck = inputBuffer[0].seq - 1;
        }

        GUI.color = new Color(0, 0, 0, 0.6f);
        GUI.Box(r, GUIContent.none);
        GUI.color = Color.white;

        GUILayout.BeginArea(r);
        bool serverOnly = NetworkServer.active && !NetworkClient.active;
        GUILayout.Label($"Server:{NetworkServer.active} Client:{NetworkClient.active} ServerOnly:{serverOnly} Owner:{isLocalPlayer}");
        GUILayout.Label($"RTT: ~{estRttMs:F1} ms    Now(Server est): {nowServer:F3}s");
        GUILayout.Label($"Input seq next: {nextSeq}  LastAck guess: {lastAck}  Buffered inputs: {inputBuffer.Count}");
        GUILayout.Label($"Owner LastAck:{lastAckSeqOwner}  LastReplays:{lastReplayCount}  Snapshots: {snapshotBuffer.Count}  InterpDelay: {(int)(interpolationDelay*1000)} ms  MaxReplays/frame:{MaxReplaysPerFrame}");
        GUILayout.Label($"Pred:{predictedTargetPosition} Vis:{visualPosition} LastAuth:{lastAuthoritativePos}");
        GUILayout.Label($"CorrOff:{correctionOffset} CorrMag:{correctionOffset.magnitude:F3} VisualSmooth:{visualSmoothTime*1000:F0}ms CorrSmooth:{correctionSmoothTime*1000:F0}ms");
        GUILayout.EndArea();
    }

    void LateUpdate()
    {
        // Apply smoothed visual transform on clients and host (not headless server)
        bool isHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        if (isHeadless) return;

        // Non-owners use interpolated target; owner uses predicted target
        Vector2 current = (Vector2)transform.position;
        // Apply small correction offset that damps to zero
        correctionOffset = Vector2.SmoothDamp(correctionOffset, Vector2.zero, ref correctionVelocity, correctionSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
        Vector2 target = predictedTargetPosition + correctionOffset;

        float smooth = visualSmoothTime;
        // Hard snap if very far (teleport)
        float dist = Vector2.Distance(current, target);
        if (dist > snapDistance)
        {
            visualPosition = target;
            visualVelocitySmoothing = Vector2.zero;
        }
        else
        {
            // SmoothDamp toward target
            visualPosition = Vector2.SmoothDamp(current, target, ref visualVelocitySmoothing, smooth, Mathf.Infinity, Time.unscaledDeltaTime);
        }

        transform.position = visualPosition;

        // Track render state for debug/camera
        renderState.pos = visualPosition;
        renderState.vel = visualVelocitySmoothing;
    }

    void UpdateAnimationAndFacing(Vector2 velocity)
    {
        if (animator)
            animator.SetBool("isMoving", Mathf.Abs(velocity.x) > MoveAnimThreshold);

        if (sprite)
        {
            if (velocity.x > 0.01f) sprite.flipX = false;
            else if (velocity.x < -0.01f) sprite.flipX = true;
        }
    }

    void InterpolatedRenderUpdate()
    {
        if (snapshotBuffer.Count < 2)
        {
            // Keep targeting latest if only one snapshot exists
            if (snapshotBuffer.Count == 1)
            {
                var s = snapshotBuffer[0];
                predictedTargetPosition = s.position;
                predictedTargetVelocity = s.velocity;
                predictedGrounded = s.grounded;
            }
            return;
        }

        double nowLocal = Time.unscaledTimeAsDouble;
        double nowServer = hasTimeOffset ? nowLocal + serverTimeOffset : nowLocal;
        double targetTime = nowServer - interpolationDelay;

        snapshotBuffer.RemoveFromFrontWhile(s => s.serverTime < nowServer - 1.0);
        int count = snapshotBuffer.Count;
        if (count < 2) return;

        ServerState a = snapshotBuffer[0];
        ServerState b = snapshotBuffer[count - 1];
        for (int i = 1; i < count; i++)
        {
            b = snapshotBuffer[i];
            if (b.serverTime >= targetTime)
            {
                a = snapshotBuffer[i - 1];
                break;
            }
        }

        double latestTime = snapshotBuffer[count - 1].serverTime;
        double earliestTime = snapshotBuffer[0].serverTime;

        if (targetTime <= earliestTime)
        {
            a = snapshotBuffer[0];
            b = snapshotBuffer[Mathf.Min(1, count - 1)];
        }
        else if (targetTime >= latestTime)
        {
            double dt = Math.Min(0.05, targetTime - latestTime);
            ServerState last = snapshotBuffer[count - 1];
            predictedTargetPosition = last.position + last.velocity * (float)dt;
            predictedTargetVelocity = last.velocity;
            predictedGrounded = last.grounded;
            return;
        }

        float t = 0f;
        double span = b.serverTime - a.serverTime;
        if (span > 1e-6) t = (float)((targetTime - a.serverTime) / span);
        predictedTargetPosition = Vector2.Lerp(a.position, b.position, t);
        predictedTargetVelocity = Vector2.Lerp(a.velocity, b.velocity, t);
        predictedGrounded = t < 0.5f ? a.grounded : b.grounded;
    }

    MotorInput ToMotorInput(in InputState s)
    {
        return new MotorInput
        {
            horizontal = s.horizontalAxis,
            jumpPressed = s.jumpPressed
        };
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        motor.Reset(transform.position);
        haveServerInput = false;
        serverLastInputRecvTime = NetworkTime.time;
    }

    [Command(channel = Channels.Unreliable)]
    void CmdSendInputUnreliable(InputState input)
    {
        if (!haveServerInput || input.seq > serverLastInput.seq)
        {
            serverLastInput = input;
            haveServerInput = true;
            serverLastInputRecvTime = NetworkTime.time;
        }
    }

    [ServerCallback]
    void FixedUpdate()
    {
        if (!isServer) return;

        InputState tickInput = serverLastInput;
        double now = NetworkTime.time;
        if (!haveServerInput || (now - serverLastInputRecvTime) > 0.1)
        {
            tickInput.horizontalAxis = 0f;
            tickInput.jumpPressed = false;
        }

        motor.SimulateTick(transform, ToMotorInput(tickInput), FixedDt);

        // On host (server+client), avoid writing transform here to prevent transform fights with client LateUpdate.
        // Only write on server-only (headless) or for non-local player objects on a server with graphics.
        bool serverOnlyRuntime = NetworkServer.active && !NetworkClient.active;
        if (serverOnlyRuntime || !isLocalPlayer)
            transform.position = motor.state.position;

        var snap = new ServerState
        {
            lastAckSeq = tickInput.seq,
            position = motor.state.position,
            velocity = motor.state.velocity,
            grounded = motor.state.grounded,
            serverTime = Time.timeAsDouble
        };

        if (connectionToClient != null)
            TargetOwnerSnapshot(connectionToClient, snap);

        RpcBroadcastSnapshot(snap);
    }

    [TargetRpc(channel = Channels.Unreliable)]
    void TargetOwnerSnapshot(NetworkConnectionToClient _, ServerState s)
    {
        if (!hasTimeOffset)
        {
            serverTimeOffset = s.serverTime - Time.unscaledTimeAsDouble;
            hasTimeOffset = true;
        }

        // Compute error vs current predicted before applying reconciliation
        Vector2 prePos = motor.state.position;
        Vector2 error = s.position - prePos;
        lastAuthoritativePos = s.position;

        // If error is small, blend it out over a short window using correctionOffset.
        // If large, hard snap predicted and render to server to avoid long rubber-bands.
        if (error.magnitude <= smallCorrectionDistance)
        {
            // Do NOT snap predicted target to server here; accumulate correction offset instead.
            correctionOffset += error; // will be damped to zero in LateUpdate
        }
        else
        {
            // Hard correction: seed motor and visuals to authoritative immediately
            motor.SetPositionVelocity(s.position, s.velocity, s.grounded);
            predictedTargetPosition = motor.state.position;
            predictedTargetVelocity = motor.state.velocity;
            predictedGrounded = motor.state.grounded;
            visualPosition = predictedTargetPosition; // teleport visuals to avoid long catch-up
            visualVelocitySmoothing = Vector2.zero;
            correctionOffset = Vector2.zero;
            correctionVelocity = Vector2.zero;
        }

        // Update authoritative snapshot for debug
        authoritativeState.pos = s.position;
        authoritativeState.vel = s.velocity;
        authoritativeState.facing = s.velocity.x >= 0f;
        authoritativeState.tick++;

        inputBuffer.RemoveFromFrontWhile(i => i.seq <= s.lastAckSeq);
        lastAckSeqOwner = s.lastAckSeq;

        int remaining = Mathf.Min(inputBuffer.Count, MaxReplaysPerFrame);
        for (int i = 0; i < remaining; i++)
        {
            var inp = inputBuffer[i];
            motor.SimulateTick(transform, ToMotorInput(inp), FixedDt);
        }
        lastReplayCount = remaining;

        // After replay, drive the predicted target for smoothing and re-seed visual smoothing
        predictedTargetPosition = motor.state.position;
        predictedTargetVelocity = motor.state.velocity;
        predictedGrounded = motor.state.grounded;
        visualVelocitySmoothing = Vector2.zero; // avoid re-smoothing from stale velocity after step change
        predictedState.pos = motor.state.position;
        predictedState.vel = motor.state.velocity;
        predictedState.facing = predictedTargetVelocity.x >= 0f;
        predictedState.tick++;
    }

    [ClientRpc(channel = Channels.Unreliable, includeOwner = false)]
    void RpcBroadcastSnapshot(ServerState s)
    {
        if (isLocalPlayer) return;

        if (!hasTimeOffset)
        {
            serverTimeOffset = s.serverTime - Time.unscaledTimeAsDouble;
            hasTimeOffset = true;
        }

        snapshotBuffer.Enqueue(s);
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        // Visualize predicted (green), render (yellow), authoritative (red)
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(predictedTargetPosition, 0.08f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(visualPosition, 0.08f);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(lastAuthoritativePos, 0.08f);
    }
#endif

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (motorSettings.colliderSize == Vector2.zero) return;

        if (motor.state.position == default)
            motor.Reset(transform.position);

        motor.settings = motorSettings;
        motor.state.position = (Vector2)transform.position;
        motor.DrawGizmos(transform, Color.cyan);

        // Visualize predicted vs visual vs last auth
        Gizmos.color = Color.green; // predicted target
        Gizmos.DrawSphere(predictedTargetPosition, 0.05f);
        Gizmos.color = Color.yellow; // visual
        Gizmos.DrawWireSphere(visualPosition == Vector2.zero ? (Vector2)transform.position : visualPosition, 0.06f);
        Gizmos.color = Color.red; // last authoritative snapshot
        Gizmos.DrawWireSphere(lastAuthoritativePos, 0.04f);
    }
#endif
}
