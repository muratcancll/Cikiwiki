using UnityEngine;
using System.Collections;

/// <summary>
/// Unity 6 uyumlu Player Controller – "E ile hızlanınca daire çizme" davranışını düzeltir.
/// Çözüm: Yön kilidi/steering + lateral (yan) hız sönümleme + boost bitişinde hız yönünü inputa snap'leme.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PlayerControllerBoostSteerFix : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform orientation;

    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 8f;
    [SerializeField] private float sprintMultiplier = 1.5f;
    [SerializeField] private float acceleration = 30f;
    [SerializeField] private float deceleration = 20f;
    [SerializeField] private float airControlMultiplier = 0.5f;

    [Header("Jump Settings")]
    [SerializeField] private KeyCode jumpKey = KeyCode.Space;
    [SerializeField] private float jumpForce = 5.8f;
    [SerializeField] private float jumpCooldown = 0.15f;
    [SerializeField] private float coyoteTime = 0.10f;
    [SerializeField] private float jumpBufferTime = 0.10f;

    [Header("Ground Check Settings")]
    [SerializeField] private float playerHeight = 2.0f;
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float groundCheckRadius = 0.25f;
    [SerializeField] private float maxSlopeAngle = 45f;

    [Header("Damping (Drag)")]
    [SerializeField] private float groundLinearDamping = 5f;
    [SerializeField] private float airLinearDamping = 0.5f;

    [Header("Boost Settings")]
    [SerializeField] private KeyCode boostKey = KeyCode.E;
    [SerializeField] private float boostMultiplier = 2f;   // hız çarpanı
    [SerializeField] private float boostDuration = 2f;     // saniye
    [SerializeField] private float boostCooldown = 3f;     // saniye

    [Header("Steering Fix Settings")]
    [Tooltip("Yön kilidi kuvveti. Daha yüksek = daha hızlı ön yöne döner.")]
    [SerializeField] private float steeringStrength = 12f;
    [Tooltip("Yan (lateral) hızın sönümlenme hızı. 0–1 tipik değerler 6–20 arası.")]
    [SerializeField] private float lateralFriction = 14f;
    [Tooltip("Boost bittiğinde mevcut planar hızı doğrudan input yönüne hizalar.")]
    [SerializeField] private bool snapOnBoostEnd = true;
    [Tooltip("Hız yönü hizalaması için input eşiği (ölü bölge)")]
    [SerializeField] private float inputDeadzone = 0.05f;

    private Rigidbody rb;
    private bool grounded;
    private bool readyToJump = true;
    private bool boosting = false;
    private bool canBoost = true;

    private float lastGroundedTime;
    private float lastJumpPressedTime;

    private float h, v;
    private Vector3 wishDir;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (!orientation)
        {
            if (Camera.main) orientation = Camera.main.transform; else orientation = transform;
        }
    }

    private void Start()
    {
        rb.linearDamping = groundLinearDamping;
    }

    private void Update()
    {
        ReadInput();
        GroundCheck();
        HandleJumpLogic();
        HandleDamping();
        HandleBoost();
        CapSpeed();
    }

    private void FixedUpdate()
    {
        Move();
        SteeringCorrection();
    }

    private void ReadInput()
    {
        h = Input.GetAxisRaw("Horizontal");
        v = Input.GetAxisRaw("Vertical");

        var f = (orientation ? orientation.forward : transform.forward);
        var r = (orientation ? orientation.right : transform.right);
        f.y = 0; r.y = 0; f.Normalize(); r.Normalize();
        wishDir = (f * v + r * h);
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        if (Input.GetKeyDown(jumpKey)) lastJumpPressedTime = Time.time;
    }

    private void Move()
    {
        float speedTarget = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);
        if (boosting) speedTarget *= boostMultiplier;

        Vector3 vel = rb.linearVelocity;
        Vector3 planarVel = new Vector3(vel.x, 0f, vel.z);

        Vector3 desiredPlanarVel = wishDir * speedTarget;
        Vector3 velDiff = desiredPlanarVel - planarVel;

        float accel = grounded ? acceleration : acceleration * airControlMultiplier;
        float decel = grounded ? deceleration : deceleration * airControlMultiplier;

        Vector3 force = Vector3.zero;
        if (desiredPlanarVel.sqrMagnitude > 0.01f)
            force += velDiff.normalized * accel * velDiff.magnitude;
        else if (planarVel.sqrMagnitude > 0.01f)
            force += -planarVel.normalized * decel * planarVel.magnitude;

        rb.AddForce(force, ForceMode.Force);
    }

    private void SteeringCorrection()
    {
        // Yön kilidi: mevcut planar hızı input yönüne döndür ve yan hızı sönümle
        if (wishDir.sqrMagnitude < inputDeadzone * inputDeadzone) return; // giriş yoksa dokunma

        Vector3 vel = rb.linearVelocity;
        Vector3 planar = new Vector3(vel.x, 0f, vel.z);
        if (planar.sqrMagnitude < 0.0001f) return;

        // Planar hızı bileşenlerine ayır
        Vector3 aligned = Vector3.Project(planar, wishDir);       // hedef yöne hizalı kısım
        Vector3 lateral = planar - aligned;                        // yan (sağa/sola) kayan kısım

        // Lateral sönümleme (yan kaymayı azalt)
        lateral = Vector3.Lerp(lateral, Vector3.zero, lateralFriction * Time.fixedDeltaTime);

        // Hizalı kısmı da hedef yöne doğru biraz daha çevir (steering)
        Vector3 desiredAligned = wishDir * aligned.magnitude;
        aligned = Vector3.Lerp(aligned, desiredAligned, steeringStrength * Time.fixedDeltaTime);

        Vector3 correctedPlanar = aligned + lateral;
        rb.linearVelocity = new Vector3(correctedPlanar.x, vel.y, correctedPlanar.z);
    }

    private void HandleJumpLogic()
    {
        if (grounded) lastGroundedTime = Time.time;
        bool withinCoyote = Time.time - lastGroundedTime <= coyoteTime;
        bool withinBuffer = Time.time - lastJumpPressedTime <= jumpBufferTime;
        if (withinBuffer && withinCoyote && readyToJump) DoJump();
    }

    private void DoJump()
    {
        readyToJump = false;
        lastJumpPressedTime = -999f;
        Vector3 vlin = rb.linearVelocity;
        rb.linearVelocity = new Vector3(vlin.x, 0f, vlin.z);
        rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        Invoke(nameof(ResetJump), jumpCooldown);
    }
    private void ResetJump() => readyToJump = true;

    private void GroundCheck()
    {
        float castDist = (playerHeight * 0.5f) + 0.2f;
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        grounded = Physics.SphereCast(origin, groundCheckRadius, Vector3.down, out _, castDist, groundMask, QueryTriggerInteraction.Ignore);
    }

    private void HandleDamping()
    {
        rb.linearDamping = grounded ? groundLinearDamping : airLinearDamping;
    }

    private void CapSpeed()
    {
        Vector3 vel = rb.linearVelocity;
        Vector3 planar = new Vector3(vel.x, 0f, vel.z);
        float cap = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f) * (boosting ? boostMultiplier : 1f) * 1.5f;
        if (planar.magnitude > cap)
        {
            Vector3 limited = planar.normalized * cap;
            rb.linearVelocity = new Vector3(limited.x, vel.y, limited.z);
        }
    }

    private void HandleBoost()
    {
        if (Input.GetKeyDown(boostKey) && canBoost && !boosting)
            StartCoroutine(DoBoost());
    }

    private IEnumerator DoBoost()
    {
        boosting = true;
        canBoost = false;
        yield return new WaitForSeconds(boostDuration);
        boosting = false;

        // Boost bittiğinde mevcut hız yönünü inputa hizala (snap)
        if (snapOnBoostEnd && wishDir.sqrMagnitude >= inputDeadzone * inputDeadzone)
        {
            Vector3 v = rb.linearVelocity;
            float planarMag = new Vector3(v.x, 0f, v.z).magnitude;
            Vector3 snappedPlanar = wishDir.normalized * planarMag;
            rb.linearVelocity = new Vector3(snappedPlanar.x, v.y, snappedPlanar.z);
        }

        yield return new WaitForSeconds(boostCooldown);
        canBoost = true;
    }
}