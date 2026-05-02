using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class RtsNetworkCommandBus : MonoBehaviour
{
    const string MoveRequestMessage = "rts.move.request";
    const string MoveApplyMessage = "rts.move.apply";
    const string ResourceGatherApplyMessage = "rts.resource.gather.apply";
    const string ProductionRequestMessage = "rts.production.request";
    const string ProductionStartedApplyMessage = "rts.production.started.apply";
    const string UnitSpawnedApplyMessage = "rts.unit.spawned.apply";

    // Mensajes de sincronización de reinicio
    const string ClientSceneReadyMessage = "rts.scene.client_ready";
    const string HostReloadSceneMessage = "rts.scene.host_reload";

    public static RtsNetworkCommandBus Instance { get; private set; }

    bool registered;
    int spawnedUnitSequence = 1;

    public static bool IsMultiplayerActive
    {
        get
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            return networkManager != null
                && networkManager.IsListening
                && MultiplayerBootstrap.Instance != null
                && MultiplayerBootstrap.Instance.HasMatch;
        }
    }

    public static bool IsServer
    {
        get
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            return networkManager != null && networkManager.IsServer;
        }
    }

    public static RtsNetworkCommandBus GetOrCreate()
    {
        if (Instance != null) return Instance;
        Instance = FindFirstObjectByType<RtsNetworkCommandBus>();
        if (Instance != null) return Instance;
        GameObject go = new GameObject("RtsNetworkCommandBus");
        Instance = go.AddComponent<RtsNetworkCommandBus>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        UnregisterHandlers();
    }

    public void Activate()
    {
        if (registered) return;
        StartCoroutine(RegisterWhenReady());
    }

    public void Deactivate()
    {
        UnregisterHandlers();
        StopAllCoroutines();
    }

    IEnumerator RegisterWhenReady()
    {
        while (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            yield return null;
        RegisterHandlers();
    }

    void RegisterHandlers()
    {
        if (registered || NetworkManager.Singleton == null) return;

        CustomMessagingManager messaging = NetworkManager.Singleton.CustomMessagingManager;
        messaging.RegisterNamedMessageHandler(MoveRequestMessage, OnMoveRequestMessage);
        messaging.RegisterNamedMessageHandler(MoveApplyMessage, OnMoveApplyMessage);
        messaging.RegisterNamedMessageHandler(ResourceGatherApplyMessage, OnResourceGatherApplyMessage);
        messaging.RegisterNamedMessageHandler(ProductionRequestMessage, OnProductionRequestMessage);
        messaging.RegisterNamedMessageHandler(ProductionStartedApplyMessage, OnProductionStartedApplyMessage);
        messaging.RegisterNamedMessageHandler(UnitSpawnedApplyMessage, OnUnitSpawnedApplyMessage);
        messaging.RegisterNamedMessageHandler(ClientSceneReadyMessage, OnClientSceneReadyMessage);
        messaging.RegisterNamedMessageHandler(HostReloadSceneMessage, OnHostReloadSceneMessage);
        registered = true;
    }

    void EnsureRegisteredNow()
    {
        if (!registered && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            RegisterHandlers();
    }

    void UnregisterHandlers()
    {
        if (!registered || NetworkManager.Singleton == null) return;

        CustomMessagingManager messaging = NetworkManager.Singleton.CustomMessagingManager;
        messaging.UnregisterNamedMessageHandler(MoveRequestMessage);
        messaging.UnregisterNamedMessageHandler(MoveApplyMessage);
        messaging.UnregisterNamedMessageHandler(ResourceGatherApplyMessage);
        messaging.UnregisterNamedMessageHandler(ProductionRequestMessage);
        messaging.UnregisterNamedMessageHandler(ProductionStartedApplyMessage);
        messaging.UnregisterNamedMessageHandler(UnitSpawnedApplyMessage);
        messaging.UnregisterNamedMessageHandler(ClientSceneReadyMessage);
        messaging.UnregisterNamedMessageHandler(HostReloadSceneMessage);
        registered = false;
    }

    // ── Sincronización de reinicio ────────────────────────────────────────────

    /// <summary>
    /// El cliente llama esto cuando su escena terminó de cargar y está listo.
    /// El host recibirá el mensaje y recargará su propia escena.
    /// </summary>
    public void SendClientSceneReady()
    {
        if (!IsMultiplayerActive || IsServer) return;

        EnsureRegisteredNow();
        using (FastBufferWriter writer = new FastBufferWriter(4, Allocator.Temp))
        {
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                ClientSceneReadyMessage,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }

        Debug.Log("[MULTIPLAYER] Cliente envió ClientSceneReady al host.");
    }

    void OnClientSceneReadyMessage(ulong senderId, FastBufferReader reader)
    {
        if (!IsServer) return;

        Debug.Log("[MULTIPLAYER] Host recibió ClientSceneReady — recargando escena.");

        // Avisar al cliente que el host también va a recargar
        using (FastBufferWriter writer = new FastBufferWriter(4, Allocator.Temp))
        {
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                HostReloadSceneMessage,
                senderId,
                writer,
                NetworkDelivery.Reliable);
        }

        // Recargar la escena del host
        MultiplayerBootstrap.Instance?.TriggerHostReinitialize();
    }

    void OnHostReloadSceneMessage(ulong senderId, FastBufferReader reader)
    {
        if (IsServer) return;

        Debug.Log("[MULTIPLAYER] Cliente recibió HostReloadScene — recargando escena.");
        MultiplayerBootstrap.Instance?.TriggerClientReinitialize();
    }

    // ── Movimiento ────────────────────────────────────────────────────────────

    public bool RequestMoveSelectedUnits(List<GameObject> selectedUnits, Vector3 destination, ResourceNode resourceTarget)
    {
        if (!IsMultiplayerActive) return false;

        EnsureRegisteredNow();

        int localSlot = MultiplayerBootstrap.Instance.GetLocalPlayerSlot();
        List<int> unitIds = new List<int>();

        foreach (GameObject selected in selectedUnits)
        {
            if (selected == null) continue;
            RtsNetworkEntity entity = selected.GetComponent<RtsNetworkEntity>();
            if (entity == null) entity = selected.GetComponentInParent<RtsNetworkEntity>();
            if (entity == null || entity.Kind != RtsEntityKind.Unit || entity.OwnerSlot != localSlot) continue;
            unitIds.Add(entity.EntityId);
            if (unitIds.Count >= 128) break;
        }

        if (unitIds.Count == 0) return true;

        int resourceId = 0;
        if (resourceTarget != null)
        {
            RtsNetworkEntity resourceEntity = resourceTarget.GetComponent<RtsNetworkEntity>();
            if (resourceEntity == null) resourceEntity = resourceTarget.GetComponentInParent<RtsNetworkEntity>();
            resourceId = resourceEntity != null ? resourceEntity.EntityId : 0;
        }

        if (IsServer)
        {
            HandleMoveRequest(AuthSession.UserId, unitIds.ToArray(), destination, resourceId);
            return true;
        }

        SendMoveRequest(unitIds, destination, resourceId);
        return true;
    }

    public static bool TryHandleResourceArrival(abr unit, ResourceNode resource)
    {
        if (!IsMultiplayerActive) return false;
        if (!IsServer) return true;

        RtsNetworkEntity unitEntity = unit != null ? unit.GetComponent<RtsNetworkEntity>() : null;
        RtsNetworkEntity resourceEntity = resource != null ? resource.GetComponent<RtsNetworkEntity>() : null;
        if (unitEntity == null || resourceEntity == null) return true;

        if (resource.TryFarmResourceLocal(true))
            GetOrCreate().BroadcastResourceGathered(resourceEntity.EntityId, unitEntity.OwnerSlot);

        return true;
    }

    public static bool TryRequestProduction(EdificioCentral building)
    {
        if (!IsMultiplayerActive) return false;

        RtsNetworkEntity buildingEntity = building != null ? building.GetComponent<RtsNetworkEntity>() : null;
        if (buildingEntity == null) return true;
        if (buildingEntity.OwnerSlot != MultiplayerBootstrap.Instance.GetLocalPlayerSlot()) return true;

        RtsNetworkCommandBus bus = GetOrCreate();
        bus.EnsureRegisteredNow();
        if (IsServer)
            bus.HandleProductionRequest(AuthSession.UserId, buildingEntity.EntityId);
        else
            bus.SendProductionRequest(buildingEntity.EntityId);

        return true;
    }

    void SendMoveRequest(List<int> unitIds, Vector3 destination, int resourceId)
    {
        FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(AuthSession.UserId);
            WriteIntArray(ref writer, unitIds);
            WriteVector3(ref writer, destination);
            writer.WriteValueSafe(resourceId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                MoveRequestMessage,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.ReliableSequenced);
        }
        finally { writer.Dispose(); }
    }

    void OnMoveRequestMessage(ulong senderId, FastBufferReader reader)
    {
        if (!IsServer) return;
        reader.ReadValueSafe(out int userId);
        int[] unitIds = ReadIntArray(ref reader);
        Vector3 destination = ReadVector3(ref reader);
        reader.ReadValueSafe(out int resourceId);
        HandleMoveRequest(userId, unitIds, destination, resourceId);
    }

    void HandleMoveRequest(int userId, int[] unitIds, Vector3 destination, int resourceId)
    {
        int ownerSlot = MultiplayerBootstrap.Instance.GetPlayerSlotByUserId(userId);
        if (ownerSlot < 0) return;

        List<int> authorizedUnitIds = new List<int>();
        foreach (int unitId in unitIds)
        {
            if (!RtsEntityRegistry.TryGetEntity(unitId, out RtsNetworkEntity entity)) continue;
            if (entity.Kind == RtsEntityKind.Unit && entity.OwnerSlot == ownerSlot)
                authorizedUnitIds.Add(unitId);
        }

        if (authorizedUnitIds.Count == 0) return;

        ApplyMoveOrder(authorizedUnitIds.ToArray(), destination, resourceId);
        BroadcastMoveApply(authorizedUnitIds, destination, resourceId);
    }

    void BroadcastMoveApply(List<int> unitIds, Vector3 destination, int resourceId)
    {
        FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
        try
        {
            WriteIntArray(ref writer, unitIds);
            WriteVector3(ref writer, destination);
            writer.WriteValueSafe(resourceId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                MoveApplyMessage, writer, NetworkDelivery.ReliableSequenced);
        }
        finally { writer.Dispose(); }
    }

    void OnMoveApplyMessage(ulong senderId, FastBufferReader reader)
    {
        if (IsServer) return;
        int[] unitIds = ReadIntArray(ref reader);
        Vector3 destination = ReadVector3(ref reader);
        reader.ReadValueSafe(out int resourceId);
        ApplyMoveOrder(unitIds, destination, resourceId);
    }

    void ApplyMoveOrder(int[] unitIds, Vector3 destination, int resourceId)
    {
        ResourceNode resource = null;
        if (resourceId != 0) RtsEntityRegistry.TryGetComponent(resourceId, out resource);

        foreach (int unitId in unitIds)
        {
            if (RtsEntityRegistry.TryGetComponent(unitId, out abr unit))
                unit.SetMoveTargetFromNetwork(destination, resource);
        }
    }

    void BroadcastResourceGathered(int resourceId, int ownerSlot)
    {
        using (FastBufferWriter writer = new FastBufferWriter(128, Allocator.Temp))
        {
            writer.WriteValueSafe(resourceId);
            writer.WriteValueSafe(ownerSlot);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                ResourceGatherApplyMessage, writer, NetworkDelivery.ReliableSequenced);
        }
    }

    void OnResourceGatherApplyMessage(ulong senderId, FastBufferReader reader)
    {
        if (IsServer) return;
        reader.ReadValueSafe(out int resourceId);
        reader.ReadValueSafe(out int ownerSlot);
        if (RtsEntityRegistry.TryGetComponent(resourceId, out ResourceNode resource))
            resource.ApplyGatheredFromNetwork(true);
    }

    void SendProductionRequest(int buildingId)
    {
        using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
        {
            writer.WriteValueSafe(AuthSession.UserId);
            writer.WriteValueSafe(buildingId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                ProductionRequestMessage,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.ReliableSequenced);
        }
    }

    void OnProductionRequestMessage(ulong senderId, FastBufferReader reader)
    {
        if (!IsServer) return;
        reader.ReadValueSafe(out int userId);
        reader.ReadValueSafe(out int buildingId);
        HandleProductionRequest(userId, buildingId);
    }

    void HandleProductionRequest(int userId, int buildingId)
    {
        int ownerSlot = MultiplayerBootstrap.Instance.GetPlayerSlotByUserId(userId);
        if (ownerSlot < 0) return;
        if (!RtsEntityRegistry.TryGetEntity(buildingId, out RtsNetworkEntity buildingEntity)) return;
        if (buildingEntity.Kind != RtsEntityKind.Building || buildingEntity.OwnerSlot != ownerSlot) return;

        EdificioCentral building = buildingEntity.GetComponent<EdificioCentral>();
        if (building == null || building.estaProduciendo) return;

        int newUnitId = AllocateSpawnedUnitId(ownerSlot);
        ApplyProductionStarted(buildingId);
        BroadcastProductionStarted(buildingId);
        StartCoroutine(CompleteProductionAfterDelay(buildingId, newUnitId, ownerSlot));
    }

    int AllocateSpawnedUnitId(int ownerSlot)
    {
        int entityId = 500000 + ownerSlot * 10000 + spawnedUnitSequence;
        spawnedUnitSequence++;
        return entityId;
    }

    IEnumerator CompleteProductionAfterDelay(int buildingId, int newUnitId, int ownerSlot)
    {
        if (!RtsEntityRegistry.TryGetComponent(buildingId, out EdificioCentral building)) yield break;
        yield return new WaitForSeconds(building.GetProductionDuration());
        if (!RtsEntityRegistry.TryGetComponent(buildingId, out building)) yield break;

        Vector3 spawnPosition = building.GetSpawnPosition();
        Quaternion spawnRotation = building.GetSpawnRotation();
        ApplyUnitSpawned(buildingId, newUnitId, ownerSlot, spawnPosition, spawnRotation);
        BroadcastUnitSpawned(buildingId, newUnitId, ownerSlot, spawnPosition, spawnRotation);
    }

    void BroadcastProductionStarted(int buildingId)
    {
        using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
        {
            writer.WriteValueSafe(buildingId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                ProductionStartedApplyMessage, writer, NetworkDelivery.ReliableSequenced);
        }
    }

    void OnProductionStartedApplyMessage(ulong senderId, FastBufferReader reader)
    {
        if (IsServer) return;
        reader.ReadValueSafe(out int buildingId);
        ApplyProductionStarted(buildingId);
    }

    void ApplyProductionStarted(int buildingId)
    {
        if (RtsEntityRegistry.TryGetComponent(buildingId, out EdificioCentral building))
            building.BeginNetworkProductionVisual();
    }

    void BroadcastUnitSpawned(int buildingId, int newUnitId, int ownerSlot, Vector3 spawnPosition, Quaternion spawnRotation)
    {
        FastBufferWriter writer = new FastBufferWriter(128, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(buildingId);
            writer.WriteValueSafe(newUnitId);
            writer.WriteValueSafe(ownerSlot);
            WriteVector3(ref writer, spawnPosition);
            WriteQuaternion(ref writer, spawnRotation);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                UnitSpawnedApplyMessage, writer, NetworkDelivery.ReliableSequenced);
        }
        finally { writer.Dispose(); }
    }

    void OnUnitSpawnedApplyMessage(ulong senderId, FastBufferReader reader)
    {
        if (IsServer) return;
        reader.ReadValueSafe(out int buildingId);
        reader.ReadValueSafe(out int newUnitId);
        reader.ReadValueSafe(out int ownerSlot);
        Vector3 spawnPosition = ReadVector3(ref reader);
        Quaternion spawnRotation = ReadQuaternion(ref reader);
        ApplyUnitSpawned(buildingId, newUnitId, ownerSlot, spawnPosition, spawnRotation);
    }

    void ApplyUnitSpawned(int buildingId, int newUnitId, int ownerSlot, Vector3 spawnPosition, Quaternion spawnRotation)
    {
        if (RtsEntityRegistry.TryGetComponent(buildingId, out EdificioCentral building))
            building.SpawnProducedUnitFromNetwork(newUnitId, ownerSlot, spawnPosition, spawnRotation);
    }

    // ── Serialización ─────────────────────────────────────────────────────────

    static void WriteIntArray(ref FastBufferWriter writer, List<int> values)
    {
        writer.WriteValueSafe(values.Count);
        for (int i = 0; i < values.Count; i++) writer.WriteValueSafe(values[i]);
    }

    static void WriteIntArray(ref FastBufferWriter writer, int[] values)
    {
        writer.WriteValueSafe(values.Length);
        for (int i = 0; i < values.Length; i++) writer.WriteValueSafe(values[i]);
    }

    static int[] ReadIntArray(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out int rawCount);
        rawCount = Mathf.Clamp(rawCount, 0, 512);
        int storedCount = Mathf.Min(rawCount, 128);
        int[] values = new int[storedCount];
        for (int i = 0; i < rawCount; i++)
        {
            reader.ReadValueSafe(out int value);
            if (i < storedCount) values[i] = value;
        }
        return values;
    }

    static void WriteVector3(ref FastBufferWriter writer, Vector3 value)
    {
        writer.WriteValueSafe(value.x);
        writer.WriteValueSafe(value.y);
        writer.WriteValueSafe(value.z);
    }

    static Vector3 ReadVector3(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);
        return new Vector3(x, y, z);
    }

    static void WriteQuaternion(ref FastBufferWriter writer, Quaternion value)
    {
        writer.WriteValueSafe(value.x);
        writer.WriteValueSafe(value.y);
        writer.WriteValueSafe(value.z);
        writer.WriteValueSafe(value.w);
    }

    static Quaternion ReadQuaternion(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);
        reader.ReadValueSafe(out float w);
        return new Quaternion(x, y, z, w);
    }
}