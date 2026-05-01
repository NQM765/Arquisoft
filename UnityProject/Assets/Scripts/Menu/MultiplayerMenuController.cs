using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Controlador de la escena de menú multiplayer.
///
/// Arrastra en el Inspector:
///   btnCreateMatch  → Button "Crear Partida"
///   btnJoinMatch    → Button "Unirse a Partida"
///   btnBack         → Button "Volver"
///   statusText      → Text de estado (opcional)
///   mainMenuScene   → nombre de la escena anterior, ej: "MainMenu"
/// </summary>
public class MultiplayerMenuController : MonoBehaviour
{
    [Header("Botones")]
    [SerializeField] Button btnCreateMatch;
    [SerializeField] Button btnJoinMatch;
    [SerializeField] Button btnBack;

    [Header("UI")]
    [SerializeField] TextMeshProUGUI statusText;

    [Header("Navegación")]
    [SerializeField] string mainMenuScene = "MainMenu";

    MultiplayerBootstrap bootstrap;

    void Start()
    {
        bootstrap = MultiplayerBootstrap.GetOrCreate();

        btnCreateMatch.onClick.AddListener(OnCreateMatch);
        btnJoinMatch.onClick.AddListener(OnJoinMatch);
        btnBack.onClick.AddListener(OnBack);

        SetStatus("");
    }

    void OnDestroy()
    {
        if (btnCreateMatch != null) btnCreateMatch.onClick.RemoveListener(OnCreateMatch);
        if (btnJoinMatch != null) btnJoinMatch.onClick.RemoveListener(OnJoinMatch);
        if (btnBack != null) btnBack.onClick.RemoveListener(OnBack);
    }

    void OnCreateMatch()
    {
        SetInteractable(false);
        SetStatus("Creando partida...");

        bootstrap.CreateMatch(
            onError: error =>
            {
                SetInteractable(true);
                SetStatus("Error: " + error);
            });
    }

    void OnJoinMatch()
    {
        SetInteractable(false);
        SetStatus("Buscando partida disponible...");

        bootstrap.JoinMatch(
            onEmpty: () =>
            {
                SetInteractable(true);
                SetStatus("No hay partidas disponibles. Intenta de nuevo o crea una.");
            },
            onError: error =>
            {
                SetInteractable(true);
                SetStatus("Error: " + error);
            });
    }

    void OnBack()
    {
        SceneManager.LoadScene(mainMenuScene);
    }

    void SetInteractable(bool value)
    {
        btnCreateMatch.interactable = value;
        btnJoinMatch.interactable = value;
        // Volver siempre activo para poder cancelar
    }

    void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }
}