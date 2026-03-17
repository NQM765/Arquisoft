using System;
using System.Net;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AuthManager : MonoBehaviour
{
    public string authServerUrl = "http://localhost:3000";
    public int callbackPort = 5000;

    private string _state;
    private HttpListener _listener;
    private string _pendingToken;

    public static string AccessToken { get; private set; }
    public static string Username { get; private set; }

    void Update()
    {
        // Procesa el token en el hilo principal de Unity
        if (_pendingToken != null)
        {
            string token = _pendingToken;
            _pendingToken = null;
            OnTokenReceived(token);
        }
    }
    void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    public void StartLogin()
    {
        StopLocalServer();

        _state = Guid.NewGuid().ToString("N");
        StartLocalServer();

        string url = $"{authServerUrl}/?port={callbackPort}&state={_state}";
        Application.OpenURL(url);
    }

    void StartLocalServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{callbackPort}/");
        _listener.Start();
        Debug.Log($"Esperando callback en puerto {callbackPort}...");

        var thread = new Thread(() =>
        {
            var context = _listener.GetContext();
            var query = context.Request.QueryString;
            string token = query["token"];
            string state = query["state"];

            // Responde al navegador
            string html = "<html><body style='font-family:sans-serif;text-align:center;" +
                          "padding:80px;background:#0f0f0f;color:#eee'>" +
                          "<h2 style='color:#4ade80'>&#10003; Login exitoso</h2>" +
                          "<p>Puedes cerrar esta pestaña y volver al juego.</p>" +
                          "</body></html>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
            _listener.Stop();

            if (state != _state)
            {
                Debug.LogError("State inválido");
                return;
            }

            // En vez de UnityMainThreadDispatcher, usamos una variable
            // que Update() revisa cada frame
            _pendingToken = token;
        });

        thread.IsBackground = true;
        thread.Start();
    }

    void OnTokenReceived(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogError("Token vacío");
            return;
        }

        AccessToken = token;

        // Decodifica el JWT para sacar el username
        try
        {
            var parts = token.Split('.');
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            while (padded.Length % 4 != 0) padded += '=';
            var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var data = JsonUtility.FromJson<JwtPayload>(payload);
            Username = data.username;
            Debug.Log($"✓ Autenticado correctamente. Bienvenido, {Username}!");
        }
        catch
        {
            Debug.Log("✓ Autenticado correctamente");
        }
        SceneManager.LoadScene("MainMenuV2");
    }

    void OnDestroy()
    {
        _listener?.Stop();
        StopLocalServer();
    }
    void StopLocalServer()
    {
        try
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
                _listener = null;
                Debug.Log("Listener anterior cerrado");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Error cerrando listener: {e.Message}");
        }
    }

    [Serializable]
    class JwtPayload { public string username; public string email; public int id; }
}