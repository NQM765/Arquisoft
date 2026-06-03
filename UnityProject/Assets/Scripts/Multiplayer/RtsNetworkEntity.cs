using UnityEngine;

public class RtsNetworkEntity : MonoBehaviour
{
    [SerializeField] int entityId;
    [SerializeField] int ownerSlot = -1;
    [SerializeField] RtsEntityKind kind = RtsEntityKind.None;

    static readonly Color[] PlayerColors =
    {
        new Color(0.20f, 0.55f, 1.00f, 1f),
        new Color(1.00f, 0.35f, 0.22f, 1f),
        new Color(0.20f, 0.85f, 0.40f, 1f),
        new Color(1.00f, 0.82f, 0.20f, 1f),
    };

    public int EntityId => entityId;
    public int OwnerSlot => ownerSlot;
    public RtsEntityKind Kind => kind;

    void OnEnable()
    {
        RtsEntityRegistry.Register(this);
        RefreshLocalCategory();
    }

    void OnDisable()
    {
        RtsEntityRegistry.Unregister(this);
    }

    public void Configure(int newEntityId, int newOwnerSlot, RtsEntityKind newKind)
    {
        if (entityId != 0)
        {
            RtsEntityRegistry.Unregister(this);
        }

        entityId = newEntityId;
        ownerSlot = newOwnerSlot;
        kind = newKind;

        RtsEntityRegistry.Register(this);
        RefreshLocalCategory();
    }

    public bool IsOwnedByLocalPlayer()
    {
        int localSlot = MultiplayerBootstrap.Instance != null ? MultiplayerBootstrap.Instance.GetLocalPlayerSlot() : 0;
        return ownerSlot >= 0 && ownerSlot == localSlot;
    }

    public void RefreshLocalCategory()
    {
        SelectableEntity selectable = GetComponent<SelectableEntity>();
        if (selectable == null)
        {
            selectable = GetComponentInChildren<SelectableEntity>();
        }

        if (selectable != null)
        {
            switch (kind)
            {
                case RtsEntityKind.Unit:
                    selectable.SetRuntimeCategory(IsOwnedByLocalPlayer()
                        ? SelectableEntity.SelectableCategory.Unit
                        : SelectableEntity.SelectableCategory.EnemyUnit);
                    selectable.SetRuntimeBoxSelection(IsOwnedByLocalPlayer());
                    break;
                case RtsEntityKind.Building:
                    selectable.SetRuntimeCategory(IsOwnedByLocalPlayer()
                        ? SelectableEntity.SelectableCategory.Building
                        : SelectableEntity.SelectableCategory.EnemyBuilding);
                    selectable.SetRuntimeBoxSelection(false);
                    break;
                case RtsEntityKind.Resource:
                    selectable.SetRuntimeCategory(SelectableEntity.SelectableCategory.Resource);
                    selectable.SetRuntimeBoxSelection(false);
                    break;
            }
        }

        ApplyOwnerColor();
    }

    void ApplyOwnerColor()
    {
        if (ownerSlot < 0 || kind == RtsEntityKind.Resource)
        {
            return;
        }

        Color color = PlayerColors[Mathf.Abs(ownerSlot) % PlayerColors.Length];

        foreach (SpriteRenderer spriteRenderer in GetComponentsInChildren<SpriteRenderer>(true))
        {
            spriteRenderer.color = color;
        }

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is SpriteRenderer)
            {
                continue;
            }

            ApplyRendererColor(renderer, color);
        }
    }

    static void ApplyRendererColor(Renderer renderer, Color color)
    {
        if (renderer == null || renderer.sharedMaterial == null)
        {
            return;
        }

        MaterialPropertyBlock block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);

        if (renderer.sharedMaterial.HasProperty("_BaseColor"))
        {
            block.SetColor("_BaseColor", color);
        }
        else if (renderer.sharedMaterial.HasProperty("_Color"))
        {
            block.SetColor("_Color", color);
        }

        renderer.SetPropertyBlock(block);
    }
}
