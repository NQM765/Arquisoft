using TMPro;
using UnityEngine;

public class GameUIManager : MonoBehaviour
{
    public TextMeshProUGUI opponentText;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {   
        opponentText.text = "opponent: " + GameSession.Opponent;

    }


}
