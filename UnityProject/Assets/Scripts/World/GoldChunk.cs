using UnityEngine;

public class GoldChunk : MonoBehaviour
{
    public GameObject gold_full;
    public GameObject gold_mined;

    void Awake()
    {
        gold_full = transform.Find("gold_full").gameObject;
        gold_mined = transform.Find("gold_mined").gameObject;

        gold_full.SetActive(true);
        gold_mined.SetActive(false);

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
        resourceNode.resourceType = ResourceNode.ResourceType.Gold;
    }

    public void Mine()
    {
        if (gold_full != null) gold_full.SetActive(false);
        if (gold_mined != null) gold_mined.SetActive(true);
    }
}
