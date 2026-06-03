using UnityEngine;
using UnityEngine.InputSystem;


public class MovementController : MonoBehaviour
{
    private Camera cam;
    private Mouse mouse;
    private UnitSelection selectionSystem;

    void Start()
    {
        cam = Camera.main;
        mouse = Mouse.current;
        selectionSystem = GetComponent<UnitSelection>();

        if (selectionSystem == null)
        {
            selectionSystem = FindFirstObjectByType<UnitSelection>();
        }

        if (cam == null)
        {
            Debug.LogWarning("[MOVEMENT] No se encontro MainCamera. MovementController no podra lanzar ordenes.");
        }

        if (selectionSystem == null)
        {
            Debug.LogWarning("[MOVEMENT] No se encontro UnitSelection en escena.");
        }
    }

    void Update()
    {
        if (mouse == null) return;
        if (cam == null) cam = Camera.main;
        if (selectionSystem == null) selectionSystem = FindFirstObjectByType<UnitSelection>();

        if (cam == null || selectionSystem == null) return;

        if (selectionSystem.HasSelectedFriendlyUnits())
        {
            if (mouse.rightButton.wasPressedThisFrame)
                EjecutarOrden3D(false);
            else if (mouse.rightButton.isPressed)
                EjecutarOrden3D(true);
        }

    }

    void EjecutarOrden3D(bool clicMantenido)
    {
        Vector2 mousePos = mouse.position.ReadValue();
        if (!SelectionTargetResolver.TryGetBestTarget(cam, mousePos, out SelectionTargetResolver.TargetInfo targetInfo))
        {
            // Fallback: si no hay SelectableEntity bajo el cursor, intentamos mover al primer collider tocado.
            Ray fallbackRay = cam.ScreenPointToRay(mousePos);
            if (Physics.Raycast(fallbackRay, out RaycastHit fallbackHit))
            {
                if (!clicMantenido)
                {
                    int movedUnits = OrdenarMovimientoUnidades(fallbackHit.point);
                    Debug.Log($"<color=cyan>[MOVIMIENTO]</color> Orden fallback a {movedUnits} unidades hacia {fallbackHit.point}");
                }
            }
            return;
        }

        SelectableEntity objetivoPrioritario = targetInfo.selectable;
        Vector3 puntoDestino = targetInfo.worldPoint;

        if (objetivoPrioritario != null)
        {
            BuildingInteraction.HideAllUI();

            if (clicMantenido)
            {
                if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.Ground)
                {
                    Debug.Log($"<color=#00FFCC>[SIGUIENDO]</color> Actualizando ruta hacia {puntoDestino}");
                }
                return;
            }

            if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.EnemyUnit)
            {
                Debug.Log($"<color=red>[ATAQUE]</color> Ataque a unidad enemiga: {objetivoPrioritario.name}");

                Humano targetHuman = GetHumanoFromSelectable(objetivoPrioritario);

                if (targetHuman != null)
                {
                    int movedUnits = OrdenarMovimientoUnidades(puntoDestino, null, targetHuman);
                    Debug.Log($"<color=red>[ATAQUE]</color> Ordenado movimiento de unidades a unidad enemiga: {objetivoPrioritario.name}");
                }

            }
            else if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.EnemyBuilding)
            {
                Debug.Log($"<color=red>[ATAQUE]</color> Ataque a edificio enemigo: {objetivoPrioritario.name}");
            }
            else if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.Ground)
            {
                int movedUnits = OrdenarMovimientoUnidades(puntoDestino, null, null);
                Debug.Log($"<color=cyan>[MOVIMIENTO]</color> Acción de movimiento para {movedUnits} unidades hacia {puntoDestino}");
            }
            else if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.Building)
            {
                BuildingState state = objetivoPrioritario.GetComponent<BuildingState>();

                if (state == null)
                {
                    Debug.Log($"<color=orange>[EDIFICIO]</color> {objetivoPrioritario.name} no tiene BuildingState. No hay acción automática.");
                }
                else if (state.currentState != BuildingState.State.Normal)
                {
                    Debug.Log($"<color=orange>[AVISO]</color> El edificio ya está en estado: {state.currentState}. Manteniendo orden actual.");
                }
                else
                {
                    state.currentState = BuildingState.State.Repairing;
                    Debug.Log($"<color=orange>[ACTION]</color> Orden directa: REPAIR sobre {objetivoPrioritario.name}.");
                }
            }
            else if (objetivoPrioritario.Category == SelectableEntity.SelectableCategory.Resource)
            {
                ResourceNode resource = objetivoPrioritario.GetComponent<ResourceNode>();

                if(resource.resourceState == ResourceNode.ResourceState.Available){
                    string action = resource != null ? resource.GetActionName() : "RECOLECTAR";
                    Debug.Log($"<color=yellow>[ACTION]</color> Orden directa: {action} en {objetivoPrioritario.name}.");

                    int movedUnits = OrdenarMovimientoUnidades(puntoDestino, resource, null);
                    Debug.Log($"<color=cyan>[MOVIMIENTO]</color> Acción de movimiento para {movedUnits} unidades hacia {puntoDestino}");
                }
                else{
                    Debug.Log($"<color=orange>[AVISO]</color> El recurso {objetivoPrioritario.name} ya está agotado.");
                }
            }
        }
    }

    private int OrdenarMovimientoUnidades(Vector3 destino, ResourceNode resourceTarget = null, Humano targetHuman = null)
    {

        if (selectionSystem == null) return 0;

        MultiplayerBootstrap bootstrap = MultiplayerBootstrap.Instance;
        if (bootstrap != null && bootstrap.HasMatch)
        {
            if (bootstrap.IsMigrationActive || !RtsNetworkCommandBus.IsMultiplayerActive)
            {
                Debug.Log("[MIGRATION] Orden de movimiento ignorada mientras se restaura la partida.");
                return 0;
            }
        }

        if (RtsNetworkCommandBus.IsMultiplayerActive)
        {
            bool handledByNetwork = RtsNetworkCommandBus.GetOrCreate().RequestMoveSelectedUnits(
                selectionSystem.selectedUnits,
                destino,
                resourceTarget,
                targetHuman);
            return handledByNetwork ? selectionSystem.selectedUnits.Count : 0;
        }

        int movedCount = 0;

        foreach (GameObject selected in selectionSystem.selectedUnits)
        {
            if (selected == null) continue;

            SelectableEntity selectable = SelectionTargetResolver.GetSelectableFromObject(selected);
            if (selectable == null || selectable.Category != SelectableEntity.SelectableCategory.Unit) continue;

            Humano unitMovement = selected.GetComponent<Humano>();
            if (unitMovement == null)
            {
                unitMovement = selected.GetComponentInChildren<Humano>();
            }

            if (unitMovement != null)
            {
                unitMovement.SetMoveTarget(destino, resourceTarget, targetHuman);
                movedCount++;
            }
        }

        if (movedCount == 0)
        {
            Debug.LogWarning("[MOVIMIENTO] No se encontró componente Humano en unidades seleccionadas para mover.");
        }

        return movedCount;
    }

    private Humano GetHumanoFromSelectable(SelectableEntity selectable)
    {
        if (selectable == null)
        {
            return null;
        }

        Humano unit = selectable.GetComponent<Humano>();
        if (unit == null)
        {
            unit = selectable.GetComponentInParent<Humano>();
        }
        if (unit == null)
        {
            unit = selectable.GetComponentInChildren<Humano>();
        }

        return unit;
    }
}
