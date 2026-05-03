using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

public class OAuthLongPolling : MonoBehaviour
{
    private const string BACKEND_URL = "http://localhost:8000";
    
    [System.Serializable]
    public class OAuthInitResponse {
        public string session_id;
        public string state;
        public string auth_url;
    }
    
    [System.Serializable]
    public class OAuthPollResponse {
        public string session_id;
        public string status;
        public OAuthUserData user_data;
    }
    
    [System.Serializable]
    public class OAuthUserData {
        public int user_id;
        public string username;
        public string email;
    }
    
    private string _sessionId;
    private string _authUrl;
    private bool _isComplete;
    private OAuthUserData _userData;
    
    void Start()
    {
        StartCoroutine(StartOAuthFlow());
    }
    
    public IEnumerator StartOAuthFlow()
    {
        Debug.Log("=== OAuth Long Polling Test ===");
        
        // 1. Iniciar flujo
        Debug.Log("1. Iniciando flujo OAuth...");
        using (var request = UnityWebRequest.PostWwwForm(BACKEND_URL + "/auth/oauth/init", ""))
        {
            yield return request.SendWebRequest();
            
            if (request.result != UnityWebRequest.Result.Success) {
                Debug.LogError("Error en /oauth/init: " + request.error);
                yield break;
            }
            
            var initResponse = JsonUtility.FromJson<OAuthInitResponse>(request.downloadHandler.text);
            _sessionId = initResponse.session_id;
            _authUrl = initResponse.auth_url;
            
            Debug.Log("Session ID: " + _sessionId);
            Debug.Log("Auth URL: " + _authUrl);
        }
        
        // 2. Abrir navegador con auth_url
        Debug.Log("2. Abriendo navegador...");
        Application.OpenURL(_authUrl);
        
        // 3. Polling hasta completar o timeout
        Debug.Log("3. Iniciando polling (esperando login en navegador)...");
        float timeout = 60f;
        float elapsed = 0f;
        
        while (elapsed < timeout && !_isComplete) {
            yield return new WaitForSeconds(1f);
            elapsed += 1f;
            
            using (var request = UnityWebRequest.Get(BACKEND_URL + "/auth/oauth/poll/" + _sessionId))
            {
                yield return request.SendWebRequest();
                
                if (request.result == UnityWebRequest.Result.Success) {
                    var pollResponse = JsonUtility.FromJson<OAuthPollResponse>(request.downloadHandler.text);
                    Debug.Log("Status: " + pollResponse.status);
                    
                    if (pollResponse.status == "completed") {
                        _isComplete = true;
                        _userData = pollResponse.user_data;
                        Debug.Log("¡Login completado!");
                        Debug.Log("User ID: " + _userData.user_id);
                        Debug.Log("Username: " + _userData.username);
                        Debug.Log("Email: " + _userData.email);
                        break;
                    }
                } else {
                    Debug.LogWarning("Error en poll: " + request.error);
                }
            }
        }
        
        if (!_isComplete) {
            Debug.LogWarning("Timeout - No se completó el login");
        }
        
        Debug.Log("=== Fin del test ===");
    }
    
    public string GetSessionId() => _sessionId;
    public bool IsComplete() => _isComplete;
    public OAuthUserData GetUserData() => _userData;
}