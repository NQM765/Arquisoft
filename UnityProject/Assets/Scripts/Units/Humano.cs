using UnityEngine;
using UnityEngine.AI;

/// Clase base para todas las unidades humanas.
/// Define la lógica común de movimiento, animación y manejo de recursos.
public abstract class Humano : MonoBehaviour
{
    [Header("Movement")]
    public float health = 100f;
    public float speed = 5f;
    public float stoppingDistance = 2f;
    public float stuckTimeout = 1.2f;
    public float progressEpsilon = 0.01f;

    [Header("Debug")]
    public bool enableKeyboardDebugMovement = false;

    [Header("Physics Tuning")]
    public float rbMass = 20f;
    public float rbDrag = 8f;

    // Componentes privados
    protected Rigidbody rb;
    protected Animator anim;
    protected SpriteRenderer spriteRenderer;
    protected Vector3 movement;
    protected bool hasMoveOrder;
    protected Vector3 moveTarget;
    protected ResourceNode resourceTarget;
    protected bool resourceActionPending;
    protected float previousDistanceToTarget = -1f;
    protected float stuckTimer;
    protected NavMeshAgent navMesh;

    protected virtual void Start()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        spriteRenderer = GetComponent<SpriteRenderer>();

        navMesh = GetComponent<NavMeshAgent>();
        navMesh.speed = speed;
        navMesh.stoppingDistance = 3.0f;


        if (rb == null)
        {
            Debug.LogWarning($"[UNIDAD] {name} necesita un Rigidbody para moverse.");
            enabled = false;
            return;
        }

        rb.freezeRotation = true;
        rb.isKinematic = false;
        rb.mass = rbMass;
        rb.linearDamping = rbDrag;
        rb.angularDamping = 999f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    protected virtual void Update()
    {
        if (enableKeyboardDebugMovement)
        {
            float moveX = Input.GetAxisRaw("Horizontal");
            float moveZ = Input.GetAxisRaw("Vertical");
            movement = new Vector3(moveX, 0, moveZ).normalized;
            hasMoveOrder = false;
            previousDistanceToTarget = -1f;
            stuckTimer = 0f;
        }
        else if (hasMoveOrder)
        {
            Vector3 toTarget = moveTarget - rb.position;
            toTarget.y = 0f;
            float currentDistance = toTarget.magnitude;

            if (previousDistanceToTarget >= 0f)
            {
                float progress = previousDistanceToTarget - currentDistance;
                if (progress > progressEpsilon)
                {
                    stuckTimer = 0f;
                }
                else
                {
                    stuckTimer += Time.deltaTime;
                }
            }

            previousDistanceToTarget = currentDistance;

            if (toTarget.sqrMagnitude <= stoppingDistance * stoppingDistance)
            {
                if (resourceTarget != null && resourceActionPending && GetComponent<Villager>() != null)
                {
                    resourceActionPending = false;
                    CompleteResourceAction();
                    Debug.Log($"<color=yellow>[UNIDAD]</color> {name} ha llegado a {resourceTarget.name} y comienza a recolectar.");
                }
                CancelMoveOrder();
            }
            else
            {
                movement = toTarget.normalized;

                if (stuckTimer >= stuckTimeout)
                {
                    Debug.Log($"<color=orange>[UNIDAD]</color> {name} atascada por obstaculo. Cancelando orden de movimiento.");
                    CancelMoveOrder();
                }
            }
        }
        else
        {
            movement = Vector3.zero;
        }

        if (anim != null)
        {
            bool isMoving = movement.magnitude > 0;
            anim.SetBool("isWalking", isMoving);
        }

        if (spriteRenderer != null)
        {
            UpdateFacing(movement);
        }

        CheckArrival();
    }

    protected virtual void FixedUpdate()
    {
        if (movement.magnitude > 0.1f)
        {
            Vector3 newPosition = rb.position + movement * speed * Time.fixedDeltaTime;
            rb.MovePosition(newPosition);
        }
    }

    public virtual void SetMoveTarget(Vector3 target, ResourceNode targetResource = null, Humano targetHuman = null)
    {
        MultiplayerBootstrap bootstrap = MultiplayerBootstrap.Instance;
        if (bootstrap != null && bootstrap.HasMatch)
        {
            if (bootstrap.IsMigrationActive || !RtsNetworkCommandBus.IsMultiplayerActive)
            {
                Debug.Log("[MIGRATION] Orden directa ignorada mientras se restaura la partida.");
                return;
            }
        }

        if (RtsNetworkCommandBus.IsMultiplayerActive)
        {
            RtsNetworkCommandBus.GetOrCreate().RequestMoveSelectedUnits(
                new System.Collections.Generic.List<GameObject> { gameObject },
                target,
                targetResource,
                targetHuman);
            return;
        }

        SetMoveTargetFromNetwork(target, targetResource, targetHuman);
    }

    public virtual void PauseForMigration()
    {
        CancelMoveOrder();
        movement = Vector3.zero;

        if (navMesh != null)
        {
            navMesh.ResetPath();
            navMesh.isStopped = true;
        }

        if (anim != null)
        {
            anim.SetBool("isWalking", false);
        }
    }

    public virtual void SetMoveTargetFromNetwork(Vector3 target, ResourceNode targetResource = null, Humano targetHuman = null)
    {
        if (navMesh != null && navMesh.isStopped)
        {
            navMesh.isStopped = false;
        }

        moveTarget = target;
        resourceTarget = targetResource;
        resourceActionPending = targetResource != null;

        if (navMesh == null)
        {
            Debug.LogWarning($"[UNIDAD] {name} no tiene NavMeshAgent. No se puede ejecutar orden de movimiento.");
            return;
        }
        else if (this.GetComponentInChildren<Villager>() == null && resourceTarget != null)
        {
            Debug.Log($"<color=Red>[UNIDAD]</color> {name} no es un Villager y no puede recolectar recursos. Orden de movimiento ignorada.");
            resourceTarget = null;
            resourceActionPending = false;
            return;
        }
        else if (this.GetComponentInChildren<Warrior>() == null && targetHuman != null)
        {
            Debug.Log($"<color=Red>[UNIDAD]</color> {name} no es un Warrior y no puede atacar unidades. Orden de movimiento ignorada.");
            return;
        }
        else
        {
            navMesh.SetDestination(moveTarget);
            hasMoveOrder = true;
            previousDistanceToTarget = -1f;
            stuckTimer = 0f;
            Debug.Log($"Unidad moviendose a {moveTarget} {(resourceTarget != null ? "para recolectar recurso" : "")}.");
        }
    }

    public virtual bool CheckArrival()
    {
        if (!navMesh.pathPending && navMesh.remainingDistance <= 2f)
        {
            Debug.Log("Está dentro del rango deseado");
            if (resourceTarget != null && resourceActionPending)
            {
                resourceActionPending = false;
                CompleteResourceAction();
                Debug.Log($"<color=yellow>[UNIDAD]</color> {name} ha llegado a {resourceTarget.name} y comienza a recolectar.");
            }
            return true;
        }
        return false;
    }

    protected virtual void CompleteResourceAction()
    {
        if (resourceTarget == null)
        {
            return;
        }


    }

    protected virtual void StopMovement()
    {
        speed = 0f;
        navMesh.isStopped = true;
    }

    protected virtual void RestartMovement()
    {
        navMesh.isStopped = false;
        speed = 5f;
    }


    protected virtual void CancelMoveOrder()
    {
        movement = Vector3.zero;
        hasMoveOrder = false;
        resourceTarget = null;
        resourceActionPending = false;
        previousDistanceToTarget = -1f;
        stuckTimer = 0f;
    }


    protected virtual void UpdateFacing(Vector3 direction)
    {
        if (direction.sqrMagnitude < 0.0001f) return;

        if (Mathf.Abs(direction.z) > Mathf.Abs(direction.x))
        {
            spriteRenderer.flipX = direction.z > 0f;
        }
        else
        {
            spriteRenderer.flipX = direction.x < 0f;
        }
    }


    public virtual bool TakeDamage(float damage)
    {

        Debug.Log($"{name} tiene de salud actual: {this.health}");

        this.health -= damage;
        Debug.Log($"{name} recibió {damage} daño. Salud actual: {this.health}");

        if (health <= 0f)
        {
            Debug.Log($"<color=Blue>[Derrota]</color> {name} ha sido derrotada.");
            Die();
            return false;
        }

        return true;
    }

    protected virtual void Die()
    {
        Debug.Log($"{name} ha muerto.");
        Destroy(gameObject);
    }


}
