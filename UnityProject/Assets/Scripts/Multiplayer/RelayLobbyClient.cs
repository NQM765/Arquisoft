using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

public class RelayLobbyClient : MonoBehaviour
{
    const float NetworkShutdownTimeoutSeconds = 8f;

    public static RelayLobbyClient Instance { get; private set; }

    ISession currentSession;

    public string CurrentJoinCode => currentSession?.Code;

    // ── Singleton ─────────────────────────────────────────────────────────────

    public static RelayLobbyClient GetOrCreate()
    {
        if (Instance != null) return Instance;
        Instance = FindFirstObjectByType<RelayLobbyClient>();
        if (Instance != null) return Instance;
        var go = new GameObject("RelayLobbyClient");
        Instance = go.AddComponent<RelayLobbyClient>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── API pública ──────────────────────────────────────────────────────────

    /// <summary>
    /// HOST: Inicia una sesión Relay y devuelve el join code vía callback.
    /// </summary>
    public async void StartHostWithRelay(
        int maxPlayers,
        Action<string> onJoinCodeReady,
        Action<string> onError)
    {
        try
        {
            await EnsureNetworkReadyForNewSessionAsync();
            await EnsureUnityServicesReadyAsync();

            var options = new SessionOptions
            {
                MaxPlayers = Mathf.Clamp(maxPlayers, 2, 4)
            }.WithRelayNetwork();

            currentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            RtsNetworkCommandBus.GetOrCreate().Activate();
            onJoinCodeReady?.Invoke(currentSession.Code);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// CLIENTE: Se une a una sesión Relay existente usando el join code.
    /// </summary>
    public async void JoinByCode(
        string joinCode,
        Action onSuccess,
        Action<string> onError)
    {
        if (string.IsNullOrEmpty(joinCode)) { onError?.Invoke("Join code vacío."); return; }

        try
        {
            await EnsureNetworkReadyForNewSessionAsync();
            await EnsureUnityServicesReadyAsync();
            currentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode);
            RtsNetworkCommandBus.GetOrCreate().Activate();
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
    }

    /// <summary>Abandona la sesión Relay actual y apaga el NetworkManager.</summary>
    public async void LeaveCurrentSession()
    {
        await LeaveCurrentSessionAsync();
    }

    public async Task LeaveCurrentSessionAsync()
    {
        var session = currentSession;
        currentSession = null;

        try
        {
            if (session != null)
                await LeaveSessionWithTimeoutAsync(session);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[RELAY] Error saliendo de sesión: " + ex.Message);
        }
        finally
        {
            ShutdownNetworkManagerIfNeeded();
            await WaitForNetworkManagerIdleAsync(NetworkShutdownTimeoutSeconds);
        }
    }

    // ── Helpers internos ─────────────────────────────────────────────────────

    async Task EnsureUnityServicesReadyAsync()
    {
        string profile = AuthSession.UserId > 0 ? "u_" + AuthSession.UserId : "default";

        if (UnityServices.State == ServicesInitializationState.Uninitialized)
        {
            var options = new InitializationOptions().SetProfile(profile);
            await UnityServices.InitializeAsync(options);
        }
        else if (AuthenticationService.Instance.Profile != profile)
        {
            if (AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.SignOut(true);
            AuthenticationService.Instance.SwitchProfile(profile);
        }

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    async Task EnsureNetworkReadyForNewSessionAsync()
    {
        if (currentSession != null || IsNetworkManagerBusy(NetworkManager.Singleton))
        {
            await LeaveCurrentSessionAsync();
        }

        RtsNetcodeRuntime.EnsureNetworkManager();
        await WaitForNetworkManagerIdleAsync(NetworkShutdownTimeoutSeconds);
    }

    void ShutdownNetworkManagerIfNeeded()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.ShutdownInProgress)
        {
            return;
        }

        if (networkManager.IsListening || networkManager.IsClient || networkManager.IsServer)
        {
            networkManager.Shutdown();
        }
    }

    async Task LeaveSessionWithTimeoutAsync(ISession session)
    {
        Task leaveTask = session.LeaveAsync();
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(NetworkShutdownTimeoutSeconds));
        Task completedTask = await Task.WhenAny(leaveTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            Debug.LogWarning("[RELAY] Timeout saliendo de sesión. Continuando con el reinicio de red.");
            _ = ObserveFaultedTaskAsync(leaveTask);
            return;
        }

        await leaveTask;
    }

    async Task ObserveFaultedTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[RELAY] LeaveAsync terminó con error despues del timeout: " + ex.Message);
        }
    }

    async Task WaitForNetworkManagerIdleAsync(float timeoutSeconds)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            return;
        }

        float timeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        while (IsNetworkManagerBusy(networkManager) && Time.realtimeSinceStartup < timeoutAt)
        {
            await Task.Yield();
        }

        if (IsNetworkManagerBusy(networkManager))
        {
            Debug.LogWarning("[RELAY] NetworkManager did not finish shutting down. Recreating it before starting a new Relay session.");
            Destroy(networkManager.gameObject);
            await Task.Yield();
            await Task.Yield();
            RtsNetcodeRuntime.EnsureNetworkManager();
        }

        await Task.Yield();
        await Task.Yield();
    }

    static bool IsNetworkManagerBusy(NetworkManager networkManager)
    {
        return networkManager != null
            && (networkManager.IsListening
                || networkManager.IsClient
                || networkManager.IsServer
                || networkManager.ShutdownInProgress);
    }
}
