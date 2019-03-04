using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(BoxCollider2D))]
[RequireComponent(typeof(Actor))]
[RequireComponent(typeof(Animator))]
/// <summary>
/// This is a component used alongside Actors that controls all the movement and physics operations
/// </summary>
public class Controller2D : MonoBehaviour {
    // Info that can be used in other classes
    public static readonly string GROUND_LAYER = "Ground";
    public static readonly string OW_PLATFORM_LAYER = "OWPlatform";
    public static readonly string LADDER_LAYER = "Ladder";
    public static readonly string PLAYER_LAYER = "Player";
    public static readonly string ENEMY_LAYER = "Enemy";
    public static readonly float GRAVITY = -50f;
    public static readonly float AIR_FRICTION = 10f;
    // Colision parameters
    private static readonly float BOUNDS_EXPANSION = -2;
    private static readonly float RAY_COUNT = 4;
    public static readonly float SKIN_WIDTH = 0.015f;
    public static readonly float FALLTHROUGH_DELAY = 0.2f;
    public static readonly float LADDER_CLIMB_THRESHOLD = 0.3f;
    public static readonly float LADDER_DELAY = 0.3f;
    public static readonly float MAX_CLIMB_ANGLE = 60f;
    // Animation attributes and names
    private static readonly string ANIMATION_H_SPEED = "hSpeed";
    private static readonly string ANIMATION_V_SPEED = "vSpeed";
    private static readonly string ANIMATION_JUMP = "jump";
    private static readonly string ANIMATION_GROUNDED = "grounded";
    private static readonly string ANIMATION_DASHING = "dashing";
    private static readonly string ANIMATION_WALL = "onWall";
    private static readonly string ANIMATION_FACING = "facingRight";
    private static readonly string ANIMATION_LADDER = "onLadder";

    // Other Componenents
    private Actor actor;
    private BoxCollider2D myCollider;
    private Animator animator;

    // Physics properties
    private RaycastOrigins raycastOrigins;
    private CollisionInfo collisions;
    [SerializeField]
    private Vector2 speed = Vector2.zero;
    [SerializeField]
    private Vector2 externalForce = Vector2.zero;
    private float horizontalRaySpacing;
    private float verticalRaySpacing;
    private float gravityScale = 1;
    private float ignorePlatforms = 0;
    private float ignoreLadders = 0;
    private int extraJumps = 0;
    private int airDashes = 0;
    private float dashCooldown = 0;
    private float dashStaggerTime = 0;
    private float ladderX = 0;

    // Public propoerties
    public bool FacingRight { get; set; } // false == left, true == right
    public bool OnLadder { get; set; }
    public bool KnockedBack { get; set; }
    public bool Dashing { get; set; }

    /// <summary>
    /// Start is called on the frame when a script is enabled just before
    /// any of the Update methods is called the first time.
    /// </summary>
    void Start() {
        actor = GetComponent<Actor>();
        myCollider = GetComponent<BoxCollider2D>();
        animator = GetComponent<Animator>();
        KnockedBack = false;
        OnLadder = false;
        Dashing = false;
        CalculateSpacing();
    }

    /// <summary>
    /// Update is called once pre frame
    /// </summary>
    void Update() {
        UpdateTimers();
        UpdateDash();
        UpdateKnockback();
        UpdateExternalForce();
        UpdateGravity();
        collisions.Reset();
        Move((speed + externalForce) * Time.deltaTime);
        SetAnimations();
    }

    /// <summary>
    /// Calculates the spacing only once based on how many rays will be used
    /// </summary>
    void CalculateSpacing() {
        Bounds bounds = myCollider.bounds;
        bounds.Expand(SKIN_WIDTH * BOUNDS_EXPANSION);
        horizontalRaySpacing = bounds.size.y / (RAY_COUNT - 1);
        verticalRaySpacing = bounds.size.x / (RAY_COUNT - 1);
    }

    /// <summary>
    /// The origin of each raycast must be updated every time before checking collisions
    /// </summary>
    void UpdateRaycastOrigins() {
        Bounds bounds = myCollider.bounds;
        bounds.Expand(SKIN_WIDTH * BOUNDS_EXPANSION);
        raycastOrigins.bottomLeft = new Vector2(bounds.min.x, bounds.min.y);
        raycastOrigins.bottomRight = new Vector2(bounds.max.x, bounds.min.y);
        raycastOrigins.topLeft = new Vector2(bounds.min.x, bounds.max.y);
        raycastOrigins.topRight = new Vector2(bounds.max.x, bounds.max.y);

    }

    /*-------------------------*/
    /*--------MOVEMENT---------*/
    /*-------------------------*/

    /// <summary>
    /// Tries to move according to current speed and checking for collisions
    /// </summary>
    public void Move(Vector2 velocity) {
        UpdateRaycastOrigins();
        float xDir = Mathf.Sign(velocity.x);
        CheckGround(xDir);
        if (velocity.x != 0) {
            // Slope checks and processing
            if (velocity.y <= 0 && actor.canUseSlopes) {
                if (collisions.onSlope) {
                    if (collisions.groundDirection == xDir) {
                        if ((!Dashing && dashStaggerTime <= 0) || actor.dashDownSlopes) {
                            DescendSlope(ref velocity);
                        }
                    } else {
                        ClimbSlope(ref velocity);
                    }
                }
            }
            HorizontalCollisions(ref velocity);
        }
        if (velocity.y > 0 || (velocity.y < 0 && (!collisions.onSlope || velocity.x == 0))) {
            VerticalCollisions(ref velocity);
        }
        if (collisions.onGround && velocity.x != 0) {
            HandleSlopeChange(ref velocity);
        }
        Debug.DrawRay(transform.position, velocity * 3f, Color.green);
        transform.Translate(velocity);
        // Checks for ground and ceiling, resets jumps if grounded
        if (collisions.vHit) {
            if (collisions.below) {
                ResetJumpsAndDashes();
            }
            speed.y = 0;
            externalForce.y = 0;
        }
    }

    /// <summary>
    /// Updates the actor's vertical speed according to gravity, gravity scale and other properties
    /// </summary>
    private void UpdateGravity() {
        if (!OnLadder && !Dashing && dashStaggerTime <= 0) {
            speed.y += GRAVITY * gravityScale * Time.deltaTime;
        }
        if (collisions.hHit && actor.canWallSlide && speed.y < 0) {
            speed.y = -actor.wallSlideVelocity;
        }
    }

    /// <summary>
    /// Adds the specified force to the actor's total external force
    /// </summary>
    /// <param name="force">Force to be added</param>
    public void ApplyForce(Vector2 force) {
        externalForce += force;
    }

    /// <summary>
    /// Sets the actor's external force to the specified amount
    /// </summary>
    /// <param name="force">Force to be set</param>
    public void SetForce(Vector2 force) {
        externalForce = force;
    }

    /// <summary>
    /// Reduces the external force over time according to the air or ground frictions
    /// </summary>
    private void UpdateExternalForce() {
        if (!Dashing && dashStaggerTime <= 0) {
            externalForce = Vector2.MoveTowards(externalForce, Vector2.zero, AIR_FRICTION * Time.deltaTime);
        }
    }

    /// <summary>
    /// Updates the actor's animator with the movement and collision values
    /// </summary>
    private void SetAnimations() {
        animator.SetFloat(ANIMATION_H_SPEED, speed.x + externalForce.x);
        animator.SetFloat(ANIMATION_V_SPEED, speed.y + externalForce.y);
        animator.SetBool(ANIMATION_GROUNDED, collisions.onGround);
        animator.SetBool(ANIMATION_DASHING, Dashing);
        animator.SetBool(ANIMATION_WALL, collisions.hHit);
        animator.SetBool(ANIMATION_FACING, FacingRight);
        animator.SetBool(ANIMATION_LADDER, OnLadder);
    }

    /// <summary>
    /// Checks if actor is touching the ground, used to adjust to slopes
    /// </summary>
    /// <param name="direction">Direction the actor is moving, -1 = left, 1 = right</param>
    private void CheckGround(float direction) {
        Vector2 rayOrigin = direction == 1 ? raycastOrigins.bottomLeft : raycastOrigins.bottomRight;
        rayOrigin.y += SKIN_WIDTH * 2;
        for (int i = 0; i < RAY_COUNT; i++) {
            rayOrigin += (direction == 1 ? Vector2.right : Vector2.left) * (verticalRaySpacing * i);
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.down,
                SKIN_WIDTH * 4f, LayerMask.GetMask(GROUND_LAYER, OW_PLATFORM_LAYER));
            if (hit) {
                collisions.onGround = true;
                collisions.groundAngle = Vector2.Angle(hit.normal, Vector2.up);
                collisions.groundDirection = Mathf.Sign(hit.normal.x);
                Debug.DrawRay(rayOrigin, Vector2.down * SKIN_WIDTH * 2, Color.blue);
                break;
            }
        }
    }

    /// <summary>
    /// Checks for collisions in the horizontal axis and adjust the speed accordingly to stop at the 
    /// collided object.
    /// </summary>
    /// <param name="velocity">The current object velocity used for the raycast lenght</param>
    private void HorizontalCollisions(ref Vector2 velocity) {
        float directionX = Mathf.Sign(velocity.x);
        float rayLength = Mathf.Abs(velocity.x) + SKIN_WIDTH;
        for (int i = 0; i < RAY_COUNT; i++) {
            Vector2 rayOrigin = directionX == -1 ? raycastOrigins.bottomLeft : raycastOrigins.bottomRight;
            rayOrigin += Vector2.up * (horizontalRaySpacing * i);
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.right * directionX,
                rayLength, LayerMask.GetMask(GROUND_LAYER));
            Debug.DrawRay(rayOrigin, Vector2.right * directionX * rayLength, Color.red);
            if (hit) {
                float angle = Vector2.Angle(hit.normal, Vector2.up);
                if (i == 0 && !collisions.onSlope && angle <= MAX_CLIMB_ANGLE) {
                    collisions.onGround = true;
                    collisions.groundAngle = angle;
                    collisions.groundDirection = Mathf.Abs(hit.normal.x);
                    velocity.x -= (hit.distance - SKIN_WIDTH) * directionX;
                    ClimbSlope(ref velocity);
                    velocity.x += (hit.distance - SKIN_WIDTH) * directionX;
                }
                if (!(i == 0 && collisions.onSlope)) {
                    if (angle > MAX_CLIMB_ANGLE) {
                        velocity.x = Mathf.Min(Mathf.Abs(velocity.x), (hit.distance - SKIN_WIDTH)) * directionX;
                        rayLength = Mathf.Min(Mathf.Abs(velocity.x) + SKIN_WIDTH, hit.distance);
                        if (collisions.onSlope) {
                            if (velocity.y < 0) {
                                velocity.y = 0;
                            } else {
                                velocity.y = Mathf.Tan(collisions.groundAngle * Mathf.Deg2Rad) *
                                    Mathf.Abs(velocity.x) * Mathf.Sign(velocity.y);
                            }
                        }
                        collisions.left = directionX < 0;
                        collisions.right = directionX > 0;
                        collisions.hHit = hit;
                        speed.x = 0;
                        externalForce.x = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks for collisions in the vertical axis and adjust the speed accordingly to stop at the 
    /// collided object.
    /// </summary>
    /// <param name="velocity">The current object velocity used for the raycast lenght</param>
    private void VerticalCollisions(ref Vector2 velocity) {
        if (OnLadder) {
            Vector2 origin = myCollider.bounds.center + Vector3.up *
                (myCollider.bounds.extents.y * Mathf.Sign(velocity.y) + velocity.y);
            Collider2D hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(GROUND_LAYER));
            if (!hit) {
                return;
            }
            hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(LADDER_LAYER));
            if (hit) {
                return;
            }
        }
        float directionY = Mathf.Sign(velocity.y);
        float rayLength = Mathf.Abs(velocity.y) + SKIN_WIDTH;
        for (int i = 0; i < RAY_COUNT; i++) {
            Vector2 rayOrigin = directionY == -1 ? raycastOrigins.bottomLeft : raycastOrigins.topLeft;
            rayOrigin += Vector2.right * (verticalRaySpacing * i + velocity.x);
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.up * directionY,
                rayLength, LayerMask.GetMask(GROUND_LAYER));
            Debug.DrawRay(rayOrigin, Vector2.up * directionY * rayLength, Color.red);
            // for one way platforms
            if (ignorePlatforms <= 0 && directionY < 0 && !hit) {
                hit = Physics2D.Raycast(rayOrigin, Vector2.up * directionY,
                    rayLength, LayerMask.GetMask(OW_PLATFORM_LAYER));
            }
            if (hit) {
                velocity.y = (hit.distance - SKIN_WIDTH) * directionY;
                rayLength = hit.distance;
                if (OnLadder && directionY < 0) {
                    OnLadder = false;
                    IgnoreLadders();
                }
                if (collisions.onSlope && directionY == 1) {
                    velocity.x = velocity.y / Mathf.Tan(collisions.groundAngle * Mathf.Deg2Rad) *
                        Mathf.Sign(velocity.x);
                    speed.x = 0;
                    externalForce.x = 0;
                }
                collisions.above = directionY > 0;
                collisions.below = directionY < 0;
                collisions.vHit = hit;
            }
        }
    }

    /// <summary>
    /// Tries to move the actor horizontally based on it's current movespeed and input pressure 
    /// while checking for movement impairments
    /// </summary>
    /// <param name="direction">-1 to 1; negative values = left; positive values = right</param>
    public void Walk(float direction) {
        if (CanMove() && !Dashing && dashStaggerTime <= 0) {
            if (direction < 0)
                FacingRight = false;
            if (direction > 0)
                FacingRight = true;
            if (OnLadder) {
                return;
            }
            float acc = 0f;
            float dec = 0f;
            if (actor.advancedAirControl && !collisions.below) {
                acc = actor.airAccelerationTime;
                dec = actor.airDecelerationTime;
            } else {
                acc = actor.accelerationTime;
                dec = actor.decelerationTime;
            }
            if (acc > 0) {
                if (Mathf.Abs(speed.x) < actor.maxSpeed) {
                    speed.x += direction * (1 / acc) * actor.maxSpeed * Time.deltaTime;
                    speed.x = Mathf.Min(Mathf.Abs(speed.x), actor.maxSpeed) * Mathf.Sign(speed.x);
                }
            } else {
                speed.x = actor.maxSpeed * direction;
            }
            if (direction == 0 || Mathf.Sign(direction) != Mathf.Sign(speed.x)) {
                if (dec > 0) {
                    speed.x = Mathf.MoveTowards(speed.x, 0, (1 / dec) * actor.maxSpeed * Time.deltaTime);
                } else {
                    speed.x = 0;
                }
            }
        }
    }

    /// <summary>
    /// Adjusts to ascending a slope, transforming horizontal velocity into the angle of the slope
    /// </summary>
    /// <param name="velocity">The current actor velocity</param>
    private void ClimbSlope(ref Vector2 velocity) {
        if (collisions.groundAngle <= MAX_CLIMB_ANGLE) {
            float distance = Mathf.Abs(velocity.x);
            float yVelocity = Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad) * distance;
            if (velocity.y <= yVelocity) {
                velocity.y = yVelocity;
                velocity.x = Mathf.Cos(collisions.groundAngle * Mathf.Deg2Rad) * distance * Mathf.Sign(velocity.x);
                collisions.below = true;
                speed.y = 0;
                externalForce.y = 0;
            }
        } else {
            collisions.groundAngle = 0;
        }
    }

    /// <summary>
    /// Adjusts to descending a slope, transforming horizontal velocity into the angle of the slope
    /// </summary>
    /// <param name="velocity">The current actor velocity</param>
    private void DescendSlope(ref Vector2 velocity) {
        if (collisions.groundAngle <= MAX_CLIMB_ANGLE) {
            float distance = Mathf.Abs(velocity.x);
            velocity.x = (Mathf.Cos(collisions.groundAngle * Mathf.Deg2Rad) * distance) * Mathf.Sign(velocity.x);
            velocity.y = -Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad) * distance;
            collisions.below = true;
            speed.y = 0;
            externalForce.y = 0;
        } else {
            collisions.groundAngle = 0;
        }
    }

    /// <summary>
    /// Checks for angle changes on the ground, preventing the actor from briefly passing through ground 
    /// and losing velocity or leaving the ground and floating (lots of trigonometry)
    /// </summary>
    /// <param name="velocity">The current actor velocity</param>
    private void HandleSlopeChange(ref Vector2 velocity) {
        float directionX = Mathf.Sign(velocity.x);
        if (velocity.y > 0) {
            // climb steeper slope
            float rayLength = Mathf.Abs(velocity.x) + SKIN_WIDTH * 2;
            Vector2 rayOrigin = (directionX == -1 ? raycastOrigins.bottomLeft : raycastOrigins.bottomRight) +
                Vector2.up * velocity.y;
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.right * directionX, rayLength, LayerMask.GetMask(GROUND_LAYER));
            if (hit) {
                float angle = Vector2.Angle(hit.normal, Vector2.up);
                if (angle != collisions.groundAngle) {
                    velocity.x = (hit.distance - SKIN_WIDTH) * directionX;
                    collisions.groundAngle = angle;
                }
            } else {
                // climb milder slope or flat ground
                rayOrigin = (directionX == -1 ? raycastOrigins.bottomLeft : raycastOrigins.bottomRight) + velocity;
                hit = Physics2D.Raycast(rayOrigin, Vector2.down, 1f, LayerMask.GetMask(GROUND_LAYER, OW_PLATFORM_LAYER));
                Debug.DrawRay(rayOrigin, Vector2.down, Color.yellow);
                if (hit) {
                    float angle = Vector2.Angle(hit.normal, Vector2.up);
                    float overshoot = 0;
                    if (angle < collisions.groundAngle) {
                        if (angle > 0) {
                            float tanA = Mathf.Tan(angle * Mathf.Deg2Rad);
                            float tanB = Mathf.Tan(collisions.groundAngle * Mathf.Deg2Rad);
                            float sin = Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad);
                            overshoot = (2 * tanA * hit.distance - tanB * hit.distance) /
                                (tanA * sin - tanB * sin);
                        } else {
                            overshoot = hit.distance / Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad);
                        }
                        float removeX = Mathf.Cos(collisions.groundAngle * Mathf.Deg2Rad) * overshoot * Mathf.Sign(velocity.x);
                        float removeY = Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad) * overshoot;
                        float addX = Mathf.Cos(angle * Mathf.Deg2Rad) * overshoot * Mathf.Sign(velocity.x);
                        float addY = Mathf.Sin(angle * Mathf.Deg2Rad) * overshoot;
                        velocity += new Vector2(addX - removeX, addY - removeY + SKIN_WIDTH);
                    }
                }
            }
        } else {
            // descend milder slope or flat ground
            float rayLength = Mathf.Abs(velocity.y) + SKIN_WIDTH;
            Vector2 rayOrigin = (directionX == -1 ? raycastOrigins.bottomRight : raycastOrigins.bottomLeft) +
                Vector2.right * velocity.x;
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.down, rayLength, LayerMask.GetMask(GROUND_LAYER, OW_PLATFORM_LAYER));
            float angle = Vector2.Angle(hit.normal, Vector2.up);
            if (hit && angle < collisions.groundAngle) {
                velocity.y = -(hit.distance - SKIN_WIDTH);
                collisions.groundAngle = angle;
            } else {
                // descend steeper slope
                if ((Dashing || dashStaggerTime > 0) && !actor.dashDownSlopes) {
                    return;
                }
                rayOrigin = (directionX == 1 ? raycastOrigins.bottomLeft : raycastOrigins.bottomRight) + velocity;
                hit = Physics2D.Raycast(rayOrigin, Vector2.down, 1f, LayerMask.GetMask(GROUND_LAYER, OW_PLATFORM_LAYER));
                Debug.DrawRay(rayOrigin, Vector2.down, Color.yellow);
                if (hit && Mathf.Sign(hit.normal.x) == directionX) {
                    angle = Vector2.Angle(hit.normal, Vector2.up);
                    float overshoot = 0;
                    if (angle > collisions.groundAngle) {
                        if (collisions.groundAngle > 0) {
                            float sin = Mathf.Sin((collisions.groundAngle) * Mathf.Deg2Rad);
                            float cos = Mathf.Cos((collisions.groundAngle) * Mathf.Deg2Rad);
                            float tan = Mathf.Tan(angle * Mathf.Deg2Rad);
                            overshoot = hit.distance * cos / (tan / cos - sin);
                        } else {
                            overshoot = hit.distance / Mathf.Tan(angle * Mathf.Deg2Rad);
                        }
                        float removeX = Mathf.Cos(collisions.groundAngle * Mathf.Deg2Rad) * overshoot * Mathf.Sign(velocity.x);
                        float removeY = -Mathf.Sin(collisions.groundAngle * Mathf.Deg2Rad) * overshoot;
                        float addX = Mathf.Cos(angle * Mathf.Deg2Rad) * overshoot * Mathf.Sign(velocity.x);
                        float addY = -Mathf.Sin(angle * Mathf.Deg2Rad) * overshoot;
                        velocity += new Vector2(addX - removeX, addY - removeY - SKIN_WIDTH);
                    }
                }
            }
        }
    }

    /// <summary>
    ///  Makes the actor jump if possible
    /// </summary>
    public void Jump() {
        if (CanMove() && (!Dashing || actor.canJumpDuringDash)) {
            if (collisions.below || extraJumps > 0 || (actor.canWallJump && collisions.hHit)) {
                // air jump
                if (!collisions.below && !OnLadder)
                    extraJumps--;
                float height = actor.jumpHeight;
                if (OnLadder) {
                    Vector2 origin = myCollider.bounds.center + Vector3.up * myCollider.bounds.extents.y;
                    Collider2D hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(GROUND_LAYER));
                    if (hit) {
                        return;
                    }
                    origin = myCollider.bounds.center + Vector3.down * myCollider.bounds.extents.y;
                    hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(GROUND_LAYER));
                    if (hit) {
                        return;
                    }
                    height = actor.ladderJumpHeight;
                    externalForce.x += actor.ladderJumpVelocity * (FacingRight ? 1 : -1);
                    OnLadder = false;
                    IgnoreLadders();
                    ResetJumpsAndDashes();
                }
                speed.y = Mathf.Sqrt(2 * Mathf.Abs(GRAVITY) * height);
                animator.SetTrigger(ANIMATION_JUMP);
                if (actor.jumpCancelStagger) {
                    dashStaggerTime = 0;
                }
                // wall jump
                if (actor.canWallJump && collisions.hHit && !collisions.below && !collisions.onSlope) {
                    externalForce.x += collisions.left ? actor.wallJumpVelocity : -actor.wallJumpVelocity;
                    ResetJumpsAndDashes();
                }
                ignorePlatforms = 0;
            }
        }
    }

    /// <summary>
    /// Makes the actor dash in the specified direction if possible.
    /// If omnidirectional dash is disabled, will only dash in the horizontal axis
    /// </summary>
    /// <param name="direction">The desired direction of the dash</param>
    public void Dash(Vector2 direction) {
        if (CanMove() && actor.canDash && dashCooldown <= 0) {
            if (OnLadder) {
                Vector2 origin = myCollider.bounds.center + Vector3.up * myCollider.bounds.extents.y;
                Collider2D hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(GROUND_LAYER));
                if (hit) {
                    return;
                }
                origin = myCollider.bounds.center + Vector3.down * myCollider.bounds.extents.y;
                hit = Physics2D.OverlapCircle(origin, 0, LayerMask.GetMask(GROUND_LAYER));
                if (hit) {
                    return;
                }
                OnLadder = false;
            }
            if (!collisions.onGround) {
                if (airDashes > 0) {
                    airDashes--;
                } else {
                    return;
                }
            }
            Dashing = true;
            if (direction.magnitude == 0 || (collisions.onGround && direction.y < 0)) {
                direction = FacingRight ? Vector2.right : Vector2.left;
            }
            // wall dash
            if (collisions.hHit) {
                direction = FacingRight ? Vector2.left : Vector2.right;
                ResetJumpsAndDashes();
            }
            if (!actor.omnidirectionalDash) {
                direction = Vector2.right * Mathf.Sign(direction.x);
            }
            direction = direction.normalized * actor.dashSpeed;
            speed.x = 0;
            speed.y = 0;
            externalForce += direction;
            dashCooldown = actor.maxDashCooldown;
            dashStaggerTime = actor.dashStagger;
            Invoke("StopDash", actor.dashDistance / actor.dashSpeed);
        }
    }

    /// <summary>
    /// Stops the dash after its duration has passed
    /// </summary>
    private void StopDash() {
        Dashing = false;
    }

    /// <summary>
    /// If the actor is standing on a platform, will ignore platforms briefly,
    /// otherwise it will just jump
    /// </summary>
    public void JumpDown() {
        if (CanMove()) {
            if (collisions.vHit &&
                collisions.vHit.collider.gameObject.layer == LayerMask.NameToLayer(OW_PLATFORM_LAYER)) {
                IgnorePlatforms();
            } else {
                Jump();
            }
        }
    }

    /// <summary>
    /// The actor will briefly ignore platforms so it can jump down through them
    /// </summary>
    private void IgnorePlatforms() {
        ignorePlatforms = FALLTHROUGH_DELAY;
    }

    /// <summary>
    /// The actor will briefly ignore ladders so it can jump or dash off of them
    /// </summary>
    private void IgnoreLadders() {
        ignoreLadders = LADDER_DELAY;
    }

    /// <summary>
    /// Gives the actor its maximum extra jumps and air dashes
    /// </summary>
    private void ResetJumpsAndDashes() {
        extraJumps = actor.maxExtraJumps;
        airDashes = actor.maxAirDashes;
    }

    /// <summary>
    /// If not already climbing a ladder, tries to find one and attach t it
    /// </summary>
    /// <param name="direction"></param>
    public void ClimbLadder(float direction) {
        if (ignoreLadders > 0 || Dashing) {
            return;
        }
        float radius = myCollider.bounds.extents.x;
        Vector2 topOrigin = ((Vector2) myCollider.bounds.center) + Vector2.up * (myCollider.bounds.extents.y - radius);
        Vector2 bottomOrigin = ((Vector2) myCollider.bounds.center) + Vector2.down *
            (myCollider.bounds.extents.y + radius + SKIN_WIDTH);
        if (!OnLadder && direction != 0 && Mathf.Abs(direction) > LADDER_CLIMB_THRESHOLD) {
            Collider2D hit = Physics2D.OverlapCircle(direction == -1 ? bottomOrigin : topOrigin,
                radius, LayerMask.GetMask(LADDER_LAYER));
            if (hit) {
                OnLadder = true;
                speed.x = 0;
                externalForce = Vector2.zero;
                ladderX = hit.transform.position.x;
            }
        }
        if (OnLadder) {
            float newX = Mathf.MoveTowards(transform.position.x, ladderX, 5f * Time.deltaTime);
            transform.Translate(newX - transform.position.x, 0, 0);
            ResetJumpsAndDashes();
            if (actor.ladderAccelerationTime > 0) {
                if (Mathf.Abs(speed.y) < actor.ladderSpeed) {
                    speed.y += direction * (1 / actor.ladderAccelerationTime) * actor.ladderSpeed * Time.deltaTime;
                }
            } else {
                speed.y = actor.ladderSpeed * direction;
            }
            if (direction == 0 || Mathf.Sign(direction) != Mathf.Sign(speed.y)) {
                if (actor.ladderDecelerationTime > 0) {
                    speed.y = Mathf.MoveTowards(speed.x, 0, (1 / actor.ladderDecelerationTime) *
                        actor.ladderSpeed * Time.deltaTime);
                } else {
                    speed.y = 0;
                }
            }
            if (Mathf.Abs(speed.y) > actor.ladderSpeed) {
                speed.y = Mathf.Min(speed.y, actor.ladderSpeed);
            }
            // checks ladder end
            Collider2D hit = Physics2D.OverlapCircle(topOrigin + Vector2.up * (speed.y * Time.deltaTime + radius),
                0, LayerMask.GetMask(LADDER_LAYER));
            if (!hit) {
                hit = Physics2D.OverlapCircle(bottomOrigin + Vector2.up * (speed.y * Time.deltaTime + radius),
                    0, LayerMask.GetMask(LADDER_LAYER));
                if (!hit) {
                    OnLadder = false;
                    if (speed.y > 0) {
                        speed.y = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Used to alter gravity strength for jump hold or other effects
    /// </summary>
    /// <param name="gravityScale">Desired gravity scale</param>
    public void SetGravityScale(float gravityScale) {
        this.gravityScale = gravityScale;
    }

    /// <summary>
    /// Handles knockback force and movement
    /// </summary>
    private void UpdateKnockback() {
        if (KnockedBack) {
            float directionX = Mathf.Sign(speed.x);
            float newHSpeed = Mathf.Abs(speed.x) - (AIR_FRICTION * Time.deltaTime);
            if (newHSpeed <= 0) {
                newHSpeed = 0;
                KnockedBack = false;
            }
            speed.x = newHSpeed * directionX;
        }
    }

    /// <summary>
    /// Updates dash related values
    /// </summary>
    private void UpdateDash() {
        if (dashCooldown > 0) {
            dashCooldown -= Time.deltaTime;
        }
        if (dashStaggerTime > 0 && !Dashing) {
            dashStaggerTime -= Time.deltaTime;
            externalForce *= Mathf.Max(1 - actor.staggerSpeedFalloff * Time.deltaTime, 0);
        }
    }

    /// <summary>
    /// Updates timers for different features
    /// </summary>
    private void UpdateTimers() {
        if (ignorePlatforms > 0) {
            ignorePlatforms -= Time.deltaTime;
        }
        if (ignoreLadders > 0) {
            ignoreLadders -= Time.deltaTime;
        }
    }

    /// <summary>
    /// Checks if there are any knockbacks or status that stop all forms of movement,
    /// including jumping and dashing
    /// </summary>
    /// <returns>Whether the actor can move or not</returns>
    public bool CanMove() {
        return (!KnockedBack);
    }

    /// <summary>
    /// Returns the actor's current speed and external force values
    /// </summary>
    /// <returns>The actor's total speed</returns>
    public Vector2 GetTotalSpeed() {
        return speed + externalForce;
    }

    // Used to store temporary locations of raycast origins (the corners of the collider)
    struct RaycastOrigins {
        public Vector2 topLeft, topRight, bottomLeft, bottomRight;
    }

    // Stores temporary collision info to be used during calculations
    struct CollisionInfo {
        public bool above, below, left, right;
        public RaycastHit2D hHit, vHit;
        public bool onGround;
        public float groundAngle;
        public float groundDirection;
        public bool onSlope { get { return onGround && groundAngle != 0; } }

        public void Reset() {
            above = false;
            below = false;
            left = false;
            right = false;
            hHit = new RaycastHit2D();
            vHit = new RaycastHit2D();
            onGround = false;
            groundAngle = 0;
            groundDirection = 0;
        }
    }
}