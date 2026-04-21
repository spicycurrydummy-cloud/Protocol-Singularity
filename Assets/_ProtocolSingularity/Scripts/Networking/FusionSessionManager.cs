using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// NetworkRunner のライフサイクル管理を担う。セッション作成 / 参加 / シャットダウン。
    /// Title シーンで生成され、ゲーム終了までシーンをまたいで生存する。
    /// </summary>
    public class FusionSessionManager : MonoBehaviour, INetworkRunnerCallbacks
    {
        public static FusionSessionManager Instance { get; private set; }

        [SerializeField] private int maxPlayersPerSession = 10;

        public NetworkRunner Runner { get; private set; }
        public string LocalPlayerName { get; private set; } = string.Empty;
        public bool IsHost => Runner != null && Runner.IsServer;
        public bool IsInLobbyOnly { get; private set; }
        public IReadOnlyList<SessionInfo> KnownSessions { get; private set; } = new List<SessionInfo>();

        public event Action<PlayerRef> PlayerJoined;
        public event Action<PlayerRef> PlayerLeft;
        public event Action<ShutdownReason> SessionShutdown;
        public event Action<List<SessionInfo>> SessionListUpdated;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _ = Mercury2ConfigLoader.LoadAsync();
        }

        public void SetPlayerName(string displayName)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
                LocalPlayerName = displayName.Trim();
        }

        /// <summary>
        /// セッション一覧取得のために Fusion ロビーに入る。NetworkRunner を確保し、セッション作成/参加まで同じ Runner を使い回す。
        /// </summary>
        public async Task<StartGameResult> EnterLobby(SessionLobby lobby = SessionLobby.ClientServer)
        {
            EnsureRunner();
            IsInLobbyOnly = true;
            var result = await Runner.JoinSessionLobby(lobby);
            if (!result.Ok) IsInLobbyOnly = false;
            return result;
        }

        public async Task<StartGameResult> CreateSession(string sessionName)
        {
            return await StartSessionInternal(GameMode.Host, sessionName, maxPlayersPerSession);
        }

        public async Task<StartGameResult> JoinSession(string sessionName)
        {
            return await StartSessionInternal(GameMode.Client, sessionName, maxPlayersPerSession);
        }

        private void EnsureRunner()
        {
            // 残留 NetworkRunner がいたら即座に除去（Destroy は遅延するため）
            foreach (var leftover in gameObject.GetComponents<NetworkRunner>())
            {
                if (leftover == Runner) continue;
                DestroyImmediate(leftover);
            }
            if (Runner == null)
            {
                Runner = gameObject.AddComponent<NetworkRunner>();
                Runner.ProvideInput = true;
                Runner.AddCallbacks(this);
            }
            if (gameObject.GetComponent<NetworkSceneManagerDefault>() == null)
                gameObject.AddComponent<NetworkSceneManagerDefault>();
        }

        private async Task<StartGameResult> StartSessionInternal(GameMode mode, string sessionName, int maxPlayers)
        {
            // ロビー閲覧用の Runner を使い回すと Photon のサーバ状態が NameServer 残りのまま
            // StartGame すると `Operation JoinOrCreateRoom not allowed on current server` で失敗する。
            // そのため一度 Shutdown してクリーンな Runner で再開する。
            if (Runner != null)
            {
                await Shutdown();
                // WebGL: Photon NameServer の WebSocket が Shutdown 直後に再接続しようとすると
                // 'WebSocket is closed before the connection is established' で弾かれる。
                // サーバ側が FIN を処理する余裕を与えるため少し待つ。
                await System.Threading.Tasks.Task.Delay(750);
            }
            EnsureRunner();
            IsInLobbyOnly = false;
            var sceneManager = gameObject.GetComponent<NetworkSceneManagerDefault>();
            var args = new StartGameArgs
            {
                GameMode = mode,
                SessionName = sessionName,
                PlayerCount = maxPlayers,
                SceneManager = sceneManager
            };
            return await Runner.StartGame(args);
        }

        /// <summary>
        /// 現在ホスト中のセッションのロック状態を切り替える（IsOpen で制御）。
        /// </summary>
        public void SetSessionLocked(bool locked)
        {
            if (Runner == null || !Runner.IsServer || Runner.SessionInfo == null) return;
            Runner.SessionInfo.IsOpen = !locked;
            Runner.SessionInfo.IsVisible = !locked;
        }

        public async Task Shutdown()
        {
            if (Runner == null) return;
            var r = Runner;
            Runner = null;
            IsInLobbyOnly = false;
            // WebGL の WebSocket が CLOSED にならずハングすることがあるため Shutdown に 3 秒タイムアウトを設ける。
            // タイムアウト後も下の DestroyImmediate で Runner コンポーネントは強制除去される。
            try
            {
                var shutdownTask = r.Shutdown();
                var completed = await System.Threading.Tasks.Task.WhenAny(shutdownTask, System.Threading.Tasks.Task.Delay(3000));
                if (completed != shutdownTask)
                {
                    UnityEngine.Debug.LogWarning("[FusionSessionManager] Runner.Shutdown() timed out after 3s; forcing destroy.");
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning($"[FusionSessionManager] Runner.Shutdown threw: {e.Message}");
            }
            // NetworkRunner コンポーネントを即座に除去。遅延させると AddComponent が重複エラーになる
            if (r != null) DestroyImmediate(r);
        }

        // ==================== INetworkRunnerCallbacks ====================
        void INetworkRunnerCallbacks.OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            // シーン配置された PlayerRegistry / HostSettings に委譲。Spawn はしない。
            PlayerJoined?.Invoke(player);
        }

        void INetworkRunnerCallbacks.OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner.IsServer && PlayerRegistry.Instance != null)
                PlayerRegistry.Instance.OnPlayerLeft(player);
            PlayerLeft?.Invoke(player);
        }

        void INetworkRunnerCallbacks.OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
            => SessionShutdown?.Invoke(shutdownReason);

        void INetworkRunnerCallbacks.OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            KnownSessions = sessionList ?? new List<SessionInfo>();
            SessionListUpdated?.Invoke(sessionList);
        }

        void INetworkRunnerCallbacks.OnInput(NetworkRunner runner, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnConnectedToServer(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        void INetworkRunnerCallbacks.OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        void INetworkRunnerCallbacks.OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        void INetworkRunnerCallbacks.OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        void INetworkRunnerCallbacks.OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        void INetworkRunnerCallbacks.OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        void INetworkRunnerCallbacks.OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        void INetworkRunnerCallbacks.OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        void INetworkRunnerCallbacks.OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnSceneLoadDone(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnSceneLoadStart(NetworkRunner runner) { }
    }
}
