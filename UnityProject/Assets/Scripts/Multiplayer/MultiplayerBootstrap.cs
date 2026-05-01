using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Orquesta el flujo multiplayer completo.
///
/// Flujo HOST  (botón "Crear Partida"):
///   1. RelayLobbyClient.StartHostWithRelay() → obtiene relayJoinCode
///   2. MatchmakingClient.CreateMatch()       → registra match + publica en RabbitMQ
///   3. Carga la escena de juego
///
/// Flujo CLIENTE (botón "Unirse a Partida"):
///   1. MatchmakingClient.GetNextAvailableMatch() → { matchId, relayJoinCode } de RabbitMQ
///   2. RelayLobbyClient.JoinByCode()             → conecta al relay del host
///   3. MatchmakingClient.JoinMatch()             → registra al cliente en el match
///   4. Carga la escena de juego
/// </summary>
public class MultiplayerBootstrap : MonoBehaviour
{
    [Header("Match Settings")]
    [SerializeField] int maxPlayers = 2;
    [SerializeField] string multiplayerSceneName = "MapGeneratorScene";

    public static MultiplayerBootstrap Instance { get; private set; }

    MatchmakingClient matchmakingClient;
    RelayLobbyClient relayLobbyClient;
    MatchmakingClient.MatchResponse currentMatch;

    public MatchmakingClient.MatchResponse CurrentMatch => currentMatch;
    public bool HasMatch => currentMatch != null && !string.IsNullOrEmpty(currentMatch.matchId);
    public bool IsHost => HasMatch && currentMatch.hostUserId == AuthSession.UserId;

    // ── Singleton ─────────────────────────────────────────────────────────────

    public static MultiplayerBootstrap GetOrCreate()
    {
        if (Instance != null) return Instance;
        Instance = FindFirstObjectByType<MultiplayerBootstrap>();
        if (Instance != null) return Instance;
        var go = new GameObject("MultiplayerBootstrap");
        Instance = go.AddComponent<MultiplayerBootstrap>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        matchmakingClient = MatchmakingClient.GetOrCreate();
        relayLobbyClient = RelayLobbyClient.GetOrCreate();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>
    /// Botón "Crear Partida".
    /// onError → se llama si algo falla (útil para restaurar la UI del menú).
    /// </summary>
    public void CreateMatch(Action<string> onError = null)
    {
        if (!AuthSession.IsAuthenticated)
        {
            const string msg = "Debes iniciar sesión antes de crear una partida.";
            Debug.LogWarning("[MULTIPLAYER] " + msg);
            onError?.Invoke(msg);
            return;
        }

        Debug.Log("[MULTIPLAYER] Creando partida...");

        relayLobbyClient.StartHostWithRelay(
            maxPlayers,
            relayJoinCode =>
            {
                matchmakingClient.CreateMatch(
                    relayJoinCode,
                    response =>
                    {
                        currentMatch = response;
                        Debug.Log($"[MULTIPLAYER] Partida creada: {response.matchId}");
                        LoadGameScene();
                    },
                    error =>
                    {
                        Debug.LogWarning("[MULTIPLAYER] Error creando match: " + error);
                        onError?.Invoke(error);
                    },
                    maxPlayers);
            },
            error =>
            {
                Debug.LogWarning("[MULTIPLAYER] Error iniciando Relay: " + error);
                onError?.Invoke(error);
            });
    }

    /// <summary>
    /// Botón "Unirse a Partida".
    /// onEmpty → se llama si no hay partidas en la cola.
    /// onError → se llama si algo falla.
    /// </summary>
    public void JoinMatch(Action onEmpty = null, Action<string> onError = null)
    {
        if (!AuthSession.IsAuthenticated)
        {
            const string msg = "Debes iniciar sesión antes de unirte a una partida.";
            Debug.LogWarning("[MULTIPLAYER] " + msg);
            onError?.Invoke(msg);
            return;
        }

        Debug.Log("[MULTIPLAYER] Buscando partida disponible...");

        matchmakingClient.GetNextAvailableMatch(
            nextMatch =>
            {
                Debug.Log($"[MULTIPLAYER] Partida encontrada: {nextMatch.matchId}");

                relayLobbyClient.JoinByCode(
                    nextMatch.relayJoinCode,
                    () =>
                    {
                        matchmakingClient.JoinMatch(
                            nextMatch.matchId,
                            response =>
                            {
                                currentMatch = response;
                                Debug.Log($"[MULTIPLAYER] Unido a partida: {response.matchId}");
                                LoadGameScene();
                            },
                            error =>
                            {
                                Debug.LogWarning("[MULTIPLAYER] Error registrando join: " + error);
                                onError?.Invoke(error);
                            });
                    },
                    error =>
                    {
                        Debug.LogWarning("[MULTIPLAYER] Error uniéndose al Relay: " + error);
                        onError?.Invoke(error);
                    });
            },
            onEmpty: () =>
            {
                Debug.Log("[MULTIPLAYER] No hay partidas disponibles.");
                onEmpty?.Invoke();
            },
            onError: error =>
            {
                Debug.LogWarning("[MULTIPLAYER] Error consultando cola: " + error);
                onError?.Invoke(error);
            });
    }

    /// <summary>Abandona la partida actual y limpia el estado.</summary>
    public void LeaveCurrentMatch()
    {
        string matchId = HasMatch ? currentMatch.matchId : null;
        currentMatch = null;

        if (RtsNetworkCommandBus.Instance != null)
            RtsNetworkCommandBus.Instance.Deactivate();

        if (RtsMultiplayerWorldInitializer.Instance != null)
            RtsMultiplayerWorldInitializer.Instance.ResetInitializationState();

        RtsEntityRegistry.Clear();
        relayLobbyClient?.LeaveCurrentSession();

        if (!string.IsNullOrEmpty(matchId) && matchmakingClient != null && AuthSession.IsAuthenticated)
        {
            matchmakingClient.LeaveMatch(
                matchId,
                _ => Debug.Log("[MULTIPLAYER] Partida abandonada."),
                error => Debug.LogWarning("[MULTIPLAYER] Error abandonando: " + error));
        }
    }

    // ── Helpers internos ─────────────────────────────────────────────────────

    void LoadGameScene() => SceneManager.LoadScene(multiplayerSceneName);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!HasMatch) return;
        RtsMultiplayerWorldInitializer.GetOrCreate().InitializeForCurrentMatch();
        RtsNetworkCommandBus.GetOrCreate().Activate();
    }

    // ── Utilidades ────────────────────────────────────────────────────────────

    public int GetLocalPlayerSlot() => GetPlayerSlotByUserId(AuthSession.UserId);

    public int GetPlayerSlotByUserId(int userId)
    {
        if (currentMatch?.players == null) return 0;
        for (int i = 0; i < currentMatch.players.Length; i++)
            if (currentMatch.players[i]?.userId == userId) return i;
        return -1;
    }

    public int GetPlayerCount()
    {
        if (currentMatch?.players == null) return 1;
        return Mathf.Clamp(currentMatch.players.Length, 1, 4);
    }

}