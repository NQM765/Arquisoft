using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

public class RelayLobbyClient : MonoBehaviour
{
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
            RtsNetcodeRuntime.EnsureNetworkManager();
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
            RtsNetcodeRuntime.EnsureNetworkManager();
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
        var session = currentSession;
        currentSession = null;

        try
        {
            if (session != null)
                await session.LeaveAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[RELAY] Error saliendo de sesión: " + ex.Message);
        }
        finally
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                nm.Shutdown();
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
}