using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class MatchmakingClient : MonoBehaviour
{
    [Header("Matchmaking API")]
    [SerializeField] string baseUrl = "http://127.0.0.1:8001";

    public static MatchmakingClient Instance { get; private set; }

    // ── DTOs ────────────────────────────────────────────────────────────────

    [Serializable]
    public class RelaySessionData
    {
        public string relayJoinCode;
    }

    [Serializable]
    public class CreateMatchRequest
    {
        public string gameMode = "standard";
        public string region = "default";
        public int maxPlayers = 2;
    }

    [Serializable]
    public class MatchPlayer
    {
        public int userId;
        public string username;
        public string role;
    }

    [Serializable]
    public class MatchResponse
    {
        public string matchId;
        public string status;       // "waiting" | "starting" | "closed"
        public string gameMode;
        public string region;
        public int maxPlayers;
        public int hostUserId;
        public MatchPlayer[] players;
        public RelaySessionData relay;
        public string createdAtUtc;
        public string updatedAtUtc;
        public string role;         // solo presente en Create/Join response
    }

    /// <summary>Respuesta del endpoint GET /queue/next.</summary>
    [Serializable]
    public class NextMatchData
    {
        public string matchId;
        public string relayJoinCode;
    }

    // ── Singleton ────────────────────────────────────────────────────────────

    public static MatchmakingClient GetOrCreate()
    {
        if (Instance != null) return Instance;
        Instance = FindFirstObjectByType<MatchmakingClient>();
        if (Instance != null) return Instance;
        var go = new GameObject("MatchmakingClient");
        Instance = go.AddComponent<MatchmakingClient>();
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
    /// Crea una nueva partida enviando los datos del relay ya generados.
    /// El host puede cargar la escena inmediatamente tras el callback.
    /// </summary>
    public void CreateMatch(
        string relayJoinCode,
        Action<MatchResponse> onSuccess,
        Action<string> onError,
        int maxPlayers = 2)
    {
        // El backend espera dos campos: CreateMatchRequest + RelaySessionData.
        // Los fusionamos en un objeto anónimo serializable.
        var payload = new CreateMatchPayload
        {
            gameMode = "standard",
            region = "default",
            maxPlayers = maxPlayers,
            relayJoinCode = relayJoinCode
        };
        StartCoroutine(PostJsonCoroutine("/matchmaking/matches", payload, onSuccess, onError));
    }

    /// <summary>
    /// Consulta el próximo match disponible en la cola de RabbitMQ.
    /// Si hay uno, devuelve { matchId, relayJoinCode }; si no hay, devuelve null.
    /// </summary>
    public void GetNextAvailableMatch(
        Action<NextMatchData> onMatch,
        Action onEmpty,
        Action<string> onError)
    {
        StartCoroutine(GetNextMatchCoroutine(onMatch, onEmpty, onError));
    }

    /// <summary>
    /// Registra al cliente en el match indicado (cambia status a "starting").
    /// </summary>
    public void JoinMatch(
        string matchId,
        Action<MatchResponse> onSuccess,
        Action<string> onError)
    {
        if (string.IsNullOrEmpty(matchId)) { onError?.Invoke("Match id vacio."); return; }
        StartCoroutine(PostJsonCoroutine(
            "/matchmaking/matches/" + UnityWebRequest.EscapeURL(matchId) + "/join",
            new EmptyPayload(),
            onSuccess,
            onError));
    }

    /// <summary>Obtiene el estado actual de un match.</summary>
    public void GetMatch(string matchId, Action<MatchResponse> onSuccess, Action<string> onError)
    {
        if (string.IsNullOrEmpty(matchId)) { onError?.Invoke("Match id vacio."); return; }
        StartCoroutine(GetJsonCoroutine(
            "/matchmaking/matches/" + UnityWebRequest.EscapeURL(matchId),
            onSuccess,
            onError));
    }

    /// <summary>Abandona el match actual y lo cierra en el servidor.</summary>
    public void LeaveMatch(string matchId, Action<MatchResponse> onSuccess, Action<string> onError)
    {
        if (string.IsNullOrEmpty(matchId)) { onError?.Invoke("Match id vacio."); return; }
        StartCoroutine(PostJsonCoroutine(
            "/matchmaking/matches/" + UnityWebRequest.EscapeURL(matchId) + "/leave",
            new EmptyPayload(),
            onSuccess,
            onError));
    }

    // ── Helpers internos ─────────────────────────────────────────────────────

    string BuildUrl(string endpoint)
    {
        string cleanBase = baseUrl.TrimEnd('/');
        string cleanEndpoint = endpoint.StartsWith("/") ? endpoint : "/" + endpoint;
        return cleanBase + cleanEndpoint;
    }

    IEnumerator GetJsonCoroutine(string endpoint, Action<MatchResponse> onSuccess, Action<string> onError)
    {
        var request = UnityWebRequest.Get(BuildUrl(endpoint));
        AuthSession.ApplyAuthorization(request);
        request.timeout = 10;
        yield return request.SendWebRequest();
        HandleMatchResponse(request, onSuccess, onError);
    }

    IEnumerator PostJsonCoroutine<T>(string endpoint, T payload, Action<MatchResponse> onSuccess, Action<string> onError)
    {
        string json = JsonUtility.ToJson(payload);
        var request = new UnityWebRequest(BuildUrl(endpoint), UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        AuthSession.ApplyAuthorization(request);
        request.timeout = 10;
        yield return request.SendWebRequest();
        HandleMatchResponse(request, onSuccess, onError);
    }

    IEnumerator GetNextMatchCoroutine(
        Action<NextMatchData> onMatch,
        Action onEmpty,
        Action<string> onError)
    {
        var request = UnityWebRequest.Get(BuildUrl("/matchmaking/queue/next"));
        AuthSession.ApplyAuthorization(request);
        request.timeout = 10;
        yield return request.SendWebRequest();

        bool hasNetError = request.result != UnityWebRequest.Result.Success;
        string text = request.downloadHandler?.text ?? string.Empty;
        request.Dispose();

        if (hasNetError) { onError?.Invoke(request.error); yield break; }
        if (string.IsNullOrWhiteSpace(text) || text == "null")
        { onEmpty?.Invoke(); yield break; }

        var data = JsonUtility.FromJson<NextMatchData>(text);
        if (data == null || string.IsNullOrEmpty(data.matchId))
        { onEmpty?.Invoke(); yield break; }

        onMatch?.Invoke(data);
    }

    void HandleMatchResponse(UnityWebRequest request, Action<MatchResponse> onSuccess, Action<string> onError)
    {
        bool hasError = request.result != UnityWebRequest.Result.Success || request.responseCode >= 400;
        string responseText = request.downloadHandler?.text ?? string.Empty;

        if (hasError)
        {
            onError?.Invoke(string.IsNullOrEmpty(responseText) ? request.error : responseText);
            request.Dispose();
            return;
        }

        onSuccess?.Invoke(JsonUtility.FromJson<MatchResponse>(responseText));
        request.Dispose();
    }

    // ── Payloads auxiliares ───────────────────────────────────────────────────

    [Serializable]
    class CreateMatchPayload
    {
        public string gameMode;
        public string region;
        public int maxPlayers;
        public string relayJoinCode;
    }

    [Serializable]
    class EmptyPayload { }
}