using System.Collections.Generic;
using Fusion;
using ProtocolSingularity.Core;
using ProtocolSingularity.Networking;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProtocolSingularity.UI
{
    /// <summary>
    /// タイトル画面の UI 制御。名前入力 → セッション作成/参加、Fusion ロビー経由のセッション一覧。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class TitleController : MonoBehaviour
    {
        [SerializeField] private string lobbySceneName = "Lobby";
        [SerializeField] private VisualTreeAsset sessionListItemTemplate;

        private UIDocument _doc;
        private TextField _nameInput;
        private TextField _codeInput;
        private Button _createBtn;
        private Button _joinBtn;
        private Button _refreshBtn;
        private ScrollView _sessionList;
        private Label _sessionListEmpty;
        private Label _statusLine;

        private Button _rulesBtn;
        private Button _rulesCloseBtn;
        private VisualElement _rulesOverlay;
        private Label _rulesBody;

        private bool _transitioning;

        private async void OnEnable()
        {
            _doc = GetComponent<UIDocument>();

            // UIDocument.rootVisualElement は OnEnable 呼出時にまだ構築されていない場合がある。
            // 1 フレーム待ってから UI ツリーを取得する。
            await System.Threading.Tasks.Task.Yield();
            if (_doc == null) return;
            var root = _doc.rootVisualElement;
            if (root == null) return;

            _nameInput = root.Q<TextField>("name-input");
            _codeInput = root.Q<TextField>("code-input");
            _createBtn = root.Q<Button>("create-btn");
            _joinBtn = root.Q<Button>("join-btn");
            _refreshBtn = root.Q<Button>("refresh-btn");
            _sessionList = root.Q<ScrollView>("session-list");
            _sessionListEmpty = root.Q<Label>("session-list-empty");
            _statusLine = root.Q<Label>("status-line");
            _rulesBtn = root.Q<Button>("rules-btn");
            _rulesCloseBtn = root.Q<Button>("rules-close-btn");
            _rulesOverlay = root.Q<VisualElement>("rules-overlay");
            _rulesBody = root.Q<Label>("rules-body");
            if (_rulesBody != null)
            {
                _rulesBody.enableRichText = true;
                _rulesBody.text = RulesText.Body;
            }

            _createBtn.clicked += OnCreateClicked;
            _joinBtn.clicked += OnJoinClicked;
            _refreshBtn.clicked += OnRefreshClicked;
            if (_rulesBtn != null) _rulesBtn.clicked += OpenRules;
            if (_rulesCloseBtn != null) _rulesCloseBtn.clicked += CloseRules;
            if (_rulesOverlay != null) _rulesOverlay.RegisterCallback<ClickEvent>(evt => { if (evt.target == _rulesOverlay) CloseRules(); });

            EnsureSessionManager();

            // TextField.value は UI Toolkit の view-data 永続化や Editor セッションの残骸で
            // 前回入力が残ることがあるため、明示的に空文字で上書きする。
            if (_nameInput != null) _nameInput.SetValueWithoutNotify(string.Empty);
            if (_codeInput != null) _codeInput.SetValueWithoutNotify(string.Empty);

            // WebGL では UI Toolkit TextField が IME / 日本語入力を取りこぼすため、
            // 画面下部の HTML overlay 経由でフォーカス中フィールドへ値を流す。
            if (ImeBridge.IsAvailable)
            {
                WireImeField(_nameInput);
                WireImeField(_codeInput);
                // 初期ターゲットは name-input (クリック前でも入力可能)
                ImeBridge.SetValue(string.Empty);
                if (_nameInput != null)
                    ImeBridge.BindActive(v => _nameInput.SetValueWithoutNotify(v), null);
                ImeBridge.Show();
            }

            SetStatus("> READY.");

            FusionSessionManager.Instance.SessionListUpdated += OnSessionListUpdated;

            // ロビー入室は [ REFRESH ] 明示クリック時のみ。
            // 自動入室すると BOOT/JOIN 時に Runner を Shutdown→再作成する過程で
            // Photon のソケット状態遷移がこじれるため、必要になるまで入らない。
        }

        private void OnDisable()
        {
            if (_createBtn != null) _createBtn.clicked -= OnCreateClicked;
            if (_joinBtn != null) _joinBtn.clicked -= OnJoinClicked;
            if (_refreshBtn != null) _refreshBtn.clicked -= OnRefreshClicked;
            if (_rulesBtn != null) _rulesBtn.clicked -= OpenRules;
            if (_rulesCloseBtn != null) _rulesCloseBtn.clicked -= CloseRules;
            if (FusionSessionManager.Instance != null)
                FusionSessionManager.Instance.SessionListUpdated -= OnSessionListUpdated;
            ImeBridge.BindActive(null, null);
        }

        /// <summary>
        /// UI Toolkit TextField にフォーカスが入ったら HTML overlay の値をミラー → 入力を受信するよう Bind。
        /// WebGL 以外では no-op (ImeBridge.IsAvailable が false で呼ばれないため)。
        /// </summary>
        private void WireImeField(TextField field)
        {
            if (field == null) return;
            field.isReadOnly = true;
            field.tooltip = "画面下部の入力欄に入力してください";
            field.RegisterCallback<FocusInEvent>(_ =>
            {
                ImeBridge.SetValue(field.value);
                ImeBridge.BindActive(v => field.SetValueWithoutNotify(v), null);
                ImeBridge.Show();
            });
        }

        private void OpenRules()
        {
            if (_rulesOverlay != null) _rulesOverlay.style.display = DisplayStyle.Flex;
        }

        private void CloseRules()
        {
            if (_rulesOverlay != null) _rulesOverlay.style.display = DisplayStyle.None;
        }

        private void EnsureSessionManager()
        {
            if (FusionSessionManager.Instance != null) return;
            var go = new GameObject("[FusionSessionManager]");
            go.AddComponent<FusionSessionManager>();
        }

        private async void OnCreateClicked()
        {
            if (_transitioning) return;
            if (!ValidateName()) return;
            var code = SessionCodeGenerator.Generate();
            SetStatus($"> BOOTING NEW SESSION [{code}]...");
            SetButtonsEnabled(false);
            _transitioning = true;

            var result = await FusionSessionManager.Instance.CreateSession(code);
            HandleResult(result, code);
        }

        private async void OnJoinClicked()
        {
            if (_transitioning) return;
            if (!ValidateName()) return;
            var code = _codeInput.value?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
            {
                SetStatus("> ERROR: SESSION CODE REQUIRED.", isError: true);
                return;
            }
            SetStatus($"> CONNECTING TO [{code}]...");
            SetButtonsEnabled(false);
            _transitioning = true;

            var result = await FusionSessionManager.Instance.JoinSession(code);
            HandleResult(result, code);
        }

        private async void OnSessionItemJoinClicked(string code)
        {
            if (_transitioning) return;
            if (!ValidateName()) return;
            SetStatus($"> CONNECTING TO [{code}]...");
            SetButtonsEnabled(false);
            _transitioning = true;

            var result = await FusionSessionManager.Instance.JoinSession(code);
            HandleResult(result, code);
        }

        private async void OnRefreshClicked()
        {
            if (_transitioning) return;
            SetStatus("> SCANNING LOBBY...");
            // Runner が既にセッション中の場合は一度落としてロビー入室
            if (FusionSessionManager.Instance.Runner != null && !FusionSessionManager.Instance.IsInLobbyOnly)
            {
                await FusionSessionManager.Instance.Shutdown();
            }
            if (FusionSessionManager.Instance.Runner == null)
            {
                var r = await FusionSessionManager.Instance.EnterLobby();
                SetStatus(r.Ok ? "> LOBBY. sessions listed below." : $"> SCAN FAILED: {r.ShutdownReason}", !r.Ok);
            }
        }

        private bool ValidateName()
        {
            var n = _nameInput.value?.Trim();
            if (string.IsNullOrWhiteSpace(n))
            {
                n = GenerateAnonymousHandle();
                _nameInput.value = n;
                SetStatus($"> NO HANDLE PROVIDED. Auto-assigned: {n}");
            }
            FusionSessionManager.Instance.SetPlayerName(n);
            return true;
        }

        private static string GenerateAnonymousHandle()
        {
            var rng = new System.Random();
            return $"OPERATOR_{rng.Next(0, 1000):000}";
        }

        private void HandleResult(StartGameResult result, string code)
        {
            if (result.Ok)
            {
                SetStatus($"> CONNECTED TO [{code}]. LOADING LOBBY...");
                if (FusionSessionManager.Instance.IsHost)
                {
                    var buildIndex = UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath(
                        $"Assets/_ProtocolSingularity/Scenes/{lobbySceneName}.unity");
                    if (buildIndex >= 0)
                    {
                        FusionSessionManager.Instance.Runner.LoadScene(SceneRef.FromIndex(buildIndex));
                    }
                    else
                    {
                        SetStatus($"> WARN: '{lobbySceneName}' scene not registered in build settings.", isError: true);
                    }
                }
                // クライアントは Fusion が自動遷移
            }
            else
            {
                SetStatus($"> CONNECTION FAILED: {result.ShutdownReason}", isError: true);
                SetButtonsEnabled(true);
                _transitioning = false;
            }
        }

        private void OnSessionListUpdated(List<SessionInfo> sessions)
        {
            if (_sessionList == null) return;
            _sessionList.Clear();

            int shown = 0;
            if (sessions != null)
            {
                foreach (var info in sessions)
                {
                    if (!info.IsOpen || !info.IsVisible) continue;
                    _sessionList.Add(CreateSessionItem(info));
                    shown++;
                }
            }

            if (_sessionListEmpty != null)
            {
                _sessionListEmpty.style.display = shown == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (shown == 0)
                {
                    _sessionListEmpty.text = FusionSessionManager.Instance != null && FusionSessionManager.Instance.IsInLobbyOnly
                        ? "> No active sessions found."
                        : "> Press [ SCAN ] to scan the session list.";
                }
                else
                {
                    _sessionListEmpty.text = string.Empty;
                }
            }
        }

        private VisualElement CreateSessionItem(SessionInfo info)
        {
            if (sessionListItemTemplate == null)
            {
                return new Label($"{info.Name}  {info.PlayerCount}/{info.MaxPlayers}");
            }
            var tree = sessionListItemTemplate.CloneTree();
            var codeLabel = tree.Q<Label>("session-code");
            var infoLabel = tree.Q<Label>("session-info");
            var joinBtn = tree.Q<Button>("session-join-btn");

            if (codeLabel != null) codeLabel.text = info.Name;
            if (infoLabel != null) infoLabel.text = $"{info.PlayerCount}/{info.MaxPlayers}";
            if (joinBtn != null)
            {
                var code = info.Name;
                joinBtn.clicked += () => OnSessionItemJoinClicked(code);
            }
            return tree;
        }

        private void SetButtonsEnabled(bool enabled)
        {
            if (_createBtn != null) _createBtn.SetEnabled(enabled);
            if (_joinBtn != null) _joinBtn.SetEnabled(enabled);
            if (_refreshBtn != null) _refreshBtn.SetEnabled(enabled);
        }

        private void SetStatus(string msg, bool isError = false)
        {
            if (_statusLine == null) return;
            _statusLine.text = msg;
            _statusLine.RemoveFromClassList("-error");
            if (isError) _statusLine.AddToClassList("-error");
        }

    }
}
