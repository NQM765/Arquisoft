using System.Collections.Generic;
using UnityEngine;

public static class RtsEntityRegistry
{
    static readonly Dictionary<int, RtsNetworkEntity> entitiesById = new Dictionary<int, RtsNetworkEntity>();

    public static void Register(RtsNetworkEntity entity)
    {
        if (entity == null || entity.EntityId == 0)
        {
            return;
        }

        entitiesById[entity.EntityId] = entity;
    }

    public static void Unregister(RtsNetworkEntity entity)
    {
        if (entity == null || entity.EntityId == 0)
        {
            return;
        }

        if (entitiesById.TryGetValue(entity.EntityId, out RtsNetworkEntity registered) && registered == entity)
        {
            entitiesById.Remove(entity.EntityId);
        }
    }

    public static bool TryGetEntity(int entityId, out RtsNetworkEntity entity)
    {
        return entitiesById.TryGetValue(entityId, out entity) && entity != null;
    }

    public static List<RtsNetworkEntity> GetAllEntities()
    {
        List<RtsNetworkEntity> entities = new List<RtsNetworkEntity>();
        foreach (RtsNetworkEntity entity in entitiesById.Values)
        {
            if (entity != null)
            {
                entities.Add(entity);
            }
        }

        return entities;
    }

    public static bool TryGetComponent<T>(int entityId, out T component) where T : Component
    {
        component = null;
        if (!TryGetEntity(entityId, out RtsNetworkEntity entity))
        {
            return false;
        }

        component = entity.GetComponent<T>();
        if (component == null)
        {
            component = entity.GetComponentInChildren<T>();
        }

        return component != null;
    }

    public static RtsNetworkEntity GetOrAdd(GameObject target, int entityId, int ownerSlot, RtsEntityKind kind)
    {
        if (target == null)
        {
            return null;
        }

        RtsNetworkEntity entity = target.GetComponent<RtsNetworkEntity>();
        if (entity == null)
        {
            entity = target.AddComponent<RtsNetworkEntity>();
        }

        entity.Configure(entityId, ownerSlot, kind);
        return entity;
    }

    public static void RefreshAllLocalCategories()
    {
        foreach (RtsNetworkEntity entity in Object.FindObjectsByType<RtsNetworkEntity>(FindObjectsSortMode.None))
        {
            if (entity != null)
            {
                entity.RefreshLocalCategory();
            }
        }
    }

    public static void Clear()
    {
        entitiesById.Clear();
    }

    public static int BuildResourceId(ResourceNode resource)
    {
        if (resource == null)
        {
            return 0;
        }

        Vector3 pos = resource.transform.position;
        int x = Mathf.RoundToInt(pos.x);
        int z = Mathf.RoundToInt(pos.z);
        int type = resource.resourceType == ResourceNode.ResourceType.Wood ? 1 : 2;
        return 300000 + (x + 10000) * 1000 + (z + 10000) * 10 + type;
    }
}
