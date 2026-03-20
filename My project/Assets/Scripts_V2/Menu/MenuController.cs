using TMPro;
using UnityEngine;

public class MenuController : MonoBehaviour {

    public TextMeshProUGUI usernameText;

    private void Start()
    {
        usernameText.text = "Bienvenido, " + AuthManager.Username;
    }

}
