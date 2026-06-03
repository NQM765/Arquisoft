using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class RtsMatchSnapshot
{
    public int version = 1;
    public int hostGeneration;
    public int playerCount;
    public RtsEntitySnapshot[] entities = new RtsEntitySnapshot[0];
}

[Serializable]
public class RtsEntitySnapshot
{
    public int entityId;
    public int ownerSlot;
    public int ownerUserId;
    public int kind;
    public int unitType;
    public RtsVector3Snapshot position;
    public RtsQuaternionSnapshot rotation;
    public float health;
    public bool depleted;
}

[Serializable]
public class RtsVector3Snapshot
{
    public float x;
    public float y;
    public float z;

    public static RtsVector3Snapshot From(Vector3 value)
    {
        return new RtsVector3Snapshot { x = value.x, y = value.y, z = value.z };
    }

    public Vector3 ToVector3()
    {
        return new Vector3(x, y, z);
    }
}

[Serializable]
public class RtsQuaternionSnapshot
{
    public float x;
    public float y;
    public float z;
    public float w;

    public static RtsQuaternionSnapshot From(Quaternion value)
    {
        return new RtsQuaternionSnapshot { x = value.x, y = value.y, z = value.z, w = value.w };
    }

    public Quaternion ToQuaternion()
    {
        return new Quaternion(x, y, z, w);
    }
}

public class RtsWorldSnapshotter : MonoBehaviour
{
    public static RtsWorldSnapshotter Instance { get; private set; }

    RtsMatchSnapshot pendingRestore;

    public static RtsWorldSnapshotter GetOrCreate()
    {
        if (Instance != null) return Instance;
        Instance = FindFirstObjectByType<RtsWorldSnapshotter>();
        if (Instance != null) return Instance;
        GameObject go = new GameObject("RtsWorldSnapshotter");
        Instance = go.AddComponent<RtsWorldSnapshotter>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public RtsMatchSnapshot Capture(int hostGeneration)
    {
        EnsureSceneResourcesRegistered();

        List<RtsEntitySnapshot> snapshots = new List<RtsEntitySnapshot>();
        int depletedResourceCount = 0;
        foreach (RtsNetworkEntity entity in RtsEntityRegistry.GetAllEntities())
        {
            if (entity == null) continue;
            bool depleted = IsResourceDepleted(entity);
            if (entity.Kind == RtsEntityKind.Resource && depleted)
            {
                depletedResourceCount++;
            }

            RtsEntitySnapshot snapshot = new RtsEntitySnapshot
            {
                entityId = entity.EntityId,
                ownerSlot = entity.OwnerSlot,
                ownerUserId = GetOwnerUserId(entity.OwnerSlot),
                kind = (int)entity.Kind,
                unitType = (int)GetUnitType(entity),
                position = RtsVector3Snapshot.From(entity.transform.position),
                rotation = RtsQuaternionSnapshot.From(entity.transform.rotation),
                health = ReadHealth(entity),
                depleted = depleted,
            };
            snapshots.Add(snapshot);
        }

        if (depletedResourceCount > 0)
        {
            Debug.Log("[MIGRATION] Snapshot captured depleted resources: " + depletedResourceCount);
        }

        return new RtsMatchSnapshot
        {
            version = 1,
            hostGeneration = hostGeneration,
            playerCount = MultiplayerBootstrap.Instance != null ? MultiplayerBootstrap.Instance.GetPlayerCount() : 1,
            entities = snapshots.ToArray(),
        };
    }

    public void QueueRestore(RtsMatchSnapshot snapshot)
    {
        pendingRestore = snapshot;
    }

    public void RestoreQueuedSnapshot()
    {
        if (pendingRestore == null) return;
        RtsMatchSnapshot snapshot = pendingRestore;
        pendingRestore = null;
        StartCoroutine(RestoreAfterSceneReady(snapshot));
    }

    IEnumerator RestoreAfterSceneReady(RtsMatchSnapshot snapshot)
    {
        yield return null;
        yield return null;
        Restore(snapshot);
    }

    void Restore(RtsMatchSnapshot snapshot)
    {
        if (snapshot?.entities == null || snapshot.entities.Length == 0)
        {
            return;
        }

        EnsureSceneResourcesRegistered();

        HashSet<int> snapshotIds = new HashSet<int>();
        int depletedResourceCount = 0;
        foreach (RtsEntitySnapshot entitySnapshot in snapshot.entities)
        {
            if (!TryResolveOwnerSlot(entitySnapshot, out int ownerSlot))
            {
                continue;
            }

            if ((RtsEntityKind)entitySnapshot.kind == RtsEntityKind.Resource && entitySnapshot.depleted)
            {
                depletedResourceCount++;
            }

            int entityId = RemapEntityId(entitySnapshot.entityId, entitySnapshot.ownerSlot, ownerSlot);
            snapshotIds.Add(entityId);
            ApplyEntitySnapshot(entitySnapshot, entityId, ownerSlot);
        }

        if (depletedResourceCount > 0)
        {
            Debug.Log("[MIGRATION] Restored depleted resources: " + depletedResourceCount);
        }

        foreach (RtsNetworkEntity entity in RtsEntityRegistry.GetAllEntities())
        {
            if (entity == null) continue;
            if (entity.Kind == RtsEntityKind.Unit && !snapshotIds.Contains(entity.EntityId))
            {
                Destroy(entity.gameObject);
            }
        }

        RtsEntityRegistry.RefreshAllLocalCategories();
    }

    void EnsureSceneResourcesRegistered()
    {
        foreach (ResourceNode resource in FindObjectsByType<ResourceNode>(FindObjectsSortMode.None))
        {
            if (resource == null)
            {
                continue;
            }

            RtsNetworkEntity entity = resource.GetComponent<RtsNetworkEntity>();
            if (entity == null || entity.Kind != RtsEntityKind.Resource || entity.EntityId == 0)
            {
                RtsEntityRegistry.GetOrAdd(
                    resource.gameObject,
                    RtsEntityRegistry.BuildResourceId(resource),
                    -1,
                    RtsEntityKind.Resource);
            }
        }
    }

    void ApplyEntitySnapshot(RtsEntitySnapshot snapshot, int entityId, int ownerSlot)
    {
        if (!RtsEntityRegistry.TryGetEntity(entityId, out RtsNetworkEntity entity))
        {
            entity = TrySpawnMissingEntity(snapshot, entityId, ownerSlot);
        }

        if (entity == null) return;

        if (entity.OwnerSlot != ownerSlot)
        {
            entity.Configure(entityId, ownerSlot, (RtsEntityKind)snapshot.kind);
        }

        entity.transform.SetPositionAndRotation(snapshot.position.ToVector3(), snapshot.rotation.ToQuaternion());
        ApplyHealth(entity, snapshot.health);
        StartCoroutine(ApplyHealthAfterStart(entity, snapshot.health));
        ApplyResourceState(entity, snapshot.depleted);
    }

    RtsNetworkEntity TrySpawnMissingEntity(RtsEntitySnapshot snapshot, int entityId, int ownerSlot)
    {
        if ((RtsEntityKind)snapshot.kind != RtsEntityKind.Unit)
        {
            return null;
        }

        GameObject prefab = FindUnitPrefab((RtsUnitType)snapshot.unitType);
        if (prefab == null)
        {
            Debug.LogWarning("[MIGRATION] No prefab found for restored unit type " + snapshot.unitType + ".");
            return null;
        }

        GameObject spawned = Instantiate(
            prefab,
            snapshot.position.ToVector3(),
            snapshot.rotation.ToQuaternion());
        return RtsEntityRegistry.GetOrAdd(
            spawned,
            entityId,
            ownerSlot,
            RtsEntityKind.Unit);
    }

    int GetOwnerUserId(int ownerSlot)
    {
        return MultiplayerBootstrap.Instance != null
            ? MultiplayerBootstrap.Instance.GetPlayerUserIdBySlot(ownerSlot)
            : 0;
    }

    bool TryResolveOwnerSlot(RtsEntitySnapshot snapshot, out int ownerSlot)
    {
        ownerSlot = snapshot.ownerSlot;
        if ((RtsEntityKind)snapshot.kind == RtsEntityKind.Resource || snapshot.ownerSlot < 0)
        {
            return true;
        }

        if (snapshot.ownerUserId <= 0)
        {
            return true;
        }

        int remappedSlot = MultiplayerBootstrap.Instance != null
            ? MultiplayerBootstrap.Instance.GetPlayerSlotByUserId(snapshot.ownerUserId)
            : snapshot.ownerSlot;
        if (remappedSlot < 0)
        {
            return false;
        }

        ownerSlot = remappedSlot;
        return true;
    }

    int RemapEntityId(int entityId, int oldOwnerSlot, int newOwnerSlot)
    {
        if (oldOwnerSlot == newOwnerSlot || oldOwnerSlot < 0 || newOwnerSlot < 0)
        {
            return entityId;
        }

        int startingUnitBase = 10000 + oldOwnerSlot * 1000;
        if (entityId > startingUnitBase && entityId < startingUnitBase + 1000)
        {
            return 10000 + newOwnerSlot * 1000 + (entityId - startingUnitBase);
        }

        int buildingBase = 20000 + oldOwnerSlot * 1000;
        if (entityId > buildingBase && entityId < buildingBase + 1000)
        {
            return 20000 + newOwnerSlot * 1000 + (entityId - buildingBase);
        }

        int spawnedUnitBase = 500000 + oldOwnerSlot * 10000;
        if (entityId > spawnedUnitBase && entityId < spawnedUnitBase + 10000)
        {
            return 500000 + newOwnerSlot * 10000 + (entityId - spawnedUnitBase);
        }

        return entityId;
    }

    GameObject FindUnitPrefab(RtsUnitType unitType)
    {
        foreach (EdificioCentral building in FindObjectsByType<EdificioCentral>(FindObjectsSortMode.None))
        {
            if (building == null || building.unidadPrefab == null) continue;
            if (RtsUnitTypeUtility.GetUnitType(building.unidadPrefab) == unitType)
            {
                return building.unidadPrefab;
            }
        }

        return null;
    }

    RtsUnitType GetUnitType(RtsNetworkEntity entity)
    {
        if (entity.Kind != RtsEntityKind.Unit) return RtsUnitType.Unknown;
        Humano unit = entity.GetComponent<Humano>();
        if (unit == null) unit = entity.GetComponentInChildren<Humano>();
        return RtsUnitTypeUtility.GetUnitType(unit);
    }

    float ReadHealth(RtsNetworkEntity entity)
    {
        Humano unit = entity.GetComponent<Humano>();
        if (unit == null) unit = entity.GetComponentInChildren<Humano>();
        if (unit != null) return unit.health;

        HealthSystem healthSystem = entity.GetComponent<HealthSystem>();
        if (healthSystem == null) healthSystem = entity.GetComponentInChildren<HealthSystem>();
        return healthSystem != null ? healthSystem.currentHealth : 0f;
    }

    void ApplyHealth(RtsNetworkEntity entity, float health)
    {
        Humano unit = entity.GetComponent<Humano>();
        if (unit == null) unit = entity.GetComponentInChildren<Humano>();
        if (unit != null)
        {
            unit.health = health;
            return;
        }

        HealthSystem healthSystem = entity.GetComponent<HealthSystem>();
        if (healthSystem == null) healthSystem = entity.GetComponentInChildren<HealthSystem>();
        if (healthSystem != null)
        {
            healthSystem.currentHealth = health;
            healthSystem.ActualizarUI();
        }
    }

    IEnumerator ApplyHealthAfterStart(RtsNetworkEntity entity, float health)
    {
        yield return null;
        if (entity != null)
        {
            ApplyHealth(entity, health);
        }
    }

    bool IsResourceDepleted(RtsNetworkEntity entity)
    {
        ResourceNode resource = entity.GetComponent<ResourceNode>();
        if (resource == null) resource = entity.GetComponentInChildren<ResourceNode>();
        return resource != null && resource.IsDepletedForSnapshot();
    }

    void ApplyResourceState(RtsNetworkEntity entity, bool depleted)
    {
        if (!depleted) return;

        ResourceNode resource = entity.GetComponent<ResourceNode>();
        if (resource == null) resource = entity.GetComponentInChildren<ResourceNode>();
        if (resource != null)
        {
            resource.ApplyDepletedFromSnapshot();
        }
    }
}
