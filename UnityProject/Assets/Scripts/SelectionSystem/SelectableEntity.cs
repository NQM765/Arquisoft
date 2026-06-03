using UnityEngine;

public class SelectableEntity : MonoBehaviour
{
    public enum SelectableCategory
    {
        None,
        Unit,
        Building,
        Resource,
        EnemyUnit,
        EnemyBuilding,
        Ground
    }

    [Header("Selection")]
    [SerializeField] private SelectableCategory category = SelectableCategory.None;
    [SerializeField] private bool canBeSelected = true;
    [SerializeField] private bool allowBoxSelection = false;

    [Header("Highlight")]
    [SerializeField] private bool useHighlightColor = true;
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private Color selectedColor = Color.cyan;

    [Header("UI")]
    [SerializeField] private BuildingInteraction interactionUI;

    private Color originalColor;
    private bool hasOriginalColor;

    public SelectableCategory Category => category;
    public bool CanBeSelected => canBeSelected;
    public bool AllowBoxSelection => allowBoxSelection;

    void Awake()
    {
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<Renderer>();
            if (targetRenderer == null)
            {
                targetRenderer = GetComponentInChildren<Renderer>();
            }
        }

        if (targetRenderer != null)
        {
            originalColor = targetRenderer.material.color;
            hasOriginalColor = true;
        }

        if (interactionUI == null)
        {
            interactionUI = GetComponent<BuildingInteraction>();
        }
    }

    public void SetRuntimeCategory(SelectableCategory runtimeCategory)
    {
        category = runtimeCategory;
    }

    public void SetRuntimeBoxSelection(bool enabled)
    {
        allowBoxSelection = enabled;
    }

    public void SetSelected(bool selected)
    {
        if (!useHighlightColor) return;

        if (selected)
        {
            ApplyColorToRenderers(selectedColor);
            return;
        }

        RtsNetworkEntity networkEntity = GetComponent<RtsNetworkEntity>();
        if (networkEntity == null)
        {
            networkEntity = GetComponentInParent<RtsNetworkEntity>();
        }
        if (networkEntity == null)
        {
            networkEntity = GetComponentInChildren<RtsNetworkEntity>();
        }

        if (networkEntity != null)
        {
            networkEntity.RefreshLocalCategory();
        }
        else if (hasOriginalColor)
        {
            ApplyColorToRenderers(originalColor);
        }
    }

    private void ApplyColorToRenderers(Color color)
    {
        bool applied = false;
        foreach (SpriteRenderer spriteRenderer in GetComponentsInChildren<SpriteRenderer>(true))
        {
            spriteRenderer.color = color;
            applied = true;
        }

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is SpriteRenderer)
            {
                continue;
            }

            renderer.material.color = color;
            applied = true;
        }

        if (!applied && targetRenderer != null)
        {
            targetRenderer.material.color = color;
        }
    }

    public void ShowInspectionUI()
    {
        if (interactionUI != null)
        {
            interactionUI.ShowInspectionUI();
        }
    }

    public void ShowCommandUI()
    {
        if (interactionUI != null)
        {
            interactionUI.ShowCommandUI();
        }
    }

    public bool HasInteractionUI()
    {
        return interactionUI != null;
    }

}
