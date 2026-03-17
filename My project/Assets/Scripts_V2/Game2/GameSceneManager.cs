using Unity.Netcode;
using UnityEngine;

public class GameSceneManager : MonoBehaviour
{
    void Start()
    {
        if (GameSession.Role == "host")
        {
            NetworkManager.Singleton.StartHost();
            Debug.Log("Iniciado como Host");
        }
        else
        {
            var transport = NetworkManager.Singleton
                .GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            transport.SetConnectionData(GameSession.HostIp, (ushort)GameSession.HostPort);
            NetworkManager.Singleton.StartClient();
            Debug.Log($"Conectando a {GameSession.HostIp}:{GameSession.HostPort}");
        }
    }
}