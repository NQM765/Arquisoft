using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Orquesta el flujo multiplayer completo.
///
/// Flujo HOST (botón "Crear Partida"):
///   1. RelayLobbyClient.StartHostWithRelay() → obtiene relayJoinCode
///   2. MatchmakingClient.CreateMatch()       → registra match + publica en RabbitMQ
///   3. Carga la escena de juego (solo con 1 jugador, host puede jugar)
///   4. Cuando el cliente carga su escena, le avisa al host via red
///   5. Host recibe el aviso, refresca el match y ambos recargan la escena juntos
///
/// Flujo CLIENTE (botón "Unirse a Partida"):
///   1. MatchmakingClient.GetNextAvailableMatch() → { matchId, relayJoinCode }
///   2. RelayLobbyClient.JoinByCode()             → conecta al relay del host
///   3. MatchmakingClient.JoinMatch()             → registra al cliente en el match
///   4. Carga la escena de juego
///   5. Al cargar, avisa al host que está listo → ambos recargan juntos
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

    // true durante el reinicio coordinado para no procesar OnSceneLoaded dos veces
    bool isReinitializing;

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
                        Debug.Log("[MULTIPLAYER] Partida creada: " + response.matchId);
                        SceneManager.LoadScene(multiplayerSceneName);
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
                Debug.Log("[MULTIPLAYER] Partida encontrada: " + nextMatch.matchId);

                relayLobbyClient.JoinByCode(
                    nextMatch.relayJoinCode,
                    () =>
                    {
                        matchmakingClient.JoinMatch(
                            nextMatch.matchId,
                            response =>
                            {
                                currentMatch = response;
                                Debug.Log("[MULTIPLAYER] Unido a partida: " + response.matchId);
                                SceneManager.LoadScene(multiplayerSceneName);
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

    public void LeaveCurrentMatch()
    {
        string matchId = HasMatch ? currentMatch.matchId : null;
        currentMatch = null;
        isReinitializing = false;

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

    // ── Reinicio coordinado ───────────────────────────────────────────────────

    /// <summary>
    /// Llamado por RtsNetworkCommandBus cuando el HOST recibe el aviso del cliente.
    /// Refresca el match y recarga la escena.
    /// </summary>
    public void TriggerHostReinitialize()
    {
        if (!HasMatch || isReinitializing) return;
        StartCoroutine(HostReinitializeCoroutine());
    }

    /// <summary>
    /// Llamado por RtsNetworkCommandBus cuando el CLIENTE recibe la confirmación del host.
    /// Ambos recargan la escena al mismo tiempo.
    /// </summary>
    public void TriggerClientReinitialize()
    {
        if (!HasMatch || isReinitializing) return;
        isReinitializing = true;
        Debug.Log("[MULTIPLAYER] Cliente recargando escena para reinicio coordinado.");
        ResetWorldState();
        SceneManager.LoadScene(multiplayerSceneName);
    }

    IEnumerator HostReinitializeCoroutine()
    {
        isReinitializing = true;
        Debug.Log("[MULTIPLAYER] Host refrescando match antes de recargar escena...");

        // Refrescar el match para tener los 2 jugadores actualizados
        bool refreshDone = false;
        matchmakingClient.GetMatch(
            currentMatch.matchId,
            response =>
            {
                currentMatch = response;
                Debug.Log("[MULTIPLAYER] Match refrescado: " + (response.players?.Length ?? 0) + " jugadores.");
                refreshDone = true;
            },
            error =>
            {
                Debug.LogWarning("[MULTIPLAYER] Error refrescando match: " + error + " — continuando.");
                refreshDone = true;
            });

        yield return new WaitUntil(() => refreshDone);
        yield return null;

        Debug.Log("[MULTIPLAYER] Host recargando escena para reinicio coordinado.");
        ResetWorldState();
        SceneManager.LoadScene(multiplayerSceneName);
    }

    void ResetWorldState()
    {
        RtsEntityRegistry.Clear();
        if (RtsMultiplayerWorldInitializer.Instance != null)
            RtsMultiplayerWorldInitializer.Instance.ResetInitializationState();
        if (GameSessionStats.GetOrCreate() != null)
            GameSessionStats.GetOrCreate().ResetSession();
    }

    // ── Scene loaded ──────────────────────────────────────────────────────────

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!HasMatch) return;

        // Inicializar el mundo en la escena recién cargada
        RtsMultiplayerWorldInitializer.GetOrCreate().InitializeForCurrentMatch();
        RtsNetworkCommandBus.GetOrCreate().Activate();

        if (isReinitializing)
        {
            // Reinicio coordinado completado en este lado
            isReinitializing = false;
            Debug.Log("[MULTIPLAYER] Reinicio coordinado completado.");
        }
        else if (!IsHost)
        {
            // Primera carga del cliente: avisar al host que la escena está lista
            StartCoroutine(SendClientReadyAfterDelay());
        }
        // Si es el host en primera carga, juega solo hasta que llegue el cliente
    }

    IEnumerator SendClientReadyAfterDelay()
    {
        // Esperar a que el CommandBus esté registrado y la red esté lista
        yield return new WaitUntil(() =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening);

        // Dos frames extra de seguridad para que los handlers estén registrados
        yield return null;
        yield return null;

        Debug.Log("[MULTIPLAYER] Cliente enviando ClientSceneReady al host.");
        RtsNetworkCommandBus.GetOrCreate().SendClientSceneReady();
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