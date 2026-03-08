using UnityEngine;
using TMPro;

public class GameHUDController : MonoBehaviour
{
    [Header("Recursos")]
    public TMP_Text goldText;
    public TMP_Text woodText;
    public TMP_Text foodText;

    [Header("Aldeanos")]
    public TMP_Text villagersText;

    [Header("Tiempo")]
    public TMP_Text timerText;

    private float elapsedSeconds = 0f;

    void Start()
    {
        SetGold(0);
        SetWood(0);
        SetFood(0);
        SetVillagers(0);
    }

    void Update()
    {
        elapsedSeconds += Time.deltaTime;
        int m = (int)(elapsedSeconds / 60f);
        int s = (int)(elapsedSeconds % 60f);
        if (timerText != null)
            timerText.text = $"{m:00}:{s:00}";
    }

    public void SetGold(int v)     { if (goldText)      goldText.text      = $"Oro: {v}"; }
    public void SetWood(int v)     { if (woodText)      woodText.text      = $"Madera: {v}"; }
    public void SetFood(int v)     { if (foodText)      foodText.text      = $"Comida: {v}"; }
    public void SetVillagers(int v){ if (villagersText) villagersText.text = $"Aldeanos: {v}"; }
}