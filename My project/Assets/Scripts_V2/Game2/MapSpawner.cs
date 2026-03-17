using Unity.Netcode;
using UnityEngine;

// Agrega esto:
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class MapSpawner : NetworkBehaviour
{
    public GameObject cubePrefab;

    void Update()
    {
        if (!IsSpawned) return;

        bool clicked = false;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null) return; // ← agrega esto
        clicked = Mouse.current.leftButton.wasPressedThisFrame;
#else
        clicked = Input.GetMouseButtonDown(0);
#endif

        if (!clicked) return; // ← sale inmediatamente si no hay click

        Ray ray = Camera.main.ScreenPointToRay(
#if ENABLE_INPUT_SYSTEM
                Mouse.current.position.ReadValue()
#else
            Input.mousePosition
#endif
        );

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            if (IsClient && !IsHost)
                SpawnCubeServerRpc(hit.point);
            if (IsHost)
                SpawnCube(hit.point);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnCubeServerRpc(Vector3 position) => SpawnCube(position);

    void SpawnCube(Vector3 position)
    {
        var cube = Instantiate(cubePrefab, position + Vector3.up * 0.5f, Quaternion.identity);
        cube.GetComponent<NetworkObject>().Spawn();
    }
}