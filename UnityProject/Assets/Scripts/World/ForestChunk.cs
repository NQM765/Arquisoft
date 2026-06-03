using UnityEngine;

public class ForestChunk : MonoBehaviour
{
    public GameObject forest_full;
    public GameObject forest_cut;

    void Awake()
    {
        forest_full = transform.Find("forest_full").gameObject;
        forest_cut = transform.Find("forest_cut").gameObject;

        forest_full.SetActive(true);
        forest_cut.SetActive(false);

        SelectableEntity selectable = GetComponent<SelectableEntity>();
        if (selectable == null)
        {
            selectable = gameObject.AddComponent<SelectableEntity>();
        }
        selectable.SetRuntimeCategory(SelectableEntity.SelectableCategory.Resource);
        selectable.SetRuntimeBoxSelection(false);

        ResourceNode resourceNode = GetComponent<ResourceNode>();
        if (resourceNode == null)
        {
            resourceNode = gameObject.AddComponent<ResourceNode>();
        }
        resourceNode.resourceType = ResourceNode.ResourceType.Wood;
    }

    public void Cut()
    {
        if (forest_full != null) forest_full.SetActive(false);
        if (forest_cut != null) forest_cut.SetActive(true);
    }
}
