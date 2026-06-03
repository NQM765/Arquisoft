using System;
using System.Collections;
using System.Threading.Tasks;
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
    [SerializeField] int maxPlayers = 3;
    [SerializeField] string multiplayerSceneName = "MapGeneratorScene";

    [Header("Reliability")]
    [SerializeField] float hostHeartbeatInterval = 5f;
    [SerializeField] float hostSnapshotInterval = 30f;
    [SerializeField] float migrationPollInterval = 5f;
    [SerializeField] int migrationStartMaxAttempts = 3;
    [SerializeField] int migrationJoinMaxAttempts = 5;
    [SerializeField] float migrationRetryDelay = 2f;

    public static MultiplayerBootstrap Instance { get; private set; }

    MatchmakingClient matchmakingClient;
    RelayLobbyClient relayLobbyClient;
    MatchmakingClient.MatchResponse currentMatch;
    Coroutine hostReliabilityRoutine;
    Coroutine clientMigrationRoutine;
    int snapshotSequence;
    bool snapshotSaveInProgress;
    bool snapshotSaveQueued;
    bool migrationInProgress;
    bool networkCallbacksRegistered;
    string migrationStatusMessage;
    float migrationStatusUntil;

    // true durante el reinicio coordinado para no procesar OnSceneLoaded dos veces
    bool isReinitializing;

    public MatchmakingClient.MatchResponse CurrentMatch => currentMatch;
    public bool HasMatch => currentMatch != null && !string.IsNullOrEmpty(currentMatch.matchId);
    public bool IsHost => HasMatch && IsLocalNetworkHost;
    public bool IsMigrationActive => migrationInProgress || (HasMatch && currentMatch.status == "migrating");
    bool IsBackendHost => HasMatch && currentMatch.hostUserId == AuthSession.UserId;
    bool CanRunHostDuties => HasMatch && IsBackendHost && IsLocalNetworkHost && !migrationInProgress;
    bool IsLocalNetworkHost
    {
        get
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            return networkManager != null
                && networkManager.IsListening
                && networkManager.IsServer
                && !networkManager.ShutdownInProgress;
        }
    }

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
        RtsWorldSnapshotter.GetOrCreate();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
        UnregisterNetworkCallbacks();
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
                        SetCurrentMatch(response);
                        Debug.Log("[MULTIPLAYER] Partida creada: " + response.matchId);
                        SceneManager.LoadScene(multiplayerSceneName);
                    },
                    error =>
                    {
                        Debug.LogWarning("[MULTIPLAYER] Error creando match: " + error);
                        relayLobbyClient.LeaveCurrentSession();
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

        matchmakingClient.JoinNextAvailableMatch(
            response =>
            {
                string relayJoinCode = response.relay != null ? response.relay.relayJoinCode : string.Empty;
                Debug.Log("[MULTIPLAYER] Partida reservada: " + response.matchId);

                relayLobbyClient.JoinByCode(
                    relayJoinCode,
                    () =>
                    {
                        SetCurrentMatch(response);
                        Debug.Log("[MULTIPLAYER] Unido a partida: " + response.matchId);
                        SceneManager.LoadScene(multiplayerSceneName);
                    },
                    error =>
                    {
                        Debug.LogWarning("[MULTIPLAYER] Error uniendose al Relay reservado: " + error);
                        matchmakingClient.LeaveMatch(
                            response.matchId,
                            _ => Debug.Log("[MULTIPLAYER] Reserva liberada tras fallo de Relay."),
                            leaveError => Debug.LogWarning("[MULTIPLAYER] Error liberando reserva: " + leaveError));
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
                Debug.LogWarning("[MULTIPLAYER] Error reservando partida: " + error);
                onError?.Invoke(error);
            });
    }

    void RecoverStaleMatchFromQueue(MatchmakingClient.NextMatchData nextMatch, Action<string> onError)
    {
        if (nextMatch == null || string.IsNullOrEmpty(nextMatch.matchId))
        {
            onError?.Invoke("La partida ya no esta disponible.");
            return;
        }

        ShowMigrationStatus("La sesion anterior ya no existe. Recuperando partida...", 12f);
        matchmakingClient.GetMigrationState(
            nextMatch.matchId,
            state =>
            {
                if (state == null || state.matchId != nextMatch.matchId)
                {
                    onError?.Invoke("No se pudo recuperar el estado de migracion.");
                    return;
                }

                currentMatch = state;
                migrationInProgress = false;
                StopReliabilityRoutines();

                if (state.hostStale)
                {
                    if (IsLocalMigrationHostCandidate(state))
                    {
                        BeginHostClaim(state);
                    }
                    else
                    {
                        ShowMigrationStatus("Esperando al nuevo host de la partida...", 12f);
                        StartReliabilityRoutines();
                    }
                    return;
                }

                bool hasNewRelay = state.relay != null
                    && !string.IsNullOrEmpty(state.relay.relayJoinCode)
                    && (state.hostGeneration > nextMatch.hostGeneration
                        || state.relay.relayJoinCode != nextMatch.relayJoinCode);

                if (state.hostUserId != AuthSession.UserId
                    && state.relay != null
                    && hasNewRelay)
                {
                    JoinMigratedHost(state);
                    return;
                }

                onError?.Invoke("La partida aun esta migrando. Intenta unirte de nuevo en unos segundos.");
            },
            error =>
            {
                Debug.LogWarning("[MIGRATION] No se pudo recuperar la partida tras Join fallido: " + error);
                onError?.Invoke("La partida ya no esta disponible o aun esta migrando.");
            });
    }

    bool IsRelayNotFoundError(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        string normalized = error.ToLowerInvariant();
        return normalized.Contains("lobby not found")
            || normalized.Contains("not found")
            || normalized.Contains("does not exist")
            || normalized.Contains("404");
    }

    public void LeaveCurrentMatch()
    {
        string matchId = HasMatch ? currentMatch.matchId : null;
        currentMatch = null;
        isReinitializing = false;
        migrationInProgress = false;
        StopReliabilityRoutines();
        UnregisterNetworkCallbacks();

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
        StartCoroutine(ClientReinitializeCoroutine());
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

        RtsNetworkCommandBus.GetOrCreate().BroadcastHostReloadScene();

        Debug.Log("[MULTIPLAYER] Host recargando escena para reinicio coordinado.");
        ResetWorldState();
        SceneManager.LoadScene(multiplayerSceneName);
    }

    IEnumerator ClientReinitializeCoroutine()
    {
        isReinitializing = true;
        Debug.Log("[MULTIPLAYER] Cliente refrescando match antes de recargar escena.");

        bool refreshDone = false;
        matchmakingClient.GetMatch(
            currentMatch.matchId,
            response =>
            {
                currentMatch = response;
                Debug.Log("[MULTIPLAYER] Match actualizado en cliente: " + (response.players?.Length ?? 0) + " jugadores.");
                refreshDone = true;
            },
            error =>
            {
                Debug.LogWarning("[MULTIPLAYER] Error refrescando match en cliente: " + error + " - continuando.");
                refreshDone = true;
            });

        yield return new WaitUntil(() => refreshDone);
        yield return null;

        Debug.Log("[MULTIPLAYER] Cliente recargando escena para reinicio coordinado.");
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
        RegisterNetworkCallbacks();
        RtsWorldSnapshotter.GetOrCreate().RestoreQueuedSnapshot();
        StartReliabilityRoutines();

        if (isReinitializing)
        {
            // Reinicio coordinado completado en este lado
            isReinitializing = false;
            Debug.Log("[MULTIPLAYER] Reinicio coordinado completado.");
        }
        else if (!IsLocalNetworkHost)
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

    public int GetPlayerUserIdBySlot(int slot)
    {
        if (currentMatch?.players == null || slot < 0 || slot >= currentMatch.players.Length) return 0;
        return currentMatch.players[slot] != null ? currentMatch.players[slot].userId : 0;
    }

    public int GetPlayerCount()
    {
        if (currentMatch?.players == null) return 1;
        return Mathf.Clamp(currentMatch.players.Length, 1, 4);
    }

    void SetCurrentMatch(MatchmakingClient.MatchResponse match)
    {
        currentMatch = match;
        migrationInProgress = false;
        StartReliabilityRoutines();
    }

    void StartReliabilityRoutines()
    {
        if (!HasMatch || migrationInProgress) return;

        StopReliabilityRoutines();
        if (CanRunHostDuties)
        {
            hostReliabilityRoutine = StartCoroutine(HostReliabilityLoop());
        }
        else
        {
            clientMigrationRoutine = StartCoroutine(ClientMigrationLoop());
        }
    }

    void StopReliabilityRoutines()
    {
        if (hostReliabilityRoutine != null)
        {
            StopCoroutine(hostReliabilityRoutine);
            hostReliabilityRoutine = null;
        }

        if (clientMigrationRoutine != null)
        {
            StopCoroutine(clientMigrationRoutine);
            clientMigrationRoutine = null;
        }
    }

    IEnumerator HostReliabilityLoop()
    {
        float snapshotTimer = 0f;
        while (CanRunHostDuties)
        {
            bool heartbeatDone = false;
            bool heartbeatSucceeded = false;
            matchmakingClient.Heartbeat(
                currentMatch.matchId,
                response =>
                {
                    currentMatch = response;
                    heartbeatSucceeded = true;
                    heartbeatDone = true;
                },
                error =>
                {
                    Debug.LogWarning("[MIGRATION] Heartbeat failed: " + error);
                    CheckForMigratedHost();
                    heartbeatDone = true;
                });
            yield return new WaitUntil(() => heartbeatDone);

            snapshotTimer += hostHeartbeatInterval;
            if (heartbeatSucceeded
                && snapshotTimer >= hostSnapshotInterval
                && RtsEntityRegistry.GetAllEntities().Count > 0)
            {
                snapshotTimer = 0f;
                yield return SaveSnapshotNow("periodic");
            }

            yield return new WaitForSeconds(hostHeartbeatInterval);
        }
    }

    public void RequestImmediateSnapshot(string reason)
    {
        if (!CanRunHostDuties || matchmakingClient == null || !HasMatch)
        {
            return;
        }

        if (snapshotSaveInProgress)
        {
            snapshotSaveQueued = true;
            return;
        }

        StartCoroutine(SaveSnapshotNow(reason));
    }

    IEnumerator SaveSnapshotNow(string reason)
    {
        if (snapshotSaveInProgress)
        {
            snapshotSaveQueued = true;
            yield break;
        }

        do
        {
            snapshotSaveQueued = false;
            if (!CanRunHostDuties || RtsEntityRegistry.GetAllEntities().Count == 0)
            {
                yield break;
            }

            snapshotSaveInProgress = true;
            RtsMatchSnapshot snapshot = RtsWorldSnapshotter.GetOrCreate().Capture(currentMatch.hostGeneration);
            snapshotSequence++;

            bool snapshotDone = false;
            matchmakingClient.SaveSnapshot(
                currentMatch.matchId,
                snapshotSequence,
                snapshot,
                response =>
                {
                    currentMatch = response;
                    snapshotDone = true;
                },
                error =>
                {
                    Debug.LogWarning("[MIGRATION] Snapshot failed (" + reason + "): " + error);
                    snapshotDone = true;
                });
            yield return new WaitUntil(() => snapshotDone);
            snapshotSaveInProgress = false;
        }
        while (snapshotSaveQueued);
    }

    IEnumerator ClientMigrationLoop()
    {
        while (HasMatch && !CanRunHostDuties && !migrationInProgress)
        {
            bool done = false;
            matchmakingClient.GetMigrationState(
                currentMatch.matchId,
                state =>
                {
                    HandleMigrationState(state);
                    done = true;
                },
                error =>
                {
                    Debug.LogWarning("[MIGRATION] Poll failed: " + error);
                    done = true;
                });
            yield return new WaitUntil(() => done);
            yield return new WaitForSeconds(migrationPollInterval);
        }
    }

    void HandleMigrationState(MatchmakingClient.MigrationStateResponse state)
    {
        if (state == null || !HasMatch || state.matchId != currentMatch.matchId) return;

        bool newHostReady = state.hostGeneration > currentMatch.hostGeneration
            && state.hostUserId != AuthSession.UserId
            && state.relay != null
            && !string.IsNullOrEmpty(state.relay.relayJoinCode);

        if (newHostReady)
        {
            JoinMigratedHost(state);
            return;
        }

        if (state.hostStale)
        {
            currentMatch = state;
            PauseGameplayForMigration();
            Debug.Log("[MIGRATION] Host stale. localUserId=" + AuthSession.UserId
                + ", backendHostUserId=" + state.hostUserId
                + ", migrationHostUserId=" + state.migrationHostUserId
                + ", players=" + (state.players != null ? state.players.Length : 0));

            if (state.hostUserId == AuthSession.UserId)
            {
                migrationInProgress = true;
                ShowMigrationStatus("Migracion iniciada por los clientes. Cerrando rol de host anterior...", 12f);
                return;
            }

            if (IsLocalMigrationHostCandidate(state))
            {
                ShowMigrationStatus("Host caido. Iniciando migracion de partida...", 12f);
                BeginHostClaim(state);
            }
            else
            {
                ShowMigrationStatus("Host caido. Esperando al nuevo host...", 12f);
            }
        }
    }

    bool IsLocalMigrationHostCandidate(MatchmakingClient.MigrationStateResponse state)
    {
        if (state == null || !state.hostStale)
        {
            return false;
        }

        if (state.migrationHostUserId > 0)
        {
            return state.migrationHostUserId == AuthSession.UserId;
        }

        if (state.players == null)
        {
            return false;
        }

        for (int i = 0; i < state.players.Length; i++)
        {
            int userId = state.players[i] != null ? state.players[i].userId : 0;
            if (userId > 0 && userId != state.hostUserId)
            {
                return userId == AuthSession.UserId;
            }
        }

        return false;
    }

    void RegisterNetworkCallbacks()
    {
        if (networkCallbacksRegistered) return;
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        networkCallbacksRegistered = true;
    }

    void UnregisterNetworkCallbacks()
    {
        if (!networkCallbacksRegistered || NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        networkCallbacksRegistered = false;
    }

    void PrepareNetworkRestart()
    {
        UnregisterNetworkCallbacks();
        RtsNetworkCommandBus.GetOrCreate().PrepareForNetworkRestart();
    }

    Task LeaveRelaySessionForRestart()
    {
        PrepareNetworkRestart();
        return relayLobbyClient != null ? relayLobbyClient.LeaveCurrentSessionAsync() : null;
    }

    IEnumerator WaitForRelayLeave(Task leaveTask)
    {
        if (leaveTask == null) yield break;

        while (!leaveTask.IsCompleted)
        {
            yield return null;
        }

        if (leaveTask.IsFaulted)
        {
            Exception exception = leaveTask.Exception != null
                ? leaveTask.Exception.GetBaseException()
                : null;
            Debug.LogWarning("[MIGRATION] Relay leave failed before restart: "
                + (exception != null ? exception.Message : "unknown error"));
        }
    }

    IEnumerator WaitForNetworkShutdown()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        float timeoutAt = Time.realtimeSinceStartup + 8f;
        while (IsNetworkManagerBusy(networkManager) && Time.realtimeSinceStartup < timeoutAt)
        {
            yield return null;
        }

        yield return null;
        yield return null;
    }

    void CheckForMigratedHost()
    {
        if (!HasMatch || migrationInProgress) return;

        matchmakingClient.GetMigrationState(
            currentMatch.matchId,
            HandleMigrationState,
            migrationError => Debug.LogWarning("[MIGRATION] Host migration lookup failed: " + migrationError));
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (!HasMatch || IsLocalNetworkHost || migrationInProgress) return;
        Debug.Log("[MIGRATION] Network disconnect detected. Checking host migration.");
        PauseGameplayForMigration();
        ShowMigrationStatus("Conexion con el host perdida. Verificando migracion...", 12f);
        StartCoroutine(CheckMigrationAfterDisconnect());
    }

    IEnumerator CheckMigrationAfterDisconnect()
    {
        yield return new WaitForSeconds(2f);
        if (!HasMatch) yield break;

        string matchId = currentMatch.matchId;
        int hostGeneration = currentMatch.hostGeneration;
        bool done = false;
        matchmakingClient.ReportHostLost(
            matchId,
            hostGeneration,
            state =>
            {
                HandleMigrationState(state);
                done = true;
            },
            error =>
            {
                Debug.LogWarning("[MIGRATION] Host lost report failed: " + error);
                matchmakingClient.GetMigrationState(
                    matchId,
                    state =>
                    {
                        HandleMigrationState(state);
                        done = true;
                    },
                    migrationError =>
                    {
                        Debug.LogWarning("[MIGRATION] Disconnect migration check failed: " + migrationError);
                        done = true;
                    });
            });
        yield return new WaitUntil(() => done);
    }

    void BeginHostClaim(MatchmakingClient.MigrationStateResponse state)
    {
        if (migrationInProgress || !HasMatch) return;
        if (CanRunHostDuties && (state == null || !state.hostStale)) return;
        if (!IsLocalMigrationHostCandidate(state))
        {
            ShowMigrationStatus("Esperando al nuevo host de la partida...", 12f);
            return;
        }

        migrationInProgress = true;
        if (state.snapshot != null)
        {
            RtsWorldSnapshotter.GetOrCreate().QueueRestore(state.snapshot);
        }

        Debug.Log("[MIGRATION] Claiming host role for match " + currentMatch.matchId + ".");
        ShowMigrationStatus("Tomando rol de host y creando nueva sesion...", 20f);
        Task leaveTask = LeaveRelaySessionForRestart();
        StartCoroutine(StartMigratedHostAfterShutdown(leaveTask, 1));
    }

    IEnumerator StartMigratedHostAfterShutdown(Task leaveTask, int attempt)
    {
        Debug.Log("[MIGRATION] Preparing migrated host. attempt=" + attempt + ".");
        yield return WaitForRelayLeave(leaveTask);
        yield return WaitForNetworkShutdown();

        Debug.Log("[MIGRATION] Starting new Relay host session.");
        relayLobbyClient.StartHostWithRelay(
            maxPlayers,
            relayJoinCode =>
            {
                Debug.Log("[MIGRATION] New Relay host ready. Claiming backend host role.");
                matchmakingClient.ClaimHost(
                    currentMatch.matchId,
                    relayJoinCode,
                    response =>
                    {
                        SetCurrentMatch(response);
                        ShowMigrationStatus("Migracion completada. Eres el nuevo host.", 8f);
                        ResetWorldState();
                        SceneManager.LoadScene(multiplayerSceneName);
                    },
                    error =>
                    {
                        Debug.LogWarning("[MIGRATION] Host claim failed: " + error);
                        Task retryLeaveTask = LeaveRelaySessionForRestart();
                        migrationInProgress = false;
                        StartCoroutine(ResumeMigrationPollingAfterShutdown(retryLeaveTask));
                    });
            },
            error =>
            {
                Debug.LogWarning("[MIGRATION] Relay host restart failed: " + error);
                if (attempt < Mathf.Max(1, migrationStartMaxAttempts))
                {
                    ShowMigrationStatus("No se pudo crear el nuevo host. Reintentando...", 12f);
                    Task retryLeaveTask = LeaveRelaySessionForRestart();
                    StartCoroutine(RetryStartMigratedHostAfterDelay(retryLeaveTask, attempt + 1));
                    return;
                }

                migrationInProgress = false;
                StartReliabilityRoutines();
            });
    }

    IEnumerator RetryStartMigratedHostAfterDelay(Task leaveTask, int attempt)
    {
        yield return new WaitForSeconds(Mathf.Max(0.25f, migrationRetryDelay));
        yield return StartMigratedHostAfterShutdown(leaveTask, attempt);
    }

    void JoinMigratedHost(MatchmakingClient.MigrationStateResponse state)
    {
        if (migrationInProgress || state == null || state.relay == null) return;

        migrationInProgress = true;
        if (state.snapshot != null)
        {
            RtsWorldSnapshotter.GetOrCreate().QueueRestore(state.snapshot);
        }

        ShowMigrationStatus("Nuevo host encontrado. Reconectando partida...", 20f);
        Task leaveTask = LeaveRelaySessionForRestart();
        StartCoroutine(JoinMigratedHostAfterShutdown(state, leaveTask, 1));
    }

    IEnumerator JoinMigratedHostAfterShutdown(MatchmakingClient.MigrationStateResponse state, Task leaveTask, int attempt)
    {
        yield return WaitForRelayLeave(leaveTask);
        yield return WaitForNetworkShutdown();

        relayLobbyClient.JoinByCode(
            state.relay.relayJoinCode,
            () =>
            {
                SetCurrentMatch(state);
                ShowMigrationStatus("Reconectado al nuevo host.", 8f);
                ResetWorldState();
                SceneManager.LoadScene(multiplayerSceneName);
            },
            error =>
            {
                Debug.LogWarning("[MIGRATION] Join migrated host failed: " + error);
                if (attempt < Mathf.Max(1, migrationJoinMaxAttempts))
                {
                    ShowMigrationStatus("No se pudo reconectar al nuevo host. Reintentando...", 12f);
                    Task retryLeaveTask = LeaveRelaySessionForRestart();
                    StartCoroutine(RetryJoinMigratedHostAfterDelay(state, retryLeaveTask, attempt + 1));
                    return;
                }

                migrationInProgress = false;
                StartReliabilityRoutines();
            });
    }

    IEnumerator RetryJoinMigratedHostAfterDelay(MatchmakingClient.MigrationStateResponse state, Task leaveTask, int attempt)
    {
        yield return new WaitForSeconds(Mathf.Max(0.25f, migrationRetryDelay));
        yield return JoinMigratedHostAfterShutdown(state, leaveTask, attempt);
    }

    IEnumerator ResumeMigrationPollingAfterShutdown(Task leaveTask)
    {
        yield return WaitForRelayLeave(leaveTask);
        yield return WaitForNetworkShutdown();
        StartReliabilityRoutines();
    }

    void PauseGameplayForMigration()
    {
        foreach (Humano unit in FindObjectsByType<Humano>(FindObjectsSortMode.None))
        {
            if (unit != null)
            {
                unit.PauseForMigration();
            }
        }
    }

    void ShowMigrationStatus(string message, float seconds)
    {
        migrationStatusMessage = message;
        migrationStatusUntil = Time.realtimeSinceStartup + Mathf.Max(1f, seconds);
    }

    static bool IsNetworkManagerBusy(NetworkManager networkManager)
    {
        return networkManager != null
            && (networkManager.IsListening
                || networkManager.IsClient
                || networkManager.IsServer
                || networkManager.ShutdownInProgress);
    }

    void OnGUI()
    {
        if (string.IsNullOrEmpty(migrationStatusMessage) || Time.realtimeSinceStartup > migrationStatusUntil)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Clamp(Screen.height / 28, 18, 34),
            wordWrap = true
        };
        style.normal.textColor = Color.white;

        float width = Mathf.Min(Screen.width - 40f, 760f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, 24f, width, 72f);
        GUI.Box(rect, migrationStatusMessage, style);
    }
}
