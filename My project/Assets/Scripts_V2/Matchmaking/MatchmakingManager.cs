using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using NativeWebSocket;
using TMPro;
using UnityEngine.UIElements; // instala: com.endel.nativewebsocket

public class MatchmakingManager : MonoBehaviour
{
    public string matchmakingUrl = "ws://localhost:3001";

    public TextMeshProUGUI findMatchText;
    public GameObject findMatchButton;
    public GameObject cancelButton;

    private WebSocket _ws;
    private PendingMatch _pendingMatch;

    // Para procesar mensajes en el hilo principal
    [Serializable]
    class PendingMatch
    {
        public string role;
        public string roomId;
        public string opponentUsername;
        public string hostIp;
        public int hostPort;
    }

    void Update()
    {
        _ws?.DispatchMessageQueue();

        if (_pendingMatch != null)
        {
            var match = _pendingMatch;
            _pendingMatch = null;
            HandleMatchFound(match);
        }
    }

    public async void FindMatch()
    {
        findMatchButton.SetActive(false);
        if (string.IsNullOrEmpty(AuthManager.AccessToken))
        {
            Debug.LogError("No hay token. El usuario no está autenticado.");
            return;
        }

        _ws = new WebSocket(matchmakingUrl);

        _ws.OnOpen += () =>
        {
            Debug.Log("Conectado al matchmaking");
            // Envía el JWT directamente — el servidor lo valida
            string msg = JsonUtility.ToJson(new FindMatchMsg
            {
                type = "find_match",
                token = AuthManager.AccessToken
            });
            _ws.SendText(msg);
        };

        _ws.OnMessage += (bytes) =>
        {
            string raw = System.Text.Encoding.UTF8.GetString(bytes);
            var msg = JsonUtility.FromJson<MatchMsg>(raw);

            switch (msg.type)
            {
                case "queued":
                    Debug.Log("En cola, buscando oponente...");
                    // Aquí actualiza tu UI
                    findMatchButton.SetActive(false);
                    findMatchText.enabled = true;
                    cancelButton.SetActive(true);
                    break;
                case "match_found":
                    _pendingMatch = JsonUtility.FromJson<PendingMatch>(raw);
                    break;
                case "error":
                    Debug.LogError("Matchmaking error: " + msg.message);
                    break;
            }
        };

        _ws.OnError += (e) => Debug.LogError("WS error: " + e);

        await _ws.Connect();
    }

    public void CancelSearch()
    {
        findMatchText.enabled = false;
        findMatchButton.SetActive(true);
        cancelButton.SetActive(false);
        if (_ws != null)
            _ws.SendText("{\"type\":\"cancel_match\"}");
    }

    void HandleMatchFound(PendingMatch match)
    {
        Debug.Log($"¡Partida encontrada! Rol: {match.role} vs {match.opponentUsername}");

        // Guarda datos para la escena de juego
        GameSession.RoomId = match.roomId;
        GameSession.Role = match.role;
        GameSession.HostIp = match.hostIp;
        GameSession.HostPort = match.hostPort;
        GameSession.Opponent = match.opponentUsername;

        SceneManager.LoadScene("GameV2");
    }

    async void OnDestroy()
    {
        if (_ws != null) await _ws.Close();
    }

    // Clases de serialización
    [Serializable] class FindMatchMsg { public string type; public string token; }
    [Serializable] class MatchMsg { public string type; public string message; }
}