using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class MenuSceneManager : MonoBehaviour
{
    [Header("Scenes")]
    [SerializeField] string loginSceneName = "LoginScene";
    [SerializeField] string worldGenerationSceneName = "MapGeneratorScene";

    [Header("UI")]
    [SerializeField] TMP_Text statusText;

    public void OnPlayPressed()
    {
        if (ApiClient.Instance != null && ApiClient.Instance.IsAuthenticated)
        {
            LoadScene(worldGenerationSceneName);
            return;
        }

        SetStatus("Debes iniciar sesion para jugar.");
    }

    public void OnLoginPressed()
    {
        LoadScene(loginSceneName);
    }

    public void OnMultiplayerPressed()
    {

        SceneManager.LoadScene("MultiplayerMenuScene");

    }

    public void OnBackPressed()
    {
        int previousIndex = SceneManager.GetActiveScene().buildIndex - 1;
        if (previousIndex >= 0)
        {
            SceneManager.LoadScene(previousIndex);
            return;
        }

        SetStatus("No hay una escena anterior.");
    }

    public void OnQuitPressed()
    {
        Application.Quit();
    }

    public void LoadScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            SetStatus("Nombre de escena vacio.");
            return;
        }

        SceneManager.LoadScene(sceneName);
    }

    void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }
    }
}
