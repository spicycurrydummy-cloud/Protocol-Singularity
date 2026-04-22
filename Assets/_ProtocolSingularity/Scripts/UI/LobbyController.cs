using System.Collections;
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
        // 6 人スタンダード構成: Oracle+Admin+Operator x2 / MotherCore+Agent → Human 4 : AI 2。
        // HostSettings 同期前のローカル初期値を Agent=true に寄せておくことで
        // 初回に Host が触らなくてもすぐ「4:2」が成立する (後ほど Host が初期化で push する)。
        private bool _roleIncludeAgent = true;
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
        private Coroutine _overrideHumanAnimCo;
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
        private Button _menuRulesBtn;
        private VisualElement _rulesOverlay;
        private Label _rulesBody;
        private Button _rulesCloseBtn;
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
            ImeBridge.TextChanged += OnImeTextChanged;
            ImeBridge.Submitted += OnImeSubmitted;

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
            if (_menuRulesBtn != null) _menuRulesBtn.clicked -= OpenRulesFromMenu;
            if (_menuLeaveBtn != null) _menuLeaveBtn.clicked -= OnLeaveClicked;
            if (_rulesCloseBtn != null) _rulesCloseBtn.clicked -= CloseRules;
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
            ImeBridge.TextChanged -= OnImeTextChanged;
            ImeBridge.Submitted -= OnImeSubmitted;
            ImeBridge.BindActive(null, null);
            ImeBridge.Hide();
        }

        /// <summary>ImeBridge が存在しなければ生成する (WebGL で JS から SendMessage できるよう)。</summary>
        private static void EnsureImeBridge()
        {
            if (ImeBridge.Instance != null) return;
            var go = new GameObject("ImeBridge");
            go.AddComponent<ImeBridge>();
        }

        /// <summary>WebGL HTML overlay の入力値が変化したら Unity chat-input にミラーする。</summary>
        private void OnImeTextChanged(string text)
        {
            if (_chatInput == null) return;
            _chatInput.SetValueWithoutNotify(text ?? string.Empty);
        }

        /// <summary>JS 側 Enter キー → chat 送信。送信後 overlay もクリアする。</summary>
        private void OnImeSubmitted()
        {
            OnChatSendClicked();
            ImeBridge.SetValue(string.Empty);
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
            _menuRulesBtn = root.Q<Button>("menu-rules-btn");
            _menuLeaveBtn = root.Q<Button>("menu-leave-btn");
            _rulesOverlay = root.Q<VisualElement>("rules-overlay");
            _rulesBody = root.Q<Label>("rules-body");
            _rulesCloseBtn = root.Q<Button>("rules-close-btn");
            if (_rulesBody != null)
            {
                _rulesBody.enableRichText = true;
                _rulesBody.text = RulesText.Body;
            }

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
            if (_chatInput != null)
            {
                _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            }
            if (_gameendReturnBtn != null) _gameendReturnBtn.clicked += OnGameEndReturnClicked;
            if (_menuBtn != null) _menuBtn.clicked += OpenMenu;
            if (_menuCloseBtn != null) _menuCloseBtn.clicked += CloseMenu;
            if (_menuRulesBtn != null) _menuRulesBtn.clicked += OpenRulesFromMenu;
            if (_menuLeaveBtn != null) _menuLeaveBtn.clicked += OnLeaveClicked;
            if (_menuOverlay != null) _menuOverlay.RegisterCallback<ClickEvent>(OnOverlayClicked);
            if (_rulesCloseBtn != null) _rulesCloseBtn.clicked += CloseRules;
            if (_rulesOverlay != null) _rulesOverlay.RegisterCallback<ClickEvent>(evt => { if (evt.target == _rulesOverlay) CloseRules(); });

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
                PushSettings(); // 同期しないとクライアントが取り残される
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

            // AI 陣営が人類以上 = 5 連続否決で AI が勝つため人類は勝利不可能。
            bool unwinnable = ai >= human;
            string summaryText;
            bool isError;
            if (opCount < 0)
            {
                summaryText = "! AI 役職が多すぎます。\nAI 陣営は人類陣営より少なくしてください。";
                isError = true;
            }
            else if (unwinnable)
            {
                summaryText = "! AI 役職が多すぎます。\nAI 陣営は人類陣営より少なくしてください。";
                isError = true;
            }
            else
            {
                summaryText = $"Humans {human} : AI {ai}";
                isError = false;
            }

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
                _roleLineupList.Add(BuildRoleRow("AGENT (basic AI)", _roleIncludeAgent ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeAgent, toggleValue: _roleIncludeAgent, onChange: v => { _roleIncludeAgent = v; PushSettings(); RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("CIPHER (hidden from ORACLE)", _roleIncludeCipher ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeCipher, toggleValue: _roleIncludeCipher, onChange: v => { _roleIncludeCipher = v; PushSettings(); RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("DRONE (awakens mid-game)", _roleIncludeDrone ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeDrone, toggleValue: _roleIncludeDrone, onChange: v => { _roleIncludeDrone = v; PushSettings(); RefreshRoleLineup(); }));
                _roleLineupList.Add(BuildRoleRow("RADICAL (isolated AI)", _roleIncludeRadical ? 1 : 0, isAi: true, fixedRole: false, enabled: _roleIncludeRadical, toggleValue: _roleIncludeRadical, onChange: v => { _roleIncludeRadical = v; PushSettings(); RefreshRoleLineup(); }));
            }

            if (_roleEditBtn != null)
            {
                _roleEditBtn.SetEnabled(host);
                _roleEditBtn.style.display = host ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // Non-host clients must never see the editor even if it was open
            if (!host && _roleEditorOverlay != null)
                _roleEditorOverlay.style.display = DisplayStyle.None;

            if (_startBtn != null) _startBtn.SetEnabled(host && opCount >= 0 && !unwinnable);
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

        // CPU に割り当てるコードネーム候補。タロット大アルカナ 22 枚 ("THE" は除外) を採用。
        // CpuPlayerRef.MaxCpuCount (10) を超えて増えても候補が尽きないよう 22 枠を用意。
        private static readonly string[] CpuCodenames =
        {
            "FOOL", "MAGICIAN", "PRIESTESS", "EMPRESS", "EMPEROR",
            "HIEROPHANT", "LOVERS", "CHARIOT", "STRENGTH", "HERMIT",
            "FORTUNE", "JUSTICE", "HANGED", "DEATH", "TEMPERANCE",
            "DEVIL", "TOWER", "STAR", "MOON", "SUN",
            "JUDGEMENT", "WORLD"
        };

        private static System.Collections.Generic.List<string> BuildShuffledCpuCodenames()
        {
            var list = new System.Collections.Generic.List<string>(CpuCodenames);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
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

        private void OpenRulesFromMenu()
        {
            CloseMenu();
            if (_rulesOverlay != null) _rulesOverlay.style.display = DisplayStyle.Flex;
        }
        private void CloseRules()
        {
            if (_rulesOverlay != null) _rulesOverlay.style.display = DisplayStyle.None;
        }

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
            // 設定パネルはクライアント側でも表示して内容を確認できるようにする。
            // 編集可能なコントロールだけ host 限定で無効化する。
            if (_hostSettings != null) _hostSettings.style.display = DisplayStyle.Flex;
            if (_startBtn != null) _startBtn.SetEnabled(host);
            if (_playerCountSlider != null) _playerCountSlider.SetEnabled(host);
            if (_discussionSlider != null) _discussionSlider.SetEnabled(host);
            if (_voteSlider != null) _voteSlider.SetEnabled(host);
            if (_hackSlider != null) _hackSlider.SetEnabled(host);
            if (_cpuFillToggle != null) _cpuFillToggle.SetEnabled(host);
            // role-edit ボタンは既に RefreshRoleLineup 側で host 限定表示にしているため触らない
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
                _leaderLabel.text = $"提案者: {ResolvePlayerName(gsm.CurrentLeader)}";

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
            // 次フェーズに赤テーマが引き継がれないよう外す
            if (_phasePopupDialog != null) _phasePopupDialog.RemoveFromClassList("-danger-active");
            // 進捗アニメも停止
            StopOverrideHumanAnim();
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
                _teamProposalInstruction.enableRichText = true;
                string failClause = gsm.RequiredNoise >= 2
                    ? $"\n<color=#FFD84D>※ このラウンドは NOISE {gsm.RequiredNoise} 枚以上で失敗</color>"
                    : string.Empty;
                if (isLeader)
                {
                    _teamProposalInstruction.text = $"> あなたが <b>提案者</b> です。<b>{gsm.TeamSize}名</b>を選んで提案してください。{failClause}";
                }
                else
                {
                    // リーダーがチーム選定中の間は、他のプレイヤーにはリストではなく
                    // "待機中" が一目で分かるメッセージのみを表示する。
                    var leaderColored = ResolveColoredPlayerName(gsm.CurrentLeader);
                    _teamProposalInstruction.text =
                        $"<size=20>> <b>{leaderColored}</b> がハッキングチームを計画中...</size>\n" +
                        $"<size=14>　チーム枠: {gsm.TeamSize}名</size>{failClause}";
                }
            }
            // リーダー以外にはピックリストを隠して、注視先を meta メッセージに集中させる。
            SetDisplay(_teamPickList, isLeader);
            if (isLeader) RefreshTeamPickList(gsm, isLeader);
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
                sb.Append("> 提案者: ").Append(ResolveColoredPlayerName(gsm.CurrentLeader)).Append('\n');
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
                ? $"<color=#50FFAA>承認</color> ({yes} 対 {no})"
                : $"<color=#FF7878>却下</color> ({yes} 対 {no})";
            var sb = new System.Text.StringBuilder(headline);
            sb.Append('\n').Append("<color=#50FFAA>承認</color>: ").Append(yes == 0 ? "-" : yesList.ToString());
            sb.Append('\n').Append("<color=#FF7878>却下</color>: ").Append(no == 0 ? "-" : noList.ToString());
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
            SetStatus(approve ? "> 投票: 承認" : "> 投票: 却下");
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
            if (_phasePopupDialog != null) _phasePopupDialog.AddToClassList("-danger-active");
            bool isAi = gsm.LocalRole.IsAI();
            if (_overrideDiscussionHeadline != null)
            {
                _overrideDiscussionHeadline.text = isAi
                    ? "【危険】暗号プロテクトに深刻な障害が発生。"
                    : "【警告】不明な暗号通信を確認。";
            }
            if (_overrideDiscussionInstruction != null)
            {
                if (isAi)
                {
                    _overrideDiscussionInstruction.text = "> 特定対象: ORACLE\n> 直ちに逆探知および脅威の排除を開始。";
                    StopOverrideHumanAnim();
                }
                else
                {
                    // 数値を徐々に 80 → 90 まで上げて 90 付近で止まる演出
                    StopOverrideHumanAnim();
                    _overrideHumanAnimCo = StartCoroutine(AnimateOverrideHumanProgress());
                }
            }
        }

        private void StopOverrideHumanAnim()
        {
            if (_overrideHumanAnimCo != null)
            {
                StopCoroutine(_overrideHumanAnimCo);
                _overrideHumanAnimCo = null;
            }
        }

        /// <summary>人類側の ORACLE 破壊プロトコル進捗テキストを徐々に 90% 付近まで進めて停止する。</summary>
        private IEnumerator AnimateOverrideHumanProgress()
        {
            int[] sequence = { 40, 44, 49, 53, 58, 63, 69, 74, 79, 83, 86, 88, 89, 90, 90 };
            foreach (int pct in sequence)
            {
                if (_overrideDiscussionInstruction == null) yield break;
                _overrideDiscussionInstruction.text =
                    "> MOTHER CORE によるネットワークへの侵入と特定。\n" +
                    $"> ORACLE による破壊プロトコル実行を最優先、実行中: {pct}% ...";
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.6f, 1.1f));
            }
            // 以降は 90% のまま保持 (OVERRIDE 投票フェーズ遷移までここで待機)
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
                _gameendReturnBtn.text = isHost ? "[ ロビーへ戻る ]" : "[ ホスト待機中... ]";
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

        private int _lastRoleDiagPhase = -1;
        private void UpdateRoleDisplay()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null) return;
            if (!gsm.HasLocalRole)
            {
                // 発生条件診断: phase / HasStateAuthority / Runner ありなし
                int p = (int)gsm.Phase;
                if (p != _lastRoleDiagPhase)
                {
                    _lastRoleDiagPhase = p;
                    Debug.Log($"[UI] role display stuck on 受信中. phase={gsm.Phase} HasStateAuthority={gsm.HasStateAuthority} Runner={(_sm?.Runner != null)} LocalPlayerId={_sm?.Runner?.LocalPlayer.PlayerId}");
                }
                if (_roleLabel != null) _roleLabel.text = "... 役職を受信中 ...";
                if (_factionLabel != null) _factionLabel.text = "ホストからの割り当てを待っています";
                return;
            }
            if (_roleLabel != null)
            {
                _roleLabel.text = gsm.LocalRole.ToString().ToUpperInvariant();
                _roleLabel.style.color = FactionColors.ForRole(gsm.LocalRole);
            }
            if (_factionLabel != null)
            {
                _factionLabel.enableRichText = true;
                _factionLabel.text = BuildFactionDescription(gsm.LocalRole);
                _factionLabel.style.color = gsm.LocalRole.IsAI() ? FactionColors.AI : FactionColors.Human;
            }
        }

        /// <summary>YOUR ROLE パネル下の faction 行: 日本語で陣営 + 短い役職説明。</summary>
        private static string BuildFactionDescription(RoleType role)
        {
            string faction = role.IsAI() ? "AI 陣営" : "人類陣営";
            string desc = role switch
            {
                RoleType.Oracle     => "全員の陣営を識別できる\nただし CIPHER だけは盲点",
                RoleType.Admin      => "ORACLE と MC の両方が Oracle に見える\n本物が見分けられない",
                RoleType.Operator   => "特殊能力なし\n推理と議論で AI を見抜く",
                RoleType.MotherCore => "AI 陣営のリーダー\nOVERRIDE フェーズを主導する",
                RoleType.Agent      => "標準 AI\nNOISE 混入でハックを妨害",
                RoleType.Cipher     => "ORACLE の索引から除外された暗号化 AI\nORACLE から Operator と誤認される",
                RoleType.Drone      => "序盤は自分を Operator と誤認\n2 ハック終了後に AI として覚醒",
                RoleType.Radical    => "孤立した急進派 AI\nOVERRIDE 時のみ他 AI と合流",
                _                   => "",
            };
            return $"{faction}\n{desc}";
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
            var rLbl = new Label($"R{r.Round}  提案者: {ResolveColoredPlayerName(r.Leader)}");
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
            var body = new Label(teamSb.ToString());
            body.enableRichText = true;
            body.AddToClassList("hack-log-body");
            row.Add(body);

            // NOISE 数は SUCCESS/FAIL 表示の下に別行で (見やすさ向上のため)
            var noiseLbl = new Label($"NOISE: {r.Noise}");
            noiseLbl.AddToClassList("hack-log-noise");
            row.Add(noiseLbl);

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
                // Role 編集の真実源は HostSettings (ホストが更新するとクライアントへ同期)。
                _roleIncludeAgent = hs.IncludeAgent;
                _roleIncludeCipher = hs.IncludeCipher;
                _roleIncludeDrone = hs.IncludeDrone;
                _roleIncludeRadical = hs.IncludeRadical;
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
            hs.IncludeAgent = _roleIncludeAgent;
            hs.IncludeCipher = _roleIncludeCipher;
            hs.IncludeDrone = _roleIncludeDrone;
            hs.IncludeRadical = _roleIncludeRadical;
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
                var names = BuildShuffledCpuCodenames();
                for (int i = 0; i < needed; i++)
                    reg.RegisterCpu(names[i]);
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
            if (_chatInput != null)
            {
                _chatInput.SetValueWithoutNotify(string.Empty);
                // WebGL では IME が動かないため HTML overlay (ImeBridge) に実入力を委ねる。
                // Unity 側の chat-input は値のミラー表示のみ。overlay はフォーカス時に真上へ配置する。
                if (ImeBridge.IsAvailable)
                {
                    _chatInput.isReadOnly = true;
                    _chatInput.RegisterCallback<PointerDownEvent>(_ => PlaceOverlayOnChat());
                    _chatInput.RegisterCallback<FocusInEvent>(_ => PlaceOverlayOnChat());
                    // Blur 直後と次フレームの 2 段で shadow から書き戻す安全網 (WebGL 向け)
                    _chatInput.RegisterCallback<BlurEvent>(_ =>
                    {
                        RestoreChatShadow();
                        _chatInput.schedule.Execute(RestoreChatShadow);
                    });
                }
            }
            RefreshChatTargets();
            RefreshChatLog();
        }

        private string _chatInputShadow = string.Empty;

        private void RestoreChatShadow()
        {
            if (_chatInput == null) return;
            if (_chatInput.value != _chatInputShadow)
                _chatInput.SetValueWithoutNotify(_chatInputShadow);
        }

        private void PlaceOverlayOnChat()
        {
            if (_chatInput == null || _chatInput.panel == null) return;
            var panelRect = _chatInput.panel.visualTree.worldBound;
            // font-size や padding は USS で内部 .unity-base-text-field__input に当たっているため、
            // bounds と font-size も内部要素から取得する。
            var innerInput = _chatInput.Q<VisualElement>(className: "unity-base-text-field__input")
                             ?? (VisualElement)_chatInput;
            var fieldRect = innerInput.worldBound;
            float fontPx = innerInput.resolvedStyle.fontSize;
            ImeBridge.SetValue(_chatInput.value);
            ImeBridge.BindActive(v =>
            {
                _chatInputShadow = v;
                _chatInput.SetValueWithoutNotify(v);
            }, null);
            ImeBridge.PlaceOverField(fieldRect, panelRect, fontPx);
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
            // WebGL は HTML overlay 側にキャレット位置で挿入してフォーカスを戻す。
            if (ImeBridge.IsAvailable)
            {
                // mention ボタンクリックで chat-input はフォーカスを失っている可能性があるため、
                // まず overlay を chat-input の真上へ再配置してから Insert する。
                PlaceOverlayOnChat();
                var cur = _chatInput?.value ?? string.Empty;
                var sep = (cur.Length == 0 || cur.EndsWith(" ")) ? string.Empty : " ";
                ImeBridge.Insert(sep + "@" + name + " ");
                return;
            }
            if (_chatInput == null) return;
            {
                var cur = _chatInput.value ?? string.Empty;
                var sep = (cur.Length == 0 || cur.EndsWith(" ")) ? string.Empty : " ";
                _chatInput.value = cur + sep + "@" + name + " ";
                _chatInput.Focus();
            }
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
            _chatInputShadow = string.Empty;
            // HTML overlay 側もクリアする (WebGL のみ実効、それ以外は no-op)
            ImeBridge.SetValue(string.Empty);
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
                if (text.Contains("承認")) lbl.AddToClassList("-hack-success");
                else if (text.Contains("却下")) lbl.AddToClassList("-hack-fail");
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
