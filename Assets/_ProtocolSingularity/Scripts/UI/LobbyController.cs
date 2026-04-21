using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using ProtocolSingularity.Core;
using ProtocolSingularity.Data;
using ProtocolSingularity.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ProtocolSingularity.UI
{
    /// <summary>
    /// ゲームシーン（兼ロビー）の UI 制御。
    /// Phase に応じて UI パネルを切替える単一シーン方式。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class LobbyController : MonoBehaviour
    {
        [SerializeField] private VisualTreeAsset playerListItemTemplate;
        [SerializeField] private string titleSceneName = "Title";
        [SerializeField] private bool bypassSessionCheckInEditor = false;
        [SerializeField] private RoleDistributionConfig roleDistributionConfig;

        // Shared
        private UIDocument _doc;
        private Label _sceneTitle;
        private Label _sceneSubtitle;
        private Label _sessionCodeLabel;
        private Label _statusLine;
        private VisualElement _lobbyContent;
        private VisualElement _ingameContent;
        private VisualElement _phasePopupOverlay;
        private VisualElement _phasePopupDialog;
        private Button _phasePopupMinimizeBtn;
        private VisualElement _phasePopupReopenBar;
        private Button _phasePopupReopenBtn;
        private bool _phasePopupMinimized;

        // Lobby phase
        private ScrollView _playerList;
        private Label _playerCountLine;
        private VisualElement _hostSettings;
        private Button _startBtn;
        private SliderInt _playerCountSlider;
        private SliderInt _discussionSlider;
        private SliderInt _voteSlider;
        private SliderInt _hackSlider;
        private Toggle _cpuFillToggle;
        private Toggle _lockToggle;

        // Role lineup (host configurable)
        private VisualElement _roleLineupList;
        private Label _roleLineupCompact;
        private Label _roleLineupSummary;
        private Label _roleEditorSummary;
        private Button _roleEditBtn;
        private Button _roleEditorCloseBtn;
        private VisualElement _roleEditorOverlay;
        private bool _roleIncludeAgent;
        private bool _roleIncludeCipher;
        private bool _roleIncludeDrone;
        private bool _roleIncludeRadical;

        // In-game (always shown during gameplay)
        private Label _phaseLabel;
        private Label _roundLabel;
        private Label _countersLabel;
        private Label _leaderLabel;
        private Label _roleLabel;
        private Label _factionLabel;
        private ScrollView _ingamePlayerList;
        private ScrollView _hackLogList;
        private Label _hackLogEmpty;

        // Phase-specific action panels
        private VisualElement _teamProposalSection;
        private Label _teamProposalInstruction;
        private ScrollView _teamPickList;
        private Button _proposeBtn;
        private VisualElement _voteSection;
        private Label _voteProposalDisplay;
        private Button _voteYesBtn;
        private Button _voteNoBtn;
        private Label _voteStatusLabel;
        private VisualElement _hackingSection;
        private Label _hackingInstruction;
        private Button _hackCleanBtn;
        private Button _hackNoiseBtn;
        private Label _hackingStatusLabel;
        private VisualElement _resultSection;
        private Label _resultHeadline;
        private Label _resultDetail;
        private VisualElement _overrideDiscussionSection;
        private Label _overrideDiscussionHeadline;
        private Label _overrideDiscussionInstruction;
        private VisualElement _overrideVoteSection;
        private Label _overrideVoteInstruction;
        private ScrollView _overrideTargetList;
        private Button _overrideVoteSubmitBtn;
        private Label _overrideVoteStatus;
        private VisualElement _overrideResultSection;
        private Label _overrideResultHeadline;
        private Label _overrideResultDetail;
        private VisualElement _gameendSection;
        private Label _gameendHeadline;
        private Label _gameendDetail;
        private Label _gameendHackHistory;
        private ScrollView _gameendRolesList;
        private Button _gameendReturnBtn;

        // Menu
        private Button _menuBtn;
        private VisualElement _menuOverlay;
        private VisualElement _menuDialog;
        private Button _menuCloseBtn;
        private Button _menuLeaveBtn;

        // Chat
        private ScrollView _chatLog;
        private TextField _chatInput;
        private VisualElement _chatMentionRow;
        private Button _chatSendBtn;

        private FusionSessionManager _sm;
        private bool _suppressSettingsPush;
        private readonly HashSet<int> _pickedPlayerIds = new();
        private int _overrideTargetPickId = -1;
        private bool _hasSubmittedOverrideVote;
        private GamePhase _lastObservedPhase = GamePhase.Lobby;

        // ==========================================================
        // Lifecycle
        // ==========================================================
        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            var root = _doc.rootVisualElement;
            if (root == null) return;

            QueryElements(root);
            WireEvents();

            _sm = FusionSessionManager.Instance;
            if (_sm == null || _sm.Runner == null)
            {
#if UNITY_EDITOR
                if (bypassSessionCheckInEditor)
                {
                    if (_sessionCodeLabel != null) _sessionCodeLabel.text = "[ EDITOR-TEST ]";
                    if (_hostSettings != null) _hostSettings.style.display = DisplayStyle.Flex;
                    if (_startBtn != null) _startBtn.SetEnabled(true);
                    SetStatus("> EDITOR TEST MODE (no session).");
                    return;
                }
#endif
                SetStatus("> ERROR: No session active. Returning to title.", true);
                SceneManager.LoadScene(titleSceneName);
                return;
            }

            _sm.SessionShutdown += OnSessionShutdown;
            PlayerRegistry.Changed += OnRegistryChanged;
            HostSettings.SettingsChanged += OnSettingsChanged;
            GameStateManager.Changed += OnGameStateChanged;
            GameStateManager.LocalRoleReceived += OnLocalRoleReceived;
            ChatManager.Changed += OnChatChanged;
            GameLog.Changed += OnGameLogChanged;

            InitChatComposer();
            EnsureImeBridge();
            ImeBridge.TextReceived += OnImeTextReceived;

            UpdateSessionCode();
            ApplyHostVisibility();
            OnRegistryChanged();
            OnSettingsChanged();
            OnGameStateChanged();
            SetStatus(_sm.IsHost
                ? "> HOST. Waiting for operators to connect..."
                : "> CONNECTED. Awaiting host signal.");
        }

        private void OnDisable()
        {
            if (_startBtn != null) _startBtn.clicked -= OnStartClicked;
            if (_proposeBtn != null) _proposeBtn.clicked -= OnProposeClicked;
            if (_voteYesBtn != null) _voteYesBtn.clicked -= OnVoteYes;
            if (_voteNoBtn != null) _voteNoBtn.clicked -= OnVoteNo;
            if (_hackCleanBtn != null) _hackCleanBtn.clicked -= OnHackClean;
            if (_hackNoiseBtn != null) _hackNoiseBtn.clicked -= OnHackNoise;
            if (_overrideVoteSubmitBtn != null) _overrideVoteSubmitBtn.clicked -= OnOverrideVoteSubmit;
            if (_chatSendBtn != null) _chatSendBtn.clicked -= OnChatSendClicked;
            if (_gameendReturnBtn != null) _gameendReturnBtn.clicked -= OnGameEndReturnClicked;
            if (_menuBtn != null) _menuBtn.clicked -= OpenMenu;
            if (_menuCloseBtn != null) _menuCloseBtn.clicked -= CloseMenu;
            if (_menuLeaveBtn != null) _menuLeaveBtn.clicked -= OnLeaveClicked;
            if (_roleEditBtn != null) _roleEditBtn.clicked -= OpenRoleEditor;
            if (_roleEditorCloseBtn != null) _roleEditorCloseBtn.clicked -= CloseRoleEditor;
            if (_menuOverlay != null) _menuOverlay.UnregisterCallback<ClickEvent>(OnOverlayClicked);
            if (_sm != null) _sm.SessionShutdown -= OnSessionShutdown;
            PlayerRegistry.Changed -= OnRegistryChanged;
            HostSettings.SettingsChanged -= OnSettingsChanged;
            GameStateManager.Changed -= OnGameStateChanged;
            GameStateManager.LocalRoleReceived -= OnLocalRoleReceived;
            ChatManager.Changed -= OnChatChanged;
            GameLog.Changed -= OnGameLogChanged;
            ImeBridge.TextReceived -= OnImeTextReceived;
        }

        /// <summary>ImeBridge が存在しなければ生成する (WebGL で JS から SendMessage できるよう)。</summary>
        private static void EnsureImeBridge()
        {
            if (ImeBridge.Instance != null) return;
            var go = new GameObject("ImeBridge");
            go.AddComponent<ImeBridge>();
        }

        /// <summary>WebGL 側 JS (ime-bridge.js) から届いた IME 確定文字列を chat-input に追記。</summary>
        private void OnImeTextReceived(string text)
        {
            if (_chatInput == null || string.IsNullOrEmpty(text)) return;
            // chat-input がフォーカスを持っているときのみ追記 (他の入力欄に影響を与えない)
            if (_chatInput.focusController == null || _chatInput.focusController.focusedElement != _chatInput)
            {
                // フォーカス無しでも chat-input に入れたい場合はフォーカスを戻して追記
                _chatInput.Focus();
            }
            var cur = _chatInput.value ?? string.Empty;
            _chatInput.value = cur + text;
        }

        private void QueryElements(VisualElement root)
        {
            _sceneTitle = root.Q<Label>("scene-title");
            _sceneSubtitle = root.Q<Label>("scene-subtitle");
            _sessionCodeLabel = root.Q<Label>("session-code-line");
            _statusLine = root.Q<Label>("status-line");
            _lobbyContent = root.Q<VisualElement>("lobby-phase-content");
            _ingameContent = root.Q<VisualElement>("ingame-phase-content");
            _phasePopupOverlay = root.Q<VisualElement>("phase-popup-overlay");
            _phasePopupDialog = root.Q<VisualElement>("phase-popup-dialog");
            _phasePopupMinimizeBtn = root.Q<Button>("phase-popup-minimize-btn");
            _phasePopupReopenBar = root.Q<VisualElement>("phase-popup-reopen-bar");
            _phasePopupReopenBtn = root.Q<Button>("phase-popup-reopen-btn");
            if (_phasePopupMinimizeBtn != null) _phasePopupMinimizeBtn.clicked += OnPhasePopupMinimize;
            if (_phasePopupReopenBtn != null) _phasePopupReopenBtn.clicked += OnPhasePopupReopen;

            _playerList = root.Q<ScrollView>("player-list");
            _playerCountLine = root.Q<Label>("player-count-line");
            _hostSettings = root.Q<VisualElement>("host-settings-panel");
            _startBtn = root.Q<Button>("start-btn");
            _playerCountSlider = root.Q<SliderInt>("player-count-slider");
            _discussionSlider = root.Q<SliderInt>("discussion-slider");
            _voteSlider = root.Q<SliderInt>("vote-slider");
            _hackSlider = root.Q<SliderInt>("hack-slider");
            _cpuFillToggle = root.Q<Toggle>("cpu-fill-toggle");
            _roleLineupList = root.Q<VisualElement>("role-lineup-list");
            _roleLineupCompact = root.Q<Label>("role-lineup-compact");
            _roleLineupSummary = root.Q<Label>("role-lineup-summary");
            _roleEditorSummary = root.Q<Label>("role-editor-summary");
            _roleEditBtn = root.Q<Button>("role-edit-btn");
            _roleEditorCloseBtn = root.Q<Button>("role-editor-close-btn");
            _roleEditorOverlay = root.Q<VisualElement>("role-editor-overlay");
            if (_roleEditBtn != null) _roleEditBtn.clicked += OpenRoleEditor;
            if (_roleEditorCloseBtn != null) _roleEditorCloseBtn.clicked += CloseRoleEditor;
            if (_roleEditorOverlay != null) _roleEditorOverlay.RegisterCallback<ClickEvent>(OnRoleEditorBackdropClicked);
            _lockToggle = root.Q<Toggle>("lock-toggle");

            _phaseLabel = root.Q<Label>("ingame-phase-label");
            _roundLabel = root.Q<Label>("ingame-round-label");
            _countersLabel = root.Q<Label>("ingame-counters-label");
            _leaderLabel = root.Q<Label>("ingame-leader-label");
            _roleLabel = root.Q<Label>("ingame-role-label");
            _factionLabel = root.Q<Label>("ingame-faction-label");
            _ingamePlayerList = root.Q<ScrollView>("ingame-player-list");
            _hackLogList = root.Q<ScrollView>("hack-log-list");
            _hackLogEmpty = root.Q<Label>("hack-log-empty");

            _teamProposalSection = root.Q<VisualElement>("team-proposal-section");
            _teamProposalInstruction = root.Q<Label>("team-proposal-instruction");
            _teamPickList = root.Q<ScrollView>("team-pick-list");
            _proposeBtn = root.Q<Button>("propose-btn");
            _voteSection = root.Q<VisualElement>("vote-section");
            _voteProposalDisplay = root.Q<Label>("vote-proposal-display");
            _voteYesBtn = root.Q<Button>("vote-yes-btn");
            _voteNoBtn = root.Q<Button>("vote-no-btn");
            _voteStatusLabel = root.Q<Label>("vote-status-label");
            _hackingSection = root.Q<VisualElement>("hacking-section");
            _hackingInstruction = root.Q<Label>("hacking-instruction");
            _hackCleanBtn = root.Q<Button>("hack-clean-btn");
            _hackNoiseBtn = root.Q<Button>("hack-noise-btn");
            _hackingStatusLabel = root.Q<Label>("hacking-status-label");
            _resultSection = root.Q<VisualElement>("result-section");
            _resultHeadline = root.Q<Label>("result-headline");
            _resultDetail = root.Q<Label>("result-detail");
            _overrideDiscussionSection = root.Q<VisualElement>("override-discussion-section");
            _overrideDiscussionHeadline = root.Q<Label>("override-discussion-headline");
            _overrideDiscussionInstruction = root.Q<Label>("override-discussion-instruction");
            _overrideVoteSection = root.Q<VisualElement>("override-vote-section");
            _overrideVoteInstruction = root.Q<Label>("override-vote-instruction");
            _overrideTargetList = root.Q<ScrollView>("override-target-list");
            _overrideVoteSubmitBtn = root.Q<Button>("override-vote-submit-btn");
            _overrideVoteStatus = root.Q<Label>("override-vote-status");
            _overrideResultSection = root.Q<VisualElement>("override-result-section");
            _overrideResultHeadline = root.Q<Label>("override-result-headline");
            _overrideResultDetail = root.Q<Label>("override-result-detail");
            _gameendSection = root.Q<VisualElement>("gameend-section");
            _gameendHeadline = root.Q<Label>("gameend-headline");
            _gameendDetail = root.Q<Label>("gameend-detail");
            _gameendHackHistory = root.Q<Label>("gameend-hack-history");
            _gameendRolesList = root.Q<ScrollView>("gameend-roles-list");
            _gameendReturnBtn = root.Q<Button>("gameend-return-btn");

            _menuBtn = root.Q<Button>("menu-btn");
            _menuOverlay = root.Q<VisualElement>("menu-overlay");
            _menuDialog = root.Q<VisualElement>("menu-dialog");
            _menuCloseBtn = root.Q<Button>("menu-close-btn");
            _menuLeaveBtn = root.Q<Button>("menu-leave-btn");

            _chatLog = root.Q<ScrollView>("chat-log");
            _chatInput = root.Q<TextField>("chat-input");
            _chatMentionRow = root.Q<VisualElement>("chat-mention-row");
            _chatSendBtn = root.Q<Button>("chat-send-btn");
        }

        private void WireEvents()
        {
            if (_startBtn != null) _startBtn.clicked += OnStartClicked;
            if (_proposeBtn != null) _proposeBtn.clicked += OnProposeClicked;
            if (_voteYesBtn != null) _voteYesBtn.clicked += OnVoteYes;
            if (_voteNoBtn != null) _voteNoBtn.clicked += OnVoteNo;
            if (_hackCleanBtn != null) _hackCleanBtn.clicked += OnHackClean;
            if (_hackNoiseBtn != null) _hackNoiseBtn.clicked += OnHackNoise;
            if (_overrideVoteSubmitBtn != null) _overrideVoteSubmitBtn.clicked += OnOverrideVoteSubmit;
            if (_chatSendBtn != null) _chatSendBtn.clicked += OnChatSendClicked;
            if (_chatInput != null) _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            if (_gameendReturnBtn != null) _gameendReturnBtn.clicked += OnGameEndReturnClicked;
            if (_menuBtn != null) _menuBtn.clicked += OpenMenu;
            if (_menuCloseBtn != null) _menuCloseBtn.clicked += CloseMenu;
            if (_menuLeaveBtn != null) _menuLeaveBtn.clicked += OnLeaveClicked;
            if (_menuOverlay != null) _menuOverlay.RegisterCallback<ClickEvent>(OnOverlayClicked);

            _playerCountSlider.RegisterValueChangedCallback(evt =>
            {
                PushSettings();
                ResetRoleLineupToDefaults(evt.newValue);
            });
            _discussionSlider.RegisterValueChangedCallback(_ => PushSettings());
            _voteSlider.RegisterValueChangedCallback(_ => PushSettings());
            _hackSlider.RegisterValueChangedCallback(_ => PushSettings());
            _cpuFillToggle.RegisterValueChangedCallback(_ => PushSettings());
            _lockToggle.RegisterValueChangedCallback(evt => OnLockToggleChanged(evt.newValue));

            // Initial sync from current slider value
            if (_playerCountSlider != null) ResetRoleLineupToDefaults(_playerCountSlider.value);
        }

        /// <summary>
        /// 目標人数が変わったとき、デフォルト配分 (RoleDistributionConfig) の特殊役職選択をトグルに反映。
        /// ユーザーが後から個別にオン/オフ可能。
        /// </summary>
        private void ResetRoleLineupToDefaults(int playerCount)
        {
            var entry = roleDistributionConfig != null ? roleDistributionConfig.GetEntry(playerCount) : null;
            if (entry != null)
            {
                _roleIncludeAgent = entry.agentCount > 0;
                _roleIncludeCipher = entry.includeCipher;
                _roleIncludeDrone = entry.includeDrone;
                _roleIncludeRadical = entry.includeRadical;
            }
            RefreshRoleLineup();
        }

        private void OpenRoleEditor()
        {
            bool host = _sm != null && _sm.IsHost;
            if (!host) return;
            if (_roleEditorOverlay != null) _roleEditorOverlay.style.display = DisplayStyle.Flex;
            RefreshRoleLineup();
        }

        private void CloseRoleEditor()
        {
            if (_roleEditorOverlay != null) _roleEditorOverlay.style.display = DisplayStyle.None;
            RefreshRoleLineup();
        }

        private void OnRoleEditorBackdropClicked(ClickEvent evt)
        {
            // Close only when the backdrop (overlay itself) is clicked, not the dialog inside.
            if (evt.target == _roleEditorOverlay) CloseRoleEditor();
        }

        /// <summary>
        /// 設定パネル側: コンパクト要約 (編成を 1 行で俯瞰) + EDIT ボタン活性制御。
        /// モーダル側: 役職行リスト + summary + トグル。
        /// </summary>
        private void RefreshRoleLineup()
        {
            int target = _playerCountSlider != null ? _playerCountSlider.value : 6;
            int specials = 3
                + (_roleIncludeAgent ? 1 : 0)
                + (_roleIncludeCipher ? 1 : 0)
                + (_roleIncludeDrone ? 1 : 0)
                + (_roleIncludeRadical ? 1 : 0);
            int opCount = target - specials;
            int human = 2 + Mathf.Max(0, opCount);
            int ai = 1 + (_roleIncludeAgent ? 1 : 0) + (_roleIncludeCipher ? 1 : 0)
                       + (_roleIncludeDrone ? 1 : 0) + (_roleIncludeRadical ? 1 : 0);
            bool host = _sm != null && _sm.IsHost;
            bool editorOpen = _roleEditorOverlay != null && _roleEditorOverlay.style.display == DisplayStyle.Flex;

            // Settings panel: compact one-liner of the current lineup
            if (_roleLineupCompact != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("Oracle, Admin, MC");
                if (_roleIncludeAgent) sb.Append(", Agent");
                if (_roleIncludeCipher) sb.Append(", Cipher");
                if (_roleIncludeDrone) sb.Append(", Drone");
                if (_roleIncludeRadical) sb.Append(", Radical");
                if (opCount > 0) sb.Append($", Operator x{opCount}");
                _roleLineupCompact.text = sb.ToString();
            }

            string summaryText = opCount < 0
                ? $"! Too many AI roles ({-opCount} over). Disable one to continue."
                : $"Humans {human} : AI {ai}";
            bool isError = opCount < 0;

            if (_roleLineupSummary != null)
            {
                _roleLineupSummary.RemoveFromClassList("-error");
                if (isError) _roleLineupSummary.AddToClassList("-error");
                _roleLineupSummary.text = summaryText;
            }
            if (_roleEditorSummary != null)
            {
                _roleEditorSummary.RemoveFromClassList("-error");
                if (isError) _roleEditorSummary.AddToClassList("-error");
                _roleEditorSummary.text = summaryText;
            }

            // Only build the list rows when the modal is open (avoids wasted UI work).
            if (_roleLineupList != null && editorOpen)
            {
                _roleLineupList.Clear();
                _roleLineupList.Add(BuildRoleRow("ORACLE", 1, isAi: false, fixedRole: true, enabled: true, onChange: null));
                _roleLineupList.Add(BuildRoleRow("ADMIN",  1, isAi: false, fixedRole: true, enabled: true, onChange: null));
                _roleLineupList.Add(BuildRoleRow("OPERATOR (auto)", Mathf.Max(0, opCount), isAi: false, fixedRole: true, enabled: opCount >= 0, onChange: null));
                _roleLineupList.Add(BuildRoleRow("MOTHER CORE", 1, isAi: true, fixedRole: true, enabled: true, onChange: null));
                _roleLineupList.Add(BuildRoleRow("AGENT (basic AI)", _roleIncludeAgent ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeAgent, toggleValue: _roleIncludeAgent, onChange: v => { _roleIncludeAgent = v; RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("CIPHER (hidden from ORACLE)", _roleIncludeCipher ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeCipher, toggleValue: _roleIncludeCipher, onChange: v => { _roleIncludeCipher = v; RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("DRONE (awakens mid-game)", _roleIncludeDrone ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeDrone, toggleValue: _roleIncludeDrone, onChange: v => { _roleIncludeDrone = v; RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("RADICAL (isolated AI)", _roleIncludeRadical ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeRadical, toggleValue: _roleIncludeRadical, onChange: v => { _roleIncludeRadical = v; RefreshRoleLineup(); }));
            }

            if (_roleEditBtn != null)
            {
                _roleEditBtn.SetEnabled(host);
                _roleEditBtn.style.display = host ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // Non-host clients must never see the editor even if it was open
            if (!host && _roleEditorOverlay != null)
                _roleEditorOverlay.style.display = DisplayStyle.None;

            if (_startBtn != null) _startBtn.SetEnabled(host && opCount >= 0);
        }

        private VisualElement BuildRoleRow(string name, int count, bool isAi, bool fixedRole, bool enabled,
            bool toggleValue = false, System.Action<bool> onChange = null)
        {
            var row = new VisualElement();
            row.AddToClassList("role-lineup-row");
            if (isAi) row.AddToClassList("-ai");
            if (fixedRole) row.AddToClassList("-fixed");
            if (!enabled && !fixedRole) row.AddToClassList("-disabled");

            var nameLbl = new Label(name);
            nameLbl.AddToClassList("role-lineup-row-name");
            row.Add(nameLbl);

            var countLbl = new Label($"x{count}");
            countLbl.AddToClassList("role-lineup-row-count");
            row.Add(countLbl);

            // 可変役職にはトグルを出す。常に操作可能 (ホストのみ overlay を開ける)。
            if (!fixedRole && onChange != null)
            {
                var toggle = new Toggle();
                toggle.AddToClassList("role-lineup-row-toggle");
                toggle.SetValueWithoutNotify(toggleValue);
                toggle.RegisterValueChangedCallback(evt => onChange(evt.newValue));
                row.Add(toggle);
            }
            return row;
        }

        /// <summary>現在の UI トグルから実行時 Entry を構築。</summary>
        private RoleDistributionConfig.Entry BuildRuntimeRoleEntry(int playerCount)
        {
            int specials = 3
                + (_roleIncludeAgent ? 1 : 0)
                + (_roleIncludeCipher ? 1 : 0)
                + (_roleIncludeDrone ? 1 : 0)
                + (_roleIncludeRadical ? 1 : 0);
            return new RoleDistributionConfig.Entry
            {
                playerCount = playerCount,
                includeOracle = true,
                includeAdmin = true,
                includeMotherCore = true,
                operatorCount = Mathf.Max(0, playerCount - specials),
                agentCount = _roleIncludeAgent ? 1 : 0,
                includeCipher = _roleIncludeCipher,
                includeDrone = _roleIncludeDrone,
                includeRadical = _roleIncludeRadical,
            };
        }

        // ==========================================================
        // Menu
        // ==========================================================
        private void OpenMenu() { if (_menuOverlay != null) _menuOverlay.style.display = DisplayStyle.Flex; }
        private void CloseMenu() { if (_menuOverlay != null) _menuOverlay.style.display = DisplayStyle.None; }
        private void OnOverlayClicked(ClickEvent evt) { if (evt.target == _menuOverlay) CloseMenu(); }

        // ==========================================================
        // Session lifecycle
        // ==========================================================
        private void UpdateSessionCode()
        {
            var session = _sm.Runner?.SessionInfo?.Name ?? "----------";
            if (_sessionCodeLabel != null) _sessionCodeLabel.text = $"[ {session} ]";
        }

        private void ApplyHostVisibility()
        {
            bool host = _sm.IsHost;
            if (_hostSettings != null) _hostSettings.style.display = host ? DisplayStyle.Flex : DisplayStyle.None;
            if (_startBtn != null) _startBtn.SetEnabled(host);
        }

        private async void OnSessionShutdown(ShutdownReason reason)
        {
            SetStatus($"> SESSION ENDED ({reason}). Returning to title.", true);
            await Task.Yield();
            SceneManager.LoadScene(titleSceneName);
        }

        private async void OnLeaveClicked()
        {
            CloseMenu();
            SetStatus("> DISCONNECTING...");
            if (_sm != null) await _sm.Shutdown();
            SceneManager.LoadScene(titleSceneName);
        }

        // ==========================================================
        // Phase switching
        // ==========================================================
        private void OnGameStateChanged()
        {
            var gsm = GameStateManager.Instance;
            var phase = gsm != null ? gsm.Phase : GamePhase.Lobby;
            bool inLobby = phase == GamePhase.Lobby;

            if (_lobbyContent != null) _lobbyContent.style.display = inLobby ? DisplayStyle.Flex : DisplayStyle.None;
            if (_ingameContent != null) _ingameContent.style.display = inLobby ? DisplayStyle.None : DisplayStyle.Flex;

            if (_sceneTitle != null) _sceneTitle.text = inLobby ? "SESSION LOBBY" : "INFILTRATION";
            if (_sceneSubtitle != null) _sceneSubtitle.text = inLobby
                ? "> awaiting operator connections..."
                : "> breach protocol in progress.";

            if (phase != _lastObservedPhase)
            {
                _pickedPlayerIds.Clear();
                if (phase == GamePhase.OverrideDiscussion)
                {
                    _overrideTargetPickId = -1;
                    _hasSubmittedOverrideVote = false;
                }
                // 新しいフェーズに入ったときは必ず開いた状態にする
                _phasePopupMinimized = false;
                _lastObservedPhase = phase;
            }

            if (gsm == null || inLobby)
            {
                HideAllPhaseSections();
                SetDisplay(_phasePopupOverlay, false);
                SetDisplay(_phasePopupReopenBar, false);
                return;
            }
            ApplyPhasePopupVisibility();

            if (_phaseLabel != null) _phaseLabel.text = $"PHASE: {phase}";
            if (_roundLabel != null)
            {
                _roundLabel.text = gsm.RequiredNoise >= 2
                    ? $"ROUND: {gsm.Round}  (TEAM {gsm.TeamSize} / FAIL NOISE>={gsm.RequiredNoise})"
                    : $"ROUND: {gsm.Round}  (TEAM {gsm.TeamSize})";
            }
            if (_countersLabel != null)
                _countersLabel.text = $"SUCCESS: {gsm.SuccessCount}/{GameStateManager.RequiredHackSuccess} / FAIL: {gsm.FailureCount}/{GameStateManager.RequiredHackFailure} / REJECT: {gsm.ConsecutiveRejections}/{GameStateManager.MaxConsecutiveRejections}";
            if (_leaderLabel != null)
                _leaderLabel.text = $"LEADER: {ResolvePlayerName(gsm.CurrentLeader)}";

            UpdateRoleDisplay();
            RefreshIngamePlayerList();
            UpdateActionPanel(phase, gsm);
        }

        private void HideAllPhaseSections()
        {
            SetDisplay(_teamProposalSection, false);
            SetDisplay(_voteSection, false);
            SetDisplay(_hackingSection, false);
            SetDisplay(_resultSection, false);
            SetDisplay(_overrideDiscussionSection, false);
            SetDisplay(_overrideVoteSection, false);
            SetDisplay(_overrideResultSection, false);
            SetDisplay(_gameendSection, false);
        }

        private static void SetDisplay(VisualElement ve, bool visible)
        {
            if (ve == null) return;
            ve.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }


        /// <summary>
        /// ダイアログ最小化状態に応じて overlay と reopen bar の表示を切替える。
        /// 最小化時: overlay を隠し画面下に "OPEN" ボタンだけ残す。
        /// </summary>
        private void ApplyPhasePopupVisibility()
        {
            bool show = !_phasePopupMinimized;
            SetDisplay(_phasePopupOverlay, show);
            SetDisplay(_phasePopupReopenBar, !show);
        }

        private void OnPhasePopupMinimize()
        {
            _phasePopupMinimized = true;
            ApplyPhasePopupVisibility();
        }

        private void OnPhasePopupReopen()
        {
            _phasePopupMinimized = false;
            ApplyPhasePopupVisibility();
        }

        private void UpdateActionPanel(GamePhase phase, GameStateManager gsm)
        {
            HideAllPhaseSections();
            var localPlayer = _sm.Runner != null ? _sm.Runner.LocalPlayer : PlayerRef.None;
            switch (phase)
            {
                case GamePhase.TeamProposal:
                    UpdateTeamProposalUi(gsm, localPlayer);
                    break;
                case GamePhase.ApprovalVote:
                    UpdateVoteUi(gsm, localPlayer);
                    break;
                case GamePhase.Hacking:
                    UpdateHackingUi(gsm, localPlayer);
                    break;
                case GamePhase.RoundResult:
                    UpdateResultUi(gsm);
                    break;
                case GamePhase.OverrideDiscussion:
                    UpdateOverrideDiscussionUi(gsm, localPlayer);
                    break;
                case GamePhase.OverrideVote:
                    UpdateOverrideVoteUi(gsm, localPlayer);
                    break;
                case GamePhase.OverrideResult:
                    UpdateOverrideResultUi(gsm);
                    break;
                case GamePhase.GameEnd:
                    UpdateGameEndUi(gsm);
                    break;
            }
        }

        // ==========================================================
        // Phase: Team Proposal
        // ==========================================================
        private void UpdateTeamProposalUi(GameStateManager gsm, PlayerRef localPlayer)
        {
            SetDisplay(_teamProposalSection, true);
            bool isLeader = gsm.CurrentLeader == localPlayer;
            if (_teamProposalInstruction != null)
            {
                string failClause = gsm.RequiredNoise >= 2
                    ? $" (このラウンドは NOISE {gsm.RequiredNoise} 枚以上で FAIL)"
                    : string.Empty;
                _teamProposalInstruction.text = isLeader
                    ? $"> あなたが LEADER です。{gsm.TeamSize}名を選んで提案してください。{failClause}"
                    : $"> LEADER ({ResolvePlayerName(gsm.CurrentLeader)}) の提案を待機中... ({gsm.TeamSize}名枠){failClause}";
            }
            RefreshTeamPickList(gsm, isLeader);
            if (_proposeBtn != null)
            {
                _proposeBtn.SetEnabled(isLeader && _pickedPlayerIds.Count == gsm.TeamSize);
                _proposeBtn.style.display = isLeader ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void RefreshTeamPickList(GameStateManager gsm, bool isLeader)
        {
            if (_teamPickList == null) return;
            _teamPickList.Clear();
            var reg = PlayerRegistry.Instance;
            if (reg == null) return;
            for (int i = 0; i < reg.Count; i++)
            {
                var entry = reg.Entries[i];
                var item = CreateTeamPickItem(entry, isLeader, gsm.TeamSize);
                _teamPickList.Add(item);
            }
        }

        private VisualElement CreateTeamPickItem(PlayerRegistry.Entry entry, bool interactable, int teamSize)
        {
            var container = new VisualElement();
            container.AddToClassList("pick-item");
            int id = entry.PlayerRef.PlayerId;
            bool selected = _pickedPlayerIds.Contains(id);
            if (selected) container.AddToClassList("-selected");

            var toggle = new Toggle();
            toggle.AddToClassList("pick-toggle");
            toggle.value = selected;
            toggle.SetEnabled(interactable);
            toggle.RegisterValueChangedCallback(evt => OnPickToggleChanged(id, evt.newValue, teamSize));
            container.Add(toggle);

            var nameLabel = new Label(BuildPlayerItemLabel(entry));
            nameLabel.AddToClassList("pick-name");
            ApplyNameColor(nameLabel, entry.PlayerRef);
            container.Add(nameLabel);

            return container;
        }

        private void OnPickToggleChanged(int playerId, bool value, int teamSize)
        {
            if (value)
            {
                if (_pickedPlayerIds.Count >= teamSize)
                {
                    // Disallow: rebuild to restore UI
                    OnGameStateChanged();
                    return;
                }
                _pickedPlayerIds.Add(playerId);
            }
            else
            {
                _pickedPlayerIds.Remove(playerId);
            }
            OnGameStateChanged();
        }

        private void OnProposeClicked()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || _sm == null || _sm.Runner == null) return;
            if (gsm.Phase != GamePhase.TeamProposal) return;
            var localPlayer = _sm.Runner.LocalPlayer;
            if (gsm.CurrentLeader != localPlayer) return;
            if (_pickedPlayerIds.Count != gsm.TeamSize) return;

            // Convert ids to PlayerRefs via PlayerRegistry
            var reg = PlayerRegistry.Instance;
            var refs = new List<PlayerRef>();
            if (reg != null)
            {
                for (int i = 0; i < reg.Count; i++)
                    if (_pickedPlayerIds.Contains(reg.Entries[i].PlayerRef.PlayerId))
                        refs.Add(reg.Entries[i].PlayerRef);
            }
            while (refs.Count < 5) refs.Add(PlayerRef.None);
            gsm.Rpc_ProposeTeamFlat(localPlayer,
                refs[0], refs[1], refs[2], refs[3], refs[4],
                _pickedPlayerIds.Count);
            SetStatus("> TEAM PROPOSED.");
        }

        // ==========================================================
        // Phase: Approval Vote
        // ==========================================================
        private void UpdateVoteUi(GameStateManager gsm, PlayerRef localPlayer)
        {
            SetDisplay(_voteSection, true);
            if (_voteProposalDisplay != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("> LEADER: ").Append(ResolveColoredPlayerName(gsm.CurrentLeader)).Append('\n');
                sb.Append("> PROPOSED TEAM: ");
                for (int i = 0; i < gsm.ProposedTeamCount; i++)
                {
                    if (i > 0) sb.Append(" / ");
                    sb.Append(ResolveColoredPlayerName(gsm.ProposedTeam[i]));
                }
                _voteProposalDisplay.text = sb.ToString();
                _voteProposalDisplay.enableRichText = true;
            }

            bool alreadyVoted = HasLocalVoted(gsm, localPlayer);
            int votes = CountVotes(gsm, out int pending);
            bool revealing = pending == 0 && gsm.LeaderOrderCount > 0;
            if (_voteStatusLabel != null)
            {
                _voteStatusLabel.enableRichText = true;
                _voteStatusLabel.text = revealing
                    ? BuildVoteRevealText(gsm)
                    : $"VOTED: {votes} / PENDING: {pending}";
            }
            if (_voteYesBtn != null) _voteYesBtn.SetEnabled(!alreadyVoted && !revealing);
            if (_voteNoBtn != null) _voteNoBtn.SetEnabled(!alreadyVoted && !revealing);
        }

        /// <summary>全員投票完了後のリビール表示。APPROVE / REJECT それぞれ誰が投票したかを色付きで列挙。</summary>
        private string BuildVoteRevealText(GameStateManager gsm)
        {
            int yes = 0, no = 0;
            var yesList = new System.Text.StringBuilder();
            var noList = new System.Text.StringBuilder();
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                int v = gsm.ApprovalVotes[i];
                var pr = gsm.LeaderOrder[i];
                if (v == 1)
                {
                    if (yes > 0) yesList.Append(", ");
                    yesList.Append(ResolveColoredPlayerName(pr));
                    yes++;
                }
                else if (v == 0)
                {
                    if (no > 0) noList.Append(", ");
                    noList.Append(ResolveColoredPlayerName(pr));
                    no++;
                }
            }
            bool approved = yes > no;
            string headline = approved
                ? $"<color=#50FFAA>APPROVED</color> (Y:{yes} / N:{no})"
                : $"<color=#FF7878>REJECTED</color> (Y:{yes} / N:{no})";
            var sb = new System.Text.StringBuilder(headline);
            sb.Append('\n').Append("<color=#50FFAA>Y</color>: ").Append(yes == 0 ? "-" : yesList.ToString());
            sb.Append('\n').Append("<color=#FF7878>N</color>: ").Append(no == 0 ? "-" : noList.ToString());
            return sb.ToString();
        }

        private bool HasLocalVoted(GameStateManager gsm, PlayerRef localPlayer)
        {
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                if (gsm.LeaderOrder[i] == localPlayer) return gsm.ApprovalVotes[i] != -1;
            }
            return false;
        }

        private int CountVotes(GameStateManager gsm, out int pending)
        {
            int v = 0; pending = 0;
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                if (gsm.ApprovalVotes[i] == -1) pending++;
                else v++;
            }
            return v;
        }

        private void OnVoteYes() => SubmitVote(true);
        private void OnVoteNo() => SubmitVote(false);
        private void SubmitVote(bool approve)
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || _sm == null || _sm.Runner == null) return;
            if (gsm.Phase != GamePhase.ApprovalVote) return;
            gsm.Rpc_SubmitVote(_sm.Runner.LocalPlayer, approve);
            SetStatus(approve ? "> VOTE: APPROVE." : "> VOTE: REJECT.");
        }

        // ==========================================================
        // Phase: Hacking
        // ==========================================================
        private void UpdateHackingUi(GameStateManager gsm, PlayerRef localPlayer)
        {
            SetDisplay(_hackingSection, true);
            bool inTeam = IsInProposedTeam(gsm, localPlayer);
            bool isAI = gsm.LocalRole.IsAI();
            bool canNoise = isAI; // 覚醒前 DRONE は IsAI=true だが、サーバー側で CLEAN に矯正される

            if (_hackingInstruction != null)
            {
                string failNote = gsm.RequiredNoise >= 2
                    ? $"（このラウンドは NOISE {gsm.RequiredNoise} 枚以上で FAIL）"
                    : string.Empty;
                _hackingInstruction.text = inTeam
                    ? (isAI ? $"> あなたはチームに選出されました。CLEAN か NOISE を送信してください。{failNote}"
                            : $"> あなたはチームに選出されました (人類陣営なので自動的に CLEAN を送信します)。{failNote}")
                    : $"> チームがハッキング中... 待機してください。{failNote}";
            }
            if (_hackCleanBtn != null)
            {
                _hackCleanBtn.style.display = inTeam ? DisplayStyle.Flex : DisplayStyle.None;
                _hackCleanBtn.SetEnabled(inTeam && isAI); // 人類は自動なので押せない（表示のみ）
            }
            if (_hackNoiseBtn != null)
            {
                _hackNoiseBtn.style.display = (inTeam && canNoise) ? DisplayStyle.Flex : DisplayStyle.None;
                _hackNoiseBtn.SetEnabled(inTeam && canNoise); // 前ラウンドの disable が残らないよう毎回リセット
            }
            if (_hackingStatusLabel != null)
                _hackingStatusLabel.text = inTeam && !isAI ? "[ AUTO-SUBMIT: CLEAN ]" : string.Empty;
        }

        private bool IsInProposedTeam(GameStateManager gsm, PlayerRef pr)
        {
            for (int i = 0; i < gsm.ProposedTeamCount; i++)
                if (gsm.ProposedTeam[i] == pr) return true;
            return false;
        }

        private void OnHackClean() => SubmitHack(HackingCode.Clean);
        private void OnHackNoise() => SubmitHack(HackingCode.Noise);
        private void SubmitHack(HackingCode code)
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || _sm == null || _sm.Runner == null) return;
            if (gsm.Phase != GamePhase.Hacking) return;
            gsm.Rpc_SubmitHackCode(_sm.Runner.LocalPlayer, (int)code);
            SetStatus(code == HackingCode.Clean ? "> HACK CODE: CLEAN." : "> HACK CODE: NOISE.");
            if (_hackCleanBtn != null) _hackCleanBtn.SetEnabled(false);
            if (_hackNoiseBtn != null) _hackNoiseBtn.SetEnabled(false);
        }

        // ==========================================================
        // Phase: Round Result
        // ==========================================================
        private void UpdateResultUi(GameStateManager gsm)
        {
            SetDisplay(_resultSection, true);
            int noise = gsm.LastNoiseCount;
            bool success = noise == 0;
            if (_resultHeadline != null)
            {
                _resultHeadline.text = success ? "HACK SUCCEEDED" : $"HACK FAILED — {noise} NOISE";
                _resultHeadline.RemoveFromClassList("-fail");
                if (!success) _resultHeadline.AddToClassList("-fail");
            }
            if (_resultDetail != null)
            {
                _resultDetail.text = $"Success {gsm.SuccessCount}/{GameStateManager.RequiredHackSuccess}   Fail {gsm.FailureCount}/{GameStateManager.RequiredHackFailure}";
            }
        }

        // ==========================================================
        // Phase: OVERRIDE
        // ==========================================================
        private void UpdateOverrideDiscussionUi(GameStateManager gsm, PlayerRef localPlayer)
        {
            SetDisplay(_overrideDiscussionSection, true);
            bool isAi = gsm.LocalRole.IsAI();
            if (_overrideDiscussionInstruction != null)
            {
                _overrideDiscussionInstruction.text = isAi
                    ? "> AI 評議会招集。同胞が可視化されました。議論し、次の段階で ORACLE を特定せよ。"
                    : "> ORACLE は最終侵攻を察知した。AI 陣営の投票を待つ...";
            }
        }

        private void UpdateOverrideVoteUi(GameStateManager gsm, PlayerRef localPlayer)
        {
            SetDisplay(_overrideVoteSection, true);
            bool isAi = gsm.LocalRole.IsAI();
            if (_overrideVoteInstruction != null)
            {
                _overrideVoteInstruction.text = isAi
                    ? "> 1名を選び OVERRIDE 対象として送信してください。"
                    : "> AI 陣営の投票進行中... ORACLE を見抜かれるか、誤指名になるかを待つ。";
            }
            RefreshOverrideTargetList(isAi);
            if (_overrideVoteSubmitBtn != null)
            {
                bool canSubmit = isAi && !_hasSubmittedOverrideVote && _overrideTargetPickId >= 0;
                _overrideVoteSubmitBtn.SetEnabled(canSubmit);
                _overrideVoteSubmitBtn.style.display = isAi ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_overrideVoteStatus != null)
            {
                _overrideVoteStatus.text = $"AI VOTES SUBMITTED: {gsm.OverrideVoteCount}";
            }
        }

        private void RefreshOverrideTargetList(bool interactable)
        {
            if (_overrideTargetList == null) return;
            _overrideTargetList.Clear();
            var reg = PlayerRegistry.Instance;
            if (reg == null) return;
            for (int i = 0; i < reg.Count; i++)
            {
                var entry = reg.Entries[i];
                var item = CreateOverrideTargetItem(entry, interactable);
                _overrideTargetList.Add(item);
            }
        }

        private VisualElement CreateOverrideTargetItem(PlayerRegistry.Entry entry, bool interactable)
        {
            var container = new VisualElement();
            container.AddToClassList("pick-item");
            int id = entry.PlayerRef.PlayerId;
            bool selected = _overrideTargetPickId == id;
            if (selected) container.AddToClassList("-selected");

            var toggle = new Toggle();
            toggle.AddToClassList("pick-toggle");
            toggle.value = selected;
            toggle.SetEnabled(interactable && !_hasSubmittedOverrideVote);
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    _overrideTargetPickId = id;
                }
                else if (_overrideTargetPickId == id)
                {
                    _overrideTargetPickId = -1;
                }
                OnGameStateChanged();
            });
            container.Add(toggle);

            var nameLabel = new Label(BuildPlayerItemLabel(entry));
            nameLabel.AddToClassList("pick-name");
            ApplyNameColor(nameLabel, entry.PlayerRef);
            container.Add(nameLabel);
            return container;
        }

        private void OnOverrideVoteSubmit()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || _sm == null || _sm.Runner == null) return;
            if (gsm.Phase != GamePhase.OverrideVote) return;
            if (_overrideTargetPickId < 0) return;

            var reg = PlayerRegistry.Instance;
            PlayerRef targetRef = PlayerRef.None;
            if (reg != null)
            {
                for (int i = 0; i < reg.Count; i++)
                    if (reg.Entries[i].PlayerRef.PlayerId == _overrideTargetPickId)
                    {
                        targetRef = reg.Entries[i].PlayerRef;
                        break;
                    }
            }
            if (targetRef == PlayerRef.None) return;

            gsm.Rpc_SubmitOverrideVote(_sm.Runner.LocalPlayer, targetRef);
            _hasSubmittedOverrideVote = true;
            SetStatus($"> OVERRIDE VOTE SUBMITTED: {ResolvePlayerName(targetRef)}");
            if (_overrideVoteSubmitBtn != null) _overrideVoteSubmitBtn.SetEnabled(false);
        }

        private void UpdateOverrideResultUi(GameStateManager gsm)
        {
            SetDisplay(_overrideResultSection, true);
            if (_overrideResultHeadline != null)
            {
                _overrideResultHeadline.text = gsm.OverrideSucceeded ? "OVERRIDE SUCCEEDED" : "OVERRIDE FAILED";
                _overrideResultHeadline.RemoveFromClassList("-fail");
                if (!gsm.OverrideSucceeded) _overrideResultHeadline.AddToClassList("-fail");
            }
            if (_overrideResultDetail != null)
            {
                _overrideResultDetail.text = $"TARGET: {ResolvePlayerName(gsm.OverrideTarget)} — {(gsm.OverrideSucceeded ? "AI VICTORY" : "HUMAN VICTORY")}";
            }
        }

        // ==========================================================
        // Phase: Game End
        // ==========================================================
        private void UpdateGameEndUi(GameStateManager gsm)
        {
            SetDisplay(_gameendSection, true);
            bool humanWin = gsm.LastWinner == Faction.Human;
            if (_gameendHeadline != null)
            {
                _gameendHeadline.text = humanWin ? "HUMANITY PREVAILS" : "OVERMIND DOMINATES";
                _gameendHeadline.RemoveFromClassList("-fail");
                if (!humanWin) _gameendHeadline.AddToClassList("-fail");
            }
            if (_gameendDetail != null)
                _gameendDetail.text = $"Winner: {gsm.LastWinner}   Rounds played: {gsm.HackHistoryCount}";

            // Hack history: [ ✓ ] [ ✗ ] [ ✓ ] ...
            if (_gameendHackHistory != null)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < gsm.HackHistoryCount; i++)
                {
                    if (i > 0) sb.Append(' ');
                    int r = gsm.HackHistory[i];
                    sb.Append(r == 1 ? "[OK]" : r == 2 ? "[FAIL]" : "[??]");
                }
                _gameendHackHistory.text = sb.ToString();
            }

            // Role disclosure
            if (_gameendRolesList != null)
            {
                _gameendRolesList.Clear();
                var reveal = gsm.ParseRevealedRoles();
                var reg = PlayerRegistry.Instance;
                if (reg != null)
                {
                    for (int i = 0; i < reg.Count; i++)
                    {
                        var entry = reg.Entries[i];
                        var id = entry.PlayerRef.PlayerId;
                        var role = reveal.TryGetValue(id, out var r) ? r : RoleType.Operator;
                        _gameendRolesList.Add(BuildRoleRevealRow(entry, role));
                    }
                }
            }

            if (_gameendReturnBtn != null)
            {
                bool isHost = _sm != null && _sm.IsHost;
                _gameendReturnBtn.SetEnabled(isHost);
                _gameendReturnBtn.text = isHost ? "[ RETURN TO LOBBY ]" : "[ WAITING FOR HOST ]";
            }
        }

        private VisualElement BuildRoleRevealRow(PlayerRegistry.Entry entry, RoleType role)
        {
            var row = new VisualElement();
            row.AddToClassList("role-row");
            if (role.IsAI()) row.AddToClassList("-ai");

            string displayName = entry.DisplayName.ToString();
            if (string.IsNullOrEmpty(displayName))
                displayName = entry.IsCpu ? "CPU_???" : $"OPERATOR_{entry.PlayerRef.PlayerId:000}";
            bool isLocal = _sm?.Runner != null && entry.PlayerRef == _sm.Runner.LocalPlayer;
            if (isLocal) displayName += " (YOU)";

            var c = FactionColors.ForRole(role);
            var nameLabel = new Label(displayName);
            nameLabel.AddToClassList("role-row-name");
            nameLabel.style.color = c;
            row.Add(nameLabel);

            var roleLabel = new Label(role.ToString().ToUpperInvariant());
            roleLabel.AddToClassList("role-row-role");
            roleLabel.style.color = c;
            row.Add(roleLabel);

            return row;
        }

        private void OnGameEndReturnClicked()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || _sm == null || !_sm.IsHost) return;
            gsm.HostReturnToLobby();
            var reg = PlayerRegistry.Instance;
            if (reg != null) reg.ClearCpus();
            // セッションのロックを解除して再び公開可能に
            _sm.SetSessionLocked(false);
            SetStatus("> RETURNED TO LOBBY.");
        }

        // ==========================================================
        // Player list & role display (shared)
        // ==========================================================
        private void OnLocalRoleReceived()
        {
            UpdateRoleDisplay();
            RefreshIngamePlayerList();
        }

        private void UpdateRoleDisplay()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null) return;
            if (!gsm.HasLocalRole)
            {
                if (_roleLabel != null) _roleLabel.text = "...awaiting briefing...";
                if (_factionLabel != null) _factionLabel.text = string.Empty;
                return;
            }
            if (_roleLabel != null)
            {
                _roleLabel.text = gsm.LocalRole.ToString().ToUpperInvariant();
                _roleLabel.style.color = FactionColors.ForRole(gsm.LocalRole);
            }
            if (_factionLabel != null)
            {
                // 色で陣営は表示済みのためテキストは簡素化 (勝利条件の示唆のみ)
                _factionLabel.text = gsm.LocalRole.IsAI()
                    ? "> serve OVERMIND"
                    : "> resist OVERMIND";
                _factionLabel.style.color = gsm.LocalRole.IsAI() ? FactionColors.AI : FactionColors.Human;
            }
        }

        private string ResolveColoredPlayerName(PlayerRef pr)
        {
            var name = ResolvePlayerName(pr);
            if (pr == PlayerRef.None) return name;
            var gsm = GameStateManager.Instance;
            if (gsm == null || !gsm.HasLocalRole) return name;
            var visRole = gsm.GetVisibleRole(pr.PlayerId);
            if (!HasRoleKnowledge(gsm, pr, visRole)) return name;
            return $"<color={FactionColors.HexForRole(visRole)}>{name}</color>";
        }

        private string ResolvePlayerName(PlayerRef pr)
        {
            if (pr == PlayerRef.None) return "-";
            var reg = PlayerRegistry.Instance;
            if (reg == null) return $"#{pr.PlayerId}";
            for (int i = 0; i < reg.Count; i++)
            {
                if (reg.Entries[i].PlayerRef == pr)
                {
                    var n = reg.Entries[i].DisplayName.ToString();
                    return string.IsNullOrEmpty(n) ? $"OPERATOR_{pr.PlayerId:000}" : n;
                }
            }
            return $"#{pr.PlayerId}";
        }

        private string BuildPlayerItemLabel(PlayerRegistry.Entry entry)
        {
            string displayName = entry.DisplayName.ToString();
            if (string.IsNullOrEmpty(displayName))
                displayName = entry.IsCpu ? "CPU_???" : $"OPERATOR_{entry.PlayerRef.PlayerId:000}";
            bool isLocal = _sm?.Runner != null && entry.PlayerRef == _sm.Runner.LocalPlayer;
            if (isLocal) displayName += " (YOU)";
            return displayName;
        }

        private void RefreshIngamePlayerList()
        {
            if (_ingamePlayerList == null) return;
            _ingamePlayerList.Clear();
            var reg = PlayerRegistry.Instance;
            var gsm = GameStateManager.Instance;
            if (reg == null) return;
            for (int i = 0; i < reg.Count; i++)
            {
                var entry = reg.Entries[i];
                var visRole = gsm != null ? gsm.GetVisibleRole(entry.PlayerRef.PlayerId) : RoleType.Operator;
                bool known = gsm != null && gsm.HasLocalRole && HasRoleKnowledge(gsm, entry.PlayerRef, visRole);
                string displayName = BuildPlayerItemLabel(entry);
                string leaderPrefix = gsm != null && gsm.CurrentLeader == entry.PlayerRef ? "> " : "  ";
                bool onProposedTeam = gsm != null && IsInProposedTeam(gsm, entry.PlayerRef);
                string teamMark = onProposedTeam ? " [TEAM]" : string.Empty;
                var label = $"{leaderPrefix}{displayName}{teamMark}";
                var lbl = new Label(label);
                if (known) lbl.style.color = FactionColors.ForRole(visRole);
                _ingamePlayerList.Add(lbl);
            }
            RefreshHackLogList();
        }

        /// <summary>HACK LOG パネル: 過去のハック試行をカードで表示 (ラウンド / 結果 / リーダー / メンバー / NOISE)。</summary>
        private void RefreshHackLogList()
        {
            if (_hackLogList == null) return;
            _hackLogList.Clear();
            var gsm = GameStateManager.Instance;
            if (gsm == null) { SetDisplay(_hackLogEmpty, true); return; }
            var records = gsm.ParseHackDetails();
            SetDisplay(_hackLogEmpty, records.Count == 0);
            foreach (var r in records) _hackLogList.Add(BuildHackLogRow(r));
        }

        private VisualElement BuildHackLogRow(GameStateManager.PublicHackRecord r)
        {
            var row = new VisualElement();
            row.AddToClassList("hack-log-row");
            row.AddToClassList(r.Success ? "-success" : "-fail");

            var head = new VisualElement();
            head.AddToClassList("hack-log-head");
            var rLbl = new Label($"R{r.Round}  LEADER: {ResolveColoredPlayerName(r.Leader)}");
            rLbl.enableRichText = true;
            rLbl.AddToClassList("hack-log-round");
            head.Add(rLbl);
            var resLbl = new Label(r.Success ? "SUCCESS" : "FAIL");
            resLbl.AddToClassList("hack-log-result");
            resLbl.AddToClassList(r.Success ? "-success" : "-fail");
            head.Add(resLbl);
            row.Add(head);

            var teamSb = new System.Text.StringBuilder();
            teamSb.Append("TEAM: ");
            for (int i = 0; i < r.Team.Count; i++)
            {
                if (i > 0) teamSb.Append(", ");
                teamSb.Append(ResolveColoredPlayerName(r.Team[i]));
            }
            teamSb.Append("   NOISE: ").Append(r.Noise);
            var body = new Label(teamSb.ToString());
            body.enableRichText = true;
            body.AddToClassList("hack-log-body");
            row.Add(body);

            return row;
        }

        private void ApplyNameColor(Label nameLabel, Fusion.PlayerRef pr)
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || !gsm.HasLocalRole) return;
            var visRole = gsm.GetVisibleRole(pr.PlayerId);
            if (!HasRoleKnowledge(gsm, pr, visRole)) return;
            nameLabel.style.color = FactionColors.ForRole(visRole);
        }

        /// <summary>ローカルプレイヤーがこの対象の役職情報を実際に持っているか判定。
        /// 自分の役職によって「知っている」範囲が変わる:
        /// - Oracle: 全員 (人類は Operator 表示で青着色 / AI は真の役職で赤着色)
        /// - Admin: Oracle / MotherCore (両方 Oracle 表示) だけ知識あり
        /// - AI (MC/Agent/Cipher/覚醒 Drone): 互いを知識あり
        /// - Operator / 未覚醒 Drone / Radical(通常時): 情報なし</summary>
        private bool HasRoleKnowledge(GameStateManager gsm, Fusion.PlayerRef target, RoleType visRole)
        {
            if (_sm?.Runner != null && target == _sm.Runner.LocalPlayer) return true;
            var selfRole = gsm.LocalRole;
            switch (selfRole)
            {
                case RoleType.Oracle: return true;
                case RoleType.Admin: return visRole == RoleType.Oracle;
                case RoleType.MotherCore:
                case RoleType.Agent:
                case RoleType.Cipher:
                case RoleType.Drone:
                    return visRole.IsAI();
                default:
                    return false;
            }
        }

        // ==========================================================
        // Lobby: registry / settings / start
        // ==========================================================
        private void OnRegistryChanged()
        {
            if (_playerList != null)
            {
                _playerList.Clear();
                int count = 0;
                var reg = PlayerRegistry.Instance;
                if (reg != null)
                {
                    count = reg.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var entry = reg.Entries[i];
                        _playerList.Add(CreateLobbyPlayerItem(entry));
                    }
                }
                if (_playerCountLine != null)
                {
                    int max = HostSettings.Instance != null && HostSettings.Instance.TargetPlayerCount > 0
                        ? HostSettings.Instance.TargetPlayerCount : 10;
                    _playerCountLine.text = $"[ {count} / {max} ]";
                }
            }
            RefreshIngamePlayerList();
            RefreshChatTargets();
            // Re-refresh pick list if in proposal phase
            var gsm = GameStateManager.Instance;
            if (gsm != null && gsm.Phase == GamePhase.TeamProposal)
            {
                bool isLeader = _sm?.Runner != null && gsm.CurrentLeader == _sm.Runner.LocalPlayer;
                RefreshTeamPickList(gsm, isLeader);
            }
        }

        private VisualElement CreateLobbyPlayerItem(PlayerRegistry.Entry entry)
        {
            string displayName = entry.DisplayName.ToString();
            if (string.IsNullOrEmpty(displayName))
                displayName = entry.IsCpu ? "CPU_???" : $"OPERATOR_{entry.PlayerRef.PlayerId:000}";

            if (playerListItemTemplate == null)
                return new Label(displayName);

            var tree = playerListItemTemplate.CloneTree();
            var itemRoot = tree.Q<VisualElement>(className: "player-list-item");
            var nameLabel = tree.Q<Label>("player-name");
            var hostBadge = tree.Q<Label>("host-badge");
            var cpuBadge = tree.Q<Label>("cpu-badge");

            bool isLocal = _sm.Runner != null && entry.PlayerRef == _sm.Runner.LocalPlayer;
            if (nameLabel != null) nameLabel.text = displayName + (isLocal ? " (YOU)" : string.Empty);
            if (hostBadge != null) hostBadge.style.display = entry.IsHost ? DisplayStyle.Flex : DisplayStyle.None;
            if (cpuBadge != null) cpuBadge.style.display = entry.IsCpu ? DisplayStyle.Flex : DisplayStyle.None;
            if (entry.IsHost && itemRoot != null) itemRoot.AddToClassList("-host");
            return tree;
        }

        private void OnSettingsChanged()
        {
            var hs = HostSettings.Instance;
            if (hs == null) return;
            _suppressSettingsPush = true;
            try
            {
                if (_playerCountSlider != null) _playerCountSlider.SetValueWithoutNotify(Mathf.Clamp(hs.TargetPlayerCount, 6, 10));
                if (_discussionSlider != null) _discussionSlider.SetValueWithoutNotify(hs.DiscussionSeconds);
                if (_voteSlider != null) _voteSlider.SetValueWithoutNotify(hs.VoteSeconds);
                if (_hackSlider != null) _hackSlider.SetValueWithoutNotify(hs.HackSeconds);
                if (_cpuFillToggle != null) _cpuFillToggle.SetValueWithoutNotify(hs.EnableCpuFill);
            }
            finally { _suppressSettingsPush = false; }

            bool host = _sm != null && _sm.IsHost;
            if (_playerCountSlider != null) _playerCountSlider.SetEnabled(host);
            if (_discussionSlider != null) _discussionSlider.SetEnabled(host);
            if (_voteSlider != null) _voteSlider.SetEnabled(host);
            if (_hackSlider != null) _hackSlider.SetEnabled(host);
            if (_cpuFillToggle != null) _cpuFillToggle.SetEnabled(host);
            // ホスト権限と目標人数の変動を反映してラインナップを再描画
            RefreshRoleLineup();
            if (_lockToggle != null)
            {
                _lockToggle.SetEnabled(host);
                bool isOpen = _sm?.Runner?.SessionInfo?.IsOpen ?? true;
                _suppressSettingsPush = true;
                try { _lockToggle.SetValueWithoutNotify(!isOpen); }
                finally { _suppressSettingsPush = false; }
            }
            OnRegistryChanged();
        }

        private void OnLockToggleChanged(bool locked)
        {
            if (_suppressSettingsPush) return;
            if (_sm == null || !_sm.IsHost) return;
            _sm.SetSessionLocked(locked);
            SetStatus(locked ? "> SESSION LOCKED." : "> SESSION UNLOCKED.");
        }

        private void PushSettings()
        {
            if (_suppressSettingsPush) return;
            if (_sm == null || !_sm.IsHost) return;
            var hs = HostSettings.Instance;
            if (hs == null || !hs.HasStateAuthority) return;
            hs.TargetPlayerCount = _playerCountSlider.value;
            hs.DiscussionSeconds = _discussionSlider.value;
            hs.VoteSeconds = _voteSlider.value;
            hs.HackSeconds = _hackSlider.value;
            hs.EnableCpuFill = _cpuFillToggle.value;
        }

        private void OnStartClicked()
        {
            if (_sm == null || !_sm.IsHost) return;
            var gsm = GameStateManager.Instance;
            if (gsm == null) { SetStatus("> ERROR: GameStateManager not ready.", true); return; }
            if (roleDistributionConfig == null) { SetStatus("> ERROR: RoleDistributionConfig not assigned.", true); return; }

            var reg = PlayerRegistry.Instance;
            var hs = HostSettings.Instance;
            if (reg == null) { SetStatus("> ERROR: PlayerRegistry not ready.", true); return; }

            // CPU fill: 既存 CPU をリセットしてから TargetPlayerCount まで埋める
            reg.ClearCpus();
            int humanCount = 0;
            for (int i = 0; i < reg.Count; i++) if (!reg.Entries[i].IsCpu) humanCount++;

            if (hs != null && hs.EnableCpuFill)
            {
                int target = Mathf.Clamp(hs.TargetPlayerCount, humanCount, GameStateManager.MaxPlayers);
                int needed = Mathf.Max(0, target - humanCount);
                for (int i = 0; i < needed; i++)
                    reg.RegisterCpu($"CPU_{(i + 1):00}");
            }

            int totalPlayers = reg.Count;
            var entry = BuildRuntimeRoleEntry(totalPlayers);
            if (entry.TotalPlayers != totalPlayers)
            {
                SetStatus($"> ERROR: role lineup totals {entry.TotalPlayers} but need {totalPlayers}.", true);
                return;
            }

            _sm.SetSessionLocked(true);
            var seed = System.Environment.TickCount;
            if (!gsm.HostBeginGame(entry, seed))
            {
                SetStatus("> ERROR: failed to start game.", true);
                return;
            }
            SetStatus("> GAME STARTED.");
        }

        // ==========================================================
        // Chat
        // ==========================================================
        private void InitChatComposer()
        {
            if (_chatInput != null) _chatInput.SetValueWithoutNotify(string.Empty);
            RefreshChatTargets();
            RefreshChatLog();
        }

        private void RefreshChatTargets()
        {
            if (_chatMentionRow == null) return;
            _chatMentionRow.Clear();
            var reg = PlayerRegistry.Instance;
            if (reg == null) return;
            for (int i = 0; i < reg.Count; i++)
            {
                var entry = reg.Entries[i];
                string n = entry.DisplayName.ToString();
                if (string.IsNullOrEmpty(n)) n = entry.IsCpu ? "CPU_???" : $"OPERATOR_{entry.PlayerRef.PlayerId:000}";
                var captured = n;
                var chip = new Button(() => InsertMention(captured)) { text = "@" + n };
                chip.AddToClassList("chat-mention-chip");
                _chatMentionRow.Add(chip);
            }
        }

        private void InsertMention(string name)
        {
            if (_chatInput == null) return;
            var cur = _chatInput.value ?? string.Empty;
            var sep = (cur.Length == 0 || cur.EndsWith(" ")) ? string.Empty : " ";
            _chatInput.value = cur + sep + "@" + name + " ";
            _chatInput.Focus();
        }

        private void OnChatInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                if (!evt.shiftKey)
                {
                    OnChatSendClicked();
                    evt.StopPropagation();
                }
            }
        }

        private void OnChatSendClicked()
        {
            if (_sm == null || _sm.Runner == null) return;
            if (ChatManager.Instance == null) return;
            if (_chatInput == null) return;
            var msg = _chatInput.value?.Trim();
            if (string.IsNullOrEmpty(msg)) return;
            if (msg.Length > 60) msg = msg.Substring(0, 60);
            ChatManager.Instance.Rpc_SendThought(_sm.Runner.LocalPlayer, msg);
            _chatInput.SetValueWithoutNotify(string.Empty);
        }

        private void OnChatChanged()
        {
            RefreshChatLog();
        }

        private void OnGameLogChanged()
        {
            RefreshChatLog();
        }

        private void RefreshChatLog()
        {
            if (_chatLog == null) return;
            _chatLog.Clear();
            var cm = ChatManager.Instance;
            var gl = GameLog.Instance;

            // チャット (player-sent) とイベントログ (system) を Tick で時系列マージ
            var merged = new List<(int tick, bool isSystem, int ringIdx, ChatManager.Entry chat, GameLog.Entry sys)>();
            if (cm != null)
            {
                foreach (var (ringIdx, entry) in cm.EnumerateInOrder())
                    merged.Add((entry.Tick, false, ringIdx, entry, default));
            }
            if (gl != null)
            {
                foreach (var entry in gl.EnumerateInOrder())
                    merged.Add((entry.Tick, true, -1, default, entry));
            }
            merged.Sort((a, b) => a.tick.CompareTo(b.tick));

            foreach (var m in merged)
            {
                if (m.isSystem) _chatLog.Add(BuildSystemLogElement(m.sys));
                else _chatLog.Add(BuildChatEntryElement(m.ringIdx, m.chat));
            }
            _chatLog.scrollOffset = new Vector2(0, float.MaxValue);
        }

        private VisualElement BuildSystemLogElement(GameLog.Entry entry)
        {
            var text = entry.Text.ToString();
            var root = new VisualElement();
            root.AddToClassList("chat-entry");
            var lbl = new Label(text);
            lbl.AddToClassList("game-log-entry");
            // タグ別に強調色を付ける (頭の [TAG] で分類)
            if (text.StartsWith("[GAME]")) lbl.AddToClassList("-tag-game");
            else if (text.StartsWith("[ROUND]")) lbl.AddToClassList("-tag-round");
            else if (text.StartsWith("[TEAM]")) lbl.AddToClassList("-tag-team");
            else if (text.StartsWith("[VOTE]"))
            {
                lbl.AddToClassList("-tag-vote");
                if (text.Contains("APPROVED")) lbl.AddToClassList("-hack-success");
                else if (text.Contains("REJECTED")) lbl.AddToClassList("-hack-fail");
            }
            else if (text.StartsWith("[HACK]"))
            {
                lbl.AddToClassList("-tag-hack");
                if (text.Contains("SUCCESS")) lbl.AddToClassList("-hack-success");
                else if (text.Contains("FAIL")) lbl.AddToClassList("-hack-fail");
            }
            else if (text.StartsWith("[OVERRIDE]")) lbl.AddToClassList("-tag-override");
            root.Add(lbl);
            return root;
        }

        private VisualElement BuildChatEntryElement(int ringIdx, ChatManager.Entry entry)
        {
            var root = new VisualElement();
            root.AddToClassList("chat-entry");

            var textLabel = new Label(ChatManager.FormatEntryText(entry, ResolveColoredPlayerName));
            textLabel.AddToClassList("chat-entry-text");
            textLabel.enableRichText = true;
            root.Add(textLabel);

            return root;
        }

        // ==========================================================
        // Status line
        // ==========================================================
        private void SetStatus(string msg, bool isError = false)
        {
            if (_statusLine == null) return;
            _statusLine.text = msg;
            _statusLine.RemoveFromClassList("-error");
            if (isError) _statusLine.AddToClassList("-error");
        }
    }
}
